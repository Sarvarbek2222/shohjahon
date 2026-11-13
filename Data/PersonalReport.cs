using Microsoft.EntityFrameworkCore;

/// <summary>
/// O'quvchining shaxsiy kunlik tahlili (O'zbekiston vaqti bo'yicha bir kun): nechta savol, to'g'rilik,
/// o'qish vaqti, yangi o'zlashtirilgan va unutilgan savollar, eng qiyin mavzular, oldingi 7 kunga taqqoslash,
/// reytingdagi o'rni va shu o'quvchi uchun aniq tavsiyalar. Hech qanday tashqi xizmat ishlatilmaydi.
/// </summary>
public static class PersonalReport
{
    public static DateTime LocalToday() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, DailyReportService.Zone).Date;

    private static DateTime ToUtc(DateTime localDate) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified), DailyReportService.Zone);

    public static async Task<PersonalDay> BuildAsync(AppDbContext db, QuestionBank bank, int userId, DateTime day, LeagueData? league)
    {
        var from = ToUtc(day);
        var to = ToUtc(day.AddDays(1));
        var weekFrom = ToUtc(day.AddDays(-7));

        var attempts = await db.TestAttempts.AsNoTracking()
            .Where(a => a.UserId == userId && a.FinishedAt >= weekFrom && a.FinishedAt < to)
            .Select(a => new { a.Id, a.TestType, a.Total, a.Correct, a.Passed, a.StartedAt, a.FinishedAt, a.IsErrorReview })
            .ToListAsync();
        var todayIds = attempts.Where(a => a.FinishedAt >= from).Select(a => a.Id).ToList();

        var answers = todayIds.Count == 0 ? new() : await db.TestAnswers.AsNoTracking()
            .Where(x => todayIds.Contains(x.AttemptId) && x.SelectedOptionId != null)
            .Select(x => new { x.QuestionId, x.IsCorrect }).ToListAsync();

        // Shu kungacha bo'lgan javoblar: yangi o'zlashtirilgan / unutilgan savollarni aniqlash uchun
        var qids = answers.Select(a => a.QuestionId).Distinct().ToList();
        var before = qids.Count == 0 ? new() : await db.TestAnswers.AsNoTracking()
            .Where(x => qids.Contains(x.QuestionId) && x.SelectedOptionId != null && x.Attempt!.UserId == userId && x.Attempt.FinishedAt < from)
            .Select(x => new { x.QuestionId, x.IsCorrect }).ToListAsync();
        var everCorrect = before.Where(b => b.IsCorrect).Select(b => b.QuestionId).ToHashSet();

        var r = new PersonalDay { Day = day };
        r.Answered = answers.Count;
        r.Correct = answers.Count(a => a.IsCorrect);
        r.Tests = attempts.Count(a => a.FinishedAt >= from && !a.IsErrorReview);
        r.Seconds = (int)attempts.Where(a => a.FinishedAt >= from)
            .Sum(a => Math.Clamp((a.FinishedAt - a.StartedAt).TotalSeconds, 0, 3 * 3600));
        var exams = attempts.Where(a => a.FinishedAt >= from && TestTypes.ExamLike.Contains(a.TestType)).ToList();
        r.Exams = exams.Count;
        r.ExamsPassed = exams.Count(a => a.Passed);

        var byQ = answers.GroupBy(a => a.QuestionId).ToList();
        r.NewlyMastered = byQ.Count(g => g.Any(a => a.IsCorrect) && !everCorrect.Contains(g.Key));
        r.Forgotten = byQ.Count(g => g.All(a => !a.IsCorrect) && everCorrect.Contains(g.Key));

        // Oldingi 7 kun o'rtachasi (shu kun hisobga kirmaydi)
        var prev = attempts.Where(a => a.FinishedAt < from && !a.IsErrorReview).ToList();
        r.AvgAnswered7 = (int)Math.Round(prev.Sum(a => a.Total) / 7.0);
        r.AvgAccuracy7 = prev.Sum(a => a.Total) > 0 ? (int)Math.Round(100.0 * prev.Sum(a => a.Correct) / prev.Sum(a => a.Total)) : 0;

        // Mavzular: shu kun eng ko'p xato qilingan mavzular
        var topicOf = (await bank.ByTopicAsync()).SelectMany(kv => kv.Value.Select(q => (q.Id, kv.Key)))
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Key);
        r.Topics = answers.Where(a => topicOf.ContainsKey(a.QuestionId))
            .GroupBy(a => topicOf[a.QuestionId])
            .Select(g => new PersonalTopic(g.Key, g.Count(), g.Count(a => a.IsCorrect)))
            .OrderBy(t => t.Accuracy).ThenByDescending(t => t.Answered).ToList();

        // Hozir tuzatilishi kerak bo'lgan xatolar
        r.OpenMistakes = await db.UserMistakes.CountAsync(m => m.UserId == userId && m.ResolvedAt == null);

        r.League = league?.Of(userId);
        r.LeagueTotal = league?.Total ?? 0;
        r.Recommendations = Recommend(r);
        return r;
    }

    private static List<PersonalRec> Recommend(PersonalDay r)
    {
        var list = new List<PersonalRec>();
        if (r.Answered == 0)
        {
            list.Add(new("pr.recStart", null));
            if (r.OpenMistakes > 0) list.Add(new("pr.recMistakes", r.OpenMistakes));
            return list;
        }
        if (r.Answered < League.DailyTarget) list.Add(new("pr.recMore", League.DailyTarget - r.Answered));
        if (r.Accuracy < 70 && r.Topics.FirstOrDefault() is { } weak && weak.Accuracy < 70) list.Add(new("pr.recTopic", weak.Topic));
        if (r.Forgotten > 0) list.Add(new("pr.recForgot", r.Forgotten));
        if (r.OpenMistakes > 0) list.Add(new("pr.recMistakes", r.OpenMistakes));
        if (r.Exams == 0 && r.Accuracy >= 85) list.Add(new("pr.recExam", null));
        if (r.Exams > 0 && r.ExamsPassed < r.Exams) list.Add(new("pr.recExamFail", null));
        if (list.Count == 0) list.Add(new("pr.recGreat", null));
        return list;
    }
}

public class PersonalDay
{
    public DateTime Day { get; set; }
    public int Answered { get; set; }
    public int Correct { get; set; }
    public int Accuracy => Answered > 0 ? (int)Math.Round(100.0 * Correct / Answered) : 0;
    public int Tests { get; set; }
    public int Seconds { get; set; }
    public int Exams { get; set; }
    public int ExamsPassed { get; set; }
    public int NewlyMastered { get; set; }
    public int Forgotten { get; set; }
    public int AvgAnswered7 { get; set; }
    public int AvgAccuracy7 { get; set; }
    public int OpenMistakes { get; set; }
    public List<PersonalTopic> Topics { get; set; } = new();
    public LeagueRow? League { get; set; }
    public int LeagueTotal { get; set; }
    public List<PersonalRec> Recommendations { get; set; } = new();
}

public record PersonalTopic(string Topic, int Answered, int Correct)
{
    public int Accuracy => Answered > 0 ? (int)Math.Round(100.0 * Correct / Answered) : 0;
}

/// <summary>Tavsiya: matn kaliti "pr.rec*" va unga qo'yiladigan qiymat ({v}).</summary>
public record PersonalRec(string Key, object? Value);
