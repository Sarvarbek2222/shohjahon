using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>Jonli darsni boshlash/tugatish (holat + davomat + bildirishnoma + real vaqtda xabar) va dars hisoboti.</summary>
public class LiveService
{
    private readonly AppDbContext _db;
    private readonly LiveRoom _room;
    private readonly IHubContext<LiveHub> _hub;
    private readonly NotificationService _notify;
    private readonly LiveStreamStore _stream;

    public LiveService(AppDbContext db, LiveRoom room, IHubContext<LiveHub> hub, NotificationService notify, LiveStreamStore stream)
    {
        _db = db;
        _room = room;
        _hub = hub;
        _notify = notify;
        _stream = stream;
    }

    public async Task<bool> StartAsync(int id)
    {
        var l = await _db.LiveLessons.FirstOrDefaultAsync(x => x.Id == id);
        if (l == null || l.Status is LiveStatus.Ended or LiveStatus.Cancelled or LiveStatus.Live) return false;
        var now = DateTime.UtcNow;
        l.Status = LiveStatus.Live;
        l.StartedAt = now;

        // Kutish zalida o'tirganlar — dars boshlangan paytdan "kirgan" hisoblanadi
        var waiting = _room.Connections(id).Where(m => !m.IsAdmin).ToList();
        var rows = waiting.Select(m => new LiveAttendance { LessonId = id, UserId = m.UserId, JoinedAt = now, ConnectionId = m.ConnectionId }).ToList();
        _db.LiveAttendance.AddRange(rows);
        l.PeakViewers = Math.Max(l.PeakViewers, waiting.Select(m => m.UserId).Distinct().Count());
        await _db.SaveChangesAsync();
        foreach (var r in rows) _room.SetAttendance(id, r.ConnectionId!, r.Id);

        await _notify.NotifyStudentsAsync("liveStarted", new { title = l.Title }, "/Live/Watch/" + id);
        await _hub.Clients.Group(LiveRoom.Group(id)).SendAsync("status", LiveStatus.Live);
        return true;
    }

    public async Task<bool> EndAsync(int id)
    {
        var l = await _db.LiveLessons.FirstOrDefaultAsync(x => x.Id == id);
        if (l == null || l.Status != LiveStatus.Live) return false;
        var now = DateTime.UtcNow;
        l.Status = LiveStatus.Ended;
        l.EndedAt = now;
        l.Reactions += _room.Reactions(id);
        await _db.SaveChangesAsync();
        await _db.LiveAttendance.Where(a => a.LessonId == id && a.LeftAt == null).ExecuteUpdateAsync(s => s.SetProperty(a => a.LeftAt, now));
        await _db.LiveQuestions.Where(q => q.LessonId == id && q.Pinned).ExecuteUpdateAsync(s => s.SetProperty(q => q.Pinned, false));
        _stream.Clear(id); // efir yozib olinmaydi — dars tugashi bilan bo'laklar o'chadi
        await _hub.Clients.Group(LiveRoom.Group(id)).SendAsync("status", LiveStatus.Ended);
        _room.Close(id);
        return true;
    }

