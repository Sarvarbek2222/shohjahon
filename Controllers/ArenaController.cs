using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// "Qiyinlar arenasi" — kun, hafta va oyning eng qiyin savollari (admin ham, o'quvchilar ham ko'radi).
/// O'quvchi har bir savol bo'yicha o'z holatini (yechganmi) ko'radi va hammasini bitta chaqiriq sifatida ishlaydi.
/// </summary>
[Authorize]
public class ArenaController : Controller
{
    private readonly AppDbContext _db;
    private readonly QuestionBank _bank;
    private readonly IMemoryCache _cache;

    public ArenaController(AppDbContext db, QuestionBank bank, IMemoryCache cache)
    {
        _db = db;
        _bank = bank;
        _cache = cache;
    }

    public async Task<IActionResult> Index(string? p, [FromServices] IServiceScopeFactory scopes, [FromServices] ILogger<ArenaController> log)
    {
        p = HardQuestions.Periods.Contains(p) ? p! : "week";
        // Natija fonda tayyorlanadi — sahifa hech qachon kutmaydi; hali tayyor bo'lmasa "tayyorlanmoqda" ko'rsatiladi
        var set = HardQuestions.Peek(_cache, scopes, log, p);
        if (set == null)
        {
            ViewBag.Mine = new Dictionary<int, bool>();
            ViewBag.Preparing = true;
            return View(new HardSet(p, DateTime.UtcNow, new(), 0, DateTime.UtcNow));
        }
        var mine = new Dictionary<int, bool>(); // savol → oxirgi javobi to'g'rimi
        if (!User.IsInRole(Roles.SuperAdmin) && int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) && set.Items.Count > 0)
        {
            var ids = set.Items.Select(i => i.Q.Id).ToList();
            var rows = await _db.TestAnswers.AsNoTracking()
                .Where(a => ids.Contains(a.QuestionId) && a.SelectedOptionId != null && a.Attempt!.UserId == uid)
                .Select(a => new { a.QuestionId, a.IsCorrect, a.Attempt!.FinishedAt, a.Id })
                .ToListAsync();
            mine = rows.GroupBy(r => r.QuestionId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.FinishedAt).ThenByDescending(r => r.Id).First().IsCorrect);
        }
        ViewBag.Mine = mine;
        return View(set);
    }
}
