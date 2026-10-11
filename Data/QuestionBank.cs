using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// Savollar bazasi xotirada (tezlik uchun): test ochilganda bazaga murojaat qilinmaydi.
/// Admin savol qo'shsa / tahrirlasa / o'chirsa — Invalidate() chaqiriladi va keyingi so'rovda qayta yuklanadi.
/// Keshdagi obyektlar faqat o'qiladi (QuestionJson.From ularni o'zgartirmaydi).
///
/// Savollar manbasi (Question.Source):
///   ptest — asosiy baza: biletlar va barcha test turlari (20/40/80/100/200, marafon, imtihon);
///   old   — eski baza (o'chirilgan bo'lishi mumkin); faqat ptest savollari umuman bo'lmasa testlar shundan tuziladi.
/// Mavzular: QuestionTopics jadvali (bir savol bir nechta mavzuda bo'lishi mumkin, ptest tartibida);
/// bog'lanishi yo'q, lekin Topic ustuni to'ldirilgan savollar (masalan, admin qo'shgan) ham o'z mavzusida chiqadi.
/// </summary>
public class QuestionBank
{
    public const int QuestionsPerBilet = 20;

    private const string Key = "question-bank";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopes;

    public QuestionBank(IMemoryCache cache, IServiceScopeFactory scopes)
    {
        _cache = cache;
        _scopes = scopes;
    }

    private sealed class Snapshot
    {
        public required IReadOnlyList<Question> All { get; init; }
        public required IReadOnlyList<Question> TestPool { get; init; }
        public required IReadOnlyList<Question> TopicPool { get; init; }
        public required IReadOnlyDictionary<string, IReadOnlyList<Question>> ByTopic { get; init; }
        public required IReadOnlyList<IReadOnlyList<Question>> Bilets { get; init; }
    }

    /// <summary>Ikkala bazadagi barcha savollar (Id bo'yicha) — savolni Id orqali topish uchun (xatolar, qidiruv, natijalar).</summary>
    public async Task<IReadOnlyList<Question>> AllAsync() => (await GetAsync()).All;

    /// <summary>Biletlar va test turlari uchun savollar (ptest, bilet tartibida).</summary>
    public async Task<IReadOnlyList<Question>> TestPoolAsync() => (await GetAsync()).TestPool;

    /// <summary>Biror mavzuga tegishli barcha savollar (takrorsiz).</summary>
    public async Task<IReadOnlyList<Question>> TopicPoolAsync() => (await GetAsync()).TopicPool;

    /// <summary>Mavzu (Topics.All dagi kirill kalit) -> savollar, mavzu ichidagi tartibda.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Question>>> ByTopicAsync() => (await GetAsync()).ByTopic;

    /// <summary>Bitta mavzu savollari (topilmasa — bo'sh ro'yxat).</summary>
    public async Task<IReadOnlyList<Question>> TopicQuestionsAsync(string topic) =>
        (await GetAsync()).ByTopic.TryGetValue((topic ?? "").Trim(), out var list) ? list : Array.Empty<Question>();

    /// <summary>Biletlar: [0] — 1-bilet, ichida savollar bilet tartibida.</summary>
    public async Task<IReadOnlyList<IReadOnlyList<Question>>> BiletsAsync() => (await GetAsync()).Bilets;

    /// <summary>Test savollari ichidan tasodifiy tartibda n ta savol (filter bilan yoki filtersiz).</summary>
    public async Task<List<Question>> RandomAsync(int take, Func<Question, bool>? filter = null)
    {
        var pool = await TestPoolAsync();
        IEnumerable<Question> source = filter == null ? pool : pool.Where(filter);
        var arr = source.ToArray();
        Random.Shared.Shuffle(arr);
        return arr.Take(take).ToList();
    }

    /// <summary>Ro'yxatning aralashtirilgan nusxasi (keshdagi asl ro'yxat o'zgarmaydi).</summary>
    public static Question[] Shuffled(IEnumerable<Question> source)
    {
        var arr = source.ToArray();
        Random.Shared.Shuffle(arr);
        return arr;
    }

