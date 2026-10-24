using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

/// <summary>
/// Savol qaysi bazadan kelgani. Bitta jadvalda ikki baza saqlanadi:
///   Old   — eski savollar: faqat "Mavzular" bo'limi uchun;
///   Ptest — ptest.uz biletlari: biletlar va qolgan barcha test turlari uchun.
/// </summary>
public static class QuestionSources
{
    public const string Old = "old";
    public const string Ptest = "ptest";
}

public class Question
{
    public int Id { get; set; }

    public string QuestionUZ { get; set; } = "";  // O'zbek (lotin) — majburiy, standart til
    public string? QuestionRU { get; set; }        // Rus
    public string? QuestionUZK { get; set; }       // O'zbek (kirill)

    // Mavzu (kategoriya) — masalan "Огоҳлантирувчи белгилар"
    [MaxLength(255)]
    public string? Topic { get; set; }
    // Rasm xossasi
    // ----------------------------
    public string? ImageUrl { get; set; }  // nullable, rasm majburiy emas

    [NotMapped]
    public IFormFile? ImageFile { get; set; }  // fayl upload uchun
    public string? ExplanationUZ { get; set; }
    public string? ExplanationUZK { get; set; }
    public string? ExplanationRU { get; set; }

    // Manba va bilet joylashuvi (QuestionSources). Admin qo'shgan yangi savollar asosiy bazaga (ptest) tushadi:
    // oxirgi biletga joylanadi (to'lgan bo'lsa — yangi bilet), mavzusi QuestionTopics ga yoziladi (BiletPlacement).
    [MaxLength(16)]
    public string Source { get; set; } = QuestionSources.Ptest;
    public int? BiletNumber { get; set; }  // ptest: 1..66
    public int? BiletOrder { get; set; }   // ptest: biletdagi tartib raqami 1..20

    public List<Option> Options { get; set; } = new List<Option>();

}
