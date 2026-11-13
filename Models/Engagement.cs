using System.ComponentModel.DataAnnotations;

// ───────────────────────── Saqlangan savollar, xabarlar, video ↔ savol ─────────────────────────

/// <summary>O'quvchi saqlab qo'ygan savol ("Saqlanganlar" bo'limida qayta ishlanadi).</summary>
public class SavedQuestion
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int QuestionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Admin ↔ o'quvchi yozishmasi. Har bir o'quvchi bilan bitta suhbat (UserId — o'quvchi);
/// FromAdmin — xabarni admin yozganmi. ReadAt — qabul qiluvchi o'qigan vaqt.
/// </summary>
public class ChatMessage
{
    public int Id { get; set; }

    /// <summary>Suhbat egasi — o'quvchi.</summary>
    public int UserId { get; set; }

    public bool FromAdmin { get; set; }

    /// <summary>Xabarni yozgan foydalanuvchi (admin yoki o'quvchining o'zi).</summary>
    public int SenderId { get; set; }

    [MaxLength(2000)]
    public string Text { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}

/// <summary>Video dars qaysi savollarni tushuntiradi (bir video — bir nechta mavzudagi bir nechta savol).</summary>
public class TopicLessonQuestion
{
    public int Id { get; set; }
    public int LessonId { get; set; }
    public int QuestionId { get; set; }
    public int SortOrder { get; set; }
}

public static class DeleteReasons
{
    /// <summary>Admin o'chirdi.</summary>
    public const string Admin = "admin";

    /// <summary>1 hafta faol bo'lmagani uchun avtomatik o'chirildi.</summary>
    public const string Inactive = "inactive";
}

/// <summary>
/// Saytdan ro'yxatdan o'tish arizasi. Hozircha login faqat admin tomonidan beriladi: admin arizani ko'rib,
/// shu ma'lumotlar bilan foydalanuvchi yaratadi (ariza "approved") yoki rad etadi.
/// </summary>
public class RegistrationRequest
{
    public int Id { get; set; }

    [MaxLength(128)]
    public string FullName { get; set; } = "";

    [MaxLength(32)]
    public string Phone { get; set; } = "";

    /// <summary>O'quvchi xohlagan login (admin o'zgartirishi mumkin).</summary>
    [MaxLength(64)]
    public string? Username { get; set; }

    [MaxLength(500)]
    public string? Note { get; set; }

    [MaxLength(64)]
    public string? Ip { get; set; }

    /// <summary>pending, approved, rejected</summary>
    [MaxLength(16)]
    public string Status { get; set; } = "pending";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }

    /// <summary>Ariza bo'yicha yaratilgan foydalanuvchi.</summary>
    public int? UserId { get; set; }
}
