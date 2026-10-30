using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using propro.Localization;

namespace propro.Controllers
{
    /// <summary>
    /// Mavzu sahifasi (Hub): "Darsni ko'rish" va "Test ishlash", o'quvchining shu mavzudagi holati, savollar ro'yxati
    /// (har biri videodagi tushuntirishiga havola bilan). Dars sahifasi: pleer + bo'limlar + ko'rish jarayonini saqlash.
    /// </summary>
    public partial class TopicController
    {
        private int CurrentUserId => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;
        private bool IsAdmin => User.IsInRole(Roles.SuperAdmin);

        /// <summary>E'lon qilingan dars (admin — qoralamani ham ko'radi).</summary>
        private async Task<TopicLesson?> LessonForAsync(string topic) =>
            await _context.TopicLessons.AsNoTracking().Include(l => l.Chapters)
                .FirstOrDefaultAsync(l => l.Topic == topic && l.VideoRef != null && (l.IsPublished || IsAdmin));

        public async Task<IActionResult> Hub(string topic)
        {
            topic = (topic ?? "").Trim();
            if (!Topics.All.Contains(topic)) return RedirectToAction(nameof(Index));
            var uid = CurrentUserId;

            var questions = await _bank.TopicQuestionsAsync(topic);
            var ids = questions.Select(q => q.Id).ToList();
            var lesson = await LessonForAsync(topic);

            // O'quvchining har bir savol bo'yicha oxirgi javobi
            var answers = await _context.TestAnswers.AsNoTracking()
                .Where(a => a.SelectedOptionId != null && ids.Contains(a.QuestionId) && a.Attempt!.UserId == uid)
                .Select(a => new { a.QuestionId, a.IsCorrect, a.Attempt!.FinishedAt, a.Id })
                .ToListAsync();
            var last = answers.GroupBy(a => a.QuestionId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.FinishedAt).ThenByDescending(a => a.Id).First().IsCorrect);
            var everCorrect = answers.Where(a => a.IsCorrect).Select(a => a.QuestionId).ToHashSet();
            var mistakes = (await _context.UserMistakes.AsNoTracking()
                .Where(m => m.UserId == uid && m.ResolvedAt == null && ids.Contains(m.QuestionId))
                .Select(m => m.QuestionId).ToListAsync()).ToHashSet();

            var tests = await _context.TestAttempts.AsNoTracking()
                .Where(a => a.UserId == uid && a.TestType == TestTypes.Topic && a.TestRef == topic && !a.IsErrorReview)
                .Select(a => new { a.Total, a.Correct }).ToListAsync();

            TopicLessonProgress? progress = null;
            if (lesson != null)
                progress = await _context.TopicLessonProgress.AsNoTracking().FirstOrDefaultAsync(p => p.LessonId == lesson.Id && p.UserId == uid);

            var counts = (await _bank.ByTopicAsync()).ToDictionary(kv => kv.Key, kv => kv.Value.Count);
            var withQuestions = Topics.All.Where(t => counts.GetValueOrDefault(t) > 0).ToList();
            var pos = withQuestions.IndexOf(topic);

            var chapterOf = lesson?.Chapters.Where(c => c.QuestionId != null).GroupBy(c => c.QuestionId!.Value)
                .ToDictionary(g => g.Key, g => g.Min(c => c.StartSeconds)) ?? new();

            var model = new TopicHubModel
            {
                Topic = topic,
                Lesson = lesson,
                Progress = progress,
                Questions = questions.Select((q, i) => new TopicHubQuestion(q.Id, i + 1, q.QuestionUZ, q.QuestionRU, q.QuestionUZK,
                    last.TryGetValue(q.Id, out var ok) ? ok : null, mistakes.Contains(q.Id),
                    chapterOf.TryGetValue(q.Id, out var s) ? s : null)).ToList(),
                Solved = ids.Count(id => everCorrect.Contains(id) && !mistakes.Contains(id)),
                ActiveMistakes = mistakes.Count,
                TestsTaken = tests.Count,
                BestPercent = tests.Count > 0 ? tests.Max(a => a.Total > 0 ? 100.0 * a.Correct / a.Total : 0) : null,
                PrevTopic = pos > 0 ? withQuestions[pos - 1] : null,
                NextTopic = pos >= 0 && pos < withQuestions.Count - 1 ? withQuestions[pos + 1] : null,
            };
            return View(model);
        }

