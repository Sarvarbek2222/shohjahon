using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Admin ↔ o'quvchi yozishmasi. O'quvchida bitta suhbat — admin bilan (/Messages). Admin barcha suhbatlarni ko'radi
/// (/Messages/Admin), istalgan o'quvchiga yoki hammaga birdan yoza oladi. Yangi xabarlar har bir necha soniyada
/// so'rab olinadi (Poll) — WebSocket talab qilinmaydi, har qanday serverda ishlaydi.
/// </summary>
[Authorize]
public class MessagesController : Controller
{
    public const int MaxLength = 2000;

    private readonly AppDbContext _db;
    private readonly NotificationService _notify;

    public MessagesController(AppDbContext db, NotificationService notify)
    {
        _db = db;
        _notify = notify;
    }

    private int Me => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;
    private bool IsAdmin => User.IsInRole(Roles.SuperAdmin);

    // ───────── O'quvchi ─────────

    public IActionResult Index()
    {
        if (IsAdmin) return RedirectToAction(nameof(Admin));
        return View();
    }

    // ───────── Admin ─────────

    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Admin(int? u)
    {
        ViewBag.Open = u;
        if (u is int uid)
            ViewBag.OpenUser = await _db.Users.AsNoTracking().Where(x => x.Id == uid && x.Role == Roles.User)
                .Select(x => new MsgUser(x.Id, x.FullName ?? x.Username, x.Username)).FirstOrDefaultAsync();
        return View();
    }

    /// <summary>Admin: suhbatlar ro'yxati (oxirgi xabar, o'qilmaganlar soni). q — o'quvchini qidirish (yangi suhbat uchun).</summary>
    [Authorize(Roles = Roles.SuperAdmin)]
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Threads(string? q, [FromServices] PresenceTracker presence)
    {
        var last = await _db.Messages.AsNoTracking()
            .GroupBy(m => m.UserId)
            .Select(g => new { UserId = g.Key, LastId = g.Max(m => m.Id), Unread = g.Count(m => !m.FromAdmin && m.ReadAt == null) })
            .ToListAsync();
        var lastIds = last.Select(x => x.LastId).ToList();
        var lastMsgs = await _db.Messages.AsNoTracking().Where(m => lastIds.Contains(m.Id)).ToDictionaryAsync(m => m.UserId);

        var users = _db.Users.AsNoTracking().Where(x => x.Role == Roles.User);
        q = q?.Trim();
        if (!string.IsNullOrEmpty(q))
            users = users.Where(x => x.Username.Contains(q) || (x.FullName != null && x.FullName.Contains(q)) || (x.Phone != null && x.Phone.Contains(q)));
        // Qidiruv bo'lmasa — barcha o'quvchilar (yozishmasi borlar va onlaynlar yuqorida)
        var list = await users.Select(x => new { x.Id, x.Username, x.FullName, x.LastSeenAt, x.LastLoginAt }).Take(1000).ToListAsync();
        var byUser = last.ToDictionary(x => x.UserId);

        var rows = list.Select(x =>
        {
            byUser.TryGetValue(x.Id, out var t);
            lastMsgs.TryGetValue(x.Id, out var m);
            return new
            {
                id = x.Id,
                name = x.FullName ?? x.Username,
                username = x.Username,
                online = presence.IsOnline(x.Id),
                unread = t?.Unread ?? 0,
                last = m == null ? null : new { text = Short(m.Text), fromAdmin = m.FromAdmin, at = Iso(m.CreatedAt) },
                sort = m?.CreatedAt ?? DateTime.MinValue,
            };
        })
        .OrderByDescending(r => r.unread > 0).ThenByDescending(r => r.sort).ThenByDescending(r => r.online).ThenBy(r => r.name)
        .Select(r => new { r.id, r.name, r.username, r.online, r.unread, r.last });
        return Json(rows);
    }

