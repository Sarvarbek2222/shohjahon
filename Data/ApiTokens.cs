using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Mobil ilova uchun kirish tokeni (JWT). Imzo kaliti appsettings.json → "Api:JwtKey" dan olinadi;
/// yo'q bo'lsa, birinchi ishga tushishda App_Data/jwt.key fayliga tasodifiy kalit yoziladi.
/// Har so'rovda foydalanuvchi bazadan tekshiriladi (sayt cookie'si kabi): o'chirilgan, nofaol, paroli o'zgargan
/// yoki boshqa qurilmaga o'tkazilgan foydalanuvchining tokeni darhol ishlamay qoladi.
/// Obuna tugagan bo'lsa token ishlayveradi — ilova to'lov sahifasini ko'rsatadi, testlar esa yopiq bo'ladi.
/// </summary>
public static class ApiTokens
{
    public const string Scheme = JwtBearerDefaults.AuthenticationScheme;
    public const string Issuer = "avtoprava-sam";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(60);

    private static SymmetricSecurityKey? _key;

    public static SymmetricSecurityKey Key(IConfiguration config, IWebHostEnvironment env)
    {
        if (_key != null) return _key;

        var configured = config["Api:JwtKey"];
        byte[] bytes;
        if (!string.IsNullOrWhiteSpace(configured) && configured.Length >= 32)
        {
            bytes = System.Text.Encoding.UTF8.GetBytes(configured);
        }
        else
        {
            var dir = Path.Combine(env.ContentRootPath, "App_Data");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "jwt.key");
            if (File.Exists(file))
            {
                bytes = Convert.FromBase64String(File.ReadAllText(file).Trim());
            }
            else
            {
                bytes = RandomNumberGenerator.GetBytes(64);
                File.WriteAllText(file, Convert.ToBase64String(bytes));
            }
        }
        _key = new SymmetricSecurityKey(bytes);
        return _key;
    }

    public static void Configure(JwtBearerOptions o, IConfiguration config, IWebHostEnvironment env)
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Issuer,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = Key(config, env),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role,
        };
        o.Events = new JwtBearerEvents { OnTokenValidated = ValidateUserAsync };
    }

    public static string Issue(AppUser user, string? deviceId, IConfiguration config, IWebHostEnvironment env)
    {
        var principal = UserService.CreatePrincipal(user, deviceId);
        var creds = new SigningCredentials(Key(config, env), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Issuer,
            claims: principal.Claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.Add(Lifetime),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task ValidateUserAsync(TokenValidatedContext ctx)
    {
        var idStr = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = ctx.Principal?.FindFirstValue(UserService.StampClaim);
        var device = ctx.Principal?.FindFirstValue(UserService.DeviceClaim);

        var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var user = int.TryParse(idStr, out var id)
            ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id)
            : null;

        bool otherDevice = user != null && SiteSettings.DeviceRuleApplies(user) && user.DeviceId != null && device != user.DeviceId;
        if (user == null || !user.IsActive || user.SecurityStamp != stamp || otherDevice)
            ctx.Fail("session_revoked");
    }
}