    public void Invalidate() => _cache.Remove(Key);

    private async Task<Snapshot> GetAsync()
    {
        if (_cache.TryGetValue(Key, out Snapshot? cached) && cached != null) return cached;

        await Gate.WaitAsync();
        try
        {
            if (_cache.TryGetValue(Key, out cached) && cached != null) return cached;

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var list = await db.Questions.AsNoTracking()
                .Include(q => q.Options)
                .AsSplitQuery()
                .OrderBy(q => q.Id)
                .ToListAsync();
            var links = await db.QuestionTopics.AsNoTracking()
                .OrderBy(x => x.Topic).ThenBy(x => x.SortOrder).ThenBy(x => x.Id)
                .ToListAsync();

            var snapshot = Build(list, links);
            _cache.Set(Key, snapshot, TimeSpan.FromMinutes(30));
            return snapshot;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static Snapshot Build(List<Question> all, List<QuestionTopic> links)
    {
        var ptest = all.Where(q => q.Source == QuestionSources.Ptest).ToList();
        var old = all.Where(q => q.Source != QuestionSources.Ptest).ToList();

        IReadOnlyList<Question> testPool;
        IReadOnlyList<IReadOnlyList<Question>> bilets;

        if (ptest.Count > 0)
        {
            testPool = ptest
                .OrderBy(q => q.BiletNumber ?? int.MaxValue).ThenBy(q => q.BiletOrder ?? int.MaxValue).ThenBy(q => q.Id)
                .ToList();
            bilets = testPool
                .Where(q => q.BiletNumber != null)
                .GroupBy(q => q.BiletNumber!.Value)
                .OrderBy(g => g.Key)
                .Select(g => (IReadOnlyList<Question>)g.ToList())
                .ToList();
        }
        else
        {
            // ptest savollari yo'q — avvalgi xatti-harakat: eski savollar Id bo'yicha 20 tadan
            testPool = old;
            bilets = old.Chunk(QuestionsPerBilet).Select(c => (IReadOnlyList<Question>)c).ToList();
        }

        var byTopic = BuildTopics(all, links);
        var topicPool = byTopic.Values.SelectMany(x => x).DistinctBy(q => q.Id).ToList();

        return new Snapshot { All = all, TestPool = testPool, TopicPool = topicPool, ByTopic = byTopic, Bilets = bilets };
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<Question>> BuildTopics(List<Question> all, List<QuestionTopic> links)
    {
        var byId = all.ToDictionary(q => q.Id);
        var result = new Dictionary<string, List<Question>>(StringComparer.OrdinalIgnoreCase);
        var linked = new HashSet<int>();

        // 1) QuestionTopics jadvalidagi bog'lanishlar (ptest mavzulari, ptest tartibida)
        foreach (var l in links)
        {
            if (!byId.TryGetValue(l.QuestionId, out var q)) continue;
            linked.Add(q.Id);
            var key = l.Topic.Trim();
            if (!result.TryGetValue(key, out var list)) result[key] = list = new List<Question>();
            list.Add(q);
        }

        // 2) Bog'lanishi yo'q, lekin Topic ustuni to'ldirilgan savollar (admin qo'shgan yoki eski baza).
        //    Biletdagi ptest savollari bundan mustasno: bir savol ikki biletda bo'lsa, ikkala nusxada ham Topic
        //    yozilgan, lekin mavzuga faqat bog'langan nusxasi kiradi (aks holda mavzuda takror chiqadi).
        foreach (var q in all)
        {
            if (linked.Contains(q.Id) || string.IsNullOrWhiteSpace(q.Topic) || q.BiletNumber != null) continue;
            var key = q.Topic.Trim();
            if (!result.TryGetValue(key, out var list)) result[key] = list = new List<Question>();
            list.Add(q);
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Question>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }
}
