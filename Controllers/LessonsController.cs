using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using propro.Localization;

/// <summary>
/// Admin: video darslar. Admin mavzuga kiradi → mavzu savollari ro'yxati → kerakli savollarni belgilab, ular uchun
/// video qo'shadi (yoki mavjud videoga qo'shadi). Bitta video bir nechta mavzudagi savollarni tushuntirishi mumkin
/// (m ta mavzudan n ta savol). Har bir savolga videodagi boshlanish vaqti berilsa — o'quvchi savolni ishlayotganda
/// "🎬 Video" tugmasi videoni aynan shu joydan ochadi.
/// </summary>
[Authorize(Roles = Roles.SuperAdmin)]
public class LessonsController : Controller
{
    private readonly AppDbContext _db;
    private readonly QuestionBank _bank;
    private readonly VideoUploads _videos;

    public LessonsController(AppDbContext db, QuestionBank bank, VideoUploads videos)
    {
        _db = db;
        _bank = bank;
        _videos = videos;
    }

    /// <summary>Mavzular: har birida nechta savol, nechtasi videoda tushuntirilgan, nechta video.</summary>
    public async Task<IActionResult> Index()
    {
        var byTopic = await _bank.ByTopicAsync();
        var links = await _db.TopicLessonQuestions.AsNoTracking().Select(x => new { x.LessonId, x.QuestionId }).ToListAsync();
        var lessons = await _db.TopicLessons.AsNoTracking().ToListAsync();
        var progress = await _db.TopicLessonProgress.AsNoTracking()
            .GroupBy(p => p.LessonId)
            .Select(g => new { LessonId = g.Key, Viewers = g.Count(), Seconds = g.Sum(p => p.WatchSeconds) })
            .ToListAsync();

        var covered = links.Select(l => l.QuestionId).ToHashSet();
        var rows = Topics.All.Select(t =>
        {
            var qs = byTopic.TryGetValue(t, out var list) ? list.Select(q => q.Id).ToHashSet() : new HashSet<int>();
            var lessonIds = links.Where(l => qs.Contains(l.QuestionId)).Select(l => l.LessonId)
                .Concat(lessons.Where(l => l.Topic == t).Select(l => l.Id)).Distinct().ToList();
            return new LessonTopicRow(t, qs.Count, qs.Count(covered.Contains), lessonIds.Count,
                lessons.Count(l => lessonIds.Contains(l.Id) && l.IsPublished && l.HasVideo));
        }).ToList();

        ViewBag.Totals = new LessonTotals(lessons.Count(l => l.HasVideo), lessons.Count(l => l.IsPublished && l.HasVideo),
            progress.Sum(p => p.Viewers), progress.Sum(p => (long)p.Seconds), covered.Count);
        return View(rows);
    }

