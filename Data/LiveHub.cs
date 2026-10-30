using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Jonli dars — real vaqt aloqasi (SignalR, /hubs/live). O'quvchi: xonaga kirish, savol yuborish, reaksiya.
/// Admin: kim kirdi/chiqdi (jonli), savollar (kimdan kelgani bilan), savollarni yoqish/o'chirish, savolni "javob berilmoqda"
/// deb mahkamlash, javob berildi / yashirish. Davomat (kirish-chiqish vaqtlari) faqat dars efirda bo'lganda yoziladi.
/// </summary>
[Authorize]
public class LiveHub : Hub
{
    public static readonly string[] Emojis = { "👍", "❤️", "👏", "🔥", "😮", "❓" };

    private readonly AppDbContext _db;
    private readonly LiveRoom _room;

    public LiveHub(AppDbContext db, LiveRoom room)
    {
        _db = db;
        _room = room;
    }

    private int UserId => int.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;
    private bool IsAdmin => Context.User?.IsInRole(Roles.SuperAdmin) == true;

    public async Task<object> Join(int lessonId)
    {
        var lesson = await _db.LiveLessons.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lessonId);
        if (lesson == null || lesson.Status == LiveStatus.Cancelled) throw new HubException("not_found");
        var uid = UserId;
        var user = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == uid);
        var name = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName!;
        var now = DateTime.UtcNow;

        await Groups.AddToGroupAsync(Context.ConnectionId, LiveRoom.Group(lessonId));
        if (IsAdmin) await Groups.AddToGroupAsync(Context.ConnectionId, LiveRoom.AdminGroup(lessonId));

        var firstConnection = !_room.IsUserConnected(lessonId, uid);
        int? attendanceId = null;
        if (!IsAdmin && lesson.Status == LiveStatus.Live)
        {
            var a = new LiveAttendance { LessonId = lessonId, UserId = uid, JoinedAt = now, ConnectionId = Context.ConnectionId };
            _db.LiveAttendance.Add(a);
            await _db.SaveChangesAsync();
            attendanceId = a.Id;
        }
        _room.Add(lessonId, new LiveRoom.Member(Context.ConnectionId, uid, name, IsAdmin, now, attendanceId));

        var students = _room.Students(lessonId);
        if (!IsAdmin)
        {
            if (lesson.Status == LiveStatus.Live && students.Count > lesson.PeakViewers)
                await _db.LiveLessons.Where(l => l.Id == lessonId).ExecuteUpdateAsync(s => s.SetProperty(l => l.PeakViewers, students.Count));
            if (firstConnection)
                await Clients.Group(LiveRoom.AdminGroup(lessonId)).SendAsync("viewerJoined", new { userId = uid, name, at = now });
        }
        await Clients.Group(LiveRoom.Group(lessonId)).SendAsync("viewers", students.Count);

        var pinned = await _db.LiveQuestions.AsNoTracking()
            .Where(q => q.LessonId == lessonId && q.Pinned && q.Status != LiveQuestionStatus.Hidden)
            .Join(_db.Users, q => q.UserId, u => u.Id, (q, u) => new { q.Id, q.Text, name = u.FullName ?? u.Username })
            .FirstOrDefaultAsync();

        if (IsAdmin)
        {
            var questions = await _db.LiveQuestions.AsNoTracking().Where(q => q.LessonId == lessonId)
                .OrderByDescending(q => q.CreatedAt)
                .Join(_db.Users, q => q.UserId, u => u.Id, (q, u) => new
                {
                    q.Id, q.UserId, name = u.FullName ?? u.Username, username = u.Username, q.Text, q.Status, q.Pinned, at = q.CreatedAt,
                }).ToListAsync();
            return new
            {
                lesson.Status, lesson.QuestionsEnabled, viewers = students.Count,
                people = students.Select(s => new { s.UserId, s.Name, at = s.JoinedAt }),
                questions, pinned, reactions = _room.Reactions(lessonId) + lesson.Reactions,
            };
        }

        var mine = await _db.LiveQuestions.AsNoTracking()
            .Where(q => q.LessonId == lessonId && q.UserId == uid)
            .OrderByDescending(q => q.CreatedAt)
            .Select(q => new { q.Id, q.Text, q.Status, at = q.CreatedAt }).ToListAsync();
        return new { lesson.Status, lesson.QuestionsEnabled, viewers = students.Count, mine, pinned };
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var gone = _room.Remove(Context.ConnectionId);
        if (gone is { } g)
        {
            var now = DateTime.UtcNow;
            if (g.Member.AttendanceId is int aid)
                await _db.LiveAttendance.Where(a => a.Id == aid && a.LeftAt == null).ExecuteUpdateAsync(s => s.SetProperty(a => a.LeftAt, now));
            var students = _room.Students(g.LessonId);
            if (!g.Member.IsAdmin && !_room.IsUserConnected(g.LessonId, g.Member.UserId))
                await Clients.Group(LiveRoom.AdminGroup(g.LessonId)).SendAsync("viewerLeft", new { userId = g.Member.UserId, name = g.Member.Name, at = now });
            await Clients.Group(LiveRoom.Group(g.LessonId)).SendAsync("viewers", students.Count);
        }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task<object> Ask(int lessonId, string text)
    {
        text = (text ?? "").Trim();
        if (text.Length < 2) return new { ok = false, error = "short" };
        if (text.Length > 500) text = text[..500];
        var lesson = await _db.LiveLessons.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lessonId);
        if (lesson == null || lesson.Status != LiveStatus.Live) return new { ok = false, error = "notLive" };
        if (!lesson.QuestionsEnabled) return new { ok = false, error = "closed" };
        var uid = UserId;
        if (!_room.IsUserConnected(lessonId, uid)) return new { ok = false, error = "notJoined" };
        if (_room.QuestionCooldown(lessonId, uid) is TimeSpan wait) return new { ok = false, error = "wait", seconds = (int)Math.Ceiling(wait.TotalSeconds) };
        if (await _db.LiveQuestions.CountAsync(q => q.LessonId == lessonId && q.UserId == uid) >= 30) return new { ok = false, error = "limit" };

        var q = new LiveQuestion { LessonId = lessonId, UserId = uid, Text = text, CreatedAt = DateTime.UtcNow };
        _db.LiveQuestions.Add(q);
        await _db.SaveChangesAsync();
        var user = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == uid);
        await Clients.Group(LiveRoom.AdminGroup(lessonId)).SendAsync("questionNew", new
        {
            q.Id, q.UserId, name = user.FullName ?? user.Username, username = user.Username, q.Text, q.Status, q.Pinned, at = q.CreatedAt,
        });
        return new { ok = true, question = new { q.Id, q.Text, q.Status, at = q.CreatedAt } };
    }

    public async Task React(int lessonId, string emoji)
    {
        if (!Emojis.Contains(emoji) || !_room.AllowReaction(Context.ConnectionId)) return;
        _room.AddReaction(lessonId);
        await Clients.Group(LiveRoom.Group(lessonId)).SendAsync("reaction", emoji);
    }

    // ── Admin ──

    private void RequireAdmin()
    {
        if (!IsAdmin) throw new HubException("forbidden");
    }

    public async Task SetQuestions(int lessonId, bool enabled)
    {
        RequireAdmin();
        await _db.LiveLessons.Where(l => l.Id == lessonId).ExecuteUpdateAsync(s => s.SetProperty(l => l.QuestionsEnabled, enabled));
        await Clients.Group(LiveRoom.Group(lessonId)).SendAsync("questionsToggled", enabled);
    }

    public async Task MarkQuestion(int questionId, string status)
    {
        RequireAdmin();
        if (status != LiveQuestionStatus.Answered && status != LiveQuestionStatus.Hidden && status != LiveQuestionStatus.New) return;
        var q = await _db.LiveQuestions.FirstOrDefaultAsync(x => x.Id == questionId);
        if (q == null) return;
        q.Status = status;
        var unpinned = q.Pinned && status != LiveQuestionStatus.New;
        if (unpinned) q.Pinned = false;
        await _db.SaveChangesAsync();
        await Clients.Group(LiveRoom.AdminGroup(q.LessonId)).SendAsync("questionUpdated", new { q.Id, q.Status, q.Pinned });
        await Clients.User(q.UserId.ToString()).SendAsync("myQuestionUpdated", new { q.Id, q.Status });
        if (unpinned) await Clients.Group(LiveRoom.Group(q.LessonId)).SendAsync("pinned", null);
    }

    /// <summary>Savolni barcha o'quvchilarga "Hozir javob berilmoqda" deb ko'rsatish (bittasi mahkamlanadi).</summary>
    public async Task PinQuestion(int questionId, bool pin)
    {
        RequireAdmin();
        var q = await _db.LiveQuestions.FirstOrDefaultAsync(x => x.Id == questionId);
        if (q == null) return;
        await _db.LiveQuestions.Where(x => x.LessonId == q.LessonId && x.Pinned).ExecuteUpdateAsync(s => s.SetProperty(x => x.Pinned, false));
        q.Pinned = pin;
        await _db.SaveChangesAsync();
        var user = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == q.UserId);
        await Clients.Group(LiveRoom.AdminGroup(q.LessonId)).SendAsync("questionPinnedAdmin", pin ? q.Id : (int?)null);
        await Clients.Group(LiveRoom.Group(q.LessonId)).SendAsync("pinned", pin ? new { q.Id, q.Text, name = user.FullName ?? user.Username } : null);
    }
}
