using System.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Admin panelidan o'zgartiriladigan umumiy sozlamalar (bazadagi AppSettings jadvali: Key → Value).
/// Qiymatlar xotirada saqlanadi — har so'rovda bazaga murojaat qilinmaydi; ilova ishga tushganda yuklanadi
/// va admin o'zgartirganda darhol yangilanadi.
/// </summary>
public static class SiteSettings
{
    public const string SingleDeviceKey = "SingleDevice";

    private static volatile bool _singleDevice; // standart: o'chirilgan — o'quvchi istalgan qurilmadan kira oladi

    /// <summary>Bitta qurilma qoidasi o'quvchilar uchun yoqilganmi.</summary>
    public static bool SingleDevice => _singleDevice;

    /// <summary>
    /// Shu foydalanuvchiga bitta qurilma qoidasi qo'llanadimi: super admin — hech qachon; kompyuter hisobi — doim
    /// (u bitta kompyuter uchun); oddiy o'quvchi — faqat admin qoidani yoqqan bo'lsa.
    /// </summary>
    public static bool DeviceRuleApplies(AppUser user) => user.IsRestricted && (user.IsComputer || _singleDevice);

    public static async Task LoadAsync(AppDbContext db)
    {
        var value = await GetAsync(db, SingleDeviceKey);
        _singleDevice = value == "1";
    }

    public static async Task SetSingleDeviceAsync(AppDbContext db, bool on)
    {
        await SetAsync(db, SingleDeviceKey, on ? "1" : "0");
        _singleDevice = on;
    }

    private static async Task<string?> GetAsync(AppDbContext db, string key)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT `Value` FROM `AppSettings` WHERE `Key` = @k";
        var p = cmd.CreateParameter(); p.ParameterName = "@k"; p.Value = key; cmd.Parameters.Add(p);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static async Task SetAsync(AppDbContext db, string key, string value)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO `AppSettings` (`Key`, `Value`) VALUES (@k, @v) ON DUPLICATE KEY UPDATE `Value` = @v";
        var k = cmd.CreateParameter(); k.ParameterName = "@k"; k.Value = key; cmd.Parameters.Add(k);
        var v = cmd.CreateParameter(); v.ParameterName = "@v"; v.Value = value; cmd.Parameters.Add(v);
        await cmd.ExecuteNonQueryAsync();
    }
}
