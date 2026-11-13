using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Test sahifalaridagi yordamchi tugmalar (wwwroot/js/study-tools.js): savolni saqlash va savolga bog'langan
/// video tushuntirish. Saqlanganlar kabinetdagi "Saqlangan savollar" bo'limida ko'rinadi va qayta ishlanadi.
/// </summary>
[Authorize]
public class StudyController : Controller
{
    public const int MaxSaved = 1000;

    private readonly AppDbContext _db;

    public StudyController(AppDbContext db) => _db = db;

    private int Me => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    /// <summary>Savol holati: saqlanganmi va unga bog'langan (e'lon qilingan) videolar — boshlanish vaqti bilan.</summary>
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Info(int id)
    {
        var saved = await _db.SavedQuestions.AnyAsync(s => s.UserId == Me && s.QuestionId == id);
        var videos = await LessonsForQuestionAsync(_db, id);
        return Json(new { saved, videos = videos.Select(v => new { id = v.LessonId, title = v.Title, t = v.Start }) });
    }

    /// <summary>Savolga bog'langan e'lon qilingan videolar (bo'lim vaqti bo'lsa — shu joydan).</summary>
    public static async Task<List<(int LessonId, string Title, int Start)>> LessonsForQuestionAsync(AppDbContext db, int questionId)
    {
        var rows = await db.TopicLessonQuestions.AsNoTracking()
            .Where(x => x.QuestionId == questionId)
            .Join(db.TopicLessons.Where(l => l.IsPublished && l.VideoRef != null), x => x.LessonId, l => l.Id, (x, l) => new { l.Id, l.Title, l.Topic })
            .ToListAsync();
        if (rows.Count == 0) return new();
        var lessonIds = rows.Select(r => r.Id).ToList();
        var starts = await db.TopicLessonChapters.AsNoTracking()
            .Where(c => lessonIds.Contains(c.LessonId) && c.QuestionId == questionId)
            .GroupBy(c => c.LessonId).Select(g => new { g.Key, S = g.Min(c => c.StartSeconds) })
            .ToDictionaryAsync(x => x.Key, x => x.S);
        return rows.Select(r => (r.Id, r.Title ?? r.Topic, starts.GetValueOrDefault(r.Id))).ToList();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int id, bool on)
    {
        var uid = Me;
        var row = await _db.SavedQuestions.FirstOrDefaultAsync(s => s.UserId == uid && s.QuestionId == id);
        if (on && row == null)
        {
            if (!await _db.Questions.AnyAsync(q => q.Id == id)) return NotFound();
            if (await _db.SavedQuestions.CountAsync(s => s.UserId == uid) >= MaxSaved)
                return StatusCode(409, new { error = "limit", max = MaxSaved });
            _db.SavedQuestions.Add(new SavedQuestion { UserId = uid, QuestionId = id, CreatedAt = DateTime.UtcNow });
            try { await _db.SaveChangesAsync(); }
            catch (DbUpdateException) { /* ikki marta bosildi — allaqachon saqlangan */ }
        }
        else if (!on && row != null)
        {
            _db.SavedQuestions.Remove(row);
            await _db.SaveChangesAsync();
        }
        var count = await _db.SavedQuestions.CountAsync(s => s.UserId == uid);
        return Json(new { saved = on, count });
    }

    /// <summary>Saqlanganlarning hammasini (yoki tanlanganlarini) olib tashlash.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Clear(int[]? ids)
    {
        var q = _db.SavedQuestions.Where(s => s.UserId == Me);
        if (ids is { Length: > 0 }) q = q.Where(s => ids.Contains(s.QuestionId));
        var n = await q.ExecuteDeleteAsync();
        TempData["SvMsg"] = n;
        return RedirectToAction("Saved", "Cabinet");
    }
}