        public async Task<IActionResult> Lesson(string topic, int? t)
        {
            topic = (topic ?? "").Trim();
            if (!Topics.All.Contains(topic)) return RedirectToAction(nameof(Index));
            var lesson = await LessonForAsync(topic);
            if (lesson == null) return RedirectToAction(nameof(Hub), new { topic });

            var questions = await _bank.TopicQuestionsAsync(topic);
            var number = questions.Select((q, i) => (q.Id, i + 1)).ToDictionary(x => x.Id, x => x.Item2);
            var byId = questions.ToDictionary(q => q.Id);
            var progress = await _context.TopicLessonProgress.AsNoTracking()
                .FirstOrDefaultAsync(p => p.LessonId == lesson.Id && p.UserId == CurrentUserId);

            var chapters = lesson.Chapters.OrderBy(c => c.StartSeconds).Select(c =>
            {
                if (c.QuestionId is int qid && byId.TryGetValue(qid, out var q))
                {
                    var right = q.Options.FirstOrDefault(o => o.IsCorrect);
                    return new LessonChapterView(c.StartSeconds, number[qid], q.Id,
                        q.QuestionUZ, q.QuestionRU ?? q.QuestionUZ, q.QuestionUZK ?? q.QuestionUZ,
                        right?.OptionUZ, right?.OptionRU ?? right?.OptionUZ, right?.OptionUZK ?? right?.OptionUZ, q.ImageUrl);
                }
                var title = c.Title ?? "";
                return new LessonChapterView(c.StartSeconds, null, null, title, title, title, null, null, null, null);
            }).ToList();

            var counts = (await _bank.ByTopicAsync()).ToDictionary(kv => kv.Key, kv => kv.Value.Count);
            var withQuestions = Topics.All.Where(x => counts.GetValueOrDefault(x) > 0).ToList();
            var pos = withQuestions.IndexOf(topic);

            return View(new LessonPageModel
            {
                Topic = topic,
                Lesson = lesson,
                Chapters = chapters,
                QuestionCount = questions.Count,
                Progress = progress,
                StartAt = t,
                NextTopic = pos >= 0 && pos < withQuestions.Count - 1 ? withQuestions[pos + 1] : null,
            });
        }

        /// <summary>Ko'rish jarayoni: pleer har ~15 soniyada (va pauza/chiqishda) yuboradi.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LessonProgress([FromBody] LessonProgressRequest req)
        {
            var uid = CurrentUserId;
            var lesson = await _context.TopicLessons.FirstOrDefaultAsync(l => l.Id == req.LessonId);
            if (lesson == null || uid == 0) return NotFound();

            var duration = lesson.DurationSeconds;
            if (duration <= 0 && req.Duration > 0)
            {
                // Admin davomiylikni kiritmagan bo'lsa — pleer aniqlaganini saqlaymiz
                lesson.DurationSeconds = duration = (int)Math.Round(Math.Min(req.Duration, 6 * 3600));
            }
            if (duration <= 0) return Ok(new { ok = true });

            var len = (duration + TopicLessonProgress.Bucket - 1) / TopicLessonProgress.Bucket;
            var p = await _context.TopicLessonProgress.FirstOrDefaultAsync(x => x.LessonId == lesson.Id && x.UserId == uid);
            if (p == null)
            {
                p = new TopicLessonProgress { LessonId = lesson.Id, UserId = uid, FirstAt = DateTime.UtcNow };
                _context.TopicLessonProgress.Add(p);
            }
            var map = (p.WatchedMap ?? "").PadRight(len, '0')[..len].ToCharArray();
            foreach (var b in req.Buckets ?? Array.Empty<int>())
                if (b >= 0 && b < len) map[b] = '1';
            p.WatchedMap = new string(map);
            p.WatchSeconds += Math.Clamp(req.Watched, 0, 120);
            p.PositionSeconds = Math.Clamp((int)req.Position, 0, duration);
            if (p.WatchedShare >= 0.9) p.Completed = true;
            p.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return Ok(new { ok = true, share = Math.Round(p.WatchedShare * 100), completed = p.Completed });
        }
    }
}

public class LessonProgressRequest
{
    public int LessonId { get; set; }
    public double Position { get; set; }
    public double Duration { get; set; }
    public int Watched { get; set; }
    public int[]? Buckets { get; set; }
}

public record TopicHubQuestion(int Id, int Number, string TextUz, string? TextRu, string? TextUzk, bool? LastCorrect, bool InMistakes, int? VideoAt);

public class TopicHubModel
{
    public string Topic { get; set; } = "";
    public TopicLesson? Lesson { get; set; }
    public TopicLessonProgress? Progress { get; set; }
    public List<TopicHubQuestion> Questions { get; set; } = new();
    public int Solved { get; set; }
    public int ActiveMistakes { get; set; }
    public int TestsTaken { get; set; }
    public double? BestPercent { get; set; }
    public string? PrevTopic { get; set; }
    public string? NextTopic { get; set; }

    public int Mastery => Questions.Count == 0 ? 0 : (int)Math.Floor(100.0 * Solved / Questions.Count);
    public int Watched => Progress == null ? 0 : (int)Math.Round(Progress.WatchedShare * 100);
}

public record LessonChapterView(int Start, int? Number, int? QuestionId, string TextUz, string TextRu, string TextUzk,
    string? AnswerUz, string? AnswerRu, string? AnswerUzk, string? ImageUrl);

public class LessonPageModel
{
    public string Topic { get; set; } = "";
    public TopicLesson Lesson { get; set; } = null!;
    public List<LessonChapterView> Chapters { get; set; } = new();
    public int QuestionCount { get; set; }
    public TopicLessonProgress? Progress { get; set; }
    public int? StartAt { get; set; }
    public string? NextTopic { get; set; }
}
