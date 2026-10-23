using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// Foydalanuvchi natijalarini tahlil qiladi: tayyorlik darajasi, imtihonga tayyor bo'lish sanasi taxmini,
/// kuchsiz mavzular, xatolar va tavsiyalar. Kabinet (foydalanuvchi) va admin paneli bir xil ma'lumotni ko'radi.
///
/// Tayyorlik modeli (0–100):
///   50% — oxirgi 200 ta javob aniqligi (90% = to'liq ball),
///   30% — oxirgi 5 ta imtihon ko'rinishidagi testdan (real, 20 savol, bilet) o'tish ulushi,
///   20% — savollar bazasini qamrab olish (bazaning 70% i to'g'ri ishlangan = to'liq ball).
/// "Tayyor": aniqlik ≥ 90%, oxirgi 3 ta imtihon testi o'tilgan, qamrov ≥ 70%, kamida 100 ta javob.
/// </summary>
public class StatsService
{
    public const double TargetAccuracy = 0.90;
    /// <summary>Tayyor bo'lish uchun bazadagi barcha savollar to'g'ri ishlanishi kerak.</summary>
    public const double TargetCoverage = 1.0;
    public const int MinAnswersForForecast = 50;
    public const int MinAnswersForReady = 100;

    private readonly AppDbContext _db;
    private readonly QuestionBank _bank;
    private readonly IMemoryCache _cache;

    private const string AnswerAggKey = "stats:answerAgg";
    /// <summary>Admin tahlili uchun javoblar yig'indisi shuncha vaqt keshda turadi (har ochilishda qayta sanalmaydi).</summary>
    private static readonly TimeSpan AnswerAggTtl = TimeSpan.FromSeconds(60);

    public StatsService(AppDbContext db, QuestionBank bank, IMemoryCache cache)
    {
        _db = db;
        _bank = bank;
        _cache = cache;
    }

    /// <summary>Javoblar yig'indisi: (o'quvchi, savol, to'g'ri/xato) bo'yicha soni — bitta so'rov, indeks bo'yicha.</summary>
    private record AnswerAgg(int UserId, int QuestionId, bool IsCorrect, int Count);

    /// <summary>
    /// Admin tahlili (o'quvchilar ro'yxati va umumiy statistika) uchun javoblar jadvali bir marta guruhlab o'qiladi
    /// va qisqa muddat keshlanadi.
    /// </summary>
    private async Task<List<AnswerAgg>> AnswerAggregatesAsync()
    {
        if (_cache.TryGetValue(AnswerAggKey, out List<AnswerAgg>? cached) && cached != null) return cached;
        var rows = await _db.TestAnswers.AsNoTracking()
            .Where(x => x.SelectedOptionId != null)
            .GroupBy(x => new { x.Attempt!.UserId, x.QuestionId, x.IsCorrect })
            .Select(g => new { g.Key.UserId, g.Key.QuestionId, g.Key.IsCorrect, Count = g.Count() })
            .ToListAsync();
        var list = rows.Select(r => new AnswerAgg(r.UserId, r.QuestionId, r.IsCorrect, r.Count)).ToList();
        _cache.Set(AnswerAggKey, list, AnswerAggTtl);
        return list;
    }

    /// <summary>
    /// Tayyorlik hisoblanadigan savollar: testlarda ishlatiladigan baza (ptest; u bo'lmasa — barcha savollar).
    /// Eski bazadan qolgan savollar tayyorlik foiziga ta'sir qilmaydi.
    /// </summary>
    private async Task<Dictionary<int, string?>> BankTopicsAsync() =>
        (await _bank.TestPoolAsync()).ToDictionary(q => q.Id, q => q.Topic?.Trim());

    public async Task<UserStats?> GetUserStatsAsync(int userId)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return null;