    /// <summary>Dars hisoboti: kim qachon kirdi/chiqdi, qancha qatnashdi, savollari, faollik bahosi; kelmaganlar; tomoshabinlar grafigi.</summary>
    public async Task<LiveReport> ReportAsync(LiveLesson l)
    {
        var start = l.StartedAt ?? l.StartsAt;
        var end = l.EndedAt ?? DateTime.UtcNow;
        var att = await _db.LiveAttendance.AsNoTracking().Where(a => a.LessonId == l.Id).ToListAsync();
        var questions = await _db.LiveQuestions.AsNoTracking().Where(q => q.LessonId == l.Id).ToListAsync();
        var userIds = att.Select(a => a.UserId).Concat(questions.Select(q => q.UserId)).Distinct().ToList();
        var users = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
        var totalMinutes = Math.Max(1, (end - start).TotalMinutes);

        // Bir o'quvchining bir nechta ulanishi (ikki oyna, qayta ulanish) ustma-ust tushsa — bir marta sanaladi
        static double Union(IEnumerable<(DateTime From, DateTime To)> spans)
        {
            double sum = 0; DateTime? curFrom = null, curTo = null;
            foreach (var s in spans.OrderBy(x => x.From))
            {
                if (curTo == null || s.From > curTo) { if (curTo != null) sum += (curTo.Value - curFrom!.Value).TotalMinutes; curFrom = s.From; curTo = s.To; }
                else if (s.To > curTo) curTo = s.To;
            }
            if (curTo != null) sum += (curTo.Value - curFrom!.Value).TotalMinutes;
            return sum;
        }

        var rows = userIds.Select(uid =>
        {
            var mine = att.Where(a => a.UserId == uid).ToList();
            var spans = mine.Select(a => (From: a.JoinedAt < start ? start : a.JoinedAt, To: (a.LeftAt ?? end) > end ? end : (a.LeftAt ?? end)))
                .Where(s => s.To > s.From).ToList();
            var minutes = Union(spans);
            var qs = questions.Where(q => q.UserId == uid).ToList();
            var share = Math.Min(1, minutes / totalMinutes);
            // Faollik: qatnashgan vaqt (70%) + savollar (30%, 3 ta savol — to'liq)
            var score = (int)Math.Round(100 * (0.7 * share + 0.3 * Math.Min(1, qs.Count / 3.0)));
            users.TryGetValue(uid, out var u);
            return new LiveReportRow(uid, u?.FullName ?? u?.Username ?? "#" + uid, u?.Username ?? "",
                mine.Count > 0 ? mine.Min(a => a.JoinedAt) : null, mine.Count > 0 && mine.All(a => a.LeftAt != null) ? mine.Max(a => a.LeftAt) : null,
                mine.Count, (int)Math.Round(minutes), (int)Math.Round(share * 100), qs.Count, qs.Count(q => q.Status == LiveQuestionStatus.Answered), score);
        }).OrderByDescending(r => r.Score).ThenByDescending(r => r.Minutes).ToList();

        // Tomoshabinlar grafigi: dars davomida teng oraliqli 120 nuqtada nechta o'quvchi darsda bo'lgan (qisqa dars ham chiziladi)
        var points = new List<int>();
        const int samples = 120;
        var span = end - start;
        for (int i = 0; i < samples; i++)
        {
            var t = start + TimeSpan.FromTicks(span.Ticks * i / (samples - 1));
            points.Add(att.Where(a => a.JoinedAt <= t && (a.LeftAt ?? end) >= t).Select(a => a.UserId).Distinct().Count());
        }
        var stepMin = 1;

        var attendeeIds = rows.Where(r => r.Minutes > 0 || r.Sessions > 0).Select(r => r.UserId).ToHashSet();
        var absent = await _db.Users.AsNoTracking()
            .Where(u => u.Role == Roles.User && u.IsActive && !attendeeIds.Contains(u.Id))
            .OrderBy(u => u.Username).Select(u => u.FullName ?? u.Username).ToListAsync();

        return new LiveReport
        {
            Rows = rows,
            Absent = absent,
            Timeline = points,
            TimelineStep = stepMin,
            Attendees = attendeeIds.Count,
            AvgMinutes = rows.Count == 0 ? 0 : (int)Math.Round(rows.Average(r => r.Minutes)),
            Questions = questions.Count,
            Answered = questions.Count(q => q.Status == LiveQuestionStatus.Answered),
            DurationMinutes = (int)Math.Round(totalMinutes),
        };
    }
}

public record LiveReportRow(int UserId, string Name, string Username, DateTime? FirstJoin, DateTime? LastLeave, int Sessions,
    int Minutes, int SharePercent, int Questions, int Answered, int Score);

public class LiveReport
{
    public List<LiveReportRow> Rows { get; set; } = new();
    public List<string> Absent { get; set; } = new();
    public List<int> Timeline { get; set; } = new();
    public int TimelineStep { get; set; } = 1;
    public int Attendees { get; set; }
    public int AvgMinutes { get; set; }
    public int Questions { get; set; }
    public int Answered { get; set; }
    public int DurationMinutes { get; set; }
}

/// <summary>Har daqiqada: 15 daqiqadan keyin boshlanadigan darslar uchun eslatma bildirishnomasi.</summary>
public class LiveReminderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LiveReminderService> _log;

    public LiveReminderService(IServiceScopeFactory scopes, ILogger<LiveReminderService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var notify = scope.ServiceProvider.GetRequiredService<NotificationService>();
                var now = DateTime.UtcNow;
                var due = await db.LiveLessons
                    .Where(l => l.Status == LiveStatus.Scheduled && !l.ReminderSent && l.StartsAt <= now.AddMinutes(15) && l.StartsAt >= now.AddMinutes(-5))
                    .ToListAsync(ct);
                foreach (var l in due)
                {
                    l.ReminderSent = true;
                    await db.SaveChangesAsync(ct);
                    await notify.NotifyStudentsAsync("liveReminder", new { title = l.Title }, "/Live/Watch/" + l.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Jonli dars eslatmasini yuborib bo'lmadi");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { return; }
        }
    }
}