    /// <summary>Mavzu ichi: savollar qatori (har biri qaysi videoda tushuntirilgani bilan) va shu mavzu videolari.</summary>
    public async Task<IActionResult> Topic(string topic)
    {
        if (!Topics.All.Contains(topic)) return RedirectToAction(nameof(Index));
        var questions = await _bank.TopicQuestionsAsync(topic);
        var ids = questions.Select(q => q.Id).ToList();
        var links = await _db.TopicLessonQuestions.AsNoTracking().Where(x => ids.Contains(x.QuestionId)).ToListAsync();
        var lessonIds = links.Select(l => l.LessonId).Distinct().ToList();
        var lessons = await _db.TopicLessons.AsNoTracking()
            .Where(l => l.Topic == topic || lessonIds.Contains(l.Id)).OrderByDescending(l => l.UpdatedAt).ToListAsync();
        var allIds = lessons.Select(l => l.Id).ToList();
        var counts = await _db.TopicLessonQuestions.AsNoTracking().Where(x => allIds.Contains(x.LessonId))
            .GroupBy(x => x.LessonId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var titles = lessons.ToDictionary(l => l.Id, l => l.Title ?? Topics.Display(l.Topic, Lang.Uz));

        return View(new LessonTopicModel
        {
            Topic = topic,
            Questions = questions.Select((q, i) => new LessonPickQuestion(q.Id, i + 1, q.QuestionUZ, q.ImageUrl, q.BiletNumber,
                links.Where(l => l.QuestionId == q.Id).Select(l => new LessonRef(l.LessonId, titles.GetValueOrDefault(l.LessonId) ?? "#" + l.LessonId)).ToList())).ToList(),
            Lessons = lessons.Select(l => new LessonCard(l, counts.GetValueOrDefault(l.Id))).ToList(),
        });
    }

    /// <summary>Belgilangan savollarni mavjud videoga qo'shish.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Attach(int lessonId, int[]? q)
    {
        var lesson = await _db.TopicLessons.FirstOrDefaultAsync(l => l.Id == lessonId);
        if (lesson == null) return NotFound();
        var valid = (await _bank.AllAsync()).Select(x => x.Id).ToHashSet();
        var have = (await _db.TopicLessonQuestions.Where(x => x.LessonId == lessonId).Select(x => x.QuestionId).ToListAsync()).ToHashSet();
        var order = have.Count;
        foreach (var id in (q ?? Array.Empty<int>()).Distinct().Where(id => valid.Contains(id) && !have.Contains(id)))
            _db.TopicLessonQuestions.Add(new TopicLessonQuestion { LessonId = lessonId, QuestionId = id, SortOrder = order++ });
        lesson.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        TempData["LsMsg"] = "ls.attached";
        return RedirectToAction(nameof(Edit), new { id = lessonId });
    }

    /// <summary>Video tahrirlash. id yo'q — yangi video: topic (asosiy mavzu) va q (oldindan belgilangan savollar).</summary>
    public async Task<IActionResult> Edit(int? id, string? topic, string? q)
    {
        TopicLesson? lesson;
        List<int> picked;
        if (id is int lid)
        {
            lesson = await _db.TopicLessons.AsNoTracking().Include(l => l.Chapters).FirstOrDefaultAsync(l => l.Id == lid);
            if (lesson == null) return RedirectToAction(nameof(Index));
            picked = await _db.TopicLessonQuestions.AsNoTracking().Where(x => x.LessonId == lid).OrderBy(x => x.SortOrder).Select(x => x.QuestionId).ToListAsync();
        }
        else
        {
            if (topic == null || !Topics.All.Contains(topic)) return RedirectToAction(nameof(Index));
            lesson = new TopicLesson { Topic = topic };
            picked = (q ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).Distinct().Take(500).ToList();
        }
        return View(await BuildEditModelAsync(lesson, picked));
    }

    private async Task<LessonEditModel> BuildEditModelAsync(TopicLesson lesson, List<int> picked)
    {
        var all = await _bank.AllAsync();
        var byId = all.ToDictionary(q => q.Id);
        var byTopic = await _bank.ByTopicAsync();
        var numberInTopic = byTopic.SelectMany(kv => kv.Value.Select((q, i) => (q.Id, N: i + 1))).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().N);
        var start = lesson.Chapters.Where(c => c.QuestionId != null).GroupBy(c => c.QuestionId!.Value).ToDictionary(g => g.Key, g => g.Min(c => c.StartSeconds));
        return new LessonEditModel
        {
            Lesson = lesson,
            Questions = picked.Where(byId.ContainsKey).Select(id =>
            {
                var q = byId[id];
                return new LessonEditQuestion(q.Id, numberInTopic.GetValueOrDefault(q.Id), q.QuestionUZ, q.ImageUrl, q.Topic?.Trim(),
                    start.TryGetValue(q.Id, out var s) ? s : null);
            }).ToList(),
            Extra = lesson.Chapters.Where(c => c.QuestionId == null).OrderBy(c => c.StartSeconds).ToList(),
            TopicCounts = Topics.All.Where(byTopic.ContainsKey).Select(t => new TopicCount(t, byTopic[t].Count)).ToList(),
        };
    }

