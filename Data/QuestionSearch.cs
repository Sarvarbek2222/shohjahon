using System.Globalization;
using System.Text;
using propro.Localization;

/// <summary>
/// Savollarni matni bo'yicha qidirish (foydalanuvchi kabineti → "Savol qidirish").
///
/// Uchala tildagi savol matni (lotin, kirill, rus) va javob variantlari bitta ko'rinishga keltiriladi:
/// kirill → lotin, kichik harf, apostroflar (o', o‘, oʻ ...) va tinish belgilari olib tashlanadi.
/// Shuning uchun "ozbekiston", "O'zbekiston" va "Ўзбекистон" bir xil topiladi.
/// Indeks xotirada saqlanadi va QuestionBank yangilanganda (admin savolni o'zgartirsa) qayta quriladi.
/// </summary>
public class QuestionSearch
{
    public const int MaxResults = 30;

    private readonly QuestionBank _bank;
    private readonly object _lock = new();
    private IReadOnlyList<Question>? _source;
    private Entry[] _index = Array.Empty<Entry>();

    public QuestionSearch(QuestionBank bank) => _bank = bank;

    private sealed record Entry(Question Question, string Text, string Words, string Options, string Topic);

    public sealed record Hit(Question Question, int Score, bool InOptionsOnly);

    public async Task<(List<Hit> Hits, int Total)> SearchAsync(string? query, int take = MaxResults)
    {
        var index = await GetIndexAsync();
        var q = Normalize(query);
        if (q.Length == 0) return (new List<Hit>(), 0);

        // "#123" yoki faqat raqam — savol raqami (Id) bo'yicha ham qidiriladi
        var raw = (query ?? "").Trim().TrimStart('#', '№');
        int? idQuery = int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2 || t.All(char.IsDigit))
            .Distinct()
            .ToArray();
        if (tokens.Length == 0 && idQuery == null) return (new List<Hit>(), 0);

        var hits = new List<Hit>();
        foreach (var e in index)
        {
            if (idQuery == e.Question.Id)
            {
                hits.Add(new Hit(e.Question, 10_000, false));
                continue;
            }
            if (tokens.Length == 0) continue;

            bool allInText = tokens.All(t => e.Text.Contains(t, StringComparison.Ordinal));
            bool allAnywhere = allInText || tokens.All(t =>
                e.Text.Contains(t, StringComparison.Ordinal) ||
                e.Options.Contains(t, StringComparison.Ordinal) ||
                e.Topic.Contains(t, StringComparison.Ordinal));
            if (!allAnywhere) continue;

            int score = 0;
            if (allInText)
            {
                score += 50;
                if (tokens.Length > 1 && e.Text.Contains(q, StringComparison.Ordinal)) score += 100; // butun ibora
                foreach (var t in tokens)
                    score += e.Words.Contains(" " + t, StringComparison.Ordinal) ? 10 : 4; // so'z boshi ustun
                if (e.Text.StartsWith(tokens[0], StringComparison.Ordinal)) score += 15;
            }
            else
            {
                foreach (var t in tokens)
                    score += e.Text.Contains(t, StringComparison.Ordinal) ? 4 : 1;
            }
            score -= Math.Min(20, e.Text.Length / 60); // qisqa (aniqroq) savollar biroz yuqoriroq

            hits.Add(new Hit(e.Question, score, !allInText));
        }

        var ordered = hits.OrderByDescending(h => h.Score).ThenBy(h => h.Question.Id).ToList();
        return (ordered.Take(take).ToList(), ordered.Count);
    }

    private async Task<Entry[]> GetIndexAsync()
    {
        // Faqat testlarda ishlatiladigan savollar (test bazasi + mavzular) — eski bazadan qolganlar qidiruvda chiqmaydi
        var pool = await _bank.TestPoolAsync();
        var topicPool = await _bank.TopicPoolAsync();
        lock (_lock)
        {
            if (!ReferenceEquals(pool, _source))
            {
                _index = pool.Concat(topicPool).DistinctBy(q => q.Id).OrderBy(q => q.Id).Select(Build).ToArray();
                _source = pool;
            }
            return _index;
        }
    }

    private static Entry Build(Question q)
    {
        var text = Normalize(string.Join(" ", q.QuestionUZ, q.QuestionUZK, q.QuestionRU));
        var options = Normalize(string.Join(" ", q.Options.SelectMany(o => new[] { o.OptionUZ, o.OptionUZK, o.OptionRU })));
        var topic = Normalize(q.Topic);
        return new Entry(q, text, " " + text, options, topic);
    }

    /// <summary>Qidiruv uchun yagona ko'rinish: lotin, kichik harf, apostrof va tinish belgilarisiz.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var latin = Translit.ToLatin(s).ToLowerInvariant();

        var sb = new StringBuilder(latin.Length);
        bool space = true;
        foreach (var ch in latin.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue; // ё → е kabi belgilar
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                space = false;
            }
            else if (IsApostrophe(ch))
            {
                // o'zbek apostrofi so'zni bo'lmaydi: "o'zbek" → "ozbek"
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }
        return sb.ToString().Trim();
    }

    private static bool IsApostrophe(char ch) =>
        ch is '\'' or '`' or '´' or '‘' or '’' or 'ʻ' or 'ʼ' or 'ʹ' or '′';
}