        var attempts = await _db.TestAttempts.AsNoTracking()
            .Include(a => a.Answers)
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.FinishedAt)
            .ToListAsync();

        var mistakes = await _db.UserMistakes.AsNoTracking().Where(m => m.UserId == userId).ToListAsync();
        var questionTopics = await BankTopicsAsync();

        return Compute(user, attempts, mistakes, questionTopics);
    }

    /// <summary>
    /// Admin tahlil ro'yxati uchun yengil hisob: har bir o'quvchi uchun faqat jadvaldagi ko'rsatkichlar
    /// (urinishlar, o'rtacha natija, javoblar soni, faol xatolar, tayyorlik). Javoblarning o'zi yuklanmaydi —
    /// bazada guruhlab sanaladi, shuning uchun ma'lumot ko'p bo'lsa ham sahifa tez ochiladi.
    /// Tayyorlik GetUserStatsAsync bilan bir xil qoida bo'yicha: bazadagi to'g'ri ishlangan, "Xatolarim"da bo'lmagan savollar.
    /// </summary>
    public async Task<List<UserStats>> GetStudentSummariesAsync()
    {
        var users = await _db.Users.AsNoTracking().Where(u => u.Role == Roles.User).OrderBy(u => u.Username).ToListAsync();
        var bankIds = (await BankTopicsAsync()).Keys.ToHashSet();
        int bankSize = bankIds.Count;

        var attempts = await _db.TestAttempts.AsNoTracking()
            .Select(a => new { a.UserId, a.IsErrorReview, a.Total, a.Correct, a.FinishedAt })
            .ToListAsync();
        var attemptsByUser = attempts.GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.ToList());

        var agg = await AnswerAggregatesAsync();
        var answeredByUser = agg.GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.Sum(a => a.Count));
        var correctByUser = agg.Where(a => a.IsCorrect).GroupBy(a => a.UserId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.QuestionId).ToHashSet());

        var activeMistakes = await _db.UserMistakes.AsNoTracking()
            .Where(m => m.ResolvedAt == null)
            .Select(m => new { m.UserId, m.QuestionId })
            .ToListAsync();
        var mistakesByUser = activeMistakes.GroupBy(m => m.UserId)
            .ToDictionary(g => g.Key, g => g.Select(m => m.QuestionId).ToHashSet());

        var list = new List<UserStats>(users.Count);
        foreach (var u in users)
        {
            var s = new UserStats { User = u, BankSize = bankSize };
            var ua = attemptsByUser.GetValueOrDefault(u.Id);
            if (ua != null)
            {
                var main = ua.Where(a => !a.IsErrorReview).ToList();
                s.AllAttemptsCount = ua.Count;
                s.TotalAttempts = main.Count;
                s.AveragePercent = main.Count > 0 ? main.Average(a => a.Total > 0 ? Math.Round(100.0 * a.Correct / a.Total, 1) : 0) : 0;
                s.LastActivity = ua.Max(a => a.FinishedAt);
            }
            s.TotalAnswered = answeredByUser.GetValueOrDefault(u.Id);

            var mistakes = mistakesByUser.GetValueOrDefault(u.Id);
            s.ActiveMistakes = mistakes?.Count ?? 0;
            var correct = correctByUser.GetValueOrDefault(u.Id);
            s.DistinctCorrect = correct?.Count ?? 0;
            s.Solved = correct?.Count(q => bankIds.Contains(q) && (mistakes == null || !mistakes.Contains(q))) ?? 0;

            s.CoverageScore = bankSize > 0 ? (int)Math.Floor(100.0 * s.Solved / bankSize) : 0;
            s.IsReady = bankSize > 0 && s.Solved >= bankSize;
            s.ReadinessScore = s.IsReady ? 100 : s.CoverageScore;
            list.Add(s);
        }
        return list;
    }

    /// <summary>Admin uchun: eng ko'p xato qilinadigan savollar va mavzular.</summary>
    public async Task<PlatformStats> GetPlatformStatsAsync()
    {
        var since7 = DateTime.UtcNow.AddDays(-7);
        var ps = new PlatformStats
        {
            TotalUsers = await _db.Users.CountAsync(u => u.Role == Roles.User),
            TotalQuestions = await _db.Questions.CountAsync(),
            TotalAttempts = await _db.TestAttempts.CountAsync(a => !a.IsErrorReview),
            Attempts7d = await _db.TestAttempts.CountAsync(a => !a.IsErrorReview && a.FinishedAt >= since7),
            ActiveUsers7d = await _db.TestAttempts.Where(a => a.FinishedAt >= since7).Select(a => a.UserId).Distinct().CountAsync(),
        };

        var answerStats = (await AnswerAggregatesAsync())
            .GroupBy(a => a.QuestionId)
            .Select(g => new { QuestionId = g.Key, Total = g.Sum(x => x.Count), Wrong = g.Where(x => !x.IsCorrect).Sum(x => x.Count) })
            .ToList();

        ps.TotalAnswers = answerStats.Sum(a => a.Total);
        ps.OverallAccuracy = ps.TotalAnswers > 0 ? 1.0 - (double)answerStats.Sum(a => a.Wrong) / ps.TotalAnswers : 0;

        var examLike = await _db.TestAttempts.AsNoTracking()
            .Where(a => !a.IsErrorReview && TestTypes.ExamLike.Contains(a.TestType))
            .Select(a => a.Passed).ToListAsync();
        ps.ExamPassRate = examLike.Count > 0 ? (double)examLike.Count(p => p) / examLike.Count : 0;

        var hardIds = answerStats.Where(a => a.Total >= 3)
            .OrderByDescending(a => (double)a.Wrong / a.Total).ThenByDescending(a => a.Total).ThenBy(a => a.QuestionId)
            .Take(10).ToList();
        var ids = hardIds.Select(h => h.QuestionId).ToList();
        var qs = await _db.Questions.AsNoTracking().Where(q => ids.Contains(q.Id)).ToDictionaryAsync(q => q.Id);
        ps.HardestQuestions = hardIds.Where(h => qs.ContainsKey(h.QuestionId))
            .Select(h => new HardQuestion(qs[h.QuestionId], h.Total, h.Wrong)).ToList();

        var topicOf = await _db.Questions.AsNoTracking().Select(q => new { q.Id, q.Topic }).ToDictionaryAsync(q => q.Id, q => q.Topic?.Trim());
        ps.Topics = answerStats
            .Where(a => topicOf.TryGetValue(a.QuestionId, out var t) && !string.IsNullOrEmpty(t))
            .GroupBy(a => topicOf[a.QuestionId]!)
            .Select(g => new TopicStat(g.Key, g.Sum(x => x.Total), g.Sum(x => x.Total - x.Wrong)))
            .OrderBy(t => t.Accuracy).ToList();

        return ps;
    }

    private static UserStats Compute(AppUser user, List<TestAttempt> attempts, List<UserMistake> mistakes,
        Dictionary<int, string?> questionTopics)
    {
        var s = new UserStats { User = user, BankSize = questionTopics.Count };
        var today = DateTime.UtcNow.Date;

        var main = attempts.Where(a => !a.IsErrorReview).ToList();   // statistika uchun
        s.Attempts = main;
        s.AllAttemptsCount = attempts.Count;
        s.TotalAttempts = main.Count;
        s.LastActivity = attempts.FirstOrDefault()?.FinishedAt;

        // Javoblar (eng yangisi birinchi)
        var answered = attempts
            .SelectMany(a => a.Answers.Where(x => x.SelectedOptionId != null).Select(x => (a.FinishedAt, x.QuestionId, x.IsCorrect)))
            .OrderByDescending(x => x.FinishedAt).ToList();

        s.TotalAnswered = answered.Count;
        s.TotalCorrect = answered.Count(x => x.IsCorrect);
        s.Accuracy = s.TotalAnswered > 0 ? (double)s.TotalCorrect / s.TotalAnswered : 0;
        s.AveragePercent = main.Count > 0 ? main.Average(a => a.Percent) : 0;
        s.StudyMinutes = (int)attempts.Sum(a => Math.Clamp((a.FinishedAt - a.StartedAt).TotalMinutes, 0, 180));

        var recent = answered.Take(200).ToList();
        s.RecentAccuracy = recent.Count > 0 ? (double)recent.Count(x => x.IsCorrect) / recent.Count : 0;
        var prev = answered.Skip(100).Take(100).ToList();
        var last100 = answered.Take(100).ToList();
        if (last100.Count >= 30 && prev.Count >= 30)
            s.AccuracyTrend = (double)last100.Count(x => x.IsCorrect) / last100.Count - (double)prev.Count(x => x.IsCorrect) / prev.Count;

        // Test turlari bo'yicha
        s.ByType = main.GroupBy(a => a.TestType)
            .Select(g => new TypeStat(g.Key, g.Count(), g.Average(a => a.Percent), g.Max(a => a.Percent), g.Count(a => a.Passed), g.Max(a => a.FinishedAt)))
            .OrderBy(t => Array.IndexOf(TestTypes.All, t.Type)).ToList();

        // Imtihon ko'rinishidagi testlar
        var exams = main.Where(a => TestTypes.ExamLike.Contains(a.TestType)).ToList();
        var last5 = exams.Take(5).ToList();
        s.ExamAttempts = exams.Count;
        s.ExamPassRate = last5.Count > 0 ? (double)last5.Count(a => a.Passed) / last5.Count : 0;
        s.LastExamsPassed = exams.Count >= 3 && exams.Take(3).All(a => a.Passed);

        // Qamrov: to'g'ri ishlangan turli savollar
        var correctSet = answered.Where(x => x.IsCorrect).Select(x => x.QuestionId).ToHashSet();
        s.DistinctCorrect = correctSet.Count;
        s.Coverage = s.BankSize > 0 ? (double)s.DistinctCorrect / s.BankSize : 0;

        // Xatolar
        s.ActiveMistakes = mistakes.Count(m => m.ResolvedAt == null);
        // To'g'ri ishlangan va hozir "Xatolarim"da bo'lmagan bazadagi savollar — tayyorlik shular bo'yicha
        var activeMistakeIds = mistakes.Where(m => m.ResolvedAt == null).Select(m => m.QuestionId).ToHashSet();
        s.Solved = correctSet.Count(q => questionTopics.ContainsKey(q) && !activeMistakeIds.Contains(q));
        s.ResolvedMistakes = mistakes.Count(m => m.ResolvedAt != null);
        s.Mistakes = mistakes.Where(m => m.ResolvedAt == null).OrderByDescending(m => m.LastWrongAt).ToList();

        // Mavzular (Topics.All tartibida, keyin boshqalar)
        var byTopic = answered.Where(x => questionTopics.TryGetValue(x.QuestionId, out var t) && !string.IsNullOrEmpty(t))
            .GroupBy(x => questionTopics[x.QuestionId]!)
            .ToDictionary(g => g.Key, g => new TopicStat(g.Key, g.Count(), g.Count(x => x.IsCorrect)));
        var bankTopics = questionTopics.Values.Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToHashSet();
        s.Topics = Topics.All.Where(bankTopics.Contains).Concat(bankTopics.Except(Topics.All).OrderBy(t => t))
            .Select(t => byTopic.GetValueOrDefault(t) ?? new TopicStat(t, 0, 0)).ToList();
        s.WeakTopics = s.Topics.Where(t => t.Answered >= 3 && t.Accuracy < 0.8)
            .OrderBy(t => t.Accuracy).ThenByDescending(t => t.Answered).Take(5).ToList();
        s.UntouchedTopics = s.Topics.Where(t => t.Answered == 0).ToList();

        // Kunlik taraqqiyot (oxirgi 30 kun)
        var byDay = answered.GroupBy(x => x.FinishedAt.ToLocalTime().Date)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Correct: g.Count(x => x.IsCorrect)));
        var localToday = DateTime.Now.Date;
        s.Daily = Enumerable.Range(0, 30).Select(i => localToday.AddDays(i - 29))
            .Select(d => byDay.TryGetValue(d, out var v) ? new DayStat(d, v.Count, v.Correct) : new DayStat(d, 0, 0))
            .ToList();

        // Faol kunlar ketma-ketligi
        // (bugun hali mashq qilinmagan bo'lsa — kechagi kundan boshlab sanaladi)
        int streak = 0;
        var day = byDay.ContainsKey(localToday) ? localToday : localToday.AddDays(-1);
        while (byDay.ContainsKey(day) && streak < 366) { streak++; day = day.AddDays(-1); }
        s.StreakDays = streak;

        ComputeReadiness(s, answered.Select(x => (x.FinishedAt, x.QuestionId, x.IsCorrect)).ToList());
        BuildInsights(s);
        return s;
    }

    /// <summary>
    /// Imtihonga tayyorlik = bazadagi savollarning necha foizi to'g'ri ishlangan ("Xatolarim"dagi savollar
    /// hisobga kirmaydi). 100% — bazadagi barcha savollar to'g'ri ishlangan: imtihonga tayyor.
    /// Aniqlik va imtihon testlari — qo'shimcha ko'rsatkichlar (foizga ta'sir qilmaydi).
    /// Prognoz: qolgan savollar / o'quvchining yangi savollarni to'g'ri ishlash sur'ati.
    /// </summary>
    private static void ComputeReadiness(UserStats s, List<(DateTime At, int QuestionId, bool IsCorrect)> answered)
    {
        s.AccuracyScore = (int)Math.Round(100 * Math.Min(s.RecentAccuracy / TargetAccuracy, 1));
        s.ExamScore = (int)Math.Round(100 * s.ExamPassRate);
        // Pastga yaxlitlanadi: 100% faqat hamma savol ishlanganda
        s.CoverageScore = s.BankSize > 0 ? (int)Math.Floor(100.0 * s.Solved / s.BankSize) : 0;
        s.ReadinessScore = s.CoverageScore;

        s.NeedMoreData = s.TotalAnswered < MinAnswersForForecast;
        s.IsReady = s.BankSize > 0 && s.Solved >= s.BankSize;
        s.CoverageNeeded = Math.Max(0, s.BankSize - s.Solved);
        if (s.IsReady) { s.ReadinessScore = 100; s.DaysToReady = 0; return; }
        if (s.NeedMoreData) return;

        // Sur'at = yangi to'g'ri ishlangan savollar / o'tgan kalendar kunlar (oxirgi 14 kun; o'quvchi undan
        // kamroq vaqt oldin boshlagan bo'lsa — boshlagan kunidan beri). Kamida 1 kun.
        var windowStart = DateTime.UtcNow.AddDays(-14);
        int newCorrect = answered.Where(a => a.IsCorrect).GroupBy(a => a.QuestionId)
            .Select(g => g.Min(a => a.At)).Count(d => d >= windowStart);
        var firstAnswer = answered.Count > 0 ? answered.Min(a => a.At) : DateTime.UtcNow;
        var from = firstAnswer > windowStart ? firstAnswer : windowStart;
        double elapsedDays = Math.Clamp((DateTime.Now.Date - from.ToLocalTime().Date).TotalDays + 1, 1, 14);
        s.CoveragePerDay = newCorrect / elapsedDays;

        if (s.CoveragePerDay > 0)
        {
            s.DaysToReady = Math.Clamp((int)Math.Ceiling(s.CoverageNeeded / s.CoveragePerDay), 1, 730);
            s.PredictedReadyDate = DateTime.Now.Date.AddDays(s.DaysToReady.Value);
            s.ForecastLimit = ForecastLimits.Coverage;
        }
    }

    private static void BuildInsights(UserStats s)
    {
        var list = s.Insights;
        if (s.TotalAnswered == 0) { list.Add(new Insight("insight.start", InsightLevel.Info)); return; }
        if (s.NeedMoreData)
            list.Add(new Insight("insight.needData", InsightLevel.Info, ("n", MinAnswersForForecast - s.TotalAnswered)));
        if (s.IsReady)
            list.Add(new Insight("insight.ready", InsightLevel.Good));
        else if (s.PredictedReadyDate != null)
            list.Add(new Insight("insight.forecast", InsightLevel.Info, ("days", s.DaysToReady), ("date", s.PredictedReadyDate.Value.ToString("d MMMM"))));
        else if (!s.NeedMoreData)
            list.Add(new Insight("insight.noForecast", InsightLevel.Warn));

        if (s.AccuracyTrend is double tr)
        {
            int pp = (int)Math.Round(tr * 100);
            if (pp >= 5) list.Add(new Insight("insight.improving", InsightLevel.Good, ("n", pp)));
            else if (pp <= -5) list.Add(new Insight("insight.declining", InsightLevel.Warn, ("n", -pp)));
        }

        if (s.WeakTopics.Count > 0)
            list.Add(new Insight("insight.weakTopics", InsightLevel.Warn, ("n", s.WeakTopics.Count)) { Topics = s.WeakTopics.Take(3).Select(t => t.Topic).ToList() });
        if (s.UntouchedTopics.Count > 0)
            list.Add(new Insight("insight.untouched", InsightLevel.Info, ("n", s.UntouchedTopics.Count)));
        if (s.ActiveMistakes > 0)
            list.Add(new Insight("insight.mistakes", InsightLevel.Warn, ("n", s.ActiveMistakes)));

        if (s.ExamAttempts < 3)
            list.Add(new Insight("insight.examPractice", InsightLevel.Info));
        else if (s.ExamPassRate < 0.6)
            list.Add(new Insight("insight.examLow", InsightLevel.Warn, ("n", (int)Math.Round(s.ExamPassRate * 100))));

        if (s.Coverage < 0.5 && !s.NeedMoreData)
            list.Add(new Insight("insight.coverage", InsightLevel.Info, ("n", (int)Math.Round(s.Coverage * 100))));

        if (s.LastActivity is DateTime last)
        {
            int days = (int)(DateTime.UtcNow - last).TotalDays;
            if (days >= 3) list.Add(new Insight("insight.inactive", InsightLevel.Warn, ("n", days)));
        }
        if (s.StreakDays >= 3)
            list.Add(new Insight("insight.streak", InsightLevel.Good, ("n", s.StreakDays)));
    }
}