    /// <summary>Savol tanlagich uchun: mavzu savollari (raqami, matni, rasmi, bileti).</summary>
    [HttpGet]
    public async Task<IActionResult> TopicQuestions(string topic)
    {
        if (!Topics.All.Contains(topic)) return Json(Array.Empty<object>());
        var list = await _bank.TopicQuestionsAsync(topic);
        return Json(list.Select((q, i) => new { id = q.Id, n = i + 1, text = q.QuestionUZ, img = q.ImageUrl, bilet = q.BiletNumber, topic = q.Topic?.Trim() }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int id, string topic, string? title, string? description, string videoKind, string? videoRef,
        string? youTubeUrl, int durationSeconds, bool isPublished,
        int[]? chQuestion, string[]? chTime, string[]? xTitle, string[]? xTime)
    {
        if (!Topics.All.Contains(topic)) return RedirectToAction(nameof(Index));

        var lesson = id > 0 ? await _db.TopicLessons.Include(l => l.Chapters).FirstOrDefaultAsync(l => l.Id == id) : null;
        if (id > 0 && lesson == null) return NotFound();
        var isNew = lesson == null;
        lesson ??= new TopicLesson { CreatedAt = DateTime.UtcNow };
        lesson.Topic = topic;
        var wasPublished = !isNew && lesson.IsPublished && lesson.HasVideo;
        var oldFile = lesson.VideoKind == VideoKinds.File ? lesson.VideoRef : null;

        lesson.Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim()[..Math.Min(title.Trim().Length, 200)];
        lesson.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        if (videoKind == VideoKinds.YouTube)
        {
            var yt = VideoUploads.ParseYouTubeId(youTubeUrl);
            if (yt == null && !string.IsNullOrWhiteSpace(youTubeUrl))
            {
                TempData["LsErr"] = "ls.errYouTube";
                return isNew ? RedirectToAction(nameof(Topic), new { topic }) : RedirectToAction(nameof(Edit), new { id });
            }
            lesson.VideoKind = VideoKinds.YouTube;
            lesson.VideoRef = yt;
        }
        else
        {
            lesson.VideoKind = VideoKinds.File;
            // Faqat shu sayt yuklagan fayl nomi (32 belgili hex + kengaytma)
            lesson.VideoRef = !string.IsNullOrWhiteSpace(videoRef) && Regex.IsMatch(videoRef, "^[a-f0-9]{32}\\.(mp4|webm|m4v|mov)$") ? videoRef : null;
        }
        if (oldFile != null && (lesson.VideoKind != VideoKinds.File || lesson.VideoRef != oldFile)) _videos.Delete(oldFile);

        lesson.DurationSeconds = Math.Max(0, durationSeconds);
        lesson.IsPublished = isPublished && lesson.HasVideo;
        lesson.UpdatedAt = DateTime.UtcNow;

        // Savollar (istalgan mavzudan) + ixtiyoriy boshlanish vaqti; qo'shimcha bo'limlar (kirish, xulosa...)
        var valid = (await _bank.AllAsync()).Select(q => q.Id).ToHashSet();
        var picked = new List<int>();
        var chapters = new List<TopicLessonChapter>();
        for (int i = 0; chQuestion != null && i < chQuestion.Length && picked.Count < 500; i++)
        {
            var qid = chQuestion[i];
            if (!valid.Contains(qid) || picked.Contains(qid)) continue;
            picked.Add(qid);
            var sec = ParseTime(chTime != null && i < chTime.Length ? chTime[i] : null);
            if (sec != null) chapters.Add(new TopicLessonChapter { QuestionId = qid, StartSeconds = sec.Value });
        }
        for (int i = 0; xTitle != null && i < xTitle.Length; i++)
        {
            var sec = ParseTime(xTime != null && i < xTime.Length ? xTime[i] : null);
            if (sec != null && !string.IsNullOrWhiteSpace(xTitle[i]))
                chapters.Add(new TopicLessonChapter { Title = xTitle[i].Trim()[..Math.Min(xTitle[i].Trim().Length, 300)], StartSeconds = sec.Value });
        }
        chapters = chapters.OrderBy(c => c.StartSeconds).ToList();
        for (int i = 0; i < chapters.Count; i++) chapters[i].SortOrder = i;

        lesson.Chapters.Clear();
        lesson.Chapters.AddRange(chapters);
        if (isNew) _db.TopicLessons.Add(lesson);
        await _db.SaveChangesAsync();

        var oldLinks = await _db.TopicLessonQuestions.Where(x => x.LessonId == lesson.Id).ToListAsync();
        _db.TopicLessonQuestions.RemoveRange(oldLinks);
        _db.TopicLessonQuestions.AddRange(picked.Select((qid, i) => new TopicLessonQuestion { LessonId = lesson.Id, QuestionId = qid, SortOrder = i }));
        await _db.SaveChangesAsync();

        // Dars birinchi marta e'lon qilindi — o'quvchilarga bildirishnoma
        if (lesson.IsPublished && !wasPublished)
        {
            await HttpContext.RequestServices.GetRequiredService<NotificationService>().NotifyStudentsAsync("lessonNew",
                new { topicUz = Topics.Display(topic, Lang.Uz), topicRu = Topics.Display(topic, Lang.Ru), topicUzk = topic },
                "/Topic/Watch/" + lesson.Id);
        }

        TempData["LsMsg"] = "ls.saved";
        return RedirectToAction(nameof(Edit), new { id = lesson.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var lesson = await _db.TopicLessons.FirstOrDefaultAsync(l => l.Id == id);
        if (lesson == null) return RedirectToAction(nameof(Index));
        var topic = lesson.Topic;
        if (lesson.VideoKind == VideoKinds.File && lesson.VideoRef != null) _videos.Delete(lesson.VideoRef);
        _db.TopicLessons.Remove(lesson); // bo'limlar, savol bog'lanishlari, ko'rish tarixi — CASCADE
        await _db.SaveChangesAsync();
        TempData["LsMsg"] = "ls.deleted";
        return RedirectToAction(nameof(Topic), new { topic });
    }

    /// <summary>"1:25", "01:02:03" yoki "85" → soniya. Bo'sh yoki noto'g'ri — null.</summary>
    public static int? ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Trim().Split(':');
        if (parts.Length > 3) return null;
        int total = 0;
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out var v) || v < 0) return null;
            total = total * 60 + v;
        }
        return total;
    }

