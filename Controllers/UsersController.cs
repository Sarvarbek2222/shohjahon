using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using propro;

/// <summary>Foydalanuvchilarni boshqarish — faqat super admin.</summary>
[Authorize(Roles = Roles.SuperAdmin)]
public class UsersController : Controller
{
    private readonly AppDbContext _db;
    private readonly UserService _users;
    private readonly IStringLocalizer<SharedResource> _t;

    public UsersController(AppDbContext db, UserService users, IStringLocalizer<SharedResource> t)
    {
        _db = db;
        _users = users;
        _t = t;
    }

    private string T(string key) => _t[key];
    private int CurrentUserId => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    public async Task<IActionResult> Index(string? q, string? status, [FromServices] PresenceTracker presence)
    {
        var list = await _db.Users.AsNoTracking()
            .OrderByDescending(u => u.Role == Roles.SuperAdmin)
            .ThenBy(u => u.Username)
            .ToListAsync();

        ViewBag.CurrentUserId = CurrentUserId;
        ViewBag.Q = q;
        ViewBag.Status = status;
        ViewBag.PendingUsers = (await _db.DeviceRequests.AsNoTracking()
                .Where(r => r.Status == DeviceRequestStatus.Pending)
                .Select(r => r.UserId)
                .ToListAsync())
            .ToHashSet();
        ViewBag.Presence = presence;
        return View(list);
    }

    /// <summary>Ro'yxat sahifasi har 30 soniyada so'raydi: kim onlayn va oxirgi faollik vaqti (UTC, ISO).</summary>
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Presence([FromServices] PresenceTracker presence)
    {
        var users = await _db.Users.AsNoTracking().ToListAsync();
        return Json(users.Select(u => new
        {
            id = u.Id,
            online = presence.IsOnline(u.Id),
            seen = presence.LastSeen(u) is DateTime t ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("o") : null,
        }));
    }

    [HttpGet]
    public IActionResult Create() => View("Form", new UserForm { Role = Roles.User, IsActive = true, AccessMode = UserForm.Unlimited, AccessUnit = "m", AccessAmount = 1 });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(UserForm form)
    {
        ModelState.Clear();
        await ValidateAsync(form, isNew: true);
        if (!ModelState.IsValid) return View("Form", form);

        var user = new AppUser
        {
            Username = form.Username!.Trim(),
            FullName = Clean(form.FullName),
            Phone = UserService.NormalizePhone(form.Phone),
            Role = form.Role!,
            IsActive = form.IsActive,
            IsComputer = form.IsComputer && form.Role == Roles.User,
            AccessExpiresAt = form.ResolveExpiry(null, DateTime.UtcNow),
        };
        _users.SetPassword(user, form.Password!);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        TempData["MsgKey"] = "users.created";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        var form = new UserForm
        {
            Id = user.Id,
            Username = user.Username,
            FullName = user.FullName,
            Phone = UserService.FormatPhone(user.Phone),
            Role = user.Role,
            IsActive = user.IsActive,
            IsComputer = user.IsComputer,
            AccessMode = UserForm.Keep,
            AccessUnit = "m",
            AccessAmount = 1,
        };
        await FillInfoAsync(form, user);
        return View("Form", form);
    }

