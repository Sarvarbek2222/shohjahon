using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using propro.Localization;

/// <summary>
/// Foydalanuvchining shaxsiy kabineti: natijalar, xatolar, tahlil, xatolar ustida ishlash.
/// Super admin kabinetga emas — admin paneliga (Tahlil bo'limi) yo'naltiriladi.
/// </summary>
[Authorize]
public class CabinetController : Controller
{
    public const int PageSize = 20;

    private readonly AppDbContext _db;
    private readonly StatsService _stats;
    private readonly ResultsService _results;
    private readonly AiAnalysisService _ai;
    private readonly QuestionSearch _search;
    private readonly QuestionBank _bank;

    public CabinetController(AppDbContext db, StatsService stats, ResultsService results, AiAnalysisService ai, QuestionSearch search, QuestionBank bank)
    {
        _bank = bank;
        _db = db;
        _stats = stats;
        _results = results;
        _ai = ai;
        _search = search;
    }

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool IsAdmin => User.IsInRole(Roles.SuperAdmin);

    public async Task<IActionResult> Index()
    {
        if (IsAdmin) return RedirectToAction("Index", "Analytics");
        return View(await BuildAsync(UserId));
    }

    public async Task<IActionResult> Results(string? type, int page = 1)
    {
        if (IsAdmin) return RedirectToAction("Index", "Analytics");
        var model = await BuildAsync(UserId);
        ApplyResultsFilter(model, type, page);
        return View(model);
    }

    public async Task<IActionResult> Attempt(int id)
    {
        var detail = await LoadAttemptAsync(_db, id, UserId);
        if (detail == null) return NotFound();
        return View(detail);
    }

    public async Task<IActionResult> Mistakes()
    {
        if (IsAdmin) return RedirectToAction("Index", "Analytics");
        return View(await BuildAsync(UserId, withMistakeQuestions: true));
    }

    public async Task<IActionResult> Analysis()
    {
        if (IsAdmin) return RedirectToAction("Index", "Analytics");
        return View(await BuildAsync(UserId));
    }

    /// <summary>
    /// Xatolarni qayta ishlash — test sahifalari bilan bir xil ko'rinish.
    /// ids berilsa — "Savol qidirish"dan tanlangan savol(lar)ni xuddi shu ko'rinishda alohida ishlash.
    /// </summary>
    public async Task<IActionResult> Practice(string? ids, string? q, bool saved = false)
    {
        // Saqlangan savollarni ishlash
        if (saved)
        {
            if (!await _db.SavedQuestions.AnyAsync(s => s.UserId == UserId)) return RedirectToAction(nameof(Saved));
            ViewBag.SavedMode = true;
            ViewBag.BackUrl = Url.Action(nameof(Saved));
            return View();
        }

        // "ids=" bo'sh bo'lsa ham qidiruv rejimi (model binding bo'sh qiymatni null qiladi)
        if (Request.Query.ContainsKey("ids"))
        {
            var list = ParseIds(ids);
            if (list.Count == 0) return RedirectToAction(nameof(Search), new { q });
            ViewBag.Ids = string.Join(",", list);
            ViewBag.BackUrl = Url.Action(nameof(Search), new { q = string.IsNullOrWhiteSpace(q) ? null : q.Trim() });
            return View();
        }

        bool any = await _db.UserMistakes.AnyAsync(m => m.UserId == UserId && m.ResolvedAt == null);
        if (!any) return RedirectToAction(nameof(Mistakes));
        return View();
    }

    /// <summary>Shaxsiy kunlik tahlil (standart — kecha; d=yyyy-MM-dd — boshqa kun, oxirgi 14 kun ichida).</summary>
    public async Task<IActionResult> Daily(string? d, [FromServices] AnalyticsSnapshot snapshot)
    {
        var day = PickDay(d);
        ViewBag.Day = day;
        return View(await PersonalReport.BuildAsync(_db, _bank, UserId, day, (await snapshot.GetAsync()).League));
    }

    public static DateTime PickDay(string? d)
    {
        var today = PersonalReport.LocalToday();
        return DateTime.TryParseExact(d, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var x) && x <= today && x >= today.AddDays(-13)
            ? x.Date : today.AddDays(-1);
    }

    /// <summary>Reyting: barcha o'quvchilar (o'quvchi o'zini belgilangan holda ko'radi).</summary>
    public async Task<IActionResult> Rating([FromServices] AnalyticsSnapshot snapshot) => View((await snapshot.GetAsync()).League);