    // ── Video faylni bo'laklab yuklash ──

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> UploadChunk(string id, int index, long offset, IFormFile chunk)
    {
        if (chunk == null || chunk.Length == 0) return BadRequest(new { error = "empty" });
        var (ok, error) = await _videos.AppendAsync(id, index, offset, chunk);
        return ok ? Ok(new { ok = true }) : BadRequest(new { error });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult UploadComplete(string id, string name, long size)
    {
        var (file, error) = _videos.Complete(id, name, size);
        return file != null
            ? Ok(new { name = file, url = MediaController.VideoUrl(HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(), User, file) })
            : BadRequest(new { error });
    }
}

public record LessonTopicRow(string Topic, int QuestionCount, int Covered, int Videos, int Published);

public record LessonTotals(int Videos, int Published, int Viewers, long WatchSeconds, int CoveredQuestions);

public record LessonRef(int Id, string Title);

public record LessonPickQuestion(int Id, int Number, string Text, string? ImageUrl, int? Bilet, List<LessonRef> Lessons);

public record LessonCard(TopicLesson Lesson, int Questions);

public record TopicCount(string Topic, int Count);

public class LessonTopicModel
{
    public string Topic { get; set; } = "";
    public List<LessonPickQuestion> Questions { get; set; } = new();
    public List<LessonCard> Lessons { get; set; } = new();
}

public record LessonEditQuestion(int Id, int Number, string Text, string? ImageUrl, string? Topic, int? StartSeconds);

public class LessonEditModel
{
    public TopicLesson Lesson { get; set; } = null!;
    public List<LessonEditQuestion> Questions { get; set; } = new();
    public List<TopicLessonChapter> Extra { get; set; } = new();
    public List<TopicCount> TopicCounts { get; set; } = new();
}
