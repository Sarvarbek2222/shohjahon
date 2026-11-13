using Microsoft.AspNetCore.DataProtection;

/// <summary>
/// To'lov sozlamalari admin panelidan (Obuna va to'lovlar → Sozlamalar) o'zgartiriladi va bazada (AppSettings) saqlanadi.
/// Maxfiy kalitlar shifrlangan holda yoziladi. Admin kiritmagan qiymatlar appsettings.json → "Payments" dan olinadi.
/// Obuna o'quvchilarga faqat admin "O'quvchilarga ko'rsatish"ni yoqqanda va Payme kalitlari kiritilganda ko'rinadi.
/// </summary>
public class PaymentSettings
{
    private const string Prefix = "Pay.";
    private const string Purpose = "payment-settings";

    private readonly IDataProtector _protector;
    private readonly Dictionary<string, string?> _values = new();
    private readonly object _lock = new();

    public PaymentSettings(IDataProtectionProvider dp) => _protector = dp.CreateProtector(Purpose);

    public static readonly string[] Keys = { "MerchantId", "Key", "TestKey", "UseTest", "Visible", "ReturnUrl", "Company", "Inn", "Email" };
    private static readonly HashSet<string> Secret = new() { "Key", "TestKey" };

    public async Task LoadAsync(AppDbContext db)
    {
        foreach (var k in Keys)
        {
            var v = await SiteSettings.GetAsync(db, Prefix + k);
            if (string.IsNullOrEmpty(v)) v = null;
            if (v != null && Secret.Contains(k))
            {
                try { v = _protector.Unprotect(v); } catch { v = null; } // kalitlar almashgan bo'lsa — qayta kiritiladi
            }
            lock (_lock) _values[k] = v;
        }
    }

    public string? Get(string key) { lock (_lock) return _values.GetValueOrDefault(key); }

    public async Task SetAsync(AppDbContext db, string key, string? value)
    {
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var stored = value == null ? "" : Secret.Contains(key) ? _protector.Protect(value) : value;
        await SiteSettings.SetAsync(db, Prefix + key, stored);
        lock (_lock) _values[key] = value;
    }

    public bool HasSecret(string key) => !string.IsNullOrEmpty(Get(key));

    /// <summary>appsettings.json qiymatlari ustiga admin kiritganlari qo'yiladi.</summary>
    public PaymentOptions Effective(PaymentOptions file)
    {
        var useTest = Get("UseTest") is string t ? t == "1" : file.Payme.UseTestEnvironment;
        var key = useTest ? (Get("TestKey") ?? Get("Key") ?? file.Payme.Key) : (Get("Key") ?? file.Payme.Key);
        return new PaymentOptions
        {
            TestMode = file.TestMode,
            ReturnUrl = Get("ReturnUrl") ?? file.ReturnUrl,
            Click = file.Click,
            Payme = new PaymentOptions.PaymeOptions
            {
                MerchantId = Get("MerchantId") ?? file.Payme.MerchantId,
                Key = key,
                UseTestEnvironment = useTest,
            },
        };
    }

    /// <summary>O'quvchilarga obuna bo'limi ko'rinadimi: admin yoqqan va Payme sozlangan.</summary>
    public bool VisibleToUsers(PaymentOptions effective) => Get("Visible") == "1" && effective.Payme.Enabled;
}
