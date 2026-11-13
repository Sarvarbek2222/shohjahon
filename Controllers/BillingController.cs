using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Admin: obuna va to'lovlar. Daromad statistikasi, to'lovlar ro'yxati (qidiruv, filtr, CSV), tariflar,
/// qo'lda muddat berish, Payme sozlamalari va Payme tekshiruviga tayyorlik ro'yxati.
/// Payme kalitlari kiritilib, "O'quvchilarga ko'rsatish" yoqilmaguncha o'quvchilar obuna bo'limini ko'rmaydi.
/// </summary>
[Authorize(Roles = Roles.SuperAdmin)]
public class BillingController : Controller
{
    public const int PageSize = 50;

    private readonly AppDbContext _db;
    private readonly SubscriptionService _subs;
    private readonly PaymentSettings _settings;

    public BillingController(AppDbContext db, SubscriptionService subs, PaymentSettings settings)
    {
        _db = db;
        _subs = subs;
        _settings = settings;
    }

    private int Me => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    public async Task<IActionResult> Index()
    {
        var now = DateTime.UtcNow;
        var today = DateTime.SpecifyKind(TashkentToday(now), DateTimeKind.Utc);
        var paid = _db.Payments.AsNoTracking().Where(p => p.Status == PaymentStatus.Paid && PaymentProviders.Paid.Contains(p.Provider));
        var model = new BillingDashboard
        {
            RevenueToday = await paid.Where(p => p.PaidAt >= today).SumAsync(p => (long?)p.Amount) ?? 0,
            Revenue7 = await paid.Where(p => p.PaidAt >= now.AddDays(-7)).SumAsync(p => (long?)p.Amount) ?? 0,
            Revenue30 = await paid.Where(p => p.PaidAt >= now.AddDays(-30)).SumAsync(p => (long?)p.Amount) ?? 0,
            RevenueTotal = await paid.SumAsync(p => (long?)p.Amount) ?? 0,
            PaidCount30 = await paid.CountAsync(p => p.PaidAt >= now.AddDays(-30)),
            PendingCount = await _db.Payments.CountAsync(p => p.Status == PaymentStatus.Pending && p.CreatedAt > now - SubscriptionService.PendingLifetime),
            Payers = await paid.Select(p => p.UserId).Distinct().CountAsync(),
            ActiveSubscribers = await _db.Users.CountAsync(u => u.Role == Roles.User && u.IsActive && (u.AccessExpiresAt == null || u.AccessExpiresAt > now)),
            Expired = await _db.Users.CountAsync(u => u.Role == Roles.User && u.AccessExpiresAt != null && u.AccessExpiresAt <= now),
            ExpiringSoon = await _db.Users.AsNoTracking()
                .Where(u => u.Role == Roles.User && u.IsActive && u.AccessExpiresAt > now && u.AccessExpiresAt <= now.AddDays(7))
                .OrderBy(u => u.AccessExpiresAt).Take(20).ToListAsync(),
            Recent = await _db.Payments.AsNoTracking().Include(p => p.User).Include(p => p.Plan)
                .OrderByDescending(p => p.CreatedAt).Take(12).ToListAsync(),
            Plans = await _subs.AllPlansAsync(),
        };
        // Oxirgi 30 kun: kunlik tushum (diagramma)
        var days = await paid.Where(p => p.PaidAt >= now.AddDays(-30)).Select(p => new { p.PaidAt, p.Amount }).ToListAsync();
        model.Daily = Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29))
            .Select(d => (d, days.Where(x => x.PaidAt >= d && x.PaidAt < d.AddDays(1)).Sum(x => x.Amount))).ToList();
        FillReadiness(model.Checks);
        return View(model);
    }

    private static DateTime TashkentToday(DateTime utc) => utc.AddHours(5).Date.AddHours(-5);

    private void FillReadiness(List<BillingCheck> checks)
    {
        var o = _subs.Options;
        checks.Add(new("bill.chkMerchant", !string.IsNullOrWhiteSpace(o.Payme.MerchantId), "/Billing/Settings"));
        checks.Add(new("bill.chkKey", !string.IsNullOrWhiteSpace(o.Payme.Key), "/Billing/Settings"));
        checks.Add(new("bill.chkPlans", _db.SubscriptionPlans.Any(p => p.IsActive && p.Price > 0), "/Billing/Plans"));
        checks.Add(new("bill.chkOffer", true, "/Pay/Offer"));
        checks.Add(new("bill.chkContacts", true, "/Pay/Contacts"));
        checks.Add(new("bill.chkTariffs", true, "/Pay/Tariffs"));
        checks.Add(new("bill.chkHttps", Request.IsHttps || Request.Headers["X-Forwarded-Proto"] == "https", null));
        checks.Add(new("bill.chkVisible", _settings.VisibleToUsers(o), "/Billing/Settings"));
    }

    // ───────── To'lovlar ─────────

    public async Task<IActionResult> Payments(string? q, string? status, string? provider, DateTime? from, DateTime? to, int page = 1)
    {
        var query = Filter(q, status, provider, from, to);
        var total = await query.CountAsync();
        page = Math.Max(1, page);
        var list = await query.OrderByDescending(p => p.CreatedAt).Skip((page - 1) * PageSize).Take(PageSize).ToListAsync();
        ViewBag.Total = total;
        ViewBag.Sum = await query.Where(p => p.Status == PaymentStatus.Paid).SumAsync(p => (long?)p.Amount) ?? 0;
        ViewBag.Page = page;
        ViewBag.Pages = (total + PageSize - 1) / PageSize;
        ViewBag.Q = q; ViewBag.Status = status; ViewBag.Provider = provider;
        ViewBag.From = from?.ToString("yyyy-MM-dd"); ViewBag.To = to?.ToString("yyyy-MM-dd");
        return View(list);
    }

    private IQueryable<Payment> Filter(string? q, string? status, string? provider, DateTime? from, DateTime? to)
    {
        var query = _db.Payments.AsNoTracking().Include(p => p.User).Include(p => p.Plan).AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(p => p.Status == status);
        if (!string.IsNullOrWhiteSpace(provider)) query = query.Where(p => p.Provider == provider);
        if (from is DateTime f) query = query.Where(p => p.CreatedAt >= f);
        if (to is DateTime t) query = query.Where(p => p.CreatedAt < t.AddDays(1));
        q = q?.Trim();
        if (!string.IsNullOrEmpty(q))
        {
            int.TryParse(q.TrimStart('#'), out var id);
            query = query.Where(p => p.Id == id || p.ProviderTransId == q || (p.User != null &&
                (p.User.Username.Contains(q) || (p.User.FullName != null && p.User.FullName.Contains(q)) || (p.User.Phone != null && p.User.Phone.Contains(q)))));
        }
        return query;
    }

    /// <summary>To'lovlar ro'yxati CSV (Excel'da ochiladi).</summary>
    public async Task<IActionResult> Export(string? q, string? status, string? provider, DateTime? from, DateTime? to)
    {
        var list = await Filter(q, status, provider, from, to).OrderByDescending(p => p.CreatedAt).Take(50_000).ToListAsync();
        var sb = new StringBuilder("﻿ID;Sana;Login;F.I.Sh;Telefon;Tarif;Oy;Kun;Summa;Tizim;Holat;Payme ID;To'langan;Muddat oxiri\r\n");
        static string C(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        foreach (var p in list)
            sb.Append($"{p.Id};{p.CreatedAt.AddHours(5):yyyy-MM-dd HH:mm};{C(p.User?.Username)};{C(p.User?.FullName)};{C(p.User?.Phone)};{C(p.Plan?.Name)};{p.Months};{p.Days};{p.Amount};{p.Provider};{p.Status};{C(p.ProviderTransId)};{p.PaidAt?.AddHours(5):yyyy-MM-dd HH:mm};{p.PeriodEnd?.AddHours(5):yyyy-MM-dd}\r\n");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", $"tolovlar-{DateTime.UtcNow.AddHours(5):yyyyMMdd-HHmm}.csv");
    }

    public async Task<IActionResult> Payment(int id)
    {
        var p = await _db.Payments.AsNoTracking().Include(x => x.User).Include(x => x.Plan).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        ViewBag.History = p.User == null ? new List<Payment>() : await _db.Payments.AsNoTracking().Include(x => x.Plan)
            .Where(x => x.UserId == p.UserId).OrderByDescending(x => x.CreatedAt).Take(30).ToListAsync();
        return View(p);
    }

    /// <summary>To'lanmagan (kutilayotgan) buyurtmani bekor qilish. Payme tranzaksiyasi boshlangan bo'lsa — mumkin emas.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelOrder(int id)
    {
        var p = await _db.Payments.FirstOrDefaultAsync(x => x.Id == id);
        if (p != null && p.Status == PaymentStatus.Pending && string.IsNullOrEmpty(p.ProviderTransId))
        {
            p.Status = PaymentStatus.Cancelled;
            await _db.SaveChangesAsync();
            TempData["BillMsg"] = "bill.orderCancelled";
        }
        else TempData["BillErr"] = "bill.cantCancel";
        return RedirectToAction(nameof(Payment), new { id });
    }

    // ───────── Qo'lda muddat berish ─────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Grant(string username, int months, int days, string? back)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == (username ?? "").Trim() && u.Role == Roles.User);
        if (user == null) TempData["BillErr"] = "bill.userNotFound";
        else if (months < 0 || days < 0 || months > 36 || days > 1000 || (months == 0 && days == 0)) TempData["BillErr"] = "bill.grantInvalid";
        else
        {
            await _subs.GrantAsync(user, months, days, Me);
            TempData["BillMsg"] = "bill.granted";
        }
        return LocalRedirect(Url.IsLocalUrl(back) ? back! : "/Billing");
    }

    // ───────── Tariflar ─────────

    public async Task<IActionResult> Plans() => View(await _subs.AllPlansAsync());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePlan(int id, string? name, int months, long price, string? badge, bool isActive, int sortOrder)
    {
        if (string.IsNullOrWhiteSpace(name) || months < 1 || months > 36 || price < 1000 || price > 100_000_000)
        {
            TempData["BillErr"] = "bill.planInvalid";
            return RedirectToAction(nameof(Plans));
        }
        var plan = id > 0 ? await _db.SubscriptionPlans.FirstOrDefaultAsync(p => p.Id == id) : null;
        if (id > 0 && plan == null) return NotFound();
        plan ??= new SubscriptionPlan();
        plan.Name = name.Trim()[..Math.Min(name.Trim().Length, 64)];
        plan.Months = months;
        plan.Price = price;
        plan.Badge = string.IsNullOrWhiteSpace(badge) ? null : badge.Trim()[..Math.Min(badge.Trim().Length, 64)];
        plan.IsActive = isActive;
        plan.SortOrder = sortOrder;
        plan.UpdatedAt = DateTime.UtcNow;
        if (plan.Id == 0) _db.SubscriptionPlans.Add(plan);
        await _db.SaveChangesAsync();
        TempData["BillMsg"] = "bill.planSaved";
        return RedirectToAction(nameof(Plans));
    }

    /// <summary>To'lovi bor tarif o'chirilmaydi — nofaol qilinadi (tarix saqlanadi).</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePlan(int id)
    {
        var plan = await _db.SubscriptionPlans.FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null) return RedirectToAction(nameof(Plans));
        if (await _db.Payments.AnyAsync(p => p.PlanId == id)) { plan.IsActive = false; TempData["BillMsg"] = "bill.planDisabled"; }
        else { _db.SubscriptionPlans.Remove(plan); TempData["BillMsg"] = "bill.planDeleted"; }
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Plans));
    }

    // ───────── Sozlamalar ─────────

    public IActionResult Settings()
    {
        var o = _subs.Options;
        ViewBag.Endpoint = $"{(Request.Headers["X-Forwarded-Proto"] == "https" ? "https" : Request.Scheme)}://{Request.Host}/api/pay/payme";
        ViewBag.ReturnDefault = $"{(Request.Headers["X-Forwarded-Proto"] == "https" ? "https" : Request.Scheme)}://{Request.Host}/Pay/Done";
        return View(new BillingSettingsForm
        {
            MerchantId = _settings.Get("MerchantId") ?? o.Payme.MerchantId,
            HasKey = _settings.HasSecret("Key"),
            HasTestKey = _settings.HasSecret("TestKey"),
            UseTest = o.Payme.UseTestEnvironment,
            Visible = _settings.Get("Visible") == "1",
            ReturnUrl = _settings.Get("ReturnUrl") ?? o.ReturnUrl,
            Ready = o.Payme.Enabled,
            Company = _settings.Get("Company"),
            Inn = _settings.Get("Inn"),
            Email = _settings.Get("Email"),
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(string? merchantId, string? key, string? testKey, bool useTest, bool visible, string? returnUrl, bool clearKey, bool clearTestKey,
        string? company, string? inn, string? email)
    {
        merchantId = merchantId?.Trim();
        if (!string.IsNullOrEmpty(merchantId) && !System.Text.RegularExpressions.Regex.IsMatch(merchantId, "^[a-fA-F0-9]{24}$"))
        {
            TempData["BillErr"] = "bill.badMerchant";
            return RedirectToAction(nameof(Settings));
        }
        if (!string.IsNullOrWhiteSpace(returnUrl) && !(Uri.TryCreate(returnUrl.Trim(), UriKind.Absolute, out var ru) && ru.Scheme == Uri.UriSchemeHttps))
        {
            TempData["BillErr"] = "bill.badReturn";
            return RedirectToAction(nameof(Settings));
        }
        await _settings.SetAsync(_db, "MerchantId", merchantId);
        // Kalit maydoni bo'sh qoldirilsa — eskisi saqlanib qoladi (maydonda ko'rsatilmaydi)
        if (clearKey) await _settings.SetAsync(_db, "Key", null);
        else if (!string.IsNullOrWhiteSpace(key)) await _settings.SetAsync(_db, "Key", key);
        if (clearTestKey) await _settings.SetAsync(_db, "TestKey", null);
        else if (!string.IsNullOrWhiteSpace(testKey)) await _settings.SetAsync(_db, "TestKey", testKey);
        await _settings.SetAsync(_db, "UseTest", useTest ? "1" : "0");
        await _settings.SetAsync(_db, "Visible", visible ? "1" : "0");
        await _settings.SetAsync(_db, "ReturnUrl", returnUrl);
        await _settings.SetAsync(_db, "Company", company?.Length > 200 ? company[..200] : company);
        await _settings.SetAsync(_db, "Inn", inn?.Length > 20 ? inn[..20] : inn);
        await _settings.SetAsync(_db, "Email", email?.Length > 120 ? email[..120] : email);
        TempData["BillMsg"] = "bill.settingsSaved";
        return RedirectToAction(nameof(Settings));
    }
}

public record BillingCheck(string Key, bool Ok, string? Url);

public class BillingDashboard
{
    public long RevenueToday { get; set; }
    public long Revenue7 { get; set; }
    public long Revenue30 { get; set; }
    public long RevenueTotal { get; set; }
    public int PaidCount30 { get; set; }
    public int PendingCount { get; set; }
    public int Payers { get; set; }
    public int ActiveSubscribers { get; set; }
    public int Expired { get; set; }
    public List<AppUser> ExpiringSoon { get; set; } = new();
    public List<Payment> Recent { get; set; } = new();
    public List<SubscriptionPlan> Plans { get; set; } = new();
    public List<(DateTime Day, long Amount)> Daily { get; set; } = new();
    public List<BillingCheck> Checks { get; set; } = new();
}

public class BillingSettingsForm
{
    public string? MerchantId { get; set; }
    public bool HasKey { get; set; }
    public bool HasTestKey { get; set; }
    public bool UseTest { get; set; }
    public bool Visible { get; set; }
    public string? ReturnUrl { get; set; }
    public bool Ready { get; set; }
    public string? Company { get; set; }
    public string? Inn { get; set; }
    public string? Email { get; set; }
}
