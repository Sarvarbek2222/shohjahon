/// <summary>
/// O'quvchilar reytingi va haftalik guruhlar.
///  • O'rin (umumiy): o'zlashtirish bo'yicha — bazadagi savollarning qanchasini to'g'ri ishlagani (tayyorlik foizi),
///    teng bo'lsa ko'proq to'g'ri ishlagan, keyin ko'proq ishlagan yuqorida. 1–2–3-o'rinlarga maqom beriladi.
///  • Haftalik guruh (oxirgi 7 kun): ishlagan savollari soni va to'g'ri javoblar ulushiga qarab —
///    "Yaxshi o'zlashtirayotganlar", "O'rtacha", "Sharmandalar doskasi" (kam ishlagan yoki ko'p xato qilgan).
/// Kompyuter hisoblari (o'quv markazidagi umumiy kompyuter) reytingda qatnashmaydi.
/// </summary>
public static class League
{
    /// <summary>Haftalik me'yor: kuniga shuncha savol (to'liq hafta — 140 ta).</summary>
    public const int DailyTarget = 20;

    public const string Good = "good", Average = "average", Shame = "shame";

    /// <summary>1–2–3-o'rin maqomlari.</summary>
    public static readonly (string Key, string Emoji)[] Badges = { ("champion", "🥇"), ("master", "🥈"), ("expert", "🥉") };

    public static LeagueData Build(List<UserStats> students, Dictionary<int, (int Answered, int Correct)> week, DateTime now)
    {
        var list = students.Where(s => !s.User.IsComputer).ToList();
        var ranked = list
            .OrderByDescending(s => s.ReadinessScore)
            .ThenByDescending(s => s.Solved)
            .ThenByDescending(s => s.TotalAnswered)
            .ThenBy(s => s.User.Username)
            .ToList();

        var rows = new List<LeagueRow>(ranked.Count);
        for (int i = 0; i < ranked.Count; i++)
        {
            var s = ranked[i];
            var (a7, c7) = week.GetValueOrDefault(s.User.Id);
            // Yangi qo'shilgan o'quvchidan to'liq haftalik me'yor talab qilinmaydi
            var days = Math.Clamp((now - s.User.CreatedAt).TotalDays, 1, 7);
            var target = (int)Math.Round(DailyTarget * days);
            var acc = a7 > 0 ? (double)c7 / a7 : 0;

            string tier, reason;
            if (a7 < target * 0.25) { tier = Shame; reason = "few"; }
            else if (acc < 0.6) { tier = Shame; reason = "errors"; }
            else if (a7 >= target * 0.75 && acc >= 0.85) { tier = Good; reason = "good"; }
            else { tier = Average; reason = a7 < target * 0.75 ? "moreVolume" : "moreAccuracy"; }

            var score = (int)Math.Round(100 * acc * Math.Min(1, (double)a7 / Math.Max(1, target)));
            // Maqom faqat haqiqatan o'zlashtirgan bo'lsa (0% bilan "Chempion" bo'lib qolmasin)
            var badge = i < Badges.Length && s.Solved > 0 ? Badges[i].Key : null;
            rows.Add(new LeagueRow(s.User.Id, s.User.FullName ?? s.User.Username, s.User.Username, i + 1,
                s.ReadinessScore, a7, c7, (int)Math.Round(acc * 100), target, score, tier, reason, badge));
        }
        return new LeagueData(rows, now);
    }

    public static string? BadgeEmoji(string? key) => Badges.FirstOrDefault(b => b.Key == key).Emoji;
}

/// <param name="Rank">Umumiy o'rin (1 — eng yaxshi).</param>
/// <param name="Readiness">O'zlashtirish (tayyorlik) foizi.</param>
/// <param name="Week">Oxirgi 7 kunda ishlangan savollar.</param>
/// <param name="WeekAccuracy">Oxirgi 7 kundagi to'g'ri javoblar foizi.</param>
/// <param name="Target">Haftalik me'yor (yangi o'quvchi uchun kamroq).</param>
/// <param name="WeekScore">Hafta bahosi 0–100: to'g'rilik × me'yor bajarilishi.</param>
/// <param name="Reason">Guruh sababi: few, errors, good, moreVolume, moreAccuracy.</param>
public record LeagueRow(int UserId, string Name, string Username, int Rank, int Readiness, int Week, int WeekCorrect,
    int WeekAccuracy, int Target, int WeekScore, string Tier, string Reason, string? Badge);

public class LeagueData
{
    public LeagueData(List<LeagueRow> rows, DateTime builtAt)
    {
        Rows = rows;
        BuiltAt = builtAt;
        ById = rows.ToDictionary(r => r.UserId);
    }

    public List<LeagueRow> Rows { get; }
    public Dictionary<int, LeagueRow> ById { get; }
    public DateTime BuiltAt { get; }
    public int Total => Rows.Count;

    public LeagueRow? Of(int userId) => ById.GetValueOrDefault(userId);

    /// <summary>Guruh a'zolari: haftalik bahosi bo'yicha (sharmandalar — eng yomoni yuqorida).</summary>
    public List<LeagueRow> Tier(string tier) => tier == League.Shame
        ? Rows.Where(r => r.Tier == tier).OrderBy(r => r.WeekScore).ThenBy(r => r.Week).ToList()
        : Rows.Where(r => r.Tier == tier).OrderByDescending(r => r.WeekScore).ThenBy(r => r.Rank).ToList();
}