    /// <summary>Formada ko'rsatiladigan joriy holat: muddat va bog'langan qurilma.</summary>
    private async Task FillInfoAsync(UserForm form, AppUser user)
    {
        form.CurrentExpiresAt = user.AccessExpiresAt;
        form.DeviceInfo = user.DeviceInfo;
        form.DeviceBoundAt = user.DeviceBoundAt;
        form.HasDevice = user.DeviceId != null;
        form.PendingRequests = await _db.DeviceRequests.CountAsync(r => r.UserId == user.Id && r.Status == DeviceRequestStatus.Pending);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(UserForm form)
    {
        var user = await _db.Users.FindAsync(form.Id);
        if (user == null) return NotFound();

        ModelState.Clear();
        await ValidateAsync(form, isNew: false);

        // Oxirgi faol super adminni pasaytirish/o'chirish mumkin emas; o'zini ham nofaol qilib bo'lmaydi
        bool losesAdmin = user.Role == Roles.SuperAdmin && user.IsActive &&
                          (form.Role != Roles.SuperAdmin || !form.IsActive);
        if (losesAdmin && await _users.ActiveSuperAdminCountAsync(exceptUserId: user.Id) == 0)
            ModelState.AddModelError("", T("err.lastSuperadmin"));
        if (user.Id == CurrentUserId && !form.IsActive)
            ModelState.AddModelError("", T("err.deactivateSelf"));

        if (!ModelState.IsValid)
        {
            await FillInfoAsync(form, user);
            return View("Form", form);
        }

        bool isComputer = form.IsComputer && form.Role == Roles.User;
        // Hisob turi o'zgarsa ham qayta login — yangi cookie muddati (kompyuter: uzoq) qo'llansin
        bool accessChanged = user.Role != form.Role || user.IsActive != form.IsActive || user.IsComputer != isComputer;

        user.Username = form.Username!.Trim();
        user.FullName = Clean(form.FullName);
        user.Phone = UserService.NormalizePhone(form.Phone);
        user.Role = form.Role!;
        user.IsActive = form.IsActive;
        user.IsComputer = isComputer;
        user.AccessExpiresAt = form.ResolveExpiry(user.AccessExpiresAt, DateTime.UtcNow);

        if (!string.IsNullOrEmpty(form.Password))
            _users.SetPassword(user, form.Password); // SecurityStamp ham yangilanadi
        else if (accessChanged)
            user.SecurityStamp = Guid.NewGuid().ToString("N"); // eski sessiyalar yangi huquqlarga o'tsin

        await _db.SaveChangesAsync();

        // O'z parolini/rolini o'zgartirgan admin tizimdan chiqib ketmasligi uchun cookie yangilanadi
        if (user.Id == CurrentUserId)
            await _users.SignInAsync(HttpContext, user);

        TempData["MsgKey"] = "users.updated";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null)
        {
            TempData["ErrKey"] = "err.userNotFound";
            return RedirectToAction(nameof(Index));
        }
        if (user.Id == CurrentUserId)
        {
            TempData["ErrKey"] = "err.deleteSelf";
            return RedirectToAction(nameof(Index));
        }
        if (user.Role == Roles.SuperAdmin && user.IsActive &&
            await _users.ActiveSuperAdminCountAsync(exceptUserId: user.Id) == 0)
        {
            TempData["ErrKey"] = "err.lastSuperadmin";
            return RedirectToAction(nameof(Index));
        }

        _db.Users.Remove(user);
        await _db.SaveChangesAsync();

        TempData["MsgKey"] = "users.deleted";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Qurilmani bo'shatish: keyingi kirishda foydalanuvchi qaysi qurilmadan kirsa, o'shanga bog'lanadi.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetDevice(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        user.DeviceId = null;
        user.DeviceInfo = null;
        user.DeviceBoundAt = null;
        await _db.SaveChangesAsync();

        TempData["MsgKey"] = "dev.resetDone";
        return RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>Boshqa qurilmadan kirish so'rovlari (bildirishnomalar).</summary>
    [HttpGet]
    public async Task<IActionResult> Devices()
    {
        var pending = await _db.DeviceRequests.AsNoTracking().Include(r => r.User)
            .Where(r => r.Status == DeviceRequestStatus.Pending)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
        var history = await _db.DeviceRequests.AsNoTracking().Include(r => r.User)
            .Where(r => r.Status != DeviceRequestStatus.Pending)
            .OrderByDescending(r => r.ResolvedAt)
            .Take(30)
            .ToListAsync();

        ViewBag.History = history;
        ViewBag.SingleDevice = SiteSettings.SingleDevice;
        return View(pending);
    }

    /// <summary>Bitta qurilma qoidasini o'quvchilar uchun yoqish/o'chirish (kompyuter hisoblari doim bitta qurilmada).</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SingleDevice(bool on)
    {
        await SiteSettings.SetSingleDeviceAsync(_db, on);
        TempData["MsgKey"] = on ? "dev.ruleChangedOn" : "dev.ruleChangedOff";
        return RedirectToAction(nameof(Devices));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveDevice(int id)
    {
        if (await _users.ApproveDeviceAsync(id)) TempData["MsgKey"] = "dev.approved";
        else TempData["ErrKey"] = "dev.notFound";
        return RedirectToAction(nameof(Devices));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectDevice(int id)
    {
        if (await _users.RejectDeviceAsync(id)) TempData["MsgKey"] = "dev.rejected";
        else TempData["ErrKey"] = "dev.notFound";
        return RedirectToAction(nameof(Devices));
    }

    /// <summary>Admin panelidagi bildirishnoma belgisi uchun (bir necha soniyada bir so'raladi).</summary>
    [HttpGet]
    public async Task<IActionResult> PendingCount() =>
        Json(new { count = await _users.PendingDeviceRequestCountAsync() });

    private async Task ValidateAsync(UserForm form, bool isNew)
    {
        form.Username = form.Username?.Trim();

        // Telefon: oddiy o'quvchi uchun majburiy (kompyuter va super admin uchun — ixtiyoriy)
        bool isStudent = form.Role == Roles.User && !form.IsComputer;
        if (string.IsNullOrWhiteSpace(form.Phone))
        {
            if (isStudent) ModelState.AddModelError(nameof(form.Phone), T("err.phoneRequired"));
        }
        else if (UserService.NormalizePhone(form.Phone) == null)
            ModelState.AddModelError(nameof(form.Phone), T("err.phoneFormat"));

        if (string.IsNullOrEmpty(form.Username))
            ModelState.AddModelError(nameof(form.Username), T("err.usernameRequired"));
        else if (!UserService.IsValidUsername(form.Username))
            ModelState.AddModelError(nameof(form.Username), T("err.usernameFormat"));
        else if (await _db.Users.AnyAsync(u => u.Username == form.Username && u.Id != form.Id))
            ModelState.AddModelError(nameof(form.Username), T("err.usernameTaken"));

        if (!Roles.IsValid(form.Role))
            ModelState.AddModelError(nameof(form.Role), T("err.roleInvalid"));

        if (isNew && string.IsNullOrEmpty(form.Password))
            ModelState.AddModelError(nameof(form.Password), T("err.passwordRequired"));
        if (!string.IsNullOrEmpty(form.Password))
        {
            if (form.Password.Length < UserService.MinPasswordLength)
                ModelState.AddModelError(nameof(form.Password), T("err.passwordShort"));
            else if (form.Password != form.ConfirmPassword)
                ModelState.AddModelError(nameof(form.ConfirmPassword), T("err.passwordMismatch"));
        }

        if (form.AccessMode == UserForm.Set && !form.IsAmountValid)
            ModelState.AddModelError(nameof(form.AccessAmount), T("err.accessRange"));
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

/// <summary>Foydalanuvchi qo'shish/tahrirlash formasi.</summary>
public class UserForm
{
    public int Id { get; set; }
    public string? Username { get; set; }
    public string? FullName { get; set; }
    public string? Phone { get; set; }
    public string? Role { get; set; }
    public bool IsActive { get; set; }
    /// <summary>Hisob turi: true — o'quv markazidagi kompyuter, false — shaxsiy o'quvchi.</summary>
    public bool IsComputer { get; set; }
    public string? Password { get; set; }
    public string? ConfirmPassword { get; set; }

    public bool IsNew => Id == 0;

    // Kirish muddati: "keep" — o'zgartirmaslik, "set" — hozirdan boshlab N kun/oy, "unlimited" — cheksiz
    public const string Keep = "keep";
    public const string Set = "set";
    public const string Unlimited = "unlimited";
    public const int MaxDays = 365;
    public const int MaxMonths = 12;

    public string? AccessMode { get; set; }
    public int? AccessAmount { get; set; }
    /// <summary>"d" — kun, "m" — oy</summary>
    public string? AccessUnit { get; set; }

    public bool IsAmountValid => AccessAmount is int n && n >= 1 &&
        (AccessUnit == "m" ? n <= MaxMonths : AccessUnit == "d" && n <= MaxDays);

    public DateTime? ResolveExpiry(DateTime? current, DateTime utcNow) => AccessMode switch
    {
        Unlimited => null,
        Set when IsAmountValid => AccessUnit == "m" ? utcNow.AddMonths(AccessAmount!.Value) : utcNow.AddDays(AccessAmount!.Value),
        _ => current,
    };

    // Faqat ko'rsatish uchun (tahrirlash formasida)
    public DateTime? CurrentExpiresAt { get; set; }
    public string? DeviceInfo { get; set; }
    public DateTime? DeviceBoundAt { get; set; }
    public bool HasDevice { get; set; }
    public int PendingRequests { get; set; }
}
