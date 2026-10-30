using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Ochiq sahifa har daqiqada yuboradigan "men shu yerdaman" so'rovi (test ishlayotgan o'quvchi ham onlayn ko'rinadi).
/// Faollikni PresenceFilter belgilaydi — bu yerda faqat bo'sh javob.
/// </summary>
[Authorize]
public class PresenceController : Controller
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Ping() => NoContent();
}
