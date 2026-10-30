using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Controllers + Views. Kirill/rus harflari HTML va JSON'da &#x...; / \u... ga kodlanmasin (xavfsizlik saqlanadi: <, >, &, " baribir kodlanadi)
builder.Services.AddControllersWithViews(o => o.Filters.Add<PresenceFilter>()) // onlayn holati — har bir so'rovda
    .AddJsonOptions(o => o.JsonSerializerOptions.Encoder =
        System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All));
builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(o =>
    o.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));

// 🌐 3 til — standart ASP.NET Core lokalizatsiyasi:
//    tarjimalar Resources/SharedResource.resx (o'zbek lotin, standart), .ru.resx, .uz-Cyrl.resx;
//    til ".AspNetCore.Culture" cookie'sida saqlanadi. View'larda @L["kalit"], controllerlarda IStringLocalizer<SharedResource>.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(propro.Localization.Lang.Configure);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<propro.Localization.ViewText>();

// 🔐 Login: cookie autentifikatsiya + parollar PasswordHasher (PBKDF2) bilan xeshlanadi
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddScoped<UserService>();

// 📊 Test natijalari, kabinet tahlili va (ixtiyoriy) AI tahlili
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ResultsService>();
builder.Services.AddScoped<StatsService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddSingleton<VideoUploads>();
// Jonli darslar: real vaqt (SignalR), xona holati, eslatmalar
builder.Services.AddSignalR();
builder.Services.AddSingleton<LiveRoom>();
builder.Services.AddSingleton<LiveStreamStore>();
builder.Services.AddScoped<LiveService>();
builder.Services.AddHostedService<LiveReminderService>();
builder.Services.AddScoped<AiAnalysisService>();

// fetch() so'rovlari antiforgery tokenni shu sarlavhada yuboradi (AI tahlili tugmasi)
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

// ⚡ Tezlik: savol rasmlari WebP ga siqiladi (asl fayllar o'zgarmaydi), HTML/JSON/CSS/JS siqib yuboriladi
builder.Services.AddSingleton<ImageOptimizer>();
builder.Services.AddSingleton<QuestionBank>();
builder.Services.AddSingleton<PresenceTracker>();
// Admin tahlili fonda tayyorlanadi — sahifa kutmasdan ochiladi
builder.Services.AddSingleton<AnalyticsSnapshot>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AnalyticsSnapshot>());
builder.Services.AddSingleton<QuestionSearch>(); // kabinet → "Savol qidirish"
builder.Services.AddHostedService<ImageWarmupService>();
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o =>
    o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(o =>
    o.Level = System.IO.Compression.CompressionLevel.Fastest);

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Auth/Login";
        options.LogoutPath = "/Auth/Logout";
        options.AccessDeniedPath = "/Auth/AccessDenied";
        options.Cookie.Name = "propro.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;

        // O'chirilgan / nofaol / paroli o'zgargan foydalanuvchi darhol chiqariladi
        options.Events.OnValidatePrincipal = UserService.ValidatePrincipalAsync;

        // fetch() orqali JSON so'ralganda login sahifasiga yo'naltirish o'rniga 401/403 qaytaramiz
        options.Events.OnRedirectToLogin = ctx =>
        {
            if (IsApiRequest(ctx.Request)) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            if (IsApiRequest(ctx.Request)) ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            else ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
    });

// 📱 Mobil ilova: /api/... JWT token bilan ishlaydi (saytning cookie'siga ta'sir qilmaydi)
builder.Services.AddAuthentication()
    .AddJwtBearer(ApiTokens.Scheme, o => ApiTokens.Configure(o, builder.Configuration, builder.Environment));

// 💳 Obuna va to'lovlar (Click, Payme). Sozlamalar: appsettings.json → "Payments"
builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection("Payments"));
builder.Services.AddScoped<SubscriptionService>();

builder.Services.AddAuthorization(options =>
{
    // Butun dastur faqat login qilgan foydalanuvchilar uchun ([AllowAnonymous] — login va til sahifalari)
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

// MySQL DbContext
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        ServerVersion.AutoDetect(builder.Configuration.GetConnectionString("DefaultConnection"))
    )
);

var app = builder.Build();

// 🔐🌐 Bazani tayyorlash: Users jadvali + standart super admin, 3 tilli tuzilish (QuestionEN → QuestionUZK, zaxira bilan).
// Qayta ishga tushirish xavfsiz.
await DbUpgrade.RunAsync(app.Services);

// Middleware
/*if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}*/

app.UseResponseCompression();

// Savol rasmlari: brauzer WebP qabul qilsa — kichraytirilgan nusxa (App_Data/img-cache)
app.Use(ImageOptimizer.Middleware);

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // ?v=... (asp-append-version) bo'lgan fayllar o'zgarsa manzili ham o'zgaradi — uzoq keshlash xavfsiz
        var headers = ctx.Context.Response.Headers;
        var path = ctx.Context.Request.Path.Value ?? "";
        if (ctx.Context.Request.Query.ContainsKey("v"))
            headers.CacheControl = "public, max-age=31536000, immutable";
        else if (path.StartsWith("/uploads/") || path.StartsWith("/images/") || path.StartsWith("/lib/"))
            headers.CacheControl = "public, max-age=2592000";
        else
            headers.CacheControl = "public, max-age=3600";
    }
});

app.UseRequestLocalization();

// Qurilma identifikatori cookie'si muddatini yangilash (statik fayllardan keyin — faqat sahifa va API so'rovlarida)
app.Use(async (ctx, next) =>
{
    DeviceGuard.Refresh(ctx);
    await next();
});

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapHub<LiveHub>("/hubs/live");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}"); // Login qilinmagan bo'lsa — /Auth/Login

app.Run();

static bool IsApiRequest(HttpRequest request) =>
    request.Headers.Accept.Any(a => a != null && a.Contains("application/json")) ||
    (request.Path.Value?.Contains("/Get", StringComparison.OrdinalIgnoreCase) ?? false);
