using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using propro.Localization;

/// <summary>
/// Admin: mavzular bo'yicha video darslar. Har bir mavzuga bitta dars: video (yuklangan fayl yoki YouTube),
/// tavsif va bo'limlar — mavzudagi har bir savol tushuntirilishi boshlanadigan vaqt.
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

    public async Task<IActionResult> Index()
    {
        var byTopic = await _bank.ByTopicAsync();
        var lessons = await _db.TopicLessons.AsNoTracking().Include(l => l.Chapters).ToDictionaryAsync(l => l.Topic);
        var progress = await _db.TopicLessonProgress.AsNoTracking()
            .GroupBy(p => p.LessonId)
            .Select(g => new { LessonId = g.Key, Viewers = g.Count(), Completed = g.Count(p => p.Completed), Seconds = g.Sum(p => p.WatchSeconds) })
            .ToDictionaryAsync(x => x.LessonId);

        var rows = Topics.All.Select(t =>
        {
            lessons.TryGetValue(t, out var l);
            var p = l != null && progress.TryGetValue(l.Id, out var pp) ? pp : null;
            return new LessonRow(t, byTopic.TryGetValue(t, out var qs) ? qs.Count : 0, l,
                p?.Viewers ?? 0, p?.Completed ?? 0, p?.Seconds ?? 0);
        }).ToList();
        return View(rows);
    }

    public async Task<IActionResult> Edit(string topic)
    {
        if (!Topics.All.Contains(topic)) return RedirectToAction(nameof(Index));
        var lesson = await _db.TopicLessons.AsNoTracking().Include(l => l.Chapters).FirstOrDefaultAsync(l => l.Topic == topic)
                     ?? new TopicLesson { Topic = topic };
        return View(await BuildEditModelAsync(lesson));
    }

    private async Task<LessonEditModel> BuildEditModelAsync(TopicLesson lesson)
    {
        var questions = await _bank.TopicQuestionsAsync(lesson.Topic);
        var byQ = lesson.Chapters.Where(c => c.QuestionId != null).GroupBy(c => c.QuestionId!.Value).ToDictionary(g => g.Key, g => g.First());
        return new LessonEditModel
        {
            Lesson = lesson,
            Questions = questions.Select((q, i) => new LessonEditQuestion(q.Id, i + 1, q.QuestionUZ, q.ImageUrl,
                byQ.TryGetValue(q.Id, out var c) ? c.StartSeconds : null)).ToList(),
            Extra = lesson.Chapters.Where(c => c.QuestionId == null).OrderBy(c => c.StartSeconds).ToList(),
        };
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string topic, string? title, string? description, string videoKind, string? videoRef,
        string? youTubeUrl, int durationSeconds, bool isPublished,
        int[]? chQuestion, string[]? chTime, string[]? xTitle, string[]? xTime)
    {
        if (!Topics.All.Contains(topic)) return RedirectToAction(nameof(Index));

        var lesson = await _db.TopicLessons.Include(l => l.Chapters).FirstOrDefaultAsync(l => l.Topic == topic);
        var isNew = lesson == null;
        lesson ??= new TopicLesson { Topic = topic, CreatedAt = DateTime.UtcNow };
        var wasPublished = !isNew && lesson.IsPublished && lesson.HasVideo;
        var oldFile = lesson.VideoKind == VideoKinds.File ? lesson.VideoRef : null;

        lesson.Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim()[..Math.Min(title.Trim().Length, 200)];
        lesson.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        if (videoKind == VideoKinds.YouTube)
        {
            var id = VideoUploads.ParseYouTubeId(youTubeUrl);
            if (id == null && !string.IsNullOrWhiteSpace(youTubeUrl))
            {
                TempData["LsErr"] = "ls.errYouTube";
                return RedirectToAction(nameof(Edit), new { topic });
            }
            lesson.VideoKind = VideoKinds.YouTube;
            lesson.VideoRef = id;
        }
        else
        {
            lesson.VideoKind = VideoKinds.File;
            // Faqat shu sayt yuklagan fayl nomi (32 belgili hex + kengaytma)
            lesson.VideoRef = !string.IsNullOrWhiteSpace(videoRef) && System.Text.RegularExpressions.Regex.IsMatch(videoRef, "^[a-f0-9]{32}\\.(mp4|webm|m4v|mov)$")
                ? videoRef : null;
        }
        if (oldFile != null && (lesson.VideoKind != VideoKinds.File || lesson.VideoRef != oldFile)) _videos.Delete(oldFile);

        lesson.DurationSeconds = Math.Max(0, durationSeconds);
        lesson.IsPublished = isPublished && lesson.HasVideo;
        lesson.UpdatedAt = DateTime.UtcNow;

        // Bo'limlar: mavzu savollari + qo'shimcha (kirish, xulosa va h.k.)
        var topicIds = (await _bank.TopicQuestionsAsync(topic)).Select(q => q.Id).ToHashSet();
        var chapters = new List<TopicLessonChapter>();
        for (int i = 0; chQuestion != null && i < chQuestion.Length; i++)
        {
            var sec = ParseTime(chTime != null && i < chTime.Length ? chTime[i] : null);
            if (sec != null && topicIds.Contains(chQuestion[i]))
                chapters.Add(new TopicLessonChapter { QuestionId = chQuestion[i], StartSeconds = sec.Value });
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

        // Dars birinchi marta e'lon qilindi — o'quvchilarga bildirishnoma
        if (lesson.IsPublished && !wasPublished)
        {
            await HttpContext.RequestServices.GetRequiredService<NotificationService>().NotifyStudentsAsync("lessonNew",
                new { topicUz = Topics.Display(topic, Lang.Uz), topicRu = Topics.Display(topic, Lang.Ru), topicUzk = topic },
                "/Topic/Lesson?topic=" + Uri.EscapeDataString(topic));
        }

        TempData["LsMsg"] = "ls.saved";
        return RedirectToAction(nameof(Edit), new { topic });
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

public record LessonRow(string Topic, int QuestionCount, TopicLesson? Lesson, int Viewers, int Completed, long WatchSeconds);

public record LessonEditQuestion(int Id, int Number, string Text, string? ImageUrl, int? StartSeconds);

public class LessonEditModel
{
    public TopicLesson Lesson { get; set; } = null!;
    public List<LessonEditQuestion> Questions { get; set; } = new();
    public List<TopicLessonChapter> Extra { get; set; } = new();
}
