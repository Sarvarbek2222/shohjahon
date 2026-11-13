using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// To'lov tizimlari sozlamalari (appsettings.json → "Payments"). Kalitlar bo'sh bo'lsa, shu tizim ilovada ko'rinmaydi.
/// TestMode = true — haqiqiy pul o'tmaydi, ilovadagi "To'lash" obunani darhol faollashtiradi (faqat sinov uchun!).
/// </summary>
public class PaymentOptions
{
    public bool TestMode { get; set; }

    /// <summary>To'lovdan keyin brauzer qaytadigan manzil (masalan https://sayt.uz/api/pay/done).</summary>
    public string? ReturnUrl { get; set; }

    public ClickOptions Click { get; set; } = new();
    public PaymeOptions Payme { get; set; } = new();

    public class ClickOptions
    {
        public string? ServiceId { get; set; }
        public string? MerchantId { get; set; }
        public string? SecretKey { get; set; }
        public bool Enabled => !string.IsNullOrWhiteSpace(ServiceId) && !string.IsNullOrWhiteSpace(MerchantId) && !string.IsNullOrWhiteSpace(SecretKey);
    }

    public class PaymeOptions
    {
        public string? MerchantId { get; set; }
        /// <summary>Kassa kaliti (Payme kabinetidagi "Ключ"). Test muhitida test kaliti yoziladi.</summary>
        public string? Key { get; set; }
        /// <summary>true — checkout.test.paycom.uz (Payme sinov muhiti).</summary>
        public bool UseTestEnvironment { get; set; }
        public bool Enabled => !string.IsNullOrWhiteSpace(MerchantId) && !string.IsNullOrWhiteSpace(Key);
    }
}

/// <summary>
/// Obuna mantiqi: tariflar, buyurtma yaratish, to'lov tasdiqlanganda muddatni uzaytirish (idempotent),
/// admin to'lovsiz muddat berishi. Obunaning yagona manbai — AppUser.AccessExpiresAt (sayt ham shunga qaraydi).
/// </summary>
public class SubscriptionService
{
    /// <summary>To'lanmagan buyurtma shuncha vaqtdan keyin eskiradi (Payme talabi: 12 soat).</summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(12);

    private readonly AppDbContext _db;
    private readonly PaymentOptions _opt;

    private readonly PaymentSettings _settings;

    public SubscriptionService(AppDbContext db, IOptions<PaymentOptions> opt, PaymentSettings settings)
    {
        _db = db;
        _settings = settings;
        // appsettings.json qiymatlari + admin panelida kiritilganlar (ustun)
        _opt = settings.Effective(opt.Value);
    }

    public PaymentOptions Options => _opt;

    /// <summary>Saytdagi obuna bo'limi o'quvchilarga ko'rinadimi (admin yoqqan va Payme sozlangan).</summary>
    public bool VisibleToUsers => _settings.VisibleToUsers(_opt);

