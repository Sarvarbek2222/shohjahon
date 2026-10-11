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

        [HttpGet]
        public async Task<IActionResult> GetAllBilets()
        {
            var totalBilets = (await _bank.BiletsAsync()).Count;

            return Json(new { totalBilets });
        }
    }
}