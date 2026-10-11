using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using propro.Localization;

namespace propro.Controllers.Api
{
    /// <summary>
    /// Testlar, biletlar, mavzular, natijalarni saqlash va kabinet ma'lumotlari — faqat obunasi faol foydalanuvchiga.
    /// Savollar saytdagi bilan bir xil manbadan (QuestionBank) va bir xil JSON ko'rinishida (QuestionJson) beriladi.
    /// </summary>
    [Route("api/content")]
    public class ContentApiController : ApiControllerBase
    {
        private readonly AppDbContext _db;
        private readonly QuestionBank _bank;
        private readonly ResultsService _results;
        private readonly StatsService _stats;

        public ContentApiController(AppDbContext db, QuestionBank bank, ResultsService results, StatsService stats)
        {
            _db = db;
            _bank = bank;
            _results = results;
            _stats = stats;
        }

        /// <summary>Test turi bo'yicha savollar: real (20), q20, q40, q80, q100, q200, marathon (hammasi).</summary>
        [HttpGet("tests/{type}")]
        public async Task<IActionResult> Test(string type)
        {
            var (_, fail) = await RequireSubscriptionAsync(_db);
            if (fail != null) return fail;

            int? take = type switch
            {
                TestTypes.Real or TestTypes.Q20 => 20,
                TestTypes.Q40 => 40,
                TestTypes.Q80 => 80,
                TestTypes.Q100 => 100,
                TestTypes.Q200 => 200,
                TestTypes.Marathon => null,
                _ => -1,
            };
            if (take == -1) return Error(404, "not_found", "Test turi topilmadi.");

            var list = take is int n ? await _bank.RandomAsync(n) : QuestionBank.Shuffled(await _bank.TestPoolAsync()).ToList();
            return Ok(list.Select(QuestionJson.From));
        }

        [HttpGet("bilets")]
        public async Task<IActionResult> Bilets()
        {
            var total = (await _bank.BiletsAsync()).Count;
            return Ok(new { total });
        }

        [HttpGet("bilets/{number:int}")]
        public async Task<IActionResult> Bilet(int number)
        {
            var (_, fail) = await RequireSubscriptionAsync(_db);
            if (fail != null) return fail;

            var bilets = await _bank.BiletsAsync();
            var total = bilets.Count;
            if (number < 1 || number > total) return Error(404, "not_found", $"Bilet raqami 1 dan {total} gacha bo'lishi kerak.");
            return Ok(QuestionBank.Shuffled(bilets[number - 1]).Select(QuestionJson.From)); // faqat bilet ichida aralashadi
        }

        [HttpGet("topics")]
        public async Task<IActionResult> TopicList()
        {
            var counts = (await _bank.ByTopicAsync()).ToDictionary(kv => kv.Key, kv => kv.Value.Count);

            return Ok(Topics.All.Select((t, i) => new
            {
                key = t,
                no = i + 1,
                uz = Topics.Display(t, Lang.Uz),
                ru = Topics.Display(t, Lang.Ru),
                uzk = Topics.Display(t, Lang.Uzk),
                count = counts.TryGetValue(t, out var c) ? c : 0,
            }));
        }

        [HttpGet("topics/questions")]
        public async Task<IActionResult> TopicQuestions([FromQuery] string topic)
        {
            var (_, fail) = await RequireSubscriptionAsync(_db);
            if (fail != null) return fail;

            topic = (topic ?? "").Trim();
            var list = QuestionBank.Shuffled(await _bank.TopicQuestionsAsync(topic));
            return Ok(list.Select(QuestionJson.From));
        }

        [HttpGet("mistakes")]
        public async Task<IActionResult> Mistakes()
        {
            var (_, fail) = await RequireSubscriptionAsync(_db);
            if (fail != null) return fail;
            return Ok((await _results.GetMistakeQuestionsAsync(CurrentUserId)).Select(QuestionJson.From));
        }

        [HttpPost("results")]
        public async Task<IActionResult> SaveResult([FromBody] SaveResultRequest req)
        {
            var (_, fail) = await RequireSubscriptionAsync(_db);
            if (fail != null) return fail;
            var res = await _results.SaveAsync(CurrentUserId, req);
            return res == null ? Error(400, "result_invalid", "Natijani saqlab bo'lmadi.") : Ok(res);
        }

        /// <summary>
        /// Kabinet: saytdagi shaxsiy kabinet bilan bir xil ko'rsatkichlar (imtihonga tayyorlik, prognoz,
        /// o'rtacha natija, ketma-ket kunlar, kuchsiz mavzular) va so'nggi urinishlar.
        /// </summary>
        [HttpGet("summary")]
        public async Task<IActionResult> Summary()
        {
            var uid = CurrentUserId;
            var s = await _stats.GetUserStatsAsync(uid);
            if (s == null) return Error(401, "unauthorized", "Qaytadan kiring.");

            var recent = s.Attempts
                .Where(a => !a.IsErrorReview)
                .OrderByDescending(a => a.FinishedAt)
                .Take(20)
                .Select(a => new { a.Id, a.TestType, a.TestRef, a.Total, a.Correct, a.Wrong, a.Skipped, a.Passed, a.FinishedAt });

            return Ok(new
            {
                testsTaken = s.TotalAttempts,
                passed = s.Attempts.Count(a => a.Passed && !a.IsErrorReview),
                answered = s.TotalAnswered,
                accuracy = Math.Round(s.Accuracy * 100),
                averagePercent = Math.Round(s.AveragePercent),
                activeMistakes = s.ActiveMistakes,
                resolvedMistakes = s.ResolvedMistakes,
                readinessScore = s.ReadinessScore,
                isReady = s.IsReady,
                needMoreData = s.NeedMoreData,
                daysToReady = s.DaysToReady,
                predictedReadyDate = s.PredictedReadyDate,
                recentAccuracy = Math.Round(s.RecentAccuracy * 100),
                studyMinutes = s.StudyMinutes,
                streakDays = s.StreakDays,
                weakTopics = s.WeakTopics.Take(3).Select(t => new
                {
                    key = t.Topic,
                    uz = Topics.Display(t.Topic, Lang.Uz),
                    ru = Topics.Display(t.Topic, Lang.Ru),
                    uzk = Topics.Display(t.Topic, Lang.Uzk),
                    t.Answered,
                    t.Correct,
                    percent = Math.Round(t.Accuracy * 100),
                }),
                recent,
            });
        }
    }
}
