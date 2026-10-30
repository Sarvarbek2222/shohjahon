using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using propro.Localization;

/// <summary>
/// Super admin tahlili: umumiy statistika, barcha o'quvchilar natijalari, eng qiyin savollar.
/// O'quvchi sahifasida — o'quvchi o'z kabinetida ko'radigan hamma narsa.
/// </summary>
[Authorize(Roles = Roles.SuperAdmin)]
public class AnalyticsController : Controller
{
    private readonly AppDbContext _db;
    private readonly StatsService _stats;
    private readonly AiAnalysisService _ai;

    public AnalyticsController(AppDbContext db, StatsService stats, AiAnalysisService ai)
    {
        _db = db;
        _stats = stats;
        _ai = ai;
    }

    public async Task<IActionResult> Index([FromServices] PresenceTracker presence, [FromServices] AnalyticsSnapshot snapshot, bool refresh = false)
    {
        ViewBag.Presence = presence;
        // Ma'lumot fonda tayyorlab qo'yiladi — sahifa darhol ochiladi ("Yangilash" — hozir qayta hisoblash)
        var data = await snapshot.GetAsync(force: refresh);
        ViewBag.BuiltAt = data.BuiltAt;
        var model = new AnalyticsModel { Platform = data.Platform, Students = data.Students };
        return View(model);
    }

    public async Task<IActionResult> Student(int id, [FromServices] PresenceTracker presence, string? type, int page = 1)
    {
        ViewBag.Presence = presence;
        var stats = await _stats.GetUserStatsAsync(id);
        if (stats == null) return NotFound();

        var model = await CabinetModel.CreateAsync(_db, stats, isAdminView: true, aiEnabled: _ai.IsEnabled, withMistakeQuestions: true);
        CabinetController.ApplyResultsFilter(model, type, page);
        return View(model);
    }

    public async Task<IActionResult> Attempt(int id)
    {
        var detail = await CabinetController.LoadAttemptAsync(_db, id, ownerId: null);
        if (detail == null) return NotFound();
        return View(detail);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ai(int id, CancellationToken ct)
    {
        if (!_ai.IsEnabled) return NotFound();
        var stats = await _stats.GetUserStatsAsync(id);
        if (stats == null) return NotFound();
        var text = await _ai.AnalyzeAsync(stats, Lang.Current(HttpContext), ct);
        return text == null ? StatusCode(502) : Json(new { text });
    }
}

public class AnalyticsModel
{
    public PlatformStats Platform { get; set; } = new();
    public List<UserStats> Students { get; set; } = new();
}
