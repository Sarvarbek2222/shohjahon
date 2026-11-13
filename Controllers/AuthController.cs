using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using propro;

[AllowAnonymous]
public class AuthController : Controller
{
    private readonly UserService _users;
    private readonly IStringLocalizer<SharedResource> _t;
    private readonly LoginGuard _guard;

    public AuthController(UserService users, IStringLocalizer<SharedResource> t, LoginGuard guard)
    {
        _users = users;
        _t = t;
        _guard = guard;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectAfterLogin(User.IsInRole(Roles.SuperAdmin), returnUrl);

        ViewBag.ReturnUrl = returnUrl;
        DeviceGuard.GetOrCreateDeviceId(HttpContext); // qurilma identifikatori login sahifasidayoq beriladi
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string username, string password, string? returnUrl)
    {
        ViewBag.ReturnUrl = returnUrl;
        ViewBag.Username = username;

        // Ko'p marta noto'g'ri parol — vaqtincha bloklangan (parolni tanlab topishdan himoya)
        var ip = DeviceGuard.ClientIp(HttpContext);
        if (_guard.Blocked(username, ip) is TimeSpan wait)
        {
            var min = (int)Math.Ceiling(wait.TotalMinutes);
            ViewBag.ErrorKey = "login.locked";
            ViewBag.ErrorArgs = min;
            ViewBag.Error = _t["login.locked"].Value.Replace("{n}", min.ToString());
            return View();
        }

        var user = await _users.VerifyAsync(username, password);
        if (user == null)
        {
            _guard.Fail(username, ip);
            ViewBag.ErrorKey = "login.error";
            ViewBag.Error = _t["login.error"].Value;
            return View();
        }
        _guard.Success(username);
        var check = await _users.CheckAccessAsync(user, HttpContext);
        if (check != LoginCheck.Ok)
        {
            var key = check switch
            {
                LoginCheck.Inactive => "login.inactive",
                LoginCheck.Expired => "login.expired",
                _ => "login.devicePending",
            };
            ViewBag.ErrorKey = key;
            ViewBag.Error = _t[key].Value;
            if (check == LoginCheck.DevicePending) ViewBag.Notice = true;
            return View();
        }

        await _users.SignInAsync(HttpContext, user);
        return RedirectAfterLogin(user.Role == Roles.SuperAdmin, returnUrl);
    }

    // GET ham qoldirildi — eski "Chiqish" havolalari ishlashda davom etadi
    [AcceptVerbs("GET", "POST")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public IActionResult AccessDenied() => View();

    private IActionResult RedirectAfterLogin(bool isSuperAdmin, string? returnUrl)
    {
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);

        return isSuperAdmin
            ? RedirectToAction("Index1", "Question")
            : RedirectToAction("Index", "Home");
    }
}