public class UserStats
{
    public AppUser User { get; set; } = null!;
    public int BankSize { get; set; }

    public List<TestAttempt> Attempts { get; set; } = new();
    public int TotalAttempts { get; set; }
    public int AllAttemptsCount { get; set; }
    public DateTime? LastActivity { get; set; }

    public int TotalAnswered { get; set; }
    public int TotalCorrect { get; set; }
    public double Accuracy { get; set; }
    public double RecentAccuracy { get; set; }
    public double? AccuracyTrend { get; set; }
    public double AveragePercent { get; set; }
    public int StudyMinutes { get; set; }
    public int StreakDays { get; set; }

    public List<TypeStat> ByType { get; set; } = new();
    public int ExamAttempts { get; set; }
    public double ExamPassRate { get; set; }
    public bool LastExamsPassed { get; set; }

    public int DistinctCorrect { get; set; }
    /// <summary>To'g'ri ishlangan, hozir "Xatolarim"da bo'lmagan bazadagi savollar soni (tayyorlik shu bo'yicha).</summary>
    public int Solved { get; set; }
    public double Coverage { get; set; }

    public int ActiveMistakes { get; set; }
    public int ResolvedMistakes { get; set; }
    public List<UserMistake> Mistakes { get; set; } = new();

    public List<TopicStat> Topics { get; set; } = new();
    public List<TopicStat> WeakTopics { get; set; } = new();
    public List<TopicStat> UntouchedTopics { get; set; } = new();
    public List<DayStat> Daily { get; set; } = new();

