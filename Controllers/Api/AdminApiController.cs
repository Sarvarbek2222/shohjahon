using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using propro.Localization;

namespace propro.Controllers.Api
{
    /// <summary>
    /// Mobil ilovadagi admin bo'limi: obuna statistikasi, to'lov qilganlar va admin qo'shganlar alohida ro'yxati,
    /// to'lovsiz foydalanuvchi qo'shish / muddat berish, tarif narxlarini o'zgartirish, to'lovlar tarixi.
    /// </summary>
    [Route("api/admin")]
    [Authorize(AuthenticationSchemes = ApiTokens.Scheme, Roles = Roles.SuperAdmin)]
    public class AdminApiController : ApiControllerBase
    {
        private readonly AppDbContext _db;
        private readonly UserService _users;
        private readonly SubscriptionService _subs;
        private readonly StatsService _stats;
        private readonly AiAnalysisService _ai;
        private readonly ViewText _l;

        public AdminApiController(AppDbContext db, UserService users, SubscriptionService subs, StatsService stats, AiAnalysisService ai, ViewText l)
        {
            _db = db;
            _users = users;
            _subs = subs;
            _stats = stats;
            _ai = ai;
            _l = l;
        }

        public record AdminUserDto(int Id, string Username, string? FullName, string? Phone, string Role, bool IsActive,
            string Source, SubscriptionStatus Subscription, DateTime CreatedAt, DateTime? LastLoginAt, string? DeviceInfo,
            long TotalPaid, DateTime? LastPaidAt);

        public record CreateUserRequest(string? Username, string? FullName, string? Phone, string? Password, int Months, int Days, bool Unlimited);
        public record GrantRequest(int Months, int Days, bool Unlimited);
        public record ActiveRequest(bool IsActive);
        public record UpdateUserRequest(string? Username, string? FullName, string? Phone, string? Role, bool IsActive, string? Password);
        public record PlanRequest(string? Name, int Months, long Price, string? Badge, bool IsActive, int SortOrder);

        // ── Statistika ──────────────────────────────────────────────────────────

        [HttpGet("dashboard")]
        public async Task<IActionResult> Dashboard()
        {
            var now = DateTime.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var paidIds = await _subs.PaidUserIds().ToListAsync();
            var users = await _db.Users.AsNoTracking().Where(u => u.Role == Roles.User).ToListAsync();

            var monthPayments = await _db.Payments.AsNoTracking()
                .Where(p => p.Status == PaymentStatus.Paid && p.PaidAt >= monthStart && PaymentProviders.Paid.Contains(p.Provider))
                .Select(p => p.Amount).ToListAsync();

            return Ok(new
            {
                totalUsers = users.Count,
                paidUsers = users.Count(u => paidIds.Contains(u.Id)),
                adminUsers = users.Count(u => !paidIds.Contains(u.Id) && u.RegisteredVia != RegisteredVia.App),
                unpaidAppUsers = users.Count(u => !paidIds.Contains(u.Id) && u.RegisteredVia == RegisteredVia.App),
                activeSubscriptions = users.Count(u => u.IsActive && !u.IsExpired(now)),
                expiringIn7Days = users.Count(u => u.AccessExpiresAt > now && u.AccessExpiresAt <= now.AddDays(7)),
                expired = users.Count(u => u.IsExpired(now)),
                monthRevenue = monthPayments.Sum(),
                monthPayments = monthPayments.Count,
                pendingDeviceRequests = await _users.PendingDeviceRequestCountAsync(),
            });
        }

        // ── Foydalanuvchilar ────────────────────────────────────────────────────

        /// <summary>filter: all | paid (to'lov qilib kirganlar) | admin (admin qo'shganlar) | unpaid (ilovada ro'yxatdan o'tib, to'lamaganlar) | expiring | expired</summary>
        [HttpGet("users")]
        public async Task<IActionResult> Users([FromQuery] string? filter, [FromQuery] string? q)
        {
            var now = DateTime.UtcNow;
            var paid = await _db.Payments.AsNoTracking()
                .Where(p => p.Status == PaymentStatus.Paid && PaymentProviders.Paid.Contains(p.Provider))
                .GroupBy(p => p.UserId)
                .Select(g => new { UserId = g.Key, Total = g.Sum(p => p.Amount), Last = g.Max(p => p.PaidAt) })
                .ToDictionaryAsync(x => x.UserId);

            var query = _db.Users.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var s = q.Trim();
                query = query.Where(u => u.Username.Contains(s) || (u.FullName != null && u.FullName.Contains(s)) || (u.Phone != null && u.Phone.Contains(s)));
            }
            var list = (await query.OrderByDescending(u => u.CreatedAt).ToListAsync())
                .Select(u => ToDto(u, paid.TryGetValue(u.Id, out var p) ? (p.Total, p.Last) : null, now));

