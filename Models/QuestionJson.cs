/// <summary>
/// Test sahifalari uchun savolning JSON ko'rinishi. Barcha test controllerlari shuni ishlatadi.
/// Tarjima bo'sh bo'lsa, o'rniga o'zbek (lotin) matni qo'yiladi — brauzer hech qachon bo'sh matn olmaydi.
/// </summary>
public static class QuestionJson
{
    private static string Or(string? value, string? fallback) =>
        string.IsNullOrWhiteSpace(value) ? (fallback ?? "") : value;

    public static object From(Question q) => new
    {
        id = q.Id,

        questionUZ = q.QuestionUZ ?? "",
        questionRU = Or(q.QuestionRU, q.QuestionUZ),
        questionUZK = Or(q.QuestionUZK, q.QuestionUZ),

        imageUrl = q.ImageUrl,

        explanationUZ = q.ExplanationUZ,
        explanationRU = Or(q.ExplanationRU, q.ExplanationUZ),
        explanationUZK = Or(q.ExplanationUZK, q.ExplanationUZ),

        options = q.Options
            // ptest variantlari asl tartibda (F1..F5, "yuqoridagilarning hammasi" kabi javoblar joyida qolsin),
            // eski savollar variantlari — aralashtirib
            .OrderBy(o => q.Source == QuestionSources.Ptest ? o.Id : 0)
            .ThenBy(o => q.Source == QuestionSources.Ptest ? Guid.Empty : Guid.NewGuid())
            .Select(o => new
            {
                id = o.Id,
                textUZ = o.OptionUZ ?? "",
                textRU = Or(o.OptionRU, o.OptionUZ),
                textUZK = Or(o.OptionUZK, o.OptionUZ),
                isCorrect = o.IsCorrect
            })
            .ToList()
    };
}
