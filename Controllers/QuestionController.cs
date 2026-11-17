using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using propro;
using propro.Localization;

/// <summary>Savollar va javob variantlarini boshqarish — faqat super admin.</summary>
[Authorize(Roles = Roles.SuperAdmin)]
public class QuestionController : Controller
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _env;
    private readonly IStringLocalizer<SharedResource> _t;
    private readonly QuestionBank _bank;

    public QuestionController(AppDbContext context, IWebHostEnvironment env, IStringLocalizer<SharedResource> t, QuestionBank bank)
    {
        _context = context;
        _env = env;
        _t = t;
        _bank = bank;
    }

    /// <summary>Savol o'zgarganda test sahifalari keshini yangilash.</summary>
    public override void OnActionExecuted(Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext context)
    {
        if (HttpMethods.IsPost(Request.Method)) _bank.Invalidate();
        base.OnActionExecuted(context);
    }

    /// <summary>
    /// Savollar bazasi: qidiruv, filtr (mavzu, bilet, rasm, tarjima, video), saralash va sahifalash — xotiradagi
    /// savollar keshidan (bazaga murojaatsiz, darhol ochiladi). Bazadan faqat sahifadagi 25–100 ta savolning
    /// statistikasi (indeks bo'yicha) va video bog'lanishlari o'qiladi. "Eng qiyinlari" uchun umumiy statistika
    /// fonda tayyorlanadi va 10 daqiqa keshlanadi.
    /// </summary>
    public async Task<IActionResult> Index([FromQuery] QuestionFilter f, [FromServices] IMemoryCache cache, [FromServices] IServiceScopeFactory scopes)
    {
        f.Size = f.Size is 25 or 50 or 100 ? f.Size : 25;
        f.Page = Math.Max(1, f.Page);
        var log = HttpContext.RequestServices.GetRequiredService<ILogger<QuestionController>>();
        var all = await _bank.AllAsync();
        var byTopic = await _bank.ByTopicAsync();
        var topicOf = byTopic.SelectMany(kv => kv.Value.Select(x => (x.Id, kv.Key))).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Key);

        var withVideo = new HashSet<int>();
        try { withVideo = (await _context.TopicLessonQuestions.AsNoTracking().Select(x => x.QuestionId).Distinct().ToListAsync()).ToHashSet(); }
        catch (Exception ex) { log.LogWarning(ex, "Video bog'lanishlarini o'qib bo'lmadi"); }

        static bool Empty(string? v) => string.IsNullOrWhiteSpace(v);
        IEnumerable<Question> q = all;
        if (f.Id is int id) q = q.Where(x => x.Id == id);
        if (!Empty(f.Q))
        {
            var s = f.Q!.Trim();
            q = q.Where(x => (x.QuestionUZ ?? "").Contains(s, StringComparison.OrdinalIgnoreCase)
                          || (x.QuestionRU ?? "").Contains(s, StringComparison.OrdinalIgnoreCase)
                          || (x.QuestionUZK ?? "").Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        if (!Empty(f.Topic) && byTopic.TryGetValue(f.Topic!, out var tq))
        {
            var ids = tq.Select(x => x.Id).ToHashSet();
            q = q.Where(x => ids.Contains(x.Id));
        }
        if (f.Bilet is int b) q = q.Where(x => x.BiletNumber == b);
        if (f.Pool == "bilet") q = q.Where(x => x.BiletNumber != null);
        if (f.Pool == "other") q = q.Where(x => x.BiletNumber == null);
        if (f.Img == "yes") q = q.Where(x => !Empty(x.ImageUrl));
        if (f.Img == "no") q = q.Where(x => Empty(x.ImageUrl));
        if (f.Lang == "noru") q = q.Where(x => Empty(x.QuestionRU));
        if (f.Lang == "nouzk") q = q.Where(x => Empty(x.QuestionUZK));
        if (f.Video == "yes") q = q.Where(x => withVideo.Contains(x.Id));
        if (f.Video == "no") q = q.Where(x => !withVideo.Contains(x.Id));
        var filtered = q.ToList();

        // Qiyinlik bo'yicha saralash — umumiy statistika kerak: tayyor bo'lsa keshdan, bo'lmasa fonda boshlanadi
        Dictionary<int, (int N, int W)>? global = null;
        if (f.Sort == "hard")
        {
            global = cache.Get<Dictionary<int, (int N, int W)>>(StatsKey);
            if (global == null)
            {
                StartGlobalStats(cache, scopes, log);
                ViewBag.StatsPending = true;
            }
        }
        IEnumerable<Question> ordered = f.Sort switch
        {
            "old" => filtered.OrderBy(x => x.Id),
            "bilet" => filtered.OrderBy(x => x.BiletNumber == null).ThenBy(x => x.BiletNumber).ThenBy(x => x.BiletOrder).ThenBy(x => x.Id),
            "hard" when global != null => filtered.OrderByDescending(x => global.TryGetValue(x.Id, out var st) && st.N >= 5 ? (double)st.W / st.N : -1).ThenBy(x => x.Id),
            _ => filtered.OrderByDescending(x => x.Id),
        };
        var page = ordered.Skip((f.Page - 1) * f.Size).Take(f.Size).ToList();

        // Faqat sahifadagi savollar statistikasi (QuestionId indeksi bo'yicha — tez)
        var stats = new Dictionary<int, (int N, int W)>();
        if (global != null) stats = global;
        else if (page.Count > 0)
        {
            try
            {
                var pids = page.Select(x => x.Id).ToList();
                stats = (await _context.TestAnswers.AsNoTracking()
                    .Where(a => pids.Contains(a.QuestionId) && a.SelectedOptionId != null)
                    .GroupBy(a => a.QuestionId)
                    .Select(g => new { g.Key, N = g.Count(), W = g.Count(a => !a.IsCorrect) })
                    .ToListAsync()).ToDictionary(x => x.Key, x => (x.N, x.W));
            }
            catch (Exception ex) { log.LogWarning(ex, "Savollar statistikasini o'qib bo'lmadi"); }
        }

        return View(new QuestionListModel
        {
            Filter = f,
            Total = filtered.Count,
            Rows = page.Select(r => new QuestionListRow(r, topicOf.GetValueOrDefault(r.Id) ?? r.Topic?.Trim(),
                stats.TryGetValue(r.Id, out var st) ? st.N : 0, stats.TryGetValue(r.Id, out var st2) ? st2.W : 0,
                withVideo.Contains(r.Id))).ToList(),
            Topics = Topics.All.Where(byTopic.ContainsKey).Select(t => new TopicCount(t, byTopic[t].Count)).ToList(),
            BiletCount = (await _bank.BiletsAsync()).Count,
            KpiTotal = all.Count,
            KpiInBilets = all.Count(x => x.BiletNumber != null),
            KpiWithImage = all.Count(x => !Empty(x.ImageUrl)),
            KpiNoRu = all.Count(x => Empty(x.QuestionRU)),
            KpiNoUzk = all.Count(x => Empty(x.QuestionUZK)),
            KpiWithVideo = withVideo.Count,
        });
    }

    private const string StatsKey = "qs:globalStats";
    private static int _statsRunning;

    /// <summary>Barcha savollar bo'yicha javoblar statistikasi — fonda (sahifa kutmaydi), bir vaqtda faqat bittasi.</summary>
    private static void StartGlobalStats(IMemoryCache cache, IServiceScopeFactory scopes, ILogger log)
    {
        if (Interlocked.Exchange(ref _statsRunning, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.SetCommandTimeout(300);
                var dict = (await db.TestAnswers.AsNoTracking().Where(a => a.SelectedOptionId != null)
                    .GroupBy(a => a.QuestionId)
                    .Select(g => new { g.Key, N = g.Count(), W = g.Count(a => !a.IsCorrect) })
                    .ToListAsync()).ToDictionary(x => x.Key, x => (x.N, x.W));
                cache.Set(StatsKey, dict, TimeSpan.FromMinutes(10));
            }
            catch (Exception ex) { log.LogWarning(ex, "Savollar umumiy statistikasini hisoblab bo'lmadi"); }
            finally { Interlocked.Exchange(ref _statsRunning, 0); }
        });
    }

    /// <summary>Admin bosh sahifasi: savollar bazasi ko'rsatkichlari (xotiradagi keshdan).</summary>
    public async Task<IActionResult> Index1()
    {
        // "Jami savollar" — biletlardagi savollar (66 × 20 = 1320)
        var bilets = await _bank.BiletsAsync();
        var questions = bilets.SelectMany(b => b).ToList();
        var byTopic = await _bank.ByTopicAsync();

        var model = new AdminDashboardModel
        {
            TotalQuestions = questions.Count,
            BiletCount = bilets.Count,
            QuestionsPerBilet = bilets.Count == 0 ? 0 : bilets.Max(b => b.Count),
            TopicCount = byTopic.Count(kv => kv.Value.Count > 0),
            WithImage = questions.Count(q => !string.IsNullOrWhiteSpace(q.ImageUrl)),
            WithUz = questions.Count(q => !string.IsNullOrWhiteSpace(q.QuestionUZ)),
            WithRu = questions.Count(q => !string.IsNullOrWhiteSpace(q.QuestionRU)),
            WithUzk = questions.Count(q => !string.IsNullOrWhiteSpace(q.QuestionUZK)),
        };
        model.WithoutImage = model.TotalQuestions - model.WithImage;

        return View(model);
    }

    public async Task<IActionResult> Create()
    {
        var model = new Question
        {
            Options = new List<Option>()
        };
        ViewBag.NextBilet = await BiletPlacement.NextBiletAsync(_context);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Question question, int? CorrectIndex)
    {
        if (!PrepareAndValidate(question, CorrectIndex))
        {
            ViewBag.NextBilet = await BiletPlacement.NextBiletAsync(_context);
            return View(question);
        }

        // Rasm upload
        if (question.ImageFile != null)
            question.ImageUrl = await SaveImageAsync(question.ImageFile);

        // ptest biletlari bor bo'lsa — savol asosiy bazaga tushadi va oxirgi biletga joylanadi
        // (to'lgan bo'lsa yangi bilet ochiladi); eski tuzilishdagi bazada — eski savollar qatoriga
        question.Source = await BiletPlacement.HasBiletsAsync(_context) ? QuestionSources.Ptest : QuestionSources.Old;
        _context.Questions.Add(question);
        await _context.SaveChangesAsync();
        await BiletPlacement.PlaceUnassignedAsync(_context);
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var question = await _context.Questions
            .Include(q => q.Options)
            .FirstOrDefaultAsync(q => q.Id == id);

        if (question == null) return NotFound();
        return View(question);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Question question, int? CorrectIndex, bool RemoveImage = false)
    {
        var dbQuestion = await _context.Questions
            .Include(q => q.Options)
            .FirstOrDefaultAsync(q => q.Id == question.Id);

        if (dbQuestion == null) return NotFound();

        if (!PrepareAndValidate(question, CorrectIndex))
        {
            question.ImageUrl = dbQuestion.ImageUrl; // joriy rasm formada ko'rinib tursin
            return View(question);
        }

        var oldTopic = dbQuestion.Topic;

        // Savol matnlarini yangilash (3 til)
        dbQuestion.QuestionUZ = question.QuestionUZ;
        dbQuestion.QuestionRU = question.QuestionRU;
        dbQuestion.QuestionUZK = question.QuestionUZK;
        dbQuestion.ExplanationUZ = question.ExplanationUZ;
        dbQuestion.ExplanationRU = question.ExplanationRU;
        dbQuestion.ExplanationUZK = question.ExplanationUZK;
        dbQuestion.Topic = question.Topic;

        // Rasmni yangilash (agar yangi rasm tanlangan bo'lsa) yoki o'chirish
        if (question.ImageFile != null)
            dbQuestion.ImageUrl = await SaveImageAsync(question.ImageFile);
        else if (RemoveImage)
            dbQuestion.ImageUrl = null;

        // Eski variantlarni o'chirish
        _context.Options.RemoveRange(dbQuestion.Options);

        // Yangi variantlarni qo'shish
        foreach (var o in question.Options)
        {
            dbQuestion.Options.Add(new Option
            {
                OptionUZ = o.OptionUZ,
                OptionRU = o.OptionRU,
                OptionUZK = o.OptionUZK,
                IsCorrect = o.IsCorrect
            });
        }

        await _context.SaveChangesAsync();
        // Mavzu o'zgargan bo'lsa — Mavzular bo'limida ham yangilanadi; biletsiz qolgan savol biletga joylanadi
        await BiletPlacement.SyncTopicAsync(_context, dbQuestion, oldTopic);
        await BiletPlacement.PlaceUnassignedAsync(_context);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Formani tozalaydi va tekshiradi:
    ///  • to'liq bo'sh variantlar olib tashlanadi, to'g'ri javob belgilanadi;
    ///  • o'zbek (lotin) matni majburiy, rus/kirill ixtiyoriy;
    ///  • kirill maydoni bo'sh bo'lsa — lotin matnidan avtomatik o'giriladi.
    /// </summary>
    private bool PrepareAndValidate(Question question, int? correctIndex)
    {
        // Model binding o'zining (inglizcha) xabarlarini qo'shadi — o'rniga o'zimiz tekshiramiz
        ModelState.Clear();

        question.QuestionUZ = (question.QuestionUZ ?? "").Trim();
        question.QuestionRU = Clean(question.QuestionRU);
        question.QuestionUZK = Clean(question.QuestionUZK);
        question.ExplanationUZ = Clean(question.ExplanationUZ);
        question.ExplanationRU = Clean(question.ExplanationRU);
        question.ExplanationUZK = Clean(question.ExplanationUZK);
        question.Topic = Clean(question.Topic);

        // To'g'ri javobni bo'sh variantlarni olib tashlashdan oldin belgilaymiz
        for (int i = 0; i < question.Options.Count; i++)
            question.Options[i].IsCorrect = i == correctIndex;

        question.Options = question.Options
            .Where(o => !string.IsNullOrWhiteSpace(o.OptionUZ) ||
                        !string.IsNullOrWhiteSpace(o.OptionRU) ||
                        !string.IsNullOrWhiteSpace(o.OptionUZK))
            .ToList();

        foreach (var o in question.Options)
        {
            o.OptionUZ = (o.OptionUZ ?? "").Trim();
            o.OptionRU = Clean(o.OptionRU);
            o.OptionUZK = Clean(o.OptionUZK);
        }

        if (string.IsNullOrWhiteSpace(question.QuestionUZ))
            ModelState.AddModelError("", _t["err.questionUzRequired"]);
        if (question.Options.Count < 2)
            ModelState.AddModelError("", _t["err.minOptions"]);
        if (question.Options.Any(o => string.IsNullOrWhiteSpace(o.OptionUZ)))
            ModelState.AddModelError("", _t["err.optionUzRequired"]);
        if (!question.Options.Any(o => o.IsCorrect))
            ModelState.AddModelError("", _t["err.correctRequired"]);
        if (question.ImageFile != null && !IsAllowedImage(question.ImageFile))
            ModelState.AddModelError("", _t["err.imageType"]);

        if (!ModelState.IsValid) return false;

        // Kirill bo'sh qolgan bo'lsa — lotindan avtomatik transliteratsiya
        question.QuestionUZK ??= Translit.ToCyrillic(question.QuestionUZ);
        if (question.ExplanationUZ != null)
            question.ExplanationUZK ??= Translit.ToCyrillic(question.ExplanationUZ);
        foreach (var o in question.Options)
            o.OptionUZK ??= Translit.ToCyrillic(o.OptionUZ);

        return true;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" };

    private static bool IsAllowedImage(IFormFile file) =>
        file.Length > 0 && ImageExtensions.Contains(Path.GetExtension(file.FileName)) && HasImageSignature(file);

    /// <summary>Fayl haqiqatan rasmmi — kengaytmasiga emas, ichidagi sarlavhasiga qaraladi (JPEG, PNG, GIF, WebP, BMP).</summary>
    private static bool HasImageSignature(IFormFile file)
    {
        Span<byte> h = stackalloc byte[12];
        using var s = file.OpenReadStream();
        var n = s.Read(h);
        if (n < 4) return false;
        return (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF)                                   // JPEG
            || (h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47)                  // PNG
            || (h[0] == (byte)'G' && h[1] == (byte)'I' && h[2] == (byte)'F')                   // GIF
            || (h[0] == (byte)'B' && h[1] == (byte)'M')                                        // BMP
            || (n >= 12 && h[0] == (byte)'R' && h[1] == (byte)'I' && h[2] == (byte)'F' && h[3] == (byte)'F'
                && h[8] == (byte)'W' && h[9] == (byte)'E' && h[10] == (byte)'B' && h[11] == (byte)'P'); // WebP
    }

    /// <summary>
    /// Rasmni uploads papkasiga noyob nom bilan saqlaydi (masalan "photo_1_3f9c2a7b.jpg").
    /// Bir xil nomli fayl yuklansa ham boshqa savolning rasmi ustidan yozilmaydi.
    /// </summary>
    private async Task<string> SaveImageAsync(IFormFile file)
    {
        var dir = Path.Combine(_env.WebRootPath, "uploads");
        Directory.CreateDirectory(dir);

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var baseName = new string(Path.GetFileNameWithoutExtension(file.FileName)
            .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
            .ToArray()).Trim('_');
        if (baseName.Length == 0) baseName = "img";
        if (baseName.Length > 60) baseName = baseName[..60];

        string fileName, filePath;
        do
        {
            fileName = $"{baseName}_{Guid.NewGuid().ToString("N")[..8]}{ext}";
            filePath = Path.Combine(dir, fileName);
        } while (System.IO.File.Exists(filePath));

        // CreateNew — mavjud fayl hech qachon qayta yozilmaydi
        using var stream = new FileStream(filePath, FileMode.CreateNew);
        await file.CopyToAsync(stream);

        // Bazada faqat nom saqlanadi
        return fileName;
    }

    /// <summary>Rasmni o'chiradi — faqat boshqa hech bir savol uni ishlatmasa.</summary>
    private async Task DeleteImageIfUnusedAsync(string? imageUrl, int exceptQuestionId)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return;
        if (await _context.Questions.AnyAsync(q => q.Id != exceptQuestionId && q.ImageUrl == imageUrl)) return;

        // Bazada faqat fayl nomi saqlanadi; papka nomidan tashqariga chiqishning oldini olamiz
        var path = Path.Combine(_env.WebRootPath, "uploads", Path.GetFileName(imageUrl));
        if (System.IO.File.Exists(path))
            System.IO.File.Delete(path);
    }

    [HttpPost]
    public async Task<IActionResult> DeleteOption(int id)
    {
        var option = await _context.Options.FindAsync(id);
        if (option != null)
        {
            _context.Options.Remove(option);
            await _context.SaveChangesAsync();
        }
        return Ok();
    }

    /// <summary>O'chirishdan oldin tasdiqlash sahifasi: savol, variantlar, bilet, mavzu va u ishlatilgan joylar.</summary>
    [HttpGet]
    public async Task<IActionResult> Delete(int id)
    {
        var question = await _context.Questions.AsNoTracking()
            .Include(q => q.Options)
            .FirstOrDefaultAsync(q => q.Id == id);
        if (question == null) return NotFound();

        ViewBag.AnswerCount = await _context.TestAnswers.CountAsync(a => a.QuestionId == id);
        ViewBag.MistakeCount = await _context.UserMistakes.CountAsync(m => m.QuestionId == id && m.ResolvedAt == null);
        return View(question);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var question = await _context.Questions
            .Include(q => q.Options)
            .FirstOrDefaultAsync(q => q.Id == id);
        if (question != null)
        {
            var image = question.ImageUrl;
            // Ba'zi bazalarda variantlar savolga "o'chirishni taqiqlovchi" bog'lanish bilan ulangan —
            // shuning uchun avval variantlar, keyin savol o'chiriladi
            _context.Options.RemoveRange(question.Options);
            _context.Questions.Remove(question);
            // Saqlanganlar va video bog'lanishlarida ham qolmasin
            _context.SavedQuestions.RemoveRange(_context.SavedQuestions.Where(s => s.QuestionId == id));
            _context.TopicLessonQuestions.RemoveRange(_context.TopicLessonQuestions.Where(s => s.QuestionId == id));
            await _context.SaveChangesAsync();
            TempData["QsMsgKey"] = "qs.deleted";

            // Savol bazadan o'chgandan keyin — rasm boshqa savolda ishlatilmasa, fayl ham o'chadi
            await DeleteImageIfUnusedAsync(image, question.Id);
        }
        return RedirectToAction(nameof(Index));
    }
}
/// <summary>Savollar ro'yxati filtri (manzil satrida saqlanadi — orqaga qaytganda ham qoladi).</summary>
public class QuestionFilter
{
    public string? Q { get; set; }
    public int? Id { get; set; }
    public string? Topic { get; set; }
    public int? Bilet { get; set; }
    /// <summary>"bilet" — faqat biletlardagi, "other" — biletga kirmaganlar.</summary>
    public string? Pool { get; set; }
    public string? Img { get; set; }
    public string? Lang { get; set; }
    public string? Video { get; set; }
    /// <summary>new (standart), old, bilet, hard</summary>
    public string? Sort { get; set; }
    public int Page { get; set; } = 1;
    public int Size { get; set; } = 25;
}

public record QuestionListRow(Question Q, string? Topic, int Answers, int Wrong, bool HasVideo)
{
    public int WrongPct => Answers > 0 ? (int)Math.Round(100.0 * Wrong / Answers) : 0;
}

public class QuestionListModel
{
    public QuestionFilter Filter { get; set; } = new();
    public int Total { get; set; }
    public List<QuestionListRow> Rows { get; set; } = new();
    public List<TopicCount> Topics { get; set; } = new();
    public int BiletCount { get; set; }
    public int KpiTotal { get; set; }
    public int KpiInBilets { get; set; }
    public int KpiWithImage { get; set; }
    public int KpiNoRu { get; set; }
    public int KpiNoUzk { get; set; }
    public int KpiWithVideo { get; set; }
    public int Pages => Math.Max(1, (Total + Filter.Size - 1) / Filter.Size);
}
