using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace propro.Controllers.Api
{
    /// <summary>
    /// Mobil ilova: ro'yxatdan o'tish va kirish. Ro'yxatdan o'tgan foydalanuvchining obunasi hali yo'q —
    /// ilova darhol tarif tanlash va to'lov sahifasini ochadi; to'lov tasdiqlangach testlar avtomatik ochiladi.
    /// </summary>
    [Route("api/auth")]
    public class AuthApiController : ApiControllerBase
    {
        private static readonly Regex DeviceRx = new("^[A-Za-z0-9-]{8,64}$", RegexOptions.Compiled);
        private static readonly Regex PhoneRx = new(@"^\+?[0-9 ()-]{7,20}$", RegexOptions.Compiled);

        private readonly AppDbContext _db;
        private readonly UserService _users;
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;

        public AuthApiController(AppDbContext db, UserService users, IConfiguration config, IWebHostEnvironment env)
        {
            _db = db;
            _users = users;
            _config = config;
            _env = env;
        }

        public record RegisterRequest(string? Username, string? FullName, string? Phone, string? Password, string? DeviceId, string? DeviceInfo);
        public record LoginRequest(string? Username, string? Password, string? DeviceId, string? DeviceInfo);
        public record AuthResponse(string Token, ApiUser User, SubscriptionStatus Subscription);

        [HttpPost("register")]
        [AllowAnonymous]
        public async Task<IActionResult> Register([FromBody] RegisterRequest req)
        {
            var username = req.Username?.Trim();
            if (!UserService.IsValidUsername(username))
                return Error(400, "username_invalid", "Login 3–64 ta belgi: lotin harflari, raqamlar, '.', '_' va '-'.");
            if (string.IsNullOrEmpty(req.Password) || req.Password.Length < UserService.MinPasswordLength)
                return Error(400, "password_short", "Parol kamida 6 ta belgidan iborat bo'lishi kerak.");
            if (string.IsNullOrWhiteSpace(req.FullName))
                return Error(400, "fullname_required", "Ism-familiyani kiriting.");
            var phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim();
            if (phone != null && !PhoneRx.IsMatch(phone))
                return Error(400, "phone_invalid", "Telefon raqami noto'g'ri.");
            if (req.DeviceId == null || !DeviceRx.IsMatch(req.DeviceId))
                return Error(400, "device_invalid", "Qurilma aniqlanmadi.");
            if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Username == username))
                return Error(409, "username_taken", "Bu login band. Boshqasini tanlang.");

            var user = new AppUser
            {
                Username = username!,
                FullName = req.FullName.Trim()[..Math.Min(128, req.FullName.Trim().Length)],
                Phone = phone,
                Role = Roles.User,
                IsActive = true,
                RegisteredVia = global::RegisteredVia.App,
                // Obuna hali sotib olinmagan — muddat "hozir" tugagan hisoblanadi
                AccessExpiresAt = DateTime.UtcNow,
            };
            _users.SetPassword(user, req.Password);
            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            await _users.CheckDeviceAsync(user, req.DeviceId, Describe(req.DeviceInfo), DeviceGuard.ClientIp(HttpContext));
            return Ok(await IssueAsync(user, req.DeviceId));
        }

        [HttpPost("login")]
        [AllowAnonymous]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            if (req.DeviceId == null || !DeviceRx.IsMatch(req.DeviceId))
                return Error(400, "device_invalid", "Qurilma aniqlanmadi.");

            var guard = HttpContext.RequestServices.GetRequiredService<LoginGuard>();
            var ip = DeviceGuard.ClientIp(HttpContext);
            if (guard.Blocked(req.Username, ip) is TimeSpan wait)
                return Error(429, "locked", $"Juda ko'p noto'g'ri urinish. {Math.Ceiling(wait.TotalMinutes)} daqiqadan keyin qayta urinib ko'ring.");
            var user = await _users.VerifyAsync(req.Username, req.Password);
            if (user == null)
            {
                guard.Fail(req.Username, ip);
                return Error(401, "login_failed", "Login yoki parol noto'g'ri!");
            }
            guard.Success(req.Username);
            if (!user.IsActive) return Error(403, "inactive", "Hisobingiz o'chirib qo'yilgan. Administratorga murojaat qiling.");

            var check = await _users.CheckDeviceAsync(user, req.DeviceId, Describe(req.DeviceInfo), DeviceGuard.ClientIp(HttpContext));
            if (check == LoginCheck.DevicePending)
                return Error(409, "device_pending", "Bu akkaunt boshqa qurilmaga bog'langan. Administratorga so'rov yuborildi — u tasdiqlagach, qaytadan kiring.");

            return Ok(await IssueAsync(user, req.DeviceId));
        }

        [HttpGet("me")]
        public async Task<IActionResult> Me()
        {
            var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == CurrentUserId);
            if (user == null) return Error(401, "unauthorized", "Qaytadan kiring.");
            return Ok(new { user = ApiUser.From(user), subscription = SubscriptionService.StatusOf(user, DateTime.UtcNow) });
        }

        private async Task<AuthResponse> IssueAsync(AppUser user, string deviceId)
        {
            user.LastLoginAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            var token = ApiTokens.Issue(user, deviceId, _config, _env);
            return new AuthResponse(token, ApiUser.From(user), SubscriptionService.StatusOf(user, DateTime.UtcNow));
        }

        private static string Describe(string? info)
        {
            var s = string.IsNullOrWhiteSpace(info) ? "Mobil ilova" : "Ilova · " + info.Trim();
            return s.Length > 255 ? s[..255] : s;
        }
    }
}
