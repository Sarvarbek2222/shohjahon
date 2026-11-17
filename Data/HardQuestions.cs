using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// "Qiyinlar arenasi": kun (oxirgi 24 soat), hafta (7 kun) va oy (30 kun) davomida o'quvchilar eng ko'p adashgan
/// savollar. Savol ro'yxatga kirishi uchun shu davrda kamida N marta ishlangan bo'lishi kerak (tasodif emas).
/// Har bir savol uchun "tuzoq javob" — eng ko'p tanlangan noto'g'ri variant. Natija 10 daqiqa keshlanadi.
/// </summary>
public static class HardQuestions
{
    public const int Top = 20;
    public static readonly string[] Periods = { "day", "week", "month" };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static (TimeSpan Span, int MinAnswers) Spec(string period) => period switch
    {
        "day" => (TimeSpan.FromDays(1), 3),
        "month" => (TimeSpan.FromDays(30), 10),
        _ => (TimeSpan.FromDays(7), 5),
    };

    public static async Task<HardSet> GetAsync(AppDbContext db, QuestionBank bank, IMemoryCache cache, string period)
    {
        period = Periods.Contains(period) ? period : "week";
        var key = "hard:" + period;
        if (cache.TryGetValue(key, out HardSet? hit) && hit != null) return hit;
        await Gate.WaitAsync();
        try
        {
            if (cache.TryGetValue(key, out hit) && hit != null) return hit;
            var set = await BuildAsync(db, bank, period);
            cache.Set(key, set, TimeSpan.FromMinutes(10));
            return set;
        }
        finally { Gate.Release(); }
    }

    private static async Task<HardSet> BuildAsync(AppDbContext db, QuestionBank bank, string period)
    {
        var (span, min) = Spec(period);
        var since = DateTime.UtcNow - span;
        var byId = (await bank.AllAsync()).ToDictionary(q => q.Id);

        var agg = await db.TestAnswers.AsNoTracking()
            .Where(a => a.SelectedOptionId != null && a.Attempt!.FinishedAt >= since)
            .GroupBy(a => a.QuestionId)
            .Select(g => new { Id = g.Key, N = g.Count(), W = g.Count(a => !a.IsCorrect), U = g.Select(a => a.Attempt!.UserId).Distinct().Count() })
            .ToListAsync();

        var top = agg.Where(x => x.N >= min && x.W > 0 && byId.ContainsKey(x.Id))
            .OrderByDescending(x => (double)x.W / x.N).ThenByDescending(x => x.W).ThenBy(x => x.Id)
            .Take(Top).ToList();
        var ids = top.Select(x => x.Id).ToList();

        // Tuzoq javoblar: noto'g'ri javoblar variantlar bo'yicha
        var picks = ids.Count == 0 ? new() : await db.TestAnswers.AsNoTracking()
            .Where(a => ids.Contains(a.QuestionId) && a.SelectedOptionId != null && a.Attempt!.FinishedAt >= since)
            .GroupBy(a => new { a.QuestionId, a.SelectedOptionId })
            .Select(g => new { g.Key.QuestionId, g.Key.SelectedOptionId, C = g.Count() })
            .ToListAsync();

        var items = top.Select((x, i) =>
        {
            var q = byId[x.Id];
            var dist = q.Options.Select(o => new HardOption(o, picks.Where(p => p.QuestionId == x.Id && p.SelectedOptionId == o.Id).Sum(p => p.C)))
                .ToList();
            var trap = dist.Where(d => !d.Option.IsCorrect).OrderByDescending(d => d.Count).FirstOrDefault();
            return new HardItem(i + 1, q, x.N, x.W, x.U, dist, trap != null && trap.Count > 0 ? trap : null);
        }).ToList();

        return new HardSet(period, since, items, agg.Sum(x => x.N), DateTime.UtcNow);
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
