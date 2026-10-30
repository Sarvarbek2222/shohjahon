using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

/// <summary>Sarlavhadagi qo'ng'iroqcha: bildirishnomalar lentasi va "o'qildi" belgisi.</summary>
public class NotificationsController : Controller
{
    private readonly NotificationService _notifications;

    public NotificationsController(NotificationService notifications) => _notifications = notifications;

    private int UserId => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Feed() => Json(await _notifications.FeedAsync(UserId));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Read(int? id)
    {
        await _notifications.MarkReadAsync(UserId, id);
        return NoContent();
    }
}
