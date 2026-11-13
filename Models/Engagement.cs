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
