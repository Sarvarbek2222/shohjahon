using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using propro.Localization;

namespace propro.Controllers.Api
{
    /// <summary>
    /// Mobil ilovadagi shaxsiy kabinet — saytdagi Cabinet bo'limi bilan bir xil: umumiy ko'rinish, natijalar,
    /// urinish tafsilotlari, xatolar, tahlil (AI tahlili ham), savol qidirish.
    /// Matnlar (tavsiyalar, test nomlari, mavzular) ilova yuborgan tilda (?culture=uz-Latn | ru | uz-Cyrl).
    /// </summary>
    [Route("api/cabinet")]
    public class CabinetApiController : ApiControllerBase
    {
        private readonly AppDbContext _db;
        private readonly StatsService _stats;
        private readonly AiAnalysisService _ai;
        private readonly QuestionSearch _search;
        private readonly QuestionBank _bank;
        private readonly ViewText _l;

        public CabinetApiController(AppDbContext db, StatsService stats, AiAnalysisService ai, QuestionSearch search, QuestionBank bank, ViewText l)
        {
            _db = db;
            _stats = stats;
            _ai = ai;
            _search = search;
            _bank = bank;
            _l = l;
        }

        [HttpGet]
        public async Task<IActionResult> Overview()
        {
            var s = await _stats.GetUserStatsAsync(CurrentUserId);
            if (s == null) return Error(401, "unauthorized", "Qaytadan kiring.");
            return Ok(CabinetDtos.Overview(s, _l, _ai.IsEnabled));
        }

        [HttpGet("results")]
        public async Task<IActionResult> Results([FromQuery] string? type, [FromQuery] int page = 1)
        {
            var s = await _stats.GetUserStatsAsync(CurrentUserId);
            if (s == null) return Error(401, "unauthorized", "Qaytadan kiring.");
            return Ok(CabinetDtos.Results(s, _l, type, page));
        }

        [HttpGet("attempts/{id:int}")]
        public async Task<IActionResult> Attempt(int id)
        {
            var detail = await CabinetController.LoadAttemptAsync(_db, id, CurrentUserId);
            return detail == null ? Error(404, "not_found", "Urinish topilmadi.") : Ok(CabinetDtos.Attempt(detail, _l));
        }

        [HttpGet("mistakes")]
        public async Task<IActionResult> Mistakes()
        {
            var s = await _stats.GetUserStatsAsync(CurrentUserId);
            if (s == null) return Error(401, "unauthorized", "Qaytadan kiring.");
            return Ok(await CabinetDtos.MistakesAsync(_db, s, _l));
        }

        /// <summary>AI tahlili (Claude). Sozlanmagan bo'lsa — 404 "ai_disabled".</summary>
        [HttpPost("ai")]
        public async Task<IActionResult> Ai(CancellationToken ct)
        {
            if (!_ai.IsEnabled) return Error(404, "ai_disabled", _l["cab.aiDisabled"]);
            var (_, fail) = await RequireSubscriptionAsync(_db);
            if (fail != null) return fail;
            var s = await _stats.GetUserStatsAsync(CurrentUserId);
            if (s == null) return Error(401, "unauthorized", "Qaytadan kiring.");
            var text = await _ai.AnalyzeAsync(s, _l.Lang, ct);
            return text == null ? Error(502, "ai_failed", _l["cab.aiError"]) : Ok(new { text });
        }

        /// <summary>Savolni matni bo'yicha qidirish (saytdagi "Savol qidirish").</summary>
        [HttpGet("search")]
        public async Task<IActionResult> Search([FromQuery] string? q)
        {
            var (_, fail) = await RequireSubscriptionAsync(_db);
            if (fail != null) return fail;

            q = q?.Trim() ?? "";
            if (q.Length > 200) q = q[..200];
            var (hits, total) = await _search.SearchAsync(q);
            var ids = hits.Select(h => h.Question.Id).ToList();
            var mistakes = (await _db.UserMistakes.AsNoTracking()
                    .Where(m => m.UserId == CurrentUserId && m.ResolvedAt == null && ids.Contains(m.QuestionId))
                    .Select(m => m.QuestionId).ToListAsync())
                .ToHashSet();

            return Ok(new
            {
                query = q,
                total,
                items = hits.Select(h => new
                {
                    id = h.Question.Id,
                    question = _l.Pick(h.Question.QuestionUZ, h.Question.QuestionRU, h.Question.QuestionUZK),
                    imageUrl = h.Question.ImageUrl,
                    topic = string.IsNullOrWhiteSpace(h.Question.Topic) ? null : _l.Topic(h.Question.Topic),
                    inOptions = h.InOptionsOnly,
                    inMistakes = mistakes.Contains(h.Question.Id),
                }),
            });
        }