            list = (filter ?? "all") switch
            {
                "paid" => list.Where(u => u.Source == "paid"),
                "admin" => list.Where(u => u.Source == "admin"),
                "unpaid" => list.Where(u => u.Source == "unpaid"),
                "expiring" => list.Where(u => u.Subscription.ExpiringSoon),
                "expired" => list.Where(u => !u.Subscription.Active),
                _ => list,
            };
            return Ok(list.ToList());
        }

        /// <summary>Admin to'lovsiz foydalanuvchi qo'shadi (muddat bilan yoki cheksiz).</summary>
        [HttpPost("users")]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
        {
            var username = req.Username?.Trim();
            if (!UserService.IsValidUsername(username))
                return Error(400, "username_invalid", "Login 3–64 ta belgi: lotin harflari, raqamlar, '.', '_' va '-'.");
            if (string.IsNullOrEmpty(req.Password) || req.Password.Length < UserService.MinPasswordLength)
                return Error(400, "password_short", "Parol kamida 6 ta belgidan iborat bo'lishi kerak.");
            if (await _db.Users.AnyAsync(u => u.Username == username))
                return Error(409, "username_taken", "Bu login band.");
            if (!req.Unlimited && req.Months <= 0 && req.Days <= 0)
                return Error(400, "period_required", "Muddatni kiriting yoki 'Cheksiz'ni tanlang.");

            var user = new AppUser
            {
                Username = username!,
                FullName = string.IsNullOrWhiteSpace(req.FullName) ? null : req.FullName.Trim(),
                Phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim(),
                Role = Roles.User,
                IsActive = true,
            };
            _users.SetPassword(user, req.Password);
            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            await _subs.GrantAsync(user, req.Unlimited ? 0 : req.Months, req.Unlimited ? 0 : req.Days, CurrentUserId);
            return Ok(ToDto(user, null, DateTime.UtcNow));
        }