    /// <summary>Admin: hammaga (barcha faol o'quvchilarga) bitta xabar.</summary>
    [Authorize(Roles = Roles.SuperAdmin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Broadcast(string? text)
    {
        text = Clean(text);
        if (text == null) return BadRequest(new { error = "empty" });
        var ids = await _db.Users.AsNoTracking().Where(u => u.Role == Roles.User && u.IsActive).Select(u => u.Id).ToListAsync();
        var now = DateTime.UtcNow;
        _db.Messages.AddRange(ids.Select(id => new ChatMessage { UserId = id, FromAdmin = true, SenderId = Me, Text = text, CreatedAt = now }));
        await _db.SaveChangesAsync();
        await _notify.NotifyUsersAsync(ids, "msgNew", new { text = Short(text) }, "/Messages");
        return Ok(new { sent = ids.Count });
    }

    // ───────── Umumiy: suhbatni o'qish, yuborish ─────────

    /// <summary>
    /// Suhbat xabarlari. after — shundan keyingi xabarlar (yangilarini so'rash uchun). O'quvchi — faqat o'z suhbati;
    /// admin — u (o'quvchi) suhbati. Qabul qiluvchi ochganda xabarlar "o'qildi" bo'ladi.
    /// </summary>
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Poll(int? u, int after = 0)
    {
        var thread = IsAdmin ? u ?? 0 : Me;
        if (thread == 0) return BadRequest();
        if (IsAdmin && !await _db.Users.AnyAsync(x => x.Id == thread && x.Role == Roles.User)) return NotFound();

        var q = _db.Messages.AsNoTracking().Where(m => m.UserId == thread && m.Id > after);
        var items = after == 0
            ? (await q.OrderByDescending(m => m.Id).Take(200).ToListAsync()).OrderBy(m => m.Id).ToList()
            : await q.OrderBy(m => m.Id).Take(200).ToListAsync();

        // Ochilgan suhbatdagi menga kelgan xabarlar o'qildi
        var now = DateTime.UtcNow;
        var fromOther = !IsAdmin; // o'quvchi uchun "boshqa tomon" — admin
        await _db.Messages.Where(m => m.UserId == thread && m.FromAdmin == fromOther && m.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReadAt, now));

        // Mening xabarlarimdan qaysilari o'qilgan (✓✓ belgisi uchun)
        var myLastRead = await _db.Messages.AsNoTracking()
            .Where(m => m.UserId == thread && m.FromAdmin == IsAdmin && m.ReadAt != null)
            .MaxAsync(m => (int?)m.Id) ?? 0;

        return Json(new
        {
            items = items.Select(m => new { id = m.Id, text = m.Text, mine = m.FromAdmin == IsAdmin, at = Iso(m.CreatedAt) }),
            readUpTo = myLastRead,
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(int? u, string? text)
    {
        text = Clean(text);
        if (text == null) return BadRequest(new { error = "empty" });
        var thread = IsAdmin ? u ?? 0 : Me;
        if (thread == 0) return BadRequest();
        if (IsAdmin && !await _db.Users.AnyAsync(x => x.Id == thread && x.Role == Roles.User)) return NotFound();

        // Spamdan himoya: o'quvchi daqiqasiga ko'pi bilan 10 ta xabar
        if (!IsAdmin)
        {
            var since = DateTime.UtcNow.AddMinutes(-1);
            if (await _db.Messages.CountAsync(m => m.UserId == thread && !m.FromAdmin && m.CreatedAt > since) >= 10)
                return StatusCode(429, new { error = "tooMany" });
        }

        var msg = new ChatMessage { UserId = thread, FromAdmin = IsAdmin, SenderId = Me, Text = text, CreatedAt = DateTime.UtcNow };
        _db.Messages.Add(msg);
        await _db.SaveChangesAsync();

        // O'quvchiga — qo'ng'iroqcha orqali bildirishnoma (sahifada bo'lmasa ham ko'radi)
        if (IsAdmin) await _notify.NotifyUsersAsync(new[] { thread }, "msgNew", new { text = Short(text) }, "/Messages");
        return Ok(new { id = msg.Id, text = msg.Text, mine = true, at = Iso(msg.CreatedAt) });
    }

    /// <summary>O'qilmagan xabarlar soni (menyudagi belgi uchun).</summary>
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Unread()
    {
        var n = IsAdmin
            ? await _db.Messages.CountAsync(m => !m.FromAdmin && m.ReadAt == null)
            : await _db.Messages.CountAsync(m => m.UserId == Me && m.FromAdmin && m.ReadAt == null);
        return Json(new { count = n });
    }

    private static string? Clean(string? text)
    {
        text = text?.Replace("\r\n", "\n").Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length > MaxLength ? text[..MaxLength] : text;
    }

    private static string Short(string s) => s.Length > 90 ? s[..90] + "…" : s;
    private static string Iso(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("o");
}

public record MsgUser(int Id, string Name, string Username);
