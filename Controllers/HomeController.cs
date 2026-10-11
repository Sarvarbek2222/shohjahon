using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

using Microsoft.EntityFrameworkCore;

using propro.Localization;



public class HomeController : Controller

{

    private readonly AppDbContext _context;
    private readonly QuestionBank _bank;

    public HomeController(AppDbContext context, QuestionBank bank)
    {
        _context = context;
        _bank = bank;
    }



    /// <summary>
    /// Bosh sahifa — o'quvchi login qilgandan keyin shu yerga tushadi: imtihon va test turlari hamda savol qidirish.
    /// Natijalar, xatolar va tahlil — kabinetda. Super admin uchun kerak emas — admin paneliga.
    /// </summary>
    public async Task<IActionResult> Index()
    {
        if (User.IsInRole(Roles.SuperAdmin)) return RedirectToAction("Index1", "Question");

        var model = new HomeModel
        {
            BankSize = (await _bank.TestPoolAsync()).Count,
            BiletCount = (await _bank.BiletsAsync()).Count,
            TopicCount = (await _bank.ByTopicAsync()).Count(kv => kv.Value.Count > 0),
        };
        return View(model);
    }



    [HttpGet]

    public async Task<IActionResult> GetQuestions(string? lang = null)

    {

        lang = lang == null ? Lang.Current(HttpContext) : Lang.Normalize(lang);



        var questions = await _context.Questions

            .Include(q => q.Options)

            .OrderBy(q => Guid.NewGuid())

            .Take(2)

            .ToListAsync();



        return Json(questions.Select(q => new

        {

            Id = q.Id,

            Question = Lang.Pick(lang, q.QuestionUZ, q.QuestionRU, q.QuestionUZK),

            Options = q.Options

                .Select(o => new

                {

                    Id = o.Id,

                    Text = Lang.Pick(lang, o.OptionUZ, o.OptionRU, o.OptionUZK),

                    IsCorrect = o.IsCorrect

                })

                .OrderBy(x => Guid.NewGuid())

                .ToList()

        }));

    }



    /// <summary>Interfeys matnlari — barcha tillar (eski site.js uchun).</summary>

    [HttpGet]

    public IActionResult GetTexts()

    {

        return Content(Texts.ToJson(), "application/json; charset=utf-8");

    }

}

