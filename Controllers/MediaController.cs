using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Himoyalangan video: fayllar ochiq papkada emas (App_Data/videos), faqat shu yerdan beriladi.
/// Havola — foydalanuvchiga bog'langan, 6 soatlik shifrlangan kalit: boshqaga yuborilsa yoki muddati o'tsa ishlamaydi.
/// Manzil brauzerda to'g'ridan-to'g'ri ochilsa (saqlab olish uchun) — rad etiladi; faqat sahifadagi pleer o'qiy oladi.
/// </summary>
public class MediaController : Controller
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);
    private readonly ITimeLimitedDataProtector _protector;
    private readonly VideoUploads _videos;

    public MediaController(IDataProtectionProvider dp, VideoUploads videos)
    {
        _protector = dp.CreateProtector("media.video.v1").ToTimeLimitedDataProtector();
        _videos = videos;
    }

    /// <summary>Joriy foydalanuvchi uchun video havolasi.</summary>
    public static string VideoUrl(IDataProtectionProvider dp, ClaimsPrincipal user, string fileName)
    {
        var uid = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0";
        var key = dp.CreateProtector("media.video.v1").ToTimeLimitedDataProtector().Protect(uid + "|" + fileName, Lifetime);
        return "/Media/Video?k=" + Uri.EscapeDataString(key);
    }

    [HttpGet]
    public IActionResult Video(string k)
    {
        string payload;
        try { payload = _protector.Unprotect(k ?? ""); }
        catch { return NotFound(); }
        var parts = payload.Split('|', 2);
        if (parts.Length != 2 || parts[0] != User.FindFirstValue(ClaimTypes.NameIdentifier)) return NotFound();

        // Brauzer manzil satrida ochish (sahifa sifatida) — yuklab olishga urinish; pleer so'rovi "video" bo'ladi
        var dest = Request.Headers["Sec-Fetch-Dest"].ToString();
        if (dest is "document" or "iframe" or "embed" or "object") return NotFound();

        var path = _videos.PathOf(parts[1]);
        if (path == null) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["Content-Disposition"] = "inline";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(path, VideoUploads.ContentType(path), enableRangeProcessing: true);
    }
}
