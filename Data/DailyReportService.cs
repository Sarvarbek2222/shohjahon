using System.Text.Json;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Kunlik aqlli tahlil. Har kuni 00:00 dan keyin (O'zbekiston vaqti) o'tgan kun uchun bir marta tuziladi va saqlanadi.
/// Boshqa tahlillardan farqi — o'quvchi emas, BUTUN TIZIM va SAVOLLAR sifatini o'rganadi:
///  • savol ajratish kuchi (kuchli/kuchsiz o'quvchilar natijasi) — javob kaliti xato bo'lishi mumkin bo'lgan savollar;
///  • aldovchi variantlar — ko'pchilik bir xil noto'g'ri javobni tanlaydigan savollar (umumiy xato tushuncha);
///  • unutish — avval to'g'ri ishlangan, oradan kamida 3 kun o'tib xato qilingan savollar;
///  • imtihondan o'tish ehtimoli (binomial model: 20 savoldan ≤2 xato) va uning kunlik o'zgarishi;
///  • tashlab ketish xavfi, shubhali natijalar (juda tez va hammasi to'g'ri / tasodifiy bosish), "oltin soat".
/// Hamma narsa o'z algoritmlarimiz bilan hisoblanadi (tashqi xizmat yo'q). Natija — DailyReport.DataJson.
/// </summary>
public class DailyReportService : BackgroundService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DailyReportService> _log;

    public DailyReportService(IServiceScopeFactory scopes, ILogger<DailyReportService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    /// <summary>O'zbekiston vaqti (Linux: Asia/Tashkent, Windows: West Asia Standard Time; topilmasa — UTC+5).</summary>
    public static readonly TimeZoneInfo Zone = FindZone();
    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "Asia/Tashkent", "West Asia Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.CreateCustomTimeZone("UZ", TimeSpan.FromHours(5), "UZ", "UZ");
    }
    public static DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);
    public static DateTime ToUtc(DateTime localDay) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDay, DateTimeKind.Unspecified), Zone);
    public static DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(40), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var yesterday = LocalNow.Date.AddDays(-1);
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (!await db.DailyReports.AnyAsync(r => r.Day == yesterday, ct))
                {
                    await BuildAndSaveAsync(db, yesterday, ct);
                    _log.LogInformation("Kunlik tahlil tuzildi: {Day:yyyy-MM-dd}", yesterday);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Kunlik tahlilni tuzib bo'lmadi");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(10), ct); } catch (OperationCanceledException) { return; }
        }
    }

    public static async Task<DailyReportData> BuildAndSaveAsync(AppDbContext db, DateTime day, CancellationToken ct = default)
    {
        var data = await BuildAsync(db, day, ct);
        var json = JsonSerializer.Serialize(data, Json);
        var row = await db.DailyReports.FirstOrDefaultAsync(r => r.Day == day, ct);
        if (row == null) db.DailyReports.Add(new DailyReport { Day = day, DataJson = json, CreatedAt = DateTime.UtcNow });
        else { row.DataJson = json; row.CreatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
        return data;
    }

    private sealed record Ans(int UserId, int AttemptId, int QuestionId, int? Option, bool Ok, DateTime At);

    public static async Task<DailyReportData> BuildAsync(AppDbContext db, DateTime day, CancellationToken ct = default)
    {
        var from = ToUtc(day);
        var to = ToUtc(day.AddDays(1));
        var win30 = from.AddDays(-30);
        var win60 = from.AddDays(-60);

        var students = await db.Users.AsNoTracking().Where(u => u.Role == Roles.User)
            .Select(u => new { u.Id, u.Username, u.FullName, u.IsActive, u.CreatedAt }).ToListAsync(ct);
        var studentIds = students.Select(s => s.Id).ToHashSet();
        string Name(int id) { var s = students.FirstOrDefault(x => x.Id == id); return s == null ? "#" + id : (s.FullName ?? s.Username); }

        // 60 kunlik javoblar (kun oxirigacha) — bir marta yuklanadi
        var answers = await db.TestAnswers.AsNoTracking()
            .Where(a => a.SelectedOptionId != null && a.Attempt!.FinishedAt >= win60 && a.Attempt.FinishedAt < to)
            .Select(a => new Ans(a.Attempt!.UserId, a.AttemptId, a.QuestionId, a.SelectedOptionId, a.IsCorrect, a.Attempt.FinishedAt))
            .ToListAsync(ct);
        answers = answers.Where(a => studentIds.Contains(a.UserId)).ToList();
        var dayAns = answers.Where(a => a.At >= from).ToList();
        var last30 = answers.Where(a => a.At >= win30).ToList();

        var attempts = await db.TestAttempts.AsNoTracking()
            .Where(a => a.FinishedAt >= from.AddDays(-14) && a.FinishedAt < to)
            .Select(a => new { a.Id, a.UserId, a.TestType, a.IsErrorReview, a.Total, a.Correct, a.Passed, a.StartedAt, a.FinishedAt })
            .ToListAsync(ct);
        attempts = attempts.Where(a => studentIds.Contains(a.UserId)).ToList();
        var dayAttempts = attempts.Where(a => a.FinishedAt >= from).ToList();

        var qIds = last30.Select(a => a.QuestionId).Distinct().ToList();
        var qText = await db.Questions.AsNoTracking().Where(q => qIds.Contains(q.Id))
            .Select(q => new { q.Id, q.QuestionUZ }).ToDictionaryAsync(q => q.Id, q => q.QuestionUZ, ct);
        var options = await db.Options.AsNoTracking().Where(o => qIds.Contains(o.QuestionId))
            .Select(o => new { o.Id, o.QuestionId, o.OptionUZ, o.IsCorrect }).ToListAsync(ct);
        var optLetter = options.GroupBy(o => o.QuestionId)
            .SelectMany(g => g.OrderBy(o => o.Id).Select((o, i) => (o.Id, Letter: "F" + (i + 1), o.OptionUZ)))
            .ToDictionary(x => x.Id, x => (x.Letter, x.OptionUZ));
        string QT(int id) { var t = qText.GetValueOrDefault(id) ?? ""; return t.Length > 160 ? t[..157] + "…" : t; }

        var d = new DailyReportData { Day = day.ToString("yyyy-MM-dd"), BuiltAt = DateTime.UtcNow };
        // Tez hisob uchun oldindan tayyorlangan lug'atlar
        var countByQ30 = last30.GroupBy(a => a.QuestionId).ToDictionary(g => g.Key, g => g.Count());
        var usersBefore = answers.Where(a => a.At < from).Select(a => a.UserId).ToHashSet();
        var usersAny = answers.Select(a => a.UserId).ToHashSet();
        var answersByUser = answers.GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.At).ToList());

        // ── 1. Kun pulsi ──
        var p = d.Pulse;
        p.Active = dayAns.Select(a => a.UserId).Distinct().Count();
        p.Tests = dayAttempts.Count(a => !a.IsErrorReview);
        p.Answers = dayAns.Count;
        p.Accuracy = dayAns.Count == 0 ? 0 : Math.Round(100.0 * dayAns.Count(a => a.Ok) / dayAns.Count, 1);
        var prev7 = answers.Where(a => a.At >= from.AddDays(-7) && a.At < from).ToList();
        p.AccuracyAvg7 = prev7.Count == 0 ? 0 : Math.Round(100.0 * prev7.Count(a => a.Ok) / prev7.Count, 1);
        p.ActiveAvg7 = Math.Round(prev7.GroupBy(a => ToLocal(a.At).Date).Select(g => g.Select(a => a.UserId).Distinct().Count()).DefaultIfEmpty(0).Average(), 1);
        var exams = dayAttempts.Where(a => !a.IsErrorReview && TestTypes.ExamLike.Contains(a.TestType)).ToList();
        p.ExamAttempts = exams.Count;
        p.PassRate = exams.Count == 0 ? 0 : Math.Round(100.0 * exams.Count(a => a.Passed) / exams.Count, 1);
        p.StudyMinutes = (int)Math.Round(dayAttempts.Sum(a => Math.Clamp((a.FinishedAt - a.StartedAt).TotalMinutes, 0, 180)));
        p.FirstTimers = dayAns.Select(a => a.UserId).Distinct().Count(u => !usersBefore.Contains(u));
        for (int h = 0; h < 24; h++)
        {
            var hs = dayAns.Where(a => ToLocal(a.At).Hour == h).ToList();
            p.Hours[h] = hs.Count;
            p.HourAccuracy[h] = hs.Count == 0 ? 0 : Math.Round(100.0 * hs.Count(a => a.Ok) / hs.Count, 1);
        }
        p.PeakHour = p.Answers == 0 ? -1 : Array.IndexOf(p.Hours, p.Hours.Max());
        // "Oltin soat" — 30 kunda eng yuqori aniqlik bo'lgan soat (kamida 40 javob)
        var byHour30 = last30.GroupBy(a => ToLocal(a.At).Hour).Where(g => g.Count() >= 40)
            .Select(g => (Hour: g.Key, Acc: 100.0 * g.Count(a => a.Ok) / g.Count())).OrderByDescending(x => x.Acc).ToList();
        if (byHour30.Count >= 3) { p.GoldenHour = byHour30[0].Hour; p.GoldenAccuracy = Math.Round(byHour30[0].Acc, 1); p.WorstHour = byHour30[^1].Hour; p.WorstAccuracy = Math.Round(byHour30[^1].Acc, 1); }
        p.VideoMinutes = (int)Math.Round(await db.TopicLessonProgress.AsNoTracking().Where(x => x.UpdatedAt >= from && x.UpdatedAt < to).SumAsync(x => (double?)x.WatchSeconds, ct) / 60.0 ?? 0);
        p.LiveAttendees = await db.LiveAttendance.AsNoTracking().Where(a => a.JoinedAt >= from && a.JoinedAt < to).Select(a => a.UserId).Distinct().CountAsync(ct);

        // ── 2. Savollar sifati (30 kun) ──
        var userAcc = last30.GroupBy(a => a.UserId).Where(g => g.Count() >= 20)
            .Select(g => (User: g.Key, Acc: (double)g.Count(a => a.Ok) / g.Count())).OrderByDescending(x => x.Acc).ToList();
        if (userAcc.Count >= 8)
        {
            var k = Math.Max(2, (int)Math.Round(userAcc.Count * 0.27));
            var top = userAcc.Take(k).Select(x => x.User).ToHashSet();
            var bottom = userAcc.TakeLast(k).Select(x => x.User).ToHashSet();
            d.Items.Discrimination = last30.GroupBy(a => a.QuestionId).Select(g =>
            {
                var t = g.Where(a => top.Contains(a.UserId)).ToList();
                var b = g.Where(a => bottom.Contains(a.UserId)).ToList();
                if (t.Count < 4 || b.Count < 4) return null;
                double pt = (double)t.Count(a => a.Ok) / t.Count, pb = (double)b.Count(a => a.Ok) / b.Count;
                return new ItemStat { Id = g.Key, Text = QT(g.Key), N = g.Count(), A = Math.Round(pt * 100), B = Math.Round(pb * 100), D = Math.Round(pt - pb, 2) };
            })
            // Ishonchli holatlar: kuchlilar aniq yomonroq (D ≤ −0.1) yoki savol hech kimni ajratmaydi va kuchlilar ham ko'p xato qiladi
            .Where(x => x != null && x.N >= 20 && (x.D <= -0.1 || (x.D < 0.05 && x.A < 50))).OrderBy(x => x!.D).Take(6).ToList()!;
        }
        d.Items.Misconceptions = last30.Where(a => !a.Ok && a.Option != null).GroupBy(a => a.QuestionId)
            .Select(g =>
            {
                var all = countByQ30.GetValueOrDefault(g.Key);
                var top = g.GroupBy(a => a.Option!.Value).OrderByDescending(x => x.Count()).First();
                optLetter.TryGetValue(top.Key, out var o);
                return new ItemStat
                {
                    Id = g.Key, Text = QT(g.Key), N = all, Wrong = g.Count(),
                    A = Math.Round(100.0 * top.Count() / g.Count()), B = Math.Round(100.0 * top.Count() / all),
                    Option = o.Letter, OptionText = o.OptionUZ is { Length: > 120 } ot ? ot[..117] + "…" : o.OptionUZ,
                };
            })
            .Where(x => x.Wrong >= 8 && x.A >= 60 && x.B >= 25).OrderByDescending(x => x.Wrong).Take(6).ToList();
        d.Items.HarderToday = dayAns.GroupBy(a => a.QuestionId).Where(g => g.Count() >= 6)
            .Select(g =>
            {
                var hist = last30.Where(a => a.QuestionId == g.Key && a.At < from).ToList();
                if (hist.Count < 10) return null;
                double dayAcc = 100.0 * g.Count(a => a.Ok) / g.Count(), histAcc = 100.0 * hist.Count(a => a.Ok) / hist.Count;
                return new ItemStat { Id = g.Key, Text = QT(g.Key), N = g.Count(), A = Math.Round(dayAcc), B = Math.Round(histAcc), D = Math.Round((dayAcc - histAcc) / 100, 2) };
            }).Where(x => x != null && x.D <= -0.25).OrderBy(x => x!.D).Take(5).ToList()!;

        // ── 3. O'rganish dinamikasi: unutish va yangi o'zlashtirish ──
        var correctBefore = answers.Where(a => a.Ok && a.At < from.AddDays(-3)).Select(a => (a.UserId, a.QuestionId)).ToHashSet();
        var anyCorrectBefore = answers.Where(a => a.Ok && a.At < from).Select(a => (a.UserId, a.QuestionId)).ToHashSet();
        var forgotten = dayAns.Where(a => !a.Ok && correctBefore.Contains((a.UserId, a.QuestionId))).ToList();
        d.Learning.Forgotten = forgotten.Select(a => (a.UserId, a.QuestionId)).Distinct().Count();
        d.Learning.ForgottenTop = forgotten.GroupBy(a => a.QuestionId).OrderByDescending(g => g.Select(a => a.UserId).Distinct().Count()).Take(5)
            .Select(g => new ItemStat { Id = g.Key, Text = QT(g.Key), N = g.Select(a => a.UserId).Distinct().Count() }).ToList();
        var mastered = dayAns.Where(a => a.Ok && !anyCorrectBefore.Contains((a.UserId, a.QuestionId))).Select(a => (a.UserId, a.QuestionId)).Distinct().ToList();
        d.Learning.NewlyMastered = mastered.Count;
        var dayAnswered = dayAns.Select(a => (a.UserId, a.QuestionId)).Distinct().Count();
        d.Learning.RetentionRate = dayAns.Count(a => correctBefore.Contains((a.UserId, a.QuestionId))) is int rev && rev > 0
            ? Math.Round(100.0 * dayAns.Count(a => a.Ok && correctBefore.Contains((a.UserId, a.QuestionId))) / rev, 1) : 0;
        d.Students.Heroes = mastered.GroupBy(m => m.UserId).OrderByDescending(g => g.Count()).Take(3)
            .Select(g => new StudentStat { Id = g.Key, Name = Name(g.Key), Value = g.Count() }).ToList();

        // ── 4. Imtihondan o'tish ehtimoli (oxirgi 200 javob, Beta(1,1) bilan tekislangan; 20 savoldan ≤2 xato) ──
        static double PassProb(IEnumerable<bool> recent)
        {
            var list = recent.ToList();
            double pp = (list.Count(x => x) + 1.0) / (list.Count + 2.0), q = 1 - pp, sum = 0;
            for (int kk = 0; kk <= 2; kk++) sum += Binom(20, kk) * Math.Pow(q, kk) * Math.Pow(pp, 20 - kk);
            return sum;
        }
        static double Binom(int n, int k) { double r = 1; for (int i = 1; i <= k; i++) r = r * (n - k + i) / i; return r; }
        d.Students.PassChance = dayAns.Select(a => a.UserId).Distinct().Select(u =>
        {
            var all = answersByUser.GetValueOrDefault(u) ?? new();
            if (all.Count < 30) return null;
            var now = PassProb(all.Take(200).Select(a => a.Ok));
            var before = all.Where(a => a.At < from).ToList();
            double? prevP = before.Count >= 30 ? PassProb(before.Take(200).Select(a => a.Ok)) : null;
            return new StudentStat { Id = u, Name = Name(u), Value = Math.Round(now * 100, 1), Prev = prevP is double pv ? Math.Round(pv * 100, 1) : null };
        }).Where(x => x != null).OrderByDescending(x => x!.Value).ToList()!;
        d.Students.ReadyCount = d.Students.PassChance.Count(x => x.Value >= 80);

        // ── 5. Tashlab ketish xavfi ──
        var activeDays = attempts.GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.Select(a => ToLocal(a.FinishedAt).Date).Distinct().ToList());
        d.Students.AtRisk = students.Where(s => s.IsActive).Select(s =>
        {
            if (!activeDays.TryGetValue(s.Id, out var days) || days.Count == 0) return null;
            var last = days.Max();
            var idle = (day - last).Days;
            if (idle < 3) return null;
            var prior = days.Count(x => x >= day.AddDays(-14));
            if (prior < 2) return null;
            return new StudentStat { Id = s.Id, Name = Name(s.Id), Value = Math.Min(100, idle * 12 + prior * 6), Prev = idle };
        }).Where(x => x != null).OrderByDescending(x => x!.Value).Take(12).ToList()!;
        d.Students.NeverStarted = students.Count(s => s.IsActive && s.CreatedAt < from.AddDays(-3) && !activeDays.ContainsKey(s.Id) && !usersAny.Contains(s.Id));

        // ── 6. Shubhali natijalar ──
        var dayAnsByAttempt = dayAns.GroupBy(a => a.AttemptId).ToDictionary(g => g.Key, g => g.ToList());
        d.Students.Anomalies = dayAttempts.Where(a => dayAnsByAttempt.ContainsKey(a.Id)).Select(a =>
        {
            var list = dayAnsByAttempt[a.Id];
            if (list.Count < 10) return null;
            var sec = (a.FinishedAt - a.StartedAt).TotalSeconds / list.Count;
            var acc = (double)list.Count(x => x.Ok) / list.Count;
            string? kind = sec < 2.5 && acc >= 0.9 ? "fast" : sec < 3 && acc <= 0.35 ? "random" : null;
            return kind == null ? null : new StudentStat { Id = a.UserId, Name = Name(a.UserId), Value = Math.Round(sec, 1), Prev = Math.Round(acc * 100), Kind = kind, N = list.Count };
        }).Where(x => x != null).Take(8).ToList()!;

        // ── 7. Tavsiyalar ──
        var r = d.Recommendations;
        if (p.Answers == 0) r.Add(new Rec("rep.recQuiet"));
        if (p.AccuracyAvg7 > 0 && p.Answers >= 50 && p.Accuracy < p.AccuracyAvg7 - 5) r.Add(new Rec("rep.recAccDrop", new() { ["n"] = Math.Round(p.AccuracyAvg7 - p.Accuracy, 1) }));
        if (p.AccuracyAvg7 > 0 && p.Answers >= 50 && p.Accuracy > p.AccuracyAvg7 + 5) r.Add(new Rec("rep.recAccUp", new() { ["n"] = Math.Round(p.Accuracy - p.AccuracyAvg7, 1) }));
        if (d.Items.Misconceptions.FirstOrDefault() is { } mc) r.Add(new Rec("rep.recMisconception", new() { ["share"] = mc.A, ["opt"] = mc.Option ?? "" }, mc.Id));
        if (d.Items.Discrimination.FirstOrDefault(x => x.D < 0) is { } bad) r.Add(new Rec("rep.recKey", new() { ["a"] = bad.A, ["b"] = bad.B }, bad.Id));
        if (d.Learning.Forgotten >= 5) r.Add(new Rec("rep.recForgot", new() { ["n"] = d.Learning.Forgotten }));
        if (d.Students.AtRisk.Count > 0) r.Add(new Rec("rep.recRisk", new() { ["n"] = d.Students.AtRisk.Count }));
        if (p.GoldenHour >= 0) r.Add(new Rec("rep.recGolden", new() { ["h"] = p.GoldenHour, ["acc"] = p.GoldenAccuracy, ["w"] = p.WorstHour, ["wacc"] = p.WorstAccuracy }));
        if (d.Students.Anomalies.Count > 0) r.Add(new Rec("rep.recAnomaly", new() { ["n"] = d.Students.Anomalies.Count }));
        if (d.Students.NeverStarted > 0) r.Add(new Rec("rep.recNeverStarted", new() { ["n"] = d.Students.NeverStarted }));
        return d;
    }
}

