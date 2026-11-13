using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using Microsoft.Extensions.Localization;
using System.Linq;
using System.Threading.Tasks;

namespace propro.Controllers
{
    public class BiletController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IStringLocalizer<SharedResource> _t;

        private readonly QuestionBank _bank;

        public BiletController(AppDbContext context, IStringLocalizer<SharedResource> t, QuestionBank bank)
        {
            _context = context;
            _t = t;
            _bank = bank;
        }

        public IActionResult Index() => View();

        public IActionResult TestPage(int biletNumber)
        {
            ViewData["BiletNumber"] = biletNumber;
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetBiletQuestions(int biletNumber)
        {
            // Biletlar xotiradagi keshdan (ptest bazasi, bilet tartibida)
            var bilets = await _bank.BiletsAsync();
            var totalBilets = bilets.Count;

            if (biletNumber < 1 || biletNumber > totalBilets)
                return BadRequest(_t["bilet.range"].Value.Replace("{max}", totalBilets.ToString()));

            // Savollar faqat shu bilet ichida aralashtiriladi (boshqa biletga o'tmaydi)
            var biletQuestions = QuestionBank.Shuffled(bilets[biletNumber - 1])
                .Select(QuestionJson.From)
                .ToList();

            return Json(new
            {
                biletNumber,
                totalBilets,
                questions = biletQuestions
            });
        }

        /// <summary>Har bir biletda nechta video bor (bilet savollarini tushuntiradigan e'lon qilingan videolar).</summary>
        [HttpGet]
        public async Task<IActionResult> VideoCounts()
        {
            var map = await BiletLessonsAsync();
            return Json(map.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Count));
        }

        /// <summary>Bilet videolari — dars sifatida ko'rish uchun.</summary>
        public async Task<IActionResult> Lessons(int biletNumber)
        {
            var map = await BiletLessonsAsync();
            if (!map.TryGetValue(biletNumber, out var ids) || ids.Count == 0) return RedirectToAction(nameof(Index));
            var lessons = await _context.TopicLessons.AsNoTracking().Where(l => ids.Contains(l.Id)).ToListAsync();
            ViewBag.Bilet = biletNumber;
            return View(lessons.OrderBy(l => ids.IndexOf(l.Id)).ToList());
        }

        /// <summary>bilet raqami → shu bilet savollariga bog'langan e'lon qilingan videolar (tartib: birinchi savoldan).</summary>
        private async Task<Dictionary<int, List<int>>> BiletLessonsAsync()
        {
            var bilets = await _bank.BiletsAsync();
            var links = await _context.TopicLessonQuestions.AsNoTracking()
                .Join(_context.TopicLessons.Where(l => l.IsPublished && l.VideoRef != null), x => x.LessonId, l => l.Id, (x, l) => new { x.QuestionId, x.LessonId })
                .ToListAsync();
            var byQ = links.GroupBy(x => x.QuestionId).ToDictionary(g => g.Key, g => g.Select(x => x.LessonId).ToList());
            var map = new Dictionary<int, List<int>>();
            for (int i = 0; i < bilets.Count; i++)
                map[i + 1] = bilets[i].SelectMany(q => byQ.GetValueOrDefault(q.Id) ?? new List<int>()).Distinct().ToList();
            return map;
        }

        [HttpGet]
        public async Task<IActionResult> GetAllBilets()
        {
            var totalBilets = (await _bank.BiletsAsync()).Count;

            return Json(new { totalBilets });
        }
    }
}