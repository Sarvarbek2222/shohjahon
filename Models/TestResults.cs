using System.ComponentModel.DataAnnotations;

/// <summary>Bitta test urinishi (foydalanuvchi testni "Yakunlash" qilganda saqlanadi).</summary>
public class TestAttempt
{
    public int Id { get; set; }

    public int UserId { get; set; }

    /// <summary>Brauzerda yaratiladigan noyob kalit — bir urinish qayta yuborilsa, yangilanadi (dublikat bo'lmaydi).</summary>
    [MaxLength(64)]
    public string AttemptKey { get; set; } = "";

    /// <summary>TestTypes dagi qiymatlardan biri.</summary>
    [MaxLength(32)]
    public string TestType { get; set; } = "";

    /// <summary>Bilet raqami yoki mavzu nomi (boshqa testlarda bo'sh).</summary>
    [MaxLength(255)]
    public string? TestRef { get; set; }

    /// <summary>Test ichidagi "Xatolarni ishlash" rejimi — umumiy natijalar statistikasiga qo'shilmaydi.</summary>
    public bool IsErrorReview { get; set; }

    public int Total { get; set; }
    public int Correct { get; set; }
    public int Wrong { get; set; }
    public int Skipped { get; set; }
    public bool Passed { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }

    public List<TestAnswer> Answers { get; set; } = new();

    public double Percent => Total > 0 ? Math.Round(100.0 * Correct / Total, 1) : 0;
}

/// <summary>Urinishdagi bitta savolga berilgan javob.</summary>
public class TestAnswer
{
    public int Id { get; set; }
    public int AttemptId { get; set; }
    public int QuestionId { get; set; }

    /// <summary>Belgilangan variant; null — javob berilmagan.</summary>
    public int? SelectedOptionId { get; set; }

    public bool IsCorrect { get; set; }

    public TestAttempt? Attempt { get; set; }
}

/// <summary>
/// Foydalanuvchining xato savoli. Savolga keyinroq to'g'ri javob berilsa, ResolvedAt to'ldiriladi
/// va savol "Xatolarim" ro'yxatidan chiqadi; yana xato qilinsa — qaytadan faollashadi.
/// </summary>
public class UserMistake
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int QuestionId { get; set; }

    public int WrongCount { get; set; }
    public DateTime LastWrongAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>Test turlari (test-tracker.js dagi qiymatlar bilan bir xil).</summary>
public static class TestTypes
{
    public const string Real = "real";          // /Test — real imtihon (20 savol)
    public const string Q20 = "q20";            // /Testforty
    public const string Q40 = "q40";            // /Testsixty
    public const string Q80 = "q80";            // /Testeighty
    public const string Q100 = "q100";          // /Testhundred
    public const string Q200 = "q200";          // /Testhundredsixty
    public const string Marathon = "marathon";  // /TestAll
    public const string Bilet = "bilet";        // /Bilet/TestPage
    public const string Topic = "topic";        // /Topic/Test
    public const string Mistakes = "mistakes";  // /Cabinet/Practice — xatolar ustida ishlash
    public const string Search = "search";      // /Cabinet/Search — qidirib topilgan savolni alohida ishlash

    public static readonly string[] All = { Real, Q20, Q40, Q80, Q100, Q200, Marathon, Bilet, Topic, Mistakes, Search };

    /// <summary>Imtihonga o'xshash testlar (20 savol, o'tish balli 90%) — tayyorlikni baholashda ishlatiladi.</summary>
    public static readonly string[] ExamLike = { Real, Q20, Bilet };

    public static bool IsValid(string? t) => t != null && All.Contains(t);

    /// <summary>
    /// So'rov manzilidan test turini aniqlaydi (test sahifalarining o'zini o'zgartirmasdan natijani yozib olish uchun).
    /// Test sahifasi bo'lmasa — null.
    /// </summary>
    public static (string Type, string? Ref)? Detect(HttpRequest request)
    {
        var path = (request.Path.Value ?? "").Trim('/').ToLowerInvariant();
        if (path.EndsWith("/index")) path = path[..^"/index".Length];

        return path switch
        {
            "test" => (Real, null),
            "testforty" => (Q20, null),
            "testsixty" => (Q40, null),
            "testeighty" => (Q80, null),
            "testhundred" => (Q100, null),
            "testhundredsixty" => (Q200, null),
            "testall" => (Marathon, null),
            "bilet/testpage" => (Bilet, request.Query["biletNumber"].ToString()),
            "topic/test" => (Topic, request.Query["topic"].ToString()),
            "cabinet/practice" => request.Query.ContainsKey("ids") || request.Query.ContainsKey("saved") ? (Search, null) : (Mistakes, null),
            _ => null,
        };
    }

    /// <summary>Resx kaliti: "testType.q20" va h.k.</summary>
    public static string Key(string type) => "testType." + type;
}