    public int ReadinessScore { get; set; }
    public int AccuracyScore { get; set; }
    public int ExamScore { get; set; }
    public int CoverageScore { get; set; }
    public bool IsReady { get; set; }
    public bool NeedMoreData { get; set; }
    public int? DaysToReady { get; set; }
    public DateTime? PredictedReadyDate { get; set; }
    public double? DailyGrowth { get; set; }

    /// <summary>Sanani asosan qaysi shart belgilaydi (ForecastLimits.*) — ekranda tushuntirish uchun.</summary>
    public string? ForecastLimit { get; set; }
    /// <summary>Qamrov maqsadigacha yana nechta savolni to'g'ri ishlash kerak.</summary>
    public int CoverageNeeded { get; set; }
    /// <summary>Kuniga o'rtacha nechta yangi savol to'g'ri ishlanmoqda.</summary>
    public double CoveragePerDay { get; set; }

    public List<Insight> Insights { get; set; } = new();
}

public static class ForecastLimits
{
    public const string Accuracy = "accuracy";
    public const string Coverage = "coverage";
    public const string Exams = "exams";
}

public record TypeStat(string Type, int Attempts, double AvgPercent, double BestPercent, int Passed, DateTime LastAt);

public record TopicStat(string Topic, int Answered, int Correct)
{
    public double Accuracy => Answered > 0 ? (double)Correct / Answered : 0;
}