    /// <summary>Saqlangan savollar (oxirgi saqlangani birinchi).</summary>
    public async Task<IActionResult> Saved()
    {
        var rows = await _db.SavedQuestions.AsNoTracking().Where(s => s.UserId == UserId)
            .OrderByDescending(s => s.CreatedAt).Select(s => new { s.QuestionId, s.CreatedAt }).ToListAsync();
        var byId = (await _bank.AllAsync()).ToDictionary(x => x.Id);
        var mistakes = (await _db.UserMistakes.AsNoTracking().Where(m => m.UserId == UserId && m.ResolvedAt == null)
            .Select(m => m.QuestionId).ToListAsync()).ToHashSet();
        ViewBag.Mistakes = mistakes;
        return View(rows.Where(r => byId.ContainsKey(r.QuestionId)).Select(r => new SavedRow(byId[r.QuestionId], r.CreatedAt)).ToList());
    }

    [HttpGet]
    public async Task<IActionResult> GetSavedQuestions()
    {
        var ids = await _db.SavedQuestions.AsNoTracking().Where(s => s.UserId == UserId)
            .OrderByDescending(s => s.CreatedAt).Select(s => s.QuestionId).ToListAsync();
        var byId = (await _bank.AllAsync()).ToDictionary(x => x.Id);
        return Json(ids.Where(byId.ContainsKey).Select(id => QuestionJson.From(byId[id])));
    }

    /// <summary>Qidiruvdan tanlangan savollar — berilgan tartibda (ko'pi bilan QuestionSearch.MaxResults ta).</summary>
    [HttpGet]
    public async Task<IActionResult> GetQuestionsByIds(string? ids)
    {
        var list = ParseIds(ids);
        var byId = (await _bank.AllAsync()).ToDictionary(x => x.Id);
        return Json(list.Where(byId.ContainsKey).Select(id => QuestionJson.From(byId[id])));
    }

    private static List<int> ParseIds(string? ids) =>
        (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0)
            .Where(n => n > 0)
            .Distinct()
            .Take(QuestionSearch.MaxResults)
            .ToList();

    /// <summary>Savolni matni bo'yicha qidirib, shu savolning o'zini alohida ishlash.</summary>
    public async Task<IActionResult> Search(string? q)
    {
        ViewBag.Query = q?.Trim() ?? "";
        ViewData["ActiveMistakes"] = await _db.UserMistakes.CountAsync(m => m.UserId == UserId && m.ResolvedAt == null);
        return View();
    }

    /// <summary>Qidiruv natijalari (sahifa yozilgan sari so'raydi). Javob variantlari ham shu yerda — alohida ishlash uchun.</summary>
    [HttpGet]
    public async Task<IActionResult> SearchQuestions(string? q)
    {
        q = q?.Trim() ?? "";
        if (q.Length > 200) q = q[..200];

        var (hits, total) = await _search.SearchAsync(q);
        var ids = hits.Select(h => h.Question.Id).ToList();
        var mistakes = (await _db.UserMistakes.AsNoTracking()
                .Where(m => m.UserId == UserId && m.ResolvedAt == null && ids.Contains(m.QuestionId))
                .Select(m => m.QuestionId)
                .ToListAsync())
            .ToHashSet();

        return Json(new
        {
            query = q,
            total,
            items = hits.Select(h => new
            {
                question = QuestionJson.From(h.Question),
                topicUZ = Topics.Display(h.Question.Topic, Lang.Uz),
                topicRU = Topics.Display(h.Question.Topic, Lang.Ru),
                topicUZK = Topics.Display(h.Question.Topic, Lang.Uzk),
                inOptions = h.InOptionsOnly,
                inMistakes = mistakes.Contains(h.Question.Id),
            }),
        });
    }

    [HttpGet]
    public async Task<IActionResult> GetMistakeQuestions()
    {
        var questions = await _results.GetMistakeQuestionsAsync(UserId);
        return Json(questions.Select(QuestionJson.From));
    }

    /// <summary>Test sahifasidan (test-tracker.js) natijani qabul qiladi.</summary>
    [HttpPost]
    [IgnoreAntiforgeryToken] // faqat application/json qabul qilinadi (oddiy HTML forma yubora olmaydi) + SameSite=Lax cookie
    [Consumes("application/json")]
    public async Task<IActionResult> SaveResult([FromBody] SaveResultRequest request)
    {
        var result = await _results.SaveAsync(UserId, request);
        return result == null ? BadRequest() : Json(result);
    }

    /// <summary>AI tahlili (Claude). Sozlanmagan bo'lsa 404.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ai(CancellationToken ct)
    {
        if (!_ai.IsEnabled) return NotFound();
        var stats = await _stats.GetUserStatsAsync(UserId);
        if (stats == null) return NotFound();
        var text = await _ai.AnalyzeAsync(stats, Lang.Current(HttpContext), ct);
        return text == null ? StatusCode(502) : Json(new { text });
    }