        /// <summary>Qidiruvdan tanlangan savollar — test oynasida alohida ishlash uchun (berilgan tartibda).</summary>
        [HttpGet("questions")]
        public async Task<IActionResult> Questions([FromQuery] string? ids)
        {
            var (_, fail) = await RequireSubscriptionAsync(_db);
            if (fail != null) return fail;

            var list = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).Distinct()
                .Take(QuestionSearch.MaxResults).ToList();
            var byId = (await _bank.AllAsync()).ToDictionary(x => x.Id);
            return Ok(list.Where(byId.ContainsKey).Select(id => QuestionJson.From(byId[id])));
        }
    }

    /// <summary>Kabinet ma'lumotlari ilova uchun (foydalanuvchi o'zi va admin → o'quvchi sahifasi bir xil ko'radi).</summary>
    public static class CabinetDtos
    {
        private static double Pct(double fraction) => Math.Round(fraction * 100);

        public static object AttemptRow(TestAttempt a, ViewText l) => new
        {
            a.Id,
            name = l.TestName(a.TestType, a.TestRef),
            a.TestType,
            date = l.Stamp(a.FinishedAt),
            a.Correct,
            a.Total,
            a.Wrong,
            a.Skipped,
            percent = Math.Round(a.Percent),
            a.Passed,
            a.IsErrorReview,
            status = a.IsErrorReview ? l["testType.mistakes"] : a.Passed ? l["cab.passed"] : l["cab.failed"],
        };

        private static object TopicRow(TopicStat t, ViewText l) => new
        {
            key = t.Topic,
            name = l.Topic(t.Topic),
            t.Answered,
            t.Correct,
            percent = Pct(t.Accuracy),
        };

        public static object Overview(UserStats s, ViewText l, bool aiEnabled)
        {
            var u = s.User;
            string forecastNote = s.IsReady ? l["cab.readyText"]
                : s.PredictedReadyDate != null ? l.F("cab.forecastDays", ("n", s.DaysToReady))
                : s.NeedMoreData ? l.F("cab.forecastNoData", ("n", StatsService.MinAnswersForForecast - s.TotalAnswered))
                : l["cab.forecastUnknown"];

            return new
            {
                user = new { u.Id, u.Username, u.FullName, u.Phone, lastLoginAt = u.LastLoginAt is DateTime ll ? l.Stamp(ll) : null },
                aiEnabled,
                isEmpty = s.AllAttemptsCount == 0,
                readiness = new
                {
                    score = s.ReadinessScore,
                    s.IsReady,
                    s.NeedMoreData,
                    s.DaysToReady,
                    predictedDate = s.PredictedReadyDate is DateTime d ? l.Date(d) : null,
                    title = s.IsReady ? l["cab.readyTitle"] : l["cab.forecastTitle"],
                    note = forecastNote,
                    recentAccuracy = Pct(s.RecentAccuracy),
                    s.AccuracyScore,
                    s.ExamScore,
                    s.CoverageScore,
                    s.DistinctCorrect,
                    s.BankSize,
                },
                kpis = new
                {
                    averagePercent = Math.Round(s.AveragePercent),
                    testsTaken = s.TotalAttempts,
                    answered = s.TotalAnswered,
                    accuracy = Pct(s.Accuracy),
                    s.ActiveMistakes,
                    s.ResolvedMistakes,
                    studyTime = l.Duration(s.StudyMinutes),
                    s.StreakDays,
                },
                daily = s.Daily.Select(d => new { date = d.Date.ToString("dd.MM"), d.Answered, accuracy = d.Accuracy, minutes = d.Minutes }),
                recent = s.Attempts.Take(5).Select(a => AttemptRow(a, l)),
                weakTopics = s.WeakTopics.Select(t => TopicRow(t, l)),
                topics = s.Topics.OrderBy(t => t.Answered == 0).ThenBy(t => t.Accuracy).Select(t => TopicRow(t, l)),
                insights = s.Insights.Select(i => new { level = i.Level.ToString().ToLowerInvariant(), text = l.InsightText(i) }),
                byType = s.ByType.Select(t => new
                {
                    t.Type,
                    name = l["testType." + t.Type],
                    t.Attempts,
                    avg = Math.Round(t.AvgPercent),
                    best = Math.Round(t.BestPercent),
                    t.Passed,
                    last = l.Stamp(t.LastAt),
                }),
            };
        }

        public static object Results(UserStats s, ViewText l, string? type, int page)
        {
            const int size = CabinetController.PageSize;
            var filter = TestTypes.IsValid(type) ? type : null;
            var list = s.Attempts.Where(a => filter == null || a.TestType == filter).ToList();
            int pages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)size));
            page = Math.Clamp(page, 1, pages);
            return new
            {
                types = s.ByType.Select(t => new { t.Type, name = l["testType." + t.Type] }),
                filter,
                page,
                totalPages = pages,
                total = list.Count,
                items = list.Skip((page - 1) * size).Take(size).Select(a => AttemptRow(a, l)),
            };
        }

        public static object Attempt(AttemptDetail d, ViewText l)
        {
            var a = d.Attempt;
            return new
            {
                attempt = AttemptRow(a, l),
                duration = l.Duration((int)Math.Max(1, (a.FinishedAt - a.StartedAt).TotalMinutes)),
                user = d.User.FullName ?? d.User.Username,
                items = a.Answers.OrderBy(x => x.IsCorrect).ThenBy(x => x.Id)
                    .Where(x => d.Questions.ContainsKey(x.QuestionId))
                    .Select(x =>
                    {
                        var q = d.Questions[x.QuestionId];
                        var correct = q.Options.FirstOrDefault(o => o.IsCorrect);
                        var chosen = x.SelectedOptionId is int sel ? q.Options.FirstOrDefault(o => o.Id == sel) : null;
                        return new
                        {
                            questionId = q.Id,
                            question = l.Pick(q.QuestionUZ, q.QuestionRU, q.QuestionUZK),
                            imageUrl = q.ImageUrl,
                            topic = string.IsNullOrWhiteSpace(q.Topic) ? null : l.Topic(q.Topic),
                            x.IsCorrect,
                            yourAnswer = x.IsCorrect ? null : chosen != null ? l.Pick(chosen.OptionUZ, chosen.OptionRU, chosen.OptionUZK) : l["cab.noAnswer"],
                            correctAnswer = correct == null ? null : l.Pick(correct.OptionUZ, correct.OptionRU, correct.OptionUZK),
                        };
                    }),
            };
        }

        public static async Task<object> MistakesAsync(AppDbContext db, UserStats s, ViewText l)
        {
            var ids = s.Mistakes.Select(m => m.QuestionId).ToList();
            var qs = ids.Count == 0 ? new Dictionary<int, Question>()
                : await db.Questions.AsNoTracking().Include(q => q.Options).Where(q => ids.Contains(q.Id)).ToDictionaryAsync(q => q.Id);
            return new
            {
                active = s.ActiveMistakes,
                resolved = s.ResolvedMistakes,
                items = s.Mistakes.Where(m => qs.ContainsKey(m.QuestionId)).Select(m =>
                {
                    var q = qs[m.QuestionId];
                    var correct = q.Options.FirstOrDefault(o => o.IsCorrect);
                    return new
                    {
                        questionId = q.Id,
                        question = l.Pick(q.QuestionUZ, q.QuestionRU, q.QuestionUZK),
                        imageUrl = q.ImageUrl,
                        topic = string.IsNullOrWhiteSpace(q.Topic) ? null : l.Topic(q.Topic),
                        m.WrongCount,
                        lastWrong = l.Stamp(m.LastWrongAt),
                        correctAnswer = correct == null ? null : l.Pick(correct.OptionUZ, correct.OptionRU, correct.OptionUZK),
                    };
                }),
            };
        }
    }
}
