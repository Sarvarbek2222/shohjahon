using System.Collections.Concurrent;
using SkiaSharp;

/// <summary>
/// Rasmlarni tezlashtirish (wwwroot/uploads — savol rasmlari, wwwroot/images — sayt rasmlari). Asl fayllar (ko'pi 3–8 MB PNG) O'ZGARTIRILMAYDI —
/// brauzer so'raganda ularning kichraytirilgan WebP nusxasi berib yuboriladi va App_Data/img-cache da saqlanadi.
/// Sahifalar o'zgarmaydi: /uploads/rasm.png manzili o'sha-o'sha, faqat javob yengilroq.
///
///   /uploads/a.png          → eng uzun tomoni 1600px gacha WebP (test sahifasi, kattalashtirish oynasi)
///   /uploads/a.png?w=480    → 480px (kabinetdagi rasmlar)
///   /uploads/a.png?w=160    → 160px (admin ro'yxatidagi kichik rasmlar)
///   /uploads/a.png?w=800&fmt=webp → 800px (mobil ilova: Android/iOS "image/webp" deb so'ramaydi, lekin WebP'ni o'qiydi)
/// </summary>
public class ImageOptimizer
{
    public static readonly int[] Sizes = { 160, 480, 800, 1600 };
    public const int DefaultSize = 1600;
    private const int Quality = 80;

    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".webp" };

    /// <summary>
    /// ptest rasmlari (pt_*.webp) import paytida eng uzun tomoni 1280px, WebP 86 sifat bilan siqilgan.
    /// To'liq o'lchamda ularni qayta siqmaymiz (sifat 80 ga tushib ketmasin) — asl fayl beriladi.
    /// </summary>
    public const string PreOptimizedPrefix = "pt_";
    public const int PreOptimizedMaxSide = 1280;

    private readonly string _uploads;
    private readonly Dictionary<string, string> _roots;
    private readonly string _cacheRoot;
    private readonly ILogger<ImageOptimizer> _log;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflight = new();

    public ImageOptimizer(IWebHostEnvironment env, ILogger<ImageOptimizer> log)
    {
        _uploads = Path.Combine(env.WebRootPath, "uploads");
        _roots = new(StringComparer.OrdinalIgnoreCase)
        {
            ["uploads"] = _uploads,
            ["images"] = Path.Combine(env.WebRootPath, "images"),
        };
        _cacheRoot = Path.Combine(env.ContentRootPath, "App_Data", "img-cache");
        _log = log;
    }

    public string UploadsPath => _uploads;

    public static bool IsSupported(string fileName) =>
        Extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    public static int NormalizeSize(int? requested)
    {
        if (requested is not int w || w <= 0) return DefaultSize;
        foreach (var s in Sizes) if (w <= s) return s;
        return DefaultSize;
    }

    /// <summary>Optimallashtirilgan faylning yo'li; asl fayl allaqachon kichik bo'lsa yoki xato bo'lsa — null (asl beriladi).</summary>
    public Task<string?> GetAsync(string fileName, int size) => GetAsync("uploads", fileName, size);

    public Task<string?> GetAsync(string folder, string fileName, int size)
    {
        if (!_roots.TryGetValue(folder, out var root)) return Task.FromResult<string?>(null);

        // Faqat uploads ichidagi oddiy fayl nomi (yo'l bilan chiqib ketishning oldini olish)
        if (fileName != Path.GetFileName(fileName) || !IsSupported(fileName)) return Task.FromResult<string?>(null);

        if (size >= PreOptimizedMaxSide && fileName.StartsWith(PreOptimizedPrefix, StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<string?>(null);

        var source = Path.Combine(root, fileName);
        var info = new FileInfo(source);
        if (!info.Exists) return Task.FromResult<string?>(null);

        // Keshdagi nom asl faylning o'zgargan vaqtiga bog'liq — rasm almashtirilsa, kesh ham yangilanadi
        var cachePath = Path.Combine(_cacheRoot, folder.ToLowerInvariant(), size.ToString(),
            Path.GetFileNameWithoutExtension(fileName) + "." + info.LastWriteTimeUtc.Ticks.ToString("x") + ".webp");

        if (File.Exists(cachePath)) return Task.FromResult(Pick(cachePath, info));

        var lazy = _inflight.GetOrAdd(cachePath, key => new Lazy<Task<string?>>(() => Task.Run(() => Build(source, info, key, size))));
        return lazy.Value.ContinueWith(t =>
        {
            _inflight.TryRemove(cachePath, out _);
            return t.IsCompletedSuccessfully ? t.Result : null;
        });
    }

    /// <summary>WebP asl fayldan kichik bo'lsagina ishlatamiz.</summary>
    private static string? Pick(string cachePath, FileInfo original) =>
        new FileInfo(cachePath).Length < original.Length ? cachePath : null;

    private string? Build(string source, FileInfo original, string cachePath, int size)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(source);
            if (bitmap == null) return null;

            int w = bitmap.Width, h = bitmap.Height;
            double scale = Math.Min(1.0, (double)size / Math.Max(w, h));
            int nw = Math.Max(1, (int)Math.Round(w * scale)), nh = Math.Max(1, (int)Math.Round(h * scale));

            using var resized = scale < 1.0
                ? bitmap.Resize(new SKImageInfo(nw, nh, bitmap.ColorType, bitmap.AlphaType), new SKSamplingOptions(SKCubicResampler.Mitchell))
                : null;
            using var image = SKImage.FromBitmap(resized ?? bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Webp, Quality);
            if (data == null) return null;

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var tmp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var fs = File.Create(tmp)) data.SaveTo(fs);
            File.Move(tmp, cachePath, overwrite: true);

            return Pick(cachePath, original);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rasmni optimallashtirib bo'lmadi: {File}", source);
            return null;
        }
    }

    /// <summary>
    /// /uploads/* so'rovlari uchun middleware: brauzer WebP qabul qilsa — optimallashtirilgan nusxani beradi,
    /// aks holda (yoki xato bo'lsa) oddiy statik fayl sifatida asl rasm beriladi.
    /// </summary>
    public static async Task Middleware(HttpContext ctx, Func<Task> next)
    {
        var path = ctx.Request.Path.Value;
        if ((HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)) &&
            path != null &&
            (path.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/images/", StringComparison.OrdinalIgnoreCase)) &&
            (ctx.Request.Headers.Accept.ToString().Contains("image/webp") || ctx.Request.Query["fmt"] == "webp") &&
            IsSupported(path))
        {
            var optimizer = ctx.RequestServices.GetRequiredService<ImageOptimizer>();
            var slash = path.IndexOf('/', 1);
            var folder = path[1..slash];
            var fileName = Uri.UnescapeDataString(path[(slash + 1)..]);
            int.TryParse(ctx.Request.Query["w"], out var w);

            var file = await optimizer.GetAsync(folder, fileName, NormalizeSize(w));
            if (file != null)
            {
                ctx.Response.ContentType = "image/webp";
                ctx.Response.Headers.CacheControl = "public, max-age=2592000";
                ctx.Response.Headers.Vary = "Accept";
                await ctx.Response.SendFileAsync(file);
                return;
            }
        }
        await next();
    }
}

/// <summary>
/// Ilova ishga tushgach, fonda barcha savol rasmlarini asta-sekin optimallashtiradi — birinchi o'quvchi ham tez ochsin.
/// Bir vaqtda bitta rasm (serverni band qilib qo'ymaslik uchun). appsettings: "Images:Warmup": false — o'chirish.
/// </summary>
public class ImageWarmupService : BackgroundService
{
    private readonly ImageOptimizer _optimizer;
    private readonly IConfiguration _config;
    private readonly ILogger<ImageWarmupService> _log;

    public ImageWarmupService(ImageOptimizer optimizer, IConfiguration config, ILogger<ImageWarmupService> log)
    {
        _optimizer = optimizer;
        _config = config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Images:Warmup", true) || !Directory.Exists(_optimizer.UploadsPath)) return;

        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var files = Directory.EnumerateFiles(_optimizer.UploadsPath).Select(Path.GetFileName)
            .Where(f => f != null && ImageOptimizer.IsSupported(f)).ToList();
        int done = 0;
        var started = DateTime.UtcNow;

        foreach (var f in files)
        {
            if (stoppingToken.IsCancellationRequested) return;
            await _optimizer.GetAsync(f!, ImageOptimizer.DefaultSize);
            done++;
        }
        _log.LogInformation("Rasmlar optimallashtirildi: {Count} ta, {Seconds:0} soniya", done, (DateTime.UtcNow - started).TotalSeconds);
    }
}