public class DailyReportData
{
    public string Day { get; set; } = "";
    public DateTime BuiltAt { get; set; }
    public PulseStat Pulse { get; set; } = new();
    public ItemsBlock Items { get; set; } = new();
    public LearningBlock Learning { get; set; } = new();
    public StudentsBlock Students { get; set; } = new();
    public List<Rec> Recommendations { get; set; } = new();
}

public class PulseStat
{
    public int Active { get; set; }
    public double ActiveAvg7 { get; set; }
    public int FirstTimers { get; set; }
    public int Tests { get; set; }
    public int Answers { get; set; }
    public double Accuracy { get; set; }
    public double AccuracyAvg7 { get; set; }
    public int ExamAttempts { get; set; }
    public double PassRate { get; set; }
    public int StudyMinutes { get; set; }
    public int VideoMinutes { get; set; }
    public int LiveAttendees { get; set; }
    public int[] Hours { get; set; } = new int[24];
    public double[] HourAccuracy { get; set; } = new double[24];
    public int PeakHour { get; set; } = -1;
    public int GoldenHour { get; set; } = -1;
    public double GoldenAccuracy { get; set; }
    public int WorstHour { get; set; } = -1;
    public double WorstAccuracy { get; set; }
}

public class ItemStat
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public int N { get; set; }
    public int Wrong { get; set; }
    public double A { get; set; }
    public double B { get; set; }
    public double D { get; set; }
    public string? Option { get; set; }
    public string? OptionText { get; set; }
}

