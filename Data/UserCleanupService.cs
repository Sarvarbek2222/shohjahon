using Microsoft.EntityFrameworkCore;

/// <summary>
/// Foydalanuvchilarni tozalash (har 30 daqiqada):
///  1) 1 hafta (7 kun) davomida tizimga kirmagan va birorta ham savol ishlamagan o'quvchi avtomatik o'chiriladi
///     (o'chirilganlar ro'yxatiga tushadi) va adminga bildirishnoma boradi;
///  2) o'chirilganlar 3 kun davomida tiklanishi mumkin — keyin butunlay o'chiriladi.
/// Kompyuter hisoblari (o'quv markazi) va adminlar avtomatik o'chirilmaydi.
/// </summary>
public class UserCleanupService : BackgroundService
{
    public static readonly TimeSpan InactiveAfter = TimeSpan.FromDays(7);
    public static readonly TimeSpan RestoreWindow = TimeSpan.FromDays(3);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<UserCleanupService> _log;

    public UserCleanupService(IServiceScopeFactory scopes, ILogger<UserCleanupService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stop); }
        catch (OperationCanceledException) { return; }

        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var notify = scope.ServiceProvider.GetRequiredService<NotificationService>();
                await RunOnceAsync(db, notify, DateTime.UtcNow, _log);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Foydalanuvchilarni tozalashda xatolik");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(30), stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Bir marta tozalash. Qaytaradi: (avtomatik o'chirilganlar, butunlay o'chirilganlar).</summary>
    public static async Task<(int Deleted, int Purged)> RunOnceAsync(AppDbContext db, NotificationService notify, DateTime now, ILogger? log = null)
    {
        var limit = now - InactiveAfter;

        // Oxirgi faollik: kirish, saytdagi/ilovadagi oxirgi so'rov, oxirgi test; hech biri bo'lmasa — yaratilgan vaqt
        var candidates = await db.Users
            .Where(u => u.Role == Roles.User && !u.IsComputer && u.CreatedAt < limit &&
                        (u.LastLoginAt == null || u.LastLoginAt < limit) &&
                        (u.LastSeenAt == null || u.LastSeenAt < limit))
            .ToListAsync();
        var ids = candidates.Select(u => u.Id).ToList();
        var recentlyWorked = ids.Count == 0 ? new HashSet<int>() : (await db.TestAttempts.AsNoTracking()
            .Where(a => ids.Contains(a.UserId) && a.FinishedAt >= limit)
            .Select(a => a.UserId).Distinct().ToListAsync()).ToHashSet();

        var gone = candidates.Where(u => !recentlyWorked.Contains(u.Id)).ToList();
        foreach (var u in gone)
        {
            u.DeletedAt = now;
            u.DeletedReason = DeleteReasons.Inactive;
            u.SecurityStamp = Guid.NewGuid().ToString("N"); // ochiq sessiyalar yopiladi
        }
        if (gone.Count > 0)
        {
            await db.SaveChangesAsync();
            var admins = await db.Users.AsNoTracking().Where(u => u.Role == Roles.SuperAdmin && u.IsActive).Select(u => u.Id).ToListAsync();
            var names = string.Join(", ", gone.Take(8).Select(u => u.FullName ?? u.Username)) + (gone.Count > 8 ? " …" : "");
            await notify.NotifyUsersAsync(admins, "usersAutoDeleted", new { n = gone.Count, names }, "/Users#trashBox");
            log?.LogInformation("{Count} ta o'quvchi 1 hafta faol bo'lmagani uchun o'chirildi", gone.Count);
        }

        // Tiklash muddati o'tganlar — butunlay o'chiriladi (bog'liq ma'lumotlar bazada CASCADE bilan)
        var purgeBefore = now - RestoreWindow;
        var old = await db.Users.IgnoreQueryFilters().Where(u => u.DeletedAt != null && u.DeletedAt < purgeBefore).ToListAsync();
        if (old.Count > 0)
        {
            db.Users.RemoveRange(old);
            await db.SaveChangesAsync();
            log?.LogInformation("{Count} ta o'chirilgan foydalanuvchi butunlay o'chirildi (3 kun o'tdi)", old.Count);
        }
        return (gone.Count, old.Count);
    }
}
