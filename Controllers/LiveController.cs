using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Jonli darslar. O'quvchi: ro'yxat (efirda / rejalashtirilgan / o'tgan), dars xonasi, taqvimga qo'shish (.ics).
/// Admin: rejalashtirish, boshqaruv xonasi (boshlash/tugatish, kim kirdi-chiqdi, savollar), dars hisoboti.
/// </summary>
public partial class LiveController : Controller
{
    private readonly AppDbContext _db;
    private readonly LiveService _live;
    private readonly NotificationService _notify;

    public LiveController(AppDbContext db, LiveService live, NotificationService notify)
    {
        _db = db;
        _live = live;
        _notify = notify;
    }

    private int UserId => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    /// <summary>YouTube (oddiy, /live/, youtu.be) havolasi → embed manzili; boshqa https havola — o'zicha.</summary>
    public static string? EmbedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var yt = VideoUploads.ParseYouTubeId(url);
        if (yt != null) return $"https://www.youtube-nocookie.com/embed/{yt}?autoplay=1&rel=0&modestbranding=1&playsinline=1";
        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u.ToString() : null;
    }

    // ───────── O'quvchi ─────────

    public async Task<IActionResult> Index()
    {
        var now = DateTime.UtcNow;
        var all = await _db.LiveLessons.AsNoTracking()
            .Where(l => l.Status != LiveStatus.Cancelled && (l.Status != LiveStatus.Ended || l.EndedAt > now.AddDays(-60)))
            .OrderBy(l => l.StartsAt).ToListAsync();
        var uid = UserId;
        var myAtt = await _db.LiveAttendance.AsNoTracking().Where(a => a.UserId == uid)
            .Select(a => new { a.LessonId, a.JoinedAt, a.LeftAt }).ToListAsync();
        ViewBag.MyMinutes = myAtt.GroupBy(a => a.LessonId)
            // Darsga kirgan bo'lsa — kamida 1 daqiqa (bir necha soniya ham "qatnashmagan" bo'lib ko'rinmasin)
            .ToDictionary(g => g.Key, g => (int)Math.Max(1, Math.Round(g.Sum(a => ((a.LeftAt ?? now) - a.JoinedAt).TotalMinutes))));
        return View(all);
    }

    public async Task<IActionResult> Watch(int id)
    {
        var l = await _db.LiveLessons.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (l == null || l.Status == LiveStatus.Cancelled) return RedirectToAction(nameof(Index));
        // Video ustidagi suv belgisi: login va telefon (efir yozib olinsa — kim yozgani bilinadi)
        var me = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId);
        var phone = UserService.FormatPhone(me?.Phone);
        ViewBag.Watermark = me == null ? "" : string.IsNullOrEmpty(phone) ? me.Username : me.Username + " · " + phone;
        return View(l);
    }

    /// <summary>Taqvimga qo'shish (Google/Apple/Outlook taqvimlari ochadigan .ics fayl).</summary>
    public async Task<IActionResult> Ics(int id)
    {
        var l = await _db.LiveLessons.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (l == null) return NotFound();
        static string D(DateTime d) => d.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
        static string Esc(string? s) => (s ?? "").Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");
        var url = $"{Request.Scheme}://{Request.Host}/Live/Watch/{l.Id}";
        var ics = new StringBuilder()
            .AppendLine("BEGIN:VCALENDAR").AppendLine("VERSION:2.0").AppendLine("PRODID:-//AVTOPRAVA_SAM//Live//UZ")
            .AppendLine("BEGIN:VEVENT")
            .AppendLine($"UID:live-{l.Id}@avtoprava")
            .AppendLine($"DTSTAMP:{D(DateTime.UtcNow)}")
            .AppendLine($"DTSTART:{D(DateTime.SpecifyKind(l.StartsAt, DateTimeKind.Utc))}")
            .AppendLine($"DTEND:{D(DateTime.SpecifyKind(l.PlannedEnd, DateTimeKind.Utc))}")
            .AppendLine($"SUMMARY:{Esc(l.Title)}")
            .AppendLine($"DESCRIPTION:{Esc((l.Description ?? "") + "\n" + url)}")
            .AppendLine($"URL:{url}")
            .AppendLine("BEGIN:VALARM").AppendLine("TRIGGER:-PT15M").AppendLine("ACTION:DISPLAY").AppendLine($"DESCRIPTION:{Esc(l.Title)}").AppendLine("END:VALARM")
            .AppendLine("END:VEVENT").AppendLine("END:VCALENDAR").ToString();
        return File(Encoding.UTF8.GetBytes(ics), "text/calendar", $"jonli-dars-{l.Id}.ics");
    }

    // ───────── Admin ─────────

    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Manage(int? edit)
    {
        var all = await _db.LiveLessons.AsNoTracking().OrderByDescending(l => l.StartsAt).Take(100).ToListAsync();
        var ids = all.Select(l => l.Id).ToList();
        ViewBag.Attendees = await _db.LiveAttendance.AsNoTracking().Where(a => ids.Contains(a.LessonId))
            .GroupBy(a => a.LessonId).Select(g => new { g.Key, N = g.Select(a => a.UserId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);
        ViewBag.QuestionCounts = await _db.LiveQuestions.AsNoTracking().Where(q => ids.Contains(q.LessonId))
            .GroupBy(q => q.LessonId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        ViewBag.Edit = edit is int e ? all.FirstOrDefault(l => l.Id == e) : null;
        return View(all);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Save(int? id, string title, string? description, string startsAtUtc, int durationMinutes,
        string? streamUrl, bool questionsEnabled, bool notify)
    {
        title = (title ?? "").Trim();
        if (title.Length == 0 || !DateTimeOffset.TryParse(startsAtUtc, out var starts))
        {
            TempData["LvErr"] = "lv.errForm";
            return RedirectToAction(nameof(Manage), new { edit = id });
        }
        if (!string.IsNullOrWhiteSpace(streamUrl) && EmbedUrl(streamUrl) == null)
        {
            TempData["LvErr"] = "lv.errStream";
            return RedirectToAction(nameof(Manage), new { edit = id });
        }

        LiveLesson l;
        var isNew = id == null;
        if (isNew)
        {
            l = new LiveLesson { CreatedAt = DateTime.UtcNow };
            _db.LiveLessons.Add(l);
        }
        else
        {
            l = await _db.LiveLessons.FirstOrDefaultAsync(x => x.Id == id) ?? throw new InvalidOperationException();
            if (l.StartsAt != starts.UtcDateTime) l.ReminderSent = false;
        }
        l.Title = title.Length > 200 ? title[..200] : title;
        l.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        l.StartsAt = starts.UtcDateTime;
        l.DurationMinutes = Math.Clamp(durationMinutes, 10, 600);
        l.StreamUrl = string.IsNullOrWhiteSpace(streamUrl) ? null : streamUrl.Trim();
        l.QuestionsEnabled = questionsEnabled;
        await _db.SaveChangesAsync();

        if (isNew && notify)
            await _notify.NotifyStudentsAsync("liveScheduled", new { title = l.Title, at = DateTime.SpecifyKind(l.StartsAt, DateTimeKind.Utc).ToString("o") }, "/Live/Watch/" + l.Id);

        TempData["LvMsg"] = isNew ? "lv.created" : "lv.saved";
        return RedirectToAction(nameof(Manage));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Cancel(int id)
    {
        await _db.LiveLessons.Where(l => l.Id == id && l.Status == LiveStatus.Scheduled)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, LiveStatus.Cancelled));
        return RedirectToAction(nameof(Manage));
    }

    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Control(int id)
    {
        var l = await _db.LiveLessons.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (l == null || l.Status == LiveStatus.Cancelled) return RedirectToAction(nameof(Manage));
        ViewBag.TotalStudents = await _db.Users.CountAsync(u => u.Role == Roles.User && u.IsActive);
        return View(l);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Start(int id)
    {
        await _live.StartAsync(id);
        return RedirectToAction(nameof(Control), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> End(int id)
    {
        await _live.EndAsync(id);
        return RedirectToAction(nameof(Report), new { id });
    }

    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Report(int id)
    {
        var l = await _db.LiveLessons.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (l == null) return RedirectToAction(nameof(Manage));
        ViewBag.Report = await _live.ReportAsync(l);
        ViewBag.QuestionsList = await _db.LiveQuestions.AsNoTracking().Where(q => q.LessonId == id).OrderBy(q => q.CreatedAt)
            .Join(_db.Users, q => q.UserId, u => u.Id, (q, u) => new LiveQuestionRow(q.Text, u.FullName ?? u.Username, q.Status, q.CreatedAt))
            .ToListAsync();
        return View(l);
    }
}

public record LiveQuestionRow(string Text, string Name, string Status, DateTime At);
