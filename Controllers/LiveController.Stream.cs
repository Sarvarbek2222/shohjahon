using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Dastur ichidagi efir: admin brauzeri bo'laklarni yuboradi (Push), dars xonasidagi o'quvchilar ularni ketma-ket oladi (Seg).
/// Efir yozib olinmaydi va yuklab olinmaydi: bo'laklar faqat xotirada (~80 soniya), faqat dars efirda bo'lganda,
/// faqat shu dars xonasiga ulangan foydalanuvchiga beriladi; manzilni brauzerda to'g'ridan-to'g'ri ochib bo'lmaydi.
/// </summary>
public partial class LiveController
{
    private static readonly HashSet<string> AllowedMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "video/webm;codecs=vp8,opus", "video/webm;codecs=vp9,opus", "video/webm;codecs=vp8", "video/webm;codecs=vp9", "video/webm",
    };

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = Roles.SuperAdmin)]
    [RequestSizeLimit(6_000_000)]
    public async Task<IActionResult> Push(int id, string? mime, [FromServices] LiveStreamStore store, [FromServices] IHubContext<LiveHub> hub)
    {
        var status = await _db.LiveLessons.Where(l => l.Id == id).Select(l => l.Status).FirstOrDefaultAsync();
        if (status != LiveStatus.Live) return Conflict(new { error = "notLive" });
        mime = (mime ?? "video/webm").Replace(" ", "");
        if (!AllowedMimes.Contains(mime)) return BadRequest(new { error = "mime" });

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        if (ms.Length < 100) return BadRequest(new { error = "empty" });
        // WebM fayli EBML sarlavhasi bilan boshlanadi (1A 45 DF A3) — boshqa narsa qabul qilinmaydi
        var head = ms.GetBuffer();
        if (head[0] != 0x1A || head[1] != 0x45 || head[2] != 0xDF || head[3] != 0xA3) return BadRequest(new { error = "format" });

        var (seq, became) = store.Push(id, ms.ToArray(), mime);
        if (became) await hub.Clients.Group(LiveRoom.Group(id)).SendAsync("broadcast", new { on = true, mime });
        return Ok(new { seq });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> PushStop(int id, [FromServices] LiveStreamStore store, [FromServices] IHubContext<LiveHub> hub)
    {
        store.Stop(id);
        await hub.Clients.Group(LiveRoom.Group(id)).SendAsync("broadcast", new { on = false });
        return NoContent();
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult StreamState(int id, [FromServices] LiveStreamStore store)
    {
        var (active, last, mime) = store.State(id);
        return Json(new { active, last, mime });
    }

    /// <summary>Efir bo'lagi. Hali kelmagan bo'lsa — 8 soniyagacha kutadi (yangi bo'lak kelishi bilan javob beradi).</summary>
    [HttpGet]
    public async Task<IActionResult> Seg(int id, int seq, [FromServices] LiveStreamStore store, [FromServices] LiveRoom room, CancellationToken ct)
    {
        // Manzil satrida ochish / boshqa saytga joylash — rad etiladi (faqat sahifadagi pleer o'qiydi)
        var dest = Request.Headers["Sec-Fetch-Dest"].ToString();
        if (dest is "document" or "iframe" or "embed" or "object" or "video" or "audio") return NotFound();
        if (seq < 0) return NotFound();
        // Faqat shu dars xonasiga ulangan foydalanuvchi (yoki admin)
        var uid = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var u) ? u : 0;
        if (!User.IsInRole(Roles.SuperAdmin) && !room.IsUserConnected(id, uid)) return NotFound();

        var data = await store.GetAsync(id, seq, TimeSpan.FromSeconds(8), ct);
        if (data == null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(data, "application/octet-stream");
    }
}
