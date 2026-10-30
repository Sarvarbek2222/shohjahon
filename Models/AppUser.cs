using System.ComponentModel.DataAnnotations;

/// <summary>Tizim foydalanuvchisi. Parol ochiq holda saqlanmaydi — faqat PasswordHasher xeshi.</summary>
public class AppUser
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string Username { get; set; } = "";

    [MaxLength(128)]
    public string? FullName { get; set; }

    public string PasswordHash { get; set; } = "";

    /// <summary>Roles.SuperAdmin yoki Roles.User</summary>
    [MaxLength(32)]
    public string Role { get; set; } = Roles.User;

    /// <summary>Nofaol foydalanuvchi tizimga kira olmaydi.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Parol, rol yoki holat o'zgarganda yangilanadi — eski kirish cookie'lari bekor bo'ladi.</summary>
    [MaxLength(64)]
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }

    /// <summary>Oxirgi faollik (UTC) — saytdagi yoki mobil ilovadagi oxirgi so'rov (PresenceTracker yozadi).</summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>Kirish muddati (UTC). null — cheksiz. Muddat tugagach foydalanuvchi tizimga kira olmaydi.</summary>
    public DateTime? AccessExpiresAt { get; set; }

    /// <summary>Foydalanuvchi bog'langan qurilma identifikatori (brauzer cookie'si). null — hali bog'lanmagan.</summary>
    [MaxLength(64)]
    public string? DeviceId { get; set; }

    /// <summary>Bog'langan qurilma haqida qisqa ma'lumot (brauzer · tizim).</summary>
    [MaxLength(255)]
    public string? DeviceInfo { get; set; }

    public DateTime? DeviceBoundAt { get; set; }

    /// <summary>
    /// O'quv markazidagi kompyuter hisobi: bitta kompyuterga bog'lanadi va u yerda login doim saqlanadi
    /// (kompyuter o'chib-yonsa ham qayta login so'ralmaydi). Unda turli o'quvchilar ishlaydi.
    /// </summary>
    public bool IsComputer { get; set; }

    /// <summary>Telefon raqami (mobil ilovada ro'yxatdan o'tganda, ixtiyoriy).</summary>
    [MaxLength(32)]
    public string? Phone { get; set; }

    /// <summary>RegisteredVia.App — o'zi ilovada ro'yxatdan o'tgan; null — admin qo'shgan.</summary>
    [MaxLength(16)]
    public string? RegisteredVia { get; set; }

    /// <summary>Super admin uchun muddat va qurilma cheklovi qo'llanmaydi (o'zini bloklab qo'ymasligi uchun).</summary>
    public bool IsRestricted => Role != Roles.SuperAdmin;

    public bool IsExpired(DateTime utcNow) => IsRestricted && AccessExpiresAt != null && AccessExpiresAt <= utcNow;
}

/// <summary>Foydalanuvchi boshqa qurilmadan kirmoqchi bo'lganda adminga boradigan so'rov.</summary>
public class DeviceRequest
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }

    [MaxLength(64)]
    public string DeviceId { get; set; } = "";

    [MaxLength(255)]
    public string? DeviceInfo { get; set; }

    [MaxLength(64)]
    public string? IpAddress { get; set; }

    /// <summary>DeviceRequestStatus.*</summary>
    [MaxLength(16)]
    public string Status { get; set; } = DeviceRequestStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}

public static class DeviceRequestStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

public static class Roles
{
    public const string SuperAdmin = "superadmin";
    public const string User = "user";

    public static readonly string[] All = { SuperAdmin, User };

    public static bool IsValid(string? role) => role == SuperAdmin || role == User;
}