    public Task<List<SubscriptionPlan>> ActivePlansAsync() =>
        _db.SubscriptionPlans.AsNoTracking().Where(p => p.IsActive && p.Price > 0)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Months).ToListAsync();

    public Task<List<SubscriptionPlan>> AllPlansAsync() =>
        _db.SubscriptionPlans.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Months).ToListAsync();

    /// <summary>Ilovada ko'rinadigan to'lov usullari.</summary>
    public List<string> AvailableProviders()
    {
        var list = new List<string>();
        if (_opt.Click.Enabled) list.Add(PaymentProviders.Click);
        if (_opt.Payme.Enabled) list.Add(PaymentProviders.Payme);
        if (_opt.TestMode) list.Add(PaymentProviders.Test);
        return list;
    }

    /// <summary>Foydalanuvchi tarif va to'lov usulini tanladi — "pending" buyurtma yaratiladi.</summary>
    public async Task<Payment?> CreateOrderAsync(int userId, int planId, string provider)
    {
        if (!AvailableProviders().Contains(provider)) return null;
        var plan = await _db.SubscriptionPlans.FirstOrDefaultAsync(p => p.Id == planId && p.IsActive && p.Price > 0);
        if (plan == null) return null;

        var order = new Payment
        {
            UserId = userId,
            PlanId = plan.Id,
            Months = plan.Months,
            Amount = plan.Price,
            Provider = provider,
            Status = PaymentStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Payments.Add(order);
        await _db.SaveChangesAsync();
        return order;
    }

    /// <summary>To'lov sahifasining manzili (ilova shu manzilni brauzerda ochadi).</summary>
    public string? CheckoutUrl(Payment order)
    {
        var ret = string.IsNullOrWhiteSpace(_opt.ReturnUrl) ? null : _opt.ReturnUrl;
        switch (order.Provider)
        {
            case PaymentProviders.Click when _opt.Click.Enabled:
                var url = $"https://my.click.uz/services/pay?service_id={Uri.EscapeDataString(_opt.Click.ServiceId!)}" +
                          $"&merchant_id={Uri.EscapeDataString(_opt.Click.MerchantId!)}" +
                          $"&amount={order.Amount}&transaction_param={order.Id}";
                return ret == null ? url : url + "&return_url=" + Uri.EscapeDataString(ret);

            case PaymentProviders.Payme when _opt.Payme.Enabled:
                var data = $"m={_opt.Payme.MerchantId};ac.order_id={order.Id};a={order.Amount * 100}" + (ret == null ? "" : $";c={ret}");
                var host = _opt.Payme.UseTestEnvironment ? "https://checkout.test.paycom.uz/" : "https://checkout.paycom.uz/";
                return host + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(data));

            default:
                return null;
        }
    }

    /// <summary>
    /// To'lov tasdiqlandi: buyurtma "paid" bo'ladi va foydalanuvchi muddati uzayadi. Qayta chaqirilsa hech narsa
    /// o'zgarmaydi (to'lov tizimlari bir xabarni bir necha marta yuborishi mumkin).
    /// </summary>
    public async Task<bool> MarkPaidAsync(Payment order)
    {
        if (order.Status == PaymentStatus.Paid) return true;
        if (order.Status == PaymentStatus.Cancelled) return false;

        var user = order.User ?? await _db.Users.FirstOrDefaultAsync(u => u.Id == order.UserId);
        if (user == null) return false;

        var now = DateTime.UtcNow;
        var start = user.AccessExpiresAt is DateTime e && e > now ? e : now;
        var end = start.AddMonths(order.Months).AddDays(order.Days);

        // Super admin yoki cheksiz muddatli foydalanuvchi uchun muddat qisqarmasin
        if (user.AccessExpiresAt != null || user.IsRestricted)
            user.AccessExpiresAt = end;

        order.Status = PaymentStatus.Paid;
        order.PaidAt = now;
        order.PeriodStart = start;
        order.PeriodEnd = end;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// To'langan to'lov bekor qilindi (Payme orqali pul qaytarildi): shu to'lov qo'shgan davr muddatdan ayiriladi.
    /// </summary>
    public async Task RevokeAsync(Payment order)
    {
        if (order.Status != PaymentStatus.Paid || order.PeriodStart == null || order.PeriodEnd == null)
        {
            order.Status = PaymentStatus.Cancelled;
            await _db.SaveChangesAsync();
            return;
        }
        var user = order.User ?? await _db.Users.FirstOrDefaultAsync(u => u.Id == order.UserId);
        if (user?.AccessExpiresAt is DateTime exp)
        {
            var length = order.PeriodEnd.Value - order.PeriodStart.Value;
            user.AccessExpiresAt = exp - length;
        }
        order.Status = PaymentStatus.Cancelled;
        await _db.SaveChangesAsync();
    }

    /// <summary>Admin to'lovsiz muddat beradi (yoki uzaytiradi). months = days = 0 — cheksiz.</summary>
    public async Task GrantAsync(AppUser user, int months, int days, int adminId)
    {
        var now = DateTime.UtcNow;
        DateTime? start = null, end = null;
        if (months <= 0 && days <= 0)
        {
            user.AccessExpiresAt = null;
        }
        else
        {
            start = user.AccessExpiresAt is DateTime e && e > now ? e : now;
            end = start.Value.AddMonths(Math.Max(0, months)).AddDays(Math.Max(0, days));
            user.AccessExpiresAt = end;
        }

        _db.Payments.Add(new Payment
        {
            User = user,
            Months = Math.Max(0, months),
            Days = Math.Max(0, days),
            Amount = 0,
            Provider = PaymentProviders.Admin,
            Status = PaymentStatus.Paid,
            CreatedAt = now,
            PaidAt = now,
            PeriodStart = start,
            PeriodEnd = end,
            GrantedByUserId = adminId,
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>Foydalanuvchi pul to'lab kirganmi (Click/Payme/test) — admin ro'yxatida alohida ko'rsatish uchun.</summary>
    public IQueryable<int> PaidUserIds() =>
        _db.Payments.Where(p => p.Status == PaymentStatus.Paid && PaymentProviders.Paid.Contains(p.Provider))
            .Select(p => p.UserId).Distinct();

    public static SubscriptionStatus StatusOf(AppUser user, DateTime utcNow)
    {
        if (!user.IsRestricted || user.AccessExpiresAt == null)
            return new SubscriptionStatus(true, null, null, false);
        var exp = user.AccessExpiresAt.Value;
        var active = exp > utcNow;
        int? daysLeft = active ? (int)Math.Ceiling((exp - utcNow).TotalDays) : 0;
        return new SubscriptionStatus(active, exp, daysLeft, active && daysLeft <= 7);
    }
}

/// <summary>Obuna holati. ExpiresAt = null — cheksiz. ExpiringSoon — 7 kundan kam qoldi (ilova ogohlantiradi).</summary>
public record SubscriptionStatus(bool Active, DateTime? ExpiresAt, int? DaysLeft, bool ExpiringSoon);
