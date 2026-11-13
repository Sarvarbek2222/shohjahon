using Microsoft.AspNetCore.DataProtection;
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
    /// <summary>Faqat WebM yoki MP4 (kodeklar: harf, raqam, nuqta, vergul).</summary>
    private static readonly System.Text.RegularExpressions.Regex MimeRx =
        new(@"^video/(webm|mp4)(;codecs=[a-z0-9.,]{1,60})?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private const string StreamPurpose = "live-stream-key";

    /// <summary>O'quvchiga dars xonasi ochilganda beriladi: shu dars + shu foydalanuvchi, 12 soat.</summary>
    public static string StreamKey(HttpContext http, int lessonId, int userId) =>
        http.RequestServices.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()
            .CreateProtector(StreamPurpose).ToTimeLimitedDataProtector()
            .Protect($"{lessonId}:{userId}", TimeSpan.FromHours(12));

    private bool ValidKey(string? k, int lessonId, int userId)
    {
        if (string.IsNullOrEmpty(k) || userId == 0) return false;
        try
        {
            var p = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()
                .CreateProtector(StreamPurpose).ToTimeLimitedDataProtector();
            return p.Unprotect(k) == $"{lessonId}:{userId}";
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = Roles.SuperAdmin)]
    [RequestSizeLimit(6_000_000)]
    public async Task<IActionResult> Push(int id, string? mime, [FromServices] LiveStreamStore store, [FromServices] IHubContext<LiveHub> hub)
    {
        var status = await _db.LiveLessons.Where(l => l.Id == id).Select(l => l.Status).FirstOrDefaultAsync();
        if (status != LiveStatus.Live) return Conflict(new { error = "notLive" });
        mime = (mime ?? "video/webm").Replace(" ", "").ToLowerInvariant();
        if (!MimeRx.IsMatch(mime)) return BadRequest(new { error = "mime" });

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        if (ms.Length < 100) return BadRequest(new { error = "empty" });
        // WebM — EBML sarlavhasi (1A 45 DF A3), MP4 — "ftyp" qutisi; boshqa narsa qabul qilinmaydi
        var head = ms.GetBuffer();
        bool webm = head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3;
        bool mp4 = head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p';
        if (!(mime.StartsWith("video/webm") ? webm : mp4)) return BadRequest(new { error = "format" });

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
    public async Task<IActionResult> Seg(int id, int seq, string? k, [FromServices] LiveStreamStore store, [FromServices] LiveRoom room, CancellationToken ct)
    {
        // Manzil satrida ochish / boshqa saytga joylash — rad etiladi (faqat sahifadagi pleer o'qiydi)
        var dest = Request.Headers["Sec-Fetch-Dest"].ToString();
        if (dest is "document" or "iframe" or "embed" or "object" or "video" or "audio") return NotFound();
        if (seq < 0) return NotFound();
        // Faqat shu dars xonasini ochgan foydalanuvchi (shaxsiy kalit bilan) yoki admin
        var uid = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var u) ? u : 0;
        if (!User.IsInRole(Roles.SuperAdmin) && !ValidKey(k, id, uid) && !room.IsUserConnected(id, uid)) return NotFound();
        Response.Headers["X-Accel-Buffering"] = "no";

        var data = await store.GetAsync(id, seq, TimeSpan.FromSeconds(8), ct);
        if (data == null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(data, "application/octet-stream");
    }
}
