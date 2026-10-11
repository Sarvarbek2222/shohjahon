using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using System.Linq;
using System;

namespace propro.Controllers
{
    public class TestAllController : Controller
    {
        private readonly AppDbContext _context;
        private readonly QuestionBank _bank;
        public TestAllController(AppDbContext context, QuestionBank bank) { _context = context; _bank = bank; }

        public IActionResult Index() => View();

        [HttpGet]
        public async Task<IActionResult> GetQuestions()
        {
            // 1️⃣ Bazadagi barcha savollarni olamiz
            // Barcha savollar — xotiradagi keshdan
            var allQuestions = await _bank.TestPoolAsync();

            // 2️⃣ Savollarni tasodifiy tartibda aralashtiramiz
            var shuffledQuestions = QuestionBank.Shuffled(allQuestions)
                .Select(QuestionJson.From)
                .ToList();

            return Json(shuffledQuestions);
        }
    }
}