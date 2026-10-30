using System.Text.RegularExpressions;

/// <summary>
/// Video darslar fayllari: bo'laklab yuklash va saqlash. Katta video bitta so'rovda yuborilmaydi —
/// brauzer uni 768 KB lik bo'laklarga bo'lib yuboradi (Nginx ning standart 1 MB cheklovidan o'tadi),
/// server bo'laklarni App_Data/video-tmp ichida ketma-ket yig'adi, oxirida App_Data/videos ga ko'chiradi.
/// Videolar ochiq papkada emas — faqat MediaController orqali (kirgan foydalanuvchiga, shaxsiy havola bilan) beriladi.
/// </summary>
public class VideoUploads
{
    public const long MaxBytes = 4L * 1024 * 1024 * 1024; // 4 GB
    public static readonly string[] Extensions = { ".mp4", ".webm", ".m4v", ".mov" };
    private static readonly Regex IdRx = new("^[a-f0-9]{32}$", RegexOptions.Compiled);

    private readonly string _tmpDir;
    private readonly string _videoDir;

    public VideoUploads(IWebHostEnvironment env)
    {
        _tmpDir = Path.Combine(env.ContentRootPath, "App_Data", "video-tmp");
        _videoDir = Path.Combine(env.ContentRootPath, "App_Data", "videos");
        Directory.CreateDirectory(_tmpDir);
        Directory.CreateDirectory(_videoDir);
    }

    public static bool IsValidId(string? id) => id != null && IdRx.IsMatch(id);

    private string PartPath(string id) => Path.Combine(_tmpDir, id + ".part");

    /// <summary>Bo'lakni qo'shadi. index — bo'lak tartibi (0 dan); qayta yuborilgan bo'lak (tarmoq uzilsa) qabul qilinadi.</summary>
    public async Task<(bool ok, string? error)> AppendAsync(string id, int index, long offset, IFormFile chunk)
    {
        if (!IsValidId(id)) return (false, "id");
        var path = PartPath(id);
        var current = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (index == 0 && offset == 0 && current > 0) { File.Delete(path); current = 0; }
        if (offset != current)
        {
            // Bu bo'lak avval yozilgan (javob yo'qolgan) — takror yozmaymiz
            if (offset + chunk.Length == current) return (true, null);
            return (false, "offset");
        }
        if (current + chunk.Length > MaxBytes) return (false, "size");
        await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
        await chunk.CopyToAsync(fs);
        return (true, null);
    }

    /// <summary>Yuklash tugadi: fayl videolar papkasiga noyob nom bilan ko'chiriladi. Natija — fayl nomi.</summary>
    public (string? name, string? error) Complete(string id, string originalName, long expectedSize)
    {
        if (!IsValidId(id)) return (null, "id");
        var ext = Path.GetExtension(originalName ?? "").ToLowerInvariant();
        if (!Extensions.Contains(ext)) return (null, "type");
        var path = PartPath(id);
        if (!File.Exists(path)) return (null, "missing");
        if (new FileInfo(path).Length != expectedSize) return (null, "size");
        var name = Guid.NewGuid().ToString("N") + ext;
        File.Move(path, Path.Combine(_videoDir, name));
        CleanupStale();
        return (name, null);
    }

    /// <summary>Video fayl yo'li (faqat shu sayt bergan nom: 32 belgili hex + kengaytma); yo'q bo'lsa — null.</summary>
    public string? PathOf(string? name)
    {
        if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, @"^[a-f0-9]{32}\.(mp4|webm|m4v|mov)$")) return null;
        var path = Path.Combine(_videoDir, name);
        return File.Exists(path) ? path : null;
    }

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        _ => "video/mp4",
    };

    public void Delete(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var path = Path.Combine(_videoDir, Path.GetFileName(name));
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Tugallanmay qolgan (1 kundan eski) bo'laklarni tozalash.</summary>
    private void CleanupStale()
    {
        foreach (var f in Directory.EnumerateFiles(_tmpDir, "*.part"))
        {
            try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) File.Delete(f); } catch { /* keyingi safar */ }
        }
    }

    /// <summary>YouTube havolasidan video identifikatori (watch?v=, youtu.be/, /embed/, /live/, /shorts/ yoki 11 belgili ID).</summary>
    public static string? ParseYouTubeId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();
        if (Regex.IsMatch(input, "^[A-Za-z0-9_-]{11}$")) return input;
        var m = Regex.Match(input, @"(?:youtube\.com/(?:watch\?(?:.*&)?v=|embed/|live/|shorts/|v/)|youtu\.be/)([A-Za-z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }
}