    // ───────────── umumiy yordamchilar (admin paneli ham ishlatadi) ─────────────

    private async Task<CabinetModel> BuildAsync(int userId, bool withMistakeQuestions = false)
    {
        var stats = await _stats.GetUserStatsAsync(userId) ?? throw new InvalidOperationException("User not found");
        return await CabinetModel.CreateAsync(_db, stats, isAdminView: false, aiEnabled: _ai.IsEnabled, withMistakeQuestions);
    }

    public static void ApplyResultsFilter(CabinetModel model, string? type, int page)
    {
        var list = model.Stats.Attempts.AsEnumerable();
        if (TestTypes.IsValid(type)) list = list.Where(a => a.TestType == type);
        var filtered = list.ToList();

        model.FilterType = TestTypes.IsValid(type) ? type : null;
        model.TotalPages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)PageSize));
        model.Page = Math.Clamp(page, 1, model.TotalPages);
        model.PageAttempts = filtered.Skip((model.Page - 1) * PageSize).Take(PageSize).ToList();
    }

    public static async Task<AttemptDetail?> LoadAttemptAsync(AppDbContext db, int attemptId, int? ownerId)
    {
        var attempt = await db.TestAttempts.AsNoTracking().Include(a => a.Answers)
            .FirstOrDefaultAsync(a => a.Id == attemptId && (ownerId == null || a.UserId == ownerId));
        if (attempt == null) return null;

        var ids = attempt.Answers.Select(a => a.QuestionId).ToList();
        var questions = await db.Questions.AsNoTracking().Include(q => q.Options)
            .Where(q => ids.Contains(q.Id)).ToDictionaryAsync(q => q.Id);
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == attempt.UserId);

        return new AttemptDetail(attempt, questions, user, IsAdminView: ownerId == null);
    }
}

/// <summary>Kabinet sahifalari modeli (foydalanuvchi va admin uchun umumiy).</summary>
public class CabinetModel
{
    public UserStats Stats { get; set; } = null!;
    public bool IsAdminView { get; set; }
    public bool AiEnabled { get; set; }

    /// <summary>Savollar (xatolar ro'yxati va so'nggi urinishlar uchun).</summary>
    public Dictionary<int, Question> Questions { get; set; } = new();

    // Natijalar sahifasi
    public List<TestAttempt> PageAttempts { get; set; } = new();
    public string? FilterType { get; set; }
    public int Page { get; set; } = 1;
    public int TotalPages { get; set; } = 1;

    /// <summary>Havolalar uchun: urinish tafsilotlari manzili.</summary>
    public string AttemptUrl(int id) => IsAdminView ? $"/Analytics/Attempt/{id}" : $"/Cabinet/Attempt/{id}";

    public static async Task<CabinetModel> CreateAsync(AppDbContext db, UserStats stats, bool isAdminView, bool aiEnabled, bool withMistakeQuestions)
    {
        var model = new CabinetModel { Stats = stats, IsAdminView = isAdminView, AiEnabled = aiEnabled };
        model.PageAttempts = stats.Attempts.Take(CabinetController.PageSize).ToList();
        model.TotalPages = Math.Max(1, (int)Math.Ceiling(stats.Attempts.Count / (double)CabinetController.PageSize));

        if (withMistakeQuestions && stats.Mistakes.Count > 0)
        {
            var ids = stats.Mistakes.Select(m => m.QuestionId).ToList();
            model.Questions = await db.Questions.AsNoTracking().Include(q => q.Options)
                .Where(q => ids.Contains(q.Id)).ToDictionaryAsync(q => q.Id);
        }
        return model;
    }
}

public record AttemptDetail(TestAttempt Attempt, Dictionary<int, Question> Questions, AppUser User, bool IsAdminView);

/// <summary>Urinishlar jadvali partial'i uchun.</summary>
public record AttemptsTable(CabinetModel Cab, IList<TestAttempt> Items, bool Compact);

/// <summary>Rang: tayyorlik/foizga qarab.</summary>
public static class Score
{
    public static string Color(double percent) => percent >= 90 ? "var(--ca-ok)" : percent >= 60 ? "var(--ca-warn)" : "var(--ca-brand)";
    public static string BarClass(double percent) => percent >= 90 ? "ok" : percent >= 60 ? "warn" : "";
    public static string Pct(double fraction) => Math.Round(fraction * 100).ToString("0");
}

public record SavedRow(Question Question, DateTime SavedAt);
