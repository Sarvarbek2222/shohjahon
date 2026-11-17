using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// "Qiyinlar arenasi": kun (oxirgi 24 soat), hafta (7 kun) va oy (30 kun) davomida o'quvchilar eng ko'p adashgan
/// savollar. Savol ro'yxatga kirishi uchun shu davrda kamida N marta ishlangan bo'lishi kerak (tasodif emas).
/// Har bir savol uchun "tuzoq javob" — eng ko'p tanlangan noto'g'ri variant.
/// Hisob og'ir (javoblar jadvali katta) — shuning uchun u so'rov ichida emas, FONDA bajariladi (HardQuestionsWarmup,
/// har 10 daqiqada); sahifa doim tayyor natijani darhol oladi.
/// </summary>
public static class HardQuestions
{
    public const int Top = 20;
    public static readonly string[] Periods = { "day", "week", "month" };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static int _running;

    public static (TimeSpan Span, int MinAnswers) Spec(string period) => period switch
    {
        "day" => (TimeSpan.FromDays(1), 3),
        "month" => (TimeSpan.FromDays(30), 10),
        _ => (TimeSpan.FromDays(7), 5),
    };

    private static string Key(string period) => "hard:" + period;

    /// <summary>Tayyor natija (kutmasdan). Hali tayyor bo'lmasa — null va fonda hisob boshlanadi.</summary>
    public static HardSet? Peek(IMemoryCache cache, IServiceScopeFactory scopes, ILogger log, string period)
    {
        if (cache.TryGetValue(Key(period), out HardSet? hit) && hit != null) return hit;
        StartRefresh(cache, scopes, log);
        return null;
    }

    public static void StartRefresh(IMemoryCache cache, IServiceScopeFactory scopes, ILogger log)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await RefreshAllAsync(cache, scopes, log); }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
    }

    public static async Task RefreshAllAsync(IMemoryCache cache, IServiceScopeFactory scopes, ILogger log)
    {
        await Gate.WaitAsync();
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var bank = scope.ServiceProvider.GetRequiredService<QuestionBank>();
            db.Database.SetCommandTimeout(300);
            foreach (var p in Periods)
            {
                try { cache.Set(Key(p), await BuildAsync(db, bank, p), TimeSpan.FromMinutes(30)); }
                catch (Exception ex) { log.LogWarning(ex, "Eng qiyin savollar ({Period}) hisoblanmadi", p); }
            }
        }
        finally { Gate.Release(); }
    }

    private static async Task<HardSet> BuildAsync(AppDbContext db, QuestionBank bank, string period)
    {
        var (span, min) = Spec(period);
        var since = DateTime.UtcNow - span;
        var byId = (await bank.AllAsync()).ToDictionary(q => q.Id);

        // Avval davrdagi urinishlar (FinishedAt indeksi bo'yicha), so'ng ularning javoblari — bo'laklab (AttemptId indeksi)
        var attempts = await db.TestAttempts.AsNoTracking().Where(a => a.FinishedAt >= since)
            .Select(a => new { a.Id, a.UserId }).ToListAsync();
        var userOf = attempts.ToDictionary(a => a.Id, a => a.UserId);
        var rows = new List<(int QuestionId, int? Option, bool Ok, int AttemptId)>();
        foreach (var chunk in attempts.Select(a => a.Id).Chunk(1000))
        {
            var ids = chunk.ToList();
            var part = await db.TestAnswers.AsNoTracking()
                .Where(x => ids.Contains(x.AttemptId) && x.SelectedOptionId != null)
                .Select(x => new { x.QuestionId, x.SelectedOptionId, x.IsCorrect, x.AttemptId })
                .ToListAsync();
            rows.AddRange(part.Select(x => (x.QuestionId, x.SelectedOptionId, x.IsCorrect, x.AttemptId)));
        }

        var items = rows.GroupBy(r => r.QuestionId)
            .Where(g => byId.ContainsKey(g.Key))
            .Select(g => new { Id = g.Key, N = g.Count(), W = g.Count(r => !r.Ok), Rows = g })
            .Where(x => x.N >= min && x.W > 0)
            .OrderByDescending(x => (double)x.W / x.N).ThenByDescending(x => x.W).ThenBy(x => x.Id)
            .Take(Top)
            .Select((x, i) =>
            {
                var q = byId[x.Id];
                var dist = q.Options.Select(o => new HardOption(o, x.Rows.Count(r => r.Option == o.Id))).ToList();
                var trap = dist.Where(d => !d.Option.IsCorrect).OrderByDescending(d => d.Count).FirstOrDefault();
                var people = x.Rows.Select(r => userOf.GetValueOrDefault(r.AttemptId)).Distinct().Count();
                return new HardItem(i + 1, q, x.N, x.W, people, dist, trap != null && trap.Count > 0 ? trap : null);
            })
            .ToList();

        return new HardSet(period, since, items, rows.Count, DateTime.UtcNow);
    }
}

/// <summary>Ilova ishga tushgach va har 10 daqiqada "Qiyinlar arenasi"ni fonda tayyorlab qo'yadi.</summary>
public class HardQuestionsWarmup : BackgroundService
{
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<HardQuestionsWarmup> _log;

    public HardQuestionsWarmup(IMemoryCache cache, IServiceScopeFactory scopes, ILogger<HardQuestionsWarmup> log)
    {
        _cache = cache;
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(40), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try { await HardQuestions.RefreshAllAsync(_cache, _scopes, _log); }
            catch (Exception ex) { _log.LogWarning(ex, "Eng qiyin savollarni tayyorlab bo'lmadi"); }
            try { await Task.Delay(TimeSpan.FromMinutes(10), stop); } catch (OperationCanceledException) { return; }
        }
    }
}

public record HardOption(Option Option, int Count);

public record HardItem(int Rank, Question Q, int Answers, int Wrong, int People, List<HardOption> Options, HardOption? Trap)
{
    public int WrongPct => Answers > 0 ? (int)Math.Round(100.0 * Wrong / Answers) : 0;
    public int TrapPct => Trap != null && Answers > 0 ? (int)Math.Round(100.0 * Trap.Count / Answers) : 0;
    public int Pct(HardOption o) => Answers > 0 ? (int)Math.Round(100.0 * o.Count / Answers) : 0;
}

public record HardSet(string Period, DateTime Since, List<HardItem> Items, int TotalAnswers, DateTime BuiltAt);