public class ItemsBlock
{
    public List<ItemStat> Discrimination { get; set; } = new();
    public List<ItemStat> Misconceptions { get; set; } = new();
    public List<ItemStat> HarderToday { get; set; } = new();
}

public class LearningBlock
{
    public int Forgotten { get; set; }
    public List<ItemStat> ForgottenTop { get; set; } = new();
    public int NewlyMastered { get; set; }
    public double RetentionRate { get; set; }
}

public class StudentStat
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public double Value { get; set; }
    public double? Prev { get; set; }
    public string? Kind { get; set; }
    public int N { get; set; }
}

public class StudentsBlock
{
    public List<StudentStat> PassChance { get; set; } = new();
    public int ReadyCount { get; set; }
    public List<StudentStat> AtRisk { get; set; } = new();
    public int NeverStarted { get; set; }
    public List<StudentStat> Anomalies { get; set; } = new();
    public List<StudentStat> Heroes { get; set; } = new();
}

public class Rec
{
    public Rec() { }
    public Rec(string key, Dictionary<string, object>? args = null, int? questionId = null)
    {
        Key = key;
        Args = args ?? new();
        QuestionId = questionId;
    }
    public string Key { get; set; } = "";
    public Dictionary<string, object> Args { get; set; } = new();
    public int? QuestionId { get; set; }
}
