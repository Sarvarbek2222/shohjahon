/// <summary>
/// Admin "Tahlil" sahifasi uchun tayyor ma'lumot. Hisob (barcha o'quvchilar, javoblar jadvali) og'ir bo'lgani uchun
/// u so'rov ichida emas, fonda bajariladi: sahifa doim oxirgi tayyor natijani darhol oladi, natija eskirgan bo'lsa
/// (StaleAfter) — fonda yangisi hisoblanadi. Faqat eng birinchi ochilishda (sayt endi ishga tushganda) kutiladi.
/// Ilova ishga tushgach bir oz o'tib natija oldindan tayyorlab qo'yiladi.
/// </summary>
public class AnalyticsSnapshot : BackgroundService
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(45);

    public sealed record Data(PlatformStats Platform, List<UserStats> Students, DateTime BuiltAt);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AnalyticsSnapshot> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Data? _data;
    private Task? _refreshing;

    public AnalyticsSnapshot(IServiceScopeFactory scopes, ILogger<AnalyticsSnapshot> log)
    {
        _scopes = scopes;
        _log = log;
    }

    /// <summary>Tayyor natija (eskirgan bo'lsa — fonda yangilanadi). force: hozir qayta hisoblab, kutib oladi.</summary>
    public async Task<Data> GetAsync(bool force = false)
    {
        var d = _data;
        if (d != null && !force)
        {
            if (DateTime.UtcNow - d.BuiltAt > StaleAfter) StartRefresh();
            return d;
        }
        return await RefreshAsync();
    }

    private void StartRefresh()
    {
        lock (this)
        {
            if (_refreshing is { IsCompleted: false }) return;
            _refreshing = Task.Run(async () =>
            {
                try { await RefreshAsync(); }
                catch (Exception ex) { _log.LogWarning(ex, "Tahlil ma'lumotini fonda yangilab bo'lmadi"); }
            });
        }
    }

    private async Task<Data> RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            // Boshqa so'rov hozirgina yangilagan bo'lsa — qayta hisoblamaymiz
            if (_data is { } fresh && DateTime.UtcNow - fresh.BuiltAt < TimeSpan.FromSeconds(3)) return fresh;

            using var scope = _scopes.CreateScope();
            var stats = scope.ServiceProvider.GetRequiredService<StatsService>();
            stats.BypassAnswerCache = true;
            var platform = await stats.GetPlatformStatsAsync();
            var students = (await stats.GetStudentSummariesAsync())
                .OrderByDescending(s => s.LastActivity ?? DateTime.MinValue)
                .ThenBy(s => s.User.Username)
                .ToList();
            var data = new Data(platform, students, DateTime.UtcNow);
            _data = data;
            return data;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Sayt ishga tushgach (baza yangilanishlari tugashini kutib) natijani oldindan tayyorlab qo'yamiz
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }
        try { await RefreshAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Tahlil ma'lumotini oldindan tayyorlab bo'lmadi"); }
    }
}
