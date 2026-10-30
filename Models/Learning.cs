using System.ComponentModel.DataAnnotations;

// ───────────────────────── Mavzu video darslari ─────────────────────────

/// <summary>Mavzu bo'yicha video dars (har bir mavzuga bitta). Video — yuklangan fayl yoki YouTube.</summary>
public class TopicLesson
{
    public int Id { get; set; }

    /// <summary>Mavzu kaliti (Topics.All dagi kirill nom).</summary>
    [MaxLength(255)]
    public string Topic { get; set; } = "";

    [MaxLength(200)]
    public string? Title { get; set; }
    public string? Description { get; set; }

    /// <summary>VideoKinds.File — wwwroot/uploads/videos dagi fayl nomi; VideoKinds.YouTube — video identifikatori.</summary>
    [MaxLength(16)]
    public string VideoKind { get; set; } = VideoKinds.File;

    [MaxLength(255)]
    public string? VideoRef { get; set; }

    public int DurationSeconds { get; set; }
    public bool IsPublished { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<TopicLessonChapter> Chapters { get; set; } = new();

    public bool HasVideo => !string.IsNullOrWhiteSpace(VideoRef);
}

public static class VideoKinds
{
    public const string File = "file";
    public const string YouTube = "youtube";
}

/// <summary>Videodagi bo'lim: odatda mavzudagi bitta savolning tushuntirilishi boshlanadigan vaqt.</summary>
public class TopicLessonChapter
{
    public int Id { get; set; }
    public int LessonId { get; set; }
    public int? QuestionId { get; set; }

    [MaxLength(300)]
    public string? Title { get; set; }

    public int StartSeconds { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// O'quvchining video darsni ko'rishi: oxirgi joyi, haqiqatda ko'rilgan qismlar (10 soniyalik bo'laklar xaritasi:
/// '1' — ko'rilgan) va ko'rishga ketgan vaqt. Ko'rilgan ulush 90% dan oshsa — dars "ko'rildi".
/// </summary>
public class TopicLessonProgress
{
    public int Id { get; set; }
    public int LessonId { get; set; }
    public int UserId { get; set; }
    public int PositionSeconds { get; set; }
    public string WatchedMap { get; set; } = "";
    public int WatchSeconds { get; set; }
    public bool Completed { get; set; }
    public DateTime FirstAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public const int Bucket = 10;

    public double WatchedShare => WatchedMap.Length == 0 ? 0 : (double)WatchedMap.Count(c => c == '1') / WatchedMap.Length;
}

// ───────────────────────── Jonli darslar ─────────────────────────

public static class LiveStatus
{
    public const string Scheduled = "scheduled";
    public const string Live = "live";
    public const string Ended = "ended";
    public const string Cancelled = "cancelled";
}

/// <summary>Jonli dars. Efir — YouTube Live (yoki boshqa embed havola); savol-javob va davomat — saytning o'zida.</summary>
public class LiveLesson
{
    public int Id { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Boshlanish vaqti (UTC).</summary>
    public DateTime StartsAt { get; set; }
    public int DurationMinutes { get; set; } = 60;

    [MaxLength(500)]
    public string? StreamUrl { get; set; }

    [MaxLength(16)]
    public string Status { get; set; } = LiveStatus.Scheduled;

    public bool QuestionsEnabled { get; set; } = true;

    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int PeakViewers { get; set; }
    public int Reactions { get; set; }
    public bool ReminderSent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime PlannedEnd => StartsAt.AddMinutes(DurationMinutes);
}

/// <summary>Jonli darsga kirish-chiqish (bitta ulanish — bitta yozuv; LeftAt null — hali darsda).</summary>
public class LiveAttendance
{
    public int Id { get; set; }
    public int LessonId { get; set; }
    public int UserId { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LeftAt { get; set; }

    [MaxLength(64)]
    public string? ConnectionId { get; set; }
}

public static class LiveQuestionStatus
{
    public const string New = "new";
    public const string Answered = "answered";
    public const string Hidden = "hidden";
}

/// <summary>Jonli dars paytida o'quvchi yuborgan savol.</summary>
public class LiveQuestion
{
    public int Id { get; set; }
    public int LessonId { get; set; }
    public int UserId { get; set; }

    [MaxLength(500)]
    public string Text { get; set; } = "";

    [MaxLength(16)]
    public string Status { get; set; } = LiveQuestionStatus.New;
    public bool Pinned { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ───────────────────────── Bildirishnomalar ─────────────────────────

/// <summary>
/// Foydalanuvchiga bildirishnoma (sarlavhadagi qo'ng'iroqcha). Matn saqlanmaydi: turi (Kind → "notif.{kind}" matn kaliti)
/// va qiymatlari (ArgsJson) saqlanadi — o'quvchi qaysi tilni tanlasa, o'sha tilda ko'rinadi.
/// </summary>
public class UserNotification
{
    public int Id { get; set; }
    public int UserId { get; set; }

    [MaxLength(32)]
    public string Kind { get; set; } = "";

    [MaxLength(1000)]
    public string ArgsJson { get; set; } = "{}";

    [MaxLength(300)]
    public string? Url { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}

// ───────────────────────── Kunlik aqlli tahlil ─────────────────────────

/// <summary>
/// Har kuni 00:00 dan keyin o'tgan kun uchun tuziladigan hisobot. Faqat hisoblangan ma'lumot (JSON) saqlanadi —
/// xulosa matni ko'rsatilayotganda tanlangan tilda tuziladi.
/// </summary>
public class DailyReport
{
    public int Id { get; set; }

    /// <summary>Hisobot kuni (mahalliy sana).</summary>
    public DateTime Day { get; set; }

    public string DataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
