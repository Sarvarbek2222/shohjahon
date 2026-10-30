using System.Text.Json;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// O'quvchilarga bildirishnomalar (sarlavhadagi qo'ng'iroqcha). Matn "notif.{kind}" kaliti va qiymatlardan
/// o'quvchi tanlagan tilda tuziladi. Jonli dars efirda bo'lsa — lenta bilan birga uning ma'lumoti ham qaytadi.
/// </summary>
public class NotificationService
{
    private readonly AppDbContext _db;

    public NotificationService(AppDbContext db) => _db = db;

    /// <summary>Barcha faol o'quvchilarga (oddiy foydalanuvchi roli) bir xil bildirishnoma.</summary>
    public async Task<int> NotifyStudentsAsync(string kind, object args, string? url)
    {
        var json = JsonSerializer.Serialize(args);
        var ids = await _db.Users.AsNoTracking()
            .Where(u => u.Role == Roles.User && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync();
        var now = DateTime.UtcNow;
        _db.Notifications.AddRange(ids.Select(id => new UserNotification
        {
            UserId = id, Kind = kind, ArgsJson = json, Url = url, CreatedAt = now,
        }));
        await _db.SaveChangesAsync();
        return ids.Count;
    }

    public async Task<object> FeedAsync(int userId)
    {
        var items = await _db.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(20)
            .ToListAsync();
        var unread = await _db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null);

        var now = DateTime.UtcNow;
        var live = await _db.LiveLessons.AsNoTracking()
            .Where(l => l.Status == LiveStatus.Live)
            .OrderByDescending(l => l.StartedAt)
            .Select(l => new { l.Id, l.Title })
            .FirstOrDefaultAsync();

        return new
        {
            unread,
            live,
            items = items.Select(n => new
            {
                n.Id,
                n.Kind,
                args = JsonSerializer.Deserialize<Dictionary<string, object>>(n.ArgsJson),
                n.Url,
                at = DateTime.SpecifyKind(n.CreatedAt, DateTimeKind.Utc).ToString("o"),
                read = n.ReadAt != null,
            }),
        };
    }

    public async Task MarkReadAsync(int userId, int? id)
    {
        var now = DateTime.UtcNow;
        var q = _db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null);
        if (id != null) q = q.Where(n => n.Id == id);
        await q.ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now));
    }
}