public record DayStat(DateTime Date, int Answered, int Correct)
{
    public double Accuracy => Answered > 0 ? (double)Correct / Answered : 0;
}

public enum InsightLevel { Info, Good, Warn }

/// <summary>Tavsiya: resx kaliti + parametrlar (tanlangan tilda chiqariladi).</summary>
public class Insight
{
    public Insight(string key, InsightLevel level, params (string name, object? value)[] args)
    {
        Key = key;
        Level = level;
        Args = args;
    }

    public string Key { get; }
    public InsightLevel Level { get; }
    public (string name, object? value)[] Args { get; }
    public List<string> Topics { get; set; } = new();
}

public class PlatformStats
{
    public int TotalUsers { get; set; }
    public int ActiveUsers7d { get; set; }
    public int TotalQuestions { get; set; }
    public int TotalAttempts { get; set; }
    public int Attempts7d { get; set; }
    public int TotalAnswers { get; set; }
    public double OverallAccuracy { get; set; }
    public double ExamPassRate { get; set; }
    public List<HardQuestion> HardestQuestions { get; set; } = new();
    public List<TopicStat> Topics { get; set; } = new();
}

public record HardQuestion(Question Question, int Total, int Wrong)
{
    public double WrongRate => Total > 0 ? (double)Wrong / Total : 0;
}
