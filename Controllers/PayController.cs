using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Saytdagi obuna: ochiq sahifalar (tariflar, ommaviy oferta, kontaktlar — Payme tekshiruvi uchun ham shart)
/// va o'quvchining "Obuna" bo'limi (Payme orqali to'lash). O'quvchi bo'limi admin yoqmaguncha va Payme
/// kalitlari kiritilmaguncha ko'rinmaydi (404).
/// </summary>
public class PayController : Controller
{
    public const string Phone = "+998 95 967 29 59";
    public const string PhoneHref = "+998959672959";

    private readonly AppDbContext _db;
    private readonly SubscriptionService _subs;
    private readonly PaymentSettings _settings;

    public PayController(AppDbContext db, SubscriptionService subs, PaymentSettings settings)
    {
        _db = db;
        _subs = subs;
        _settings = settings;
    }

    private int Me => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    // ───────── Ochiq sahifalar ─────────

    [AllowAnonymous]
    public async Task<IActionResult> Tariffs()
    {
        ViewBag.CanBuy = User.Identity?.IsAuthenticated == true && !User.IsInRole(Roles.SuperAdmin) && _subs.VisibleToUsers;
        return View(await _subs.ActivePlansAsync());
    }

    [AllowAnonymous]
    public IActionResult Offer() => View(Company());

    [AllowAnonymous]
    public IActionResult Contacts() => View(Company());

    [AllowAnonymous]
    public IActionResult Privacy() => View(Company());

    private CompanyInfo Company() => new(_settings.Get("Company"), _settings.Get("Inn"), _settings.Get("Email"));

    // ───────── O'quvchi ─────────

    [Authorize]
    public async Task<IActionResult> My()
    {
        if (!_subs.VisibleToUsers || User.IsInRole(Roles.SuperAdmin)) return NotFound();
        var me = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == Me);
        ViewBag.Status = SubscriptionService.StatusOf(me, DateTime.UtcNow);
        ViewBag.History = await _db.Payments.AsNoTracking().Include(p => p.Plan)
            .Where(p => p.UserId == Me && (p.Status == PaymentStatus.Paid || p.CreatedAt > DateTime.UtcNow.AddDays(-1)))
            .OrderByDescending(p => p.CreatedAt).Take(30).ToListAsync();
        return View(await _subs.ActivePlansAsync());
    }

    /// <summary>Tarif tanlandi → Payme buyurtmasi → Payme to'lov sahifasiga yo'naltirish.</summary>
    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Buy(int planId)
    {
        if (!_subs.VisibleToUsers || User.IsInRole(Roles.SuperAdmin)) return NotFound();
        // Bir vaqtda ko'p buyurtma ochilmasin (har daqiqada ko'pi bilan 5 ta)
        if (await _db.Payments.CountAsync(p => p.UserId == Me && p.CreatedAt > DateTime.UtcNow.AddMinutes(-1)) >= 5)
            return StatusCode(429);
        var order = await _subs.CreateOrderAsync(Me, planId, PaymentProviders.Payme);
        var url = order == null ? null : _subs.CheckoutUrl(order);
        if (url == null)
        {
            TempData["PayErr"] = "pay.errOrder";
            return RedirectToAction(nameof(My));
        }
        return Redirect(url);
    }

    /// <summary>Payme to'lovdan keyin shu sahifaga qaytaradi (to'lov holati Payme serveridan alohida keladi).</summary>
    [Authorize]
    public async Task<IActionResult> Done(int? order)
    {
        var p = order is int id ? await _db.Payments.AsNoTracking().Include(x => x.Plan).FirstOrDefaultAsync(x => x.Id == id && x.UserId == Me) : null;
        p ??= await _db.Payments.AsNoTracking().Include(x => x.Plan).Where(x => x.UserId == Me).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
        return View(p);
    }
}

public record CompanyInfo(string? Name, string? Inn, string? Email);