        /// <summary>Muddat berish / uzaytirish (to'lovsiz).</summary>
        [HttpPost("users/{id:int}/grant")]
        public async Task<IActionResult> Grant(int id, [FromBody] GrantRequest req)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
            if (user == null) return Error(404, "not_found", "Foydalanuvchi topilmadi.");
            if (!req.Unlimited && req.Months <= 0 && req.Days <= 0)
                return Error(400, "period_required", "Muddatni kiriting yoki 'Cheksiz'ni tanlang.");
            await _subs.GrantAsync(user, req.Unlimited ? 0 : req.Months, req.Unlimited ? 0 : req.Days, CurrentUserId);
            return Ok(ToDto(user, null, DateTime.UtcNow));
        }

        [HttpPost("users/{id:int}/active")]
        public async Task<IActionResult> SetActive(int id, [FromBody] ActiveRequest req)
        {
            if (id == CurrentUserId && !req.IsActive) return Error(400, "self", "O'z hisobingizni nofaol qila olmaysiz.");
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
            if (user == null) return Error(404, "not_found", "Foydalanuvchi topilmadi.");
            user.IsActive = req.IsActive;
            user.SecurityStamp = Guid.NewGuid().ToString("N"); // eski sessiyalar yopiladi
            await _db.SaveChangesAsync();
            return Ok(new { user.Id, user.IsActive });
        }

        [HttpPost("users/{id:int}/reset-device")]
        public async Task<IActionResult> ResetDevice(int id)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
            if (user == null) return Error(404, "not_found", "Foydalanuvchi topilmadi.");
            user.DeviceId = null;
            user.DeviceInfo = null;
            user.DeviceBoundAt = null;
            await _db.SaveChangesAsync();
            return Ok(new { user.Id });
        }

        /// <summary>Bitta foydalanuvchi (tahrirlash sahifasi uchun) + qurilma so'rovlari soni.</summary>
        [HttpGet("users/{id:int}")]
        public async Task<IActionResult> GetUser(int id)
        {
            var u = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (u == null) return Error(404, "not_found", "Foydalanuvchi topilmadi.");
            var paid = await _db.Payments.AsNoTracking()
                .Where(p => p.UserId == id && p.Status == PaymentStatus.Paid && PaymentProviders.Paid.Contains(p.Provider))
                .GroupBy(p => p.UserId).Select(g => new { Total = g.Sum(p => p.Amount), Last = g.Max(p => p.PaidAt) })
                .FirstOrDefaultAsync();
            return Ok(new
            {
                user = ToDto(u, paid == null ? null : (paid.Total, paid.Last), DateTime.UtcNow),
                deviceBoundAt = u.DeviceBoundAt is DateTime db ? _l.Stamp(db) : null,
                hasDevice = u.DeviceId != null,
                pendingRequests = await _db.DeviceRequests.CountAsync(r => r.UserId == id && r.Status == DeviceRequestStatus.Pending),
                isSelf = id == CurrentUserId,
            });
        }

        /// <summary>Saytdagi "Foydalanuvchini tahrirlash": login, ism, telefon, rol, faollik, yangi parol.</summary>
        [HttpPut("users/{id:int}")]
        public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest req)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
            if (user == null) return Error(404, "not_found", "Foydalanuvchi topilmadi.");

            var username = req.Username?.Trim();
            if (!UserService.IsValidUsername(username))
                return Error(400, "username_invalid", "Login 3–64 ta belgi: lotin harflari, raqamlar, '.', '_' va '-'.");
            if (await _db.Users.AnyAsync(u => u.Username == username && u.Id != id))
                return Error(409, "username_taken", "Bu login band.");
            if (!Roles.IsValid(req.Role)) return Error(400, "role_invalid", "Rol noto'g'ri.");
            if (!string.IsNullOrEmpty(req.Password) && req.Password.Length < UserService.MinPasswordLength)
                return Error(400, "password_short", "Parol kamida 6 ta belgidan iborat bo'lishi kerak.");

            bool losesAdmin = user.Role == Roles.SuperAdmin && user.IsActive && (req.Role != Roles.SuperAdmin || !req.IsActive);
            if (losesAdmin && await _users.ActiveSuperAdminCountAsync(exceptUserId: user.Id) == 0)
                return Error(400, "last_admin", "Oxirgi faol administratorni o'zgartirib bo'lmaydi.");
            if (id == CurrentUserId && !req.IsActive)
                return Error(400, "self", "O'z hisobingizni nofaol qila olmaysiz.");

            bool accessChanged = user.Role != req.Role || user.IsActive != req.IsActive;
            user.Username = username!;
            user.FullName = string.IsNullOrWhiteSpace(req.FullName) ? null : req.FullName.Trim();
            user.Phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim();
            user.Role = req.Role!;
            user.IsActive = req.IsActive;
            if (!string.IsNullOrEmpty(req.Password)) _users.SetPassword(user, req.Password);
            else if (accessChanged) user.SecurityStamp = Guid.NewGuid().ToString("N");
            await _db.SaveChangesAsync();
            return Ok(ToDto(user, null, DateTime.UtcNow));
        }

        [HttpDelete("users/{id:int}")]
        public async Task<IActionResult> DeleteUser(int id)
        {
            if (id == CurrentUserId) return Error(400, "self", "O'zingizni o'chira olmaysiz.");
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
            if (user == null) return Error(404, "not_found", "Foydalanuvchi topilmadi.");
            if (user.Role == Roles.SuperAdmin && user.IsActive && await _users.ActiveSuperAdminCountAsync(exceptUserId: user.Id) == 0)
                return Error(400, "last_admin", "Oxirgi faol administratorni o'chirib bo'lmaydi.");
            _db.Users.Remove(user);
            await _db.SaveChangesAsync();
            return Ok(new { id });
        }

        // ── Qurilma so'rovlari ──────────────────────────────────────────────────

        [HttpGet("devices")]
        public async Task<IActionResult> Devices()
        {
            var pending = await _db.DeviceRequests.AsNoTracking().Include(r => r.User)
                .Where(r => r.Status == DeviceRequestStatus.Pending).OrderByDescending(r => r.CreatedAt).ToListAsync();
            var history = await _db.DeviceRequests.AsNoTracking().Include(r => r.User)
                .Where(r => r.Status != DeviceRequestStatus.Pending).OrderByDescending(r => r.ResolvedAt).Take(30).ToListAsync();
            object Row(DeviceRequest r) => new
            {
                r.Id, r.UserId, username = r.User?.Username, fullName = r.User?.FullName, currentDevice = r.User?.DeviceInfo,
                r.DeviceInfo, r.IpAddress, r.Status, createdAt = _l.Stamp(r.CreatedAt),
                resolvedAt = r.ResolvedAt is DateTime d ? _l.Stamp(d) : null,
            };
            return Ok(new { pending = pending.Select(Row), history = history.Select(Row) });
        }

        [HttpPost("devices/{id:int}/approve")]
        public async Task<IActionResult> ApproveDevice(int id) =>
            await _users.ApproveDeviceAsync(id) ? Ok(new { id }) : Error(404, "not_found", "So'rov topilmadi.");

        [HttpPost("devices/{id:int}/reject")]
        public async Task<IActionResult> RejectDevice(int id) =>
            await _users.RejectDeviceAsync(id) ? Ok(new { id }) : Error(404, "not_found", "So'rov topilmadi.");

        // ── Tahlil (saytdagi Analytics): umumiy ko'rsatkichlar, o'quvchilar, har birining natijalari ──

        [HttpGet("analytics")]
        public async Task<IActionResult> Analytics()
        {
            var p = await _stats.GetPlatformStatsAsync();
            var students = (await _stats.GetStudentSummariesAsync())
                .OrderByDescending(s => s.LastActivity ?? DateTime.MinValue).ThenBy(s => s.User.Username).ToList();
            return Ok(new
            {
                platform = new
                {
                    p.TotalUsers, p.ActiveUsers7d, p.TotalAttempts, p.Attempts7d,
                    accuracy = Math.Round(p.OverallAccuracy * 100),
                    examPassRate = Math.Round(p.ExamPassRate * 100),
                    readyCount = students.Count(s => s.IsReady),
                    studentCount = students.Count,
                },
                hardTopics = p.Topics.Select(t => new { name = _l.Topic(t.Topic), t.Answered, t.Correct, percent = Math.Round(t.Accuracy * 100) }),
                students = students.Select(s => new
                {
                    s.User.Id, s.User.Username, s.User.FullName,
                    readiness = s.ReadinessScore, s.IsReady, s.TotalAnswered, testsTaken = s.TotalAttempts,
                    averagePercent = Math.Round(s.AveragePercent), s.ActiveMistakes,
                    lastActive = s.LastActivity is DateTime la ? _l.Stamp(la) : null,
                }),
            });
        }

        [HttpGet("students/{id:int}")]
        public async Task<IActionResult> Student(int id)
        {
            var s = await _stats.GetUserStatsAsync(id);
            return s == null ? Error(404, "not_found", "Foydalanuvchi topilmadi.") : Ok(CabinetDtos.Overview(s, _l, _ai.IsEnabled));
        }

        [HttpGet("students/{id:int}/results")]
        public async Task<IActionResult> StudentResults(int id, [FromQuery] string? type, [FromQuery] int page = 1)
        {
            var s = await _stats.GetUserStatsAsync(id);
            return s == null ? Error(404, "not_found", "Foydalanuvchi topilmadi.") : Ok(CabinetDtos.Results(s, _l, type, page));
        }

        [HttpGet("students/{id:int}/mistakes")]
        public async Task<IActionResult> StudentMistakes(int id)
        {
            var s = await _stats.GetUserStatsAsync(id);
            return s == null ? Error(404, "not_found", "Foydalanuvchi topilmadi.") : Ok(await CabinetDtos.MistakesAsync(_db, s, _l));
        }

        [HttpGet("students/{id:int}/attempts/{attemptId:int}")]
        public async Task<IActionResult> StudentAttempt(int id, int attemptId)
        {
            var d = await CabinetController.LoadAttemptAsync(_db, attemptId, id);
            return d == null ? Error(404, "not_found", "Urinish topilmadi.") : Ok(CabinetDtos.Attempt(d, _l));
        }

        [HttpPost("students/{id:int}/ai")]
        public async Task<IActionResult> StudentAi(int id, CancellationToken ct)
        {
            if (!_ai.IsEnabled) return Error(404, "ai_disabled", _l["cab.aiDisabled"]);
            var s = await _stats.GetUserStatsAsync(id);
            if (s == null) return Error(404, "not_found", "Foydalanuvchi topilmadi.");
            var text = await _ai.AnalyzeAsync(s, _l.Lang, ct);
            return text == null ? Error(502, "ai_failed", _l["cab.aiError"]) : Ok(new { text });
        }

        // ── Tariflar (narxlar) ─────────────────────────────────────────────────

        [HttpGet("plans")]
        public async Task<IActionResult> Plans() => Ok(await _subs.AllPlansAsync());

        [HttpPut("plans/{id:int}")]
        public async Task<IActionResult> UpdatePlan(int id, [FromBody] PlanRequest req)
        {
            var plan = await _db.SubscriptionPlans.FirstOrDefaultAsync(p => p.Id == id);
            if (plan == null) return Error(404, "not_found", "Tarif topilmadi.");
            var invalid = Validate(req);
            if (invalid != null) return invalid;
            Apply(plan, req);
            await _db.SaveChangesAsync();
            return Ok(plan);
        }

        [HttpPost("plans")]
        public async Task<IActionResult> CreatePlan([FromBody] PlanRequest req)
        {
            var invalid = Validate(req);
            if (invalid != null) return invalid;
            var plan = new SubscriptionPlan();
            Apply(plan, req);
            _db.SubscriptionPlans.Add(plan);
            await _db.SaveChangesAsync();
            return Ok(plan);
        }

        // ── To'lovlar ───────────────────────────────────────────────────────────

        [HttpGet("payments")]
        public async Task<IActionResult> Payments([FromQuery] string? provider, [FromQuery] int take = 100)
        {
            var query = _db.Payments.AsNoTracking().Include(p => p.User).Include(p => p.Plan)
                .Where(p => p.Status != PaymentStatus.Pending || p.CreatedAt > DateTime.UtcNow.AddDays(-2));
            if (!string.IsNullOrWhiteSpace(provider)) query = query.Where(p => p.Provider == provider);
            var list = await query.OrderByDescending(p => p.CreatedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync();
            return Ok(list.Select(p => new
            {
                p.Id, p.UserId, username = p.User?.Username, fullName = p.User?.FullName, plan = p.Plan?.Name,
                p.Months, p.Days, p.Amount, p.Provider, p.Status, p.CreatedAt, p.PaidAt, p.PeriodEnd,
            }));
        }

        // ── yordamchilar ────────────────────────────────────────────────────────

        private ObjectResult? Validate(PlanRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Error(400, "name_required", "Tarif nomini kiriting.");
            if (req.Months < 1 || req.Months > 36) return Error(400, "months_invalid", "Davomiylik 1 dan 36 oygacha bo'lishi kerak.");
            if (req.Price < 1000 || req.Price > 100_000_000) return Error(400, "price_invalid", "Narx 1 000 so'mdan kam bo'lmasligi kerak.");
            return null;
        }

        private static void Apply(SubscriptionPlan plan, PlanRequest req)
        {
            plan.Name = req.Name!.Trim();
            plan.Months = req.Months;
            plan.Price = req.Price;
            plan.Badge = string.IsNullOrWhiteSpace(req.Badge) ? null : req.Badge.Trim();
            plan.IsActive = req.IsActive;
            plan.SortOrder = req.SortOrder;
            plan.UpdatedAt = DateTime.UtcNow;
        }

        private static AdminUserDto ToDto(AppUser u, (long Total, DateTime? Last)? paid, DateTime now)
        {
            var source = paid != null ? "paid" : u.RegisteredVia == RegisteredVia.App ? "unpaid" : "admin";
            return new AdminUserDto(u.Id, u.Username, u.FullName, u.Phone, u.Role, u.IsActive, source,
                SubscriptionService.StatusOf(u, now), u.CreatedAt, u.LastLoginAt, u.DeviceInfo,
                paid?.Total ?? 0, paid?.Last);
        }
    }
}
