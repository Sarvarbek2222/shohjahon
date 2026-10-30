using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Foydalanuvchilar onlayn/oflayn holati. Har bir so'rov (sayt sahifalari, test sahifalaridagi "ping" va mobil API)
/// xotirada belgilanadi; oxirgi faollik vaqti bazaga ham yoziladi (har bir foydalanuvchi uchun ko'pi bilan
/// SaveEvery da bir marta) — sayt qayta ishga tushsa ham "oxirgi faollik" yo'qolmaydi.
/// Onlayn: oxirgi OnlineWindow ichida faol bo'lgan (ochiq sahifa har daqiqada ping yuboradi).
/// </summary>
public class PresenceTracker
{
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<int, DateTime> _seen = new();
    private readonly ConcurrentDictionary<int, DateTime> _saved = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PresenceTracker> _log;

    public PresenceTracker(IServiceScopeFactory scopes, ILogger<PresenceTracker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public void Touch(int userId)
    {
        var now = DateTime.UtcNow;
        _seen[userId] = now;
        if (_saved.TryGetValue(userId, out var saved) && now - saved < SaveEvery) return;
        _saved[userId] = now;
        _ = SaveAsync(userId, now);
    }

    private async Task SaveAsync(int userId, DateTime at)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Users SET LastSeenAt = {at} WHERE Id = {userId}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Oxirgi faollik vaqtini yozib bo'lmadi (foydalanuvchi {Id})", userId);
        }
    }

    public bool IsOnline(int userId) =>
        _seen.TryGetValue(userId, out var t) && DateTime.UtcNow - t <= OnlineWindow;

    /// <summary>Oxirgi faollik (UTC): xotiradagi, bazadagi va oxirgi kirish vaqtlarining eng kechi.</summary>
    public DateTime? LastSeen(AppUser u)
    {
        DateTime? best = u.LastSeenAt;
        if (u.LastLoginAt is DateTime l && (best == null || l > best)) best = l;
        if (_seen.TryGetValue(u.Id, out var m) && (best == null || m > best)) best = m;
        return best;
    }
}

/// <summary>Har bir controller so'rovida (cookie yoki mobil API tokeni bilan kirgan) foydalanuvchini "faol" deb belgilaydi.</summary>
public class PresenceFilter : IAsyncActionFilter
{
    private readonly PresenceTracker _presence;

    public PresenceFilter(PresenceTracker presence) => _presence = presence;

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated == true &&
            int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            _presence.Touch(id);
        return next();
    }
}
