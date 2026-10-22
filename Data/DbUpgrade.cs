using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using propro.Localization;

/// <summary>
/// Ilova ishga tushganda bazani yangi tuzilishga keltiradi. Har safar xavfsiz qayta ishga tushirish mumkin (idempotent).
///
/// 0. Users jadvali yo'q bo'lsa yaratiladi; birorta super admin bo'lmasa — standart super admin qo'shiladi
///    (appsettings.json → "DefaultAdmin").
/// 1. Eski QuestionEN / OptionEN ustunlari (ular aslida o'zbek kirill matnini saqlagan) → QuestionUZK / OptionUZK
///    deb qayta nomlanadi. Ma'lumot ko'chmaydi, faqat ustun nomi o'zgaradi. Oldin to'liq zaxira nusxa olinadi:
///    Questions_backup_i18n, Options_backup_i18n.
/// 2. Rus va kirill ustunlari ixtiyoriy (NULL) qilinadi, Explanation* ustunlari yo'q bo'lsa qo'shiladi.
/// 3. Ma'lumotlar o'zbek (lotin) tiliga ko'chiriladi: lotin maydoni bo'sh yoki kirillda yozilgan bo'lsa,
///    matn kirill maydoniga o'tkaziladi va lotin maydoniga transliteratsiyasi yoziladi. Hech bir matn o'chirilmaydi.
/// </summary>
public static class DbUpgrade
{
    public static async Task RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbUpgrade");

        // Foydalanuvchilar jadvali va standart super admin — login ishlashi uchun eng muhim qadam
        try
        {
            await EnsureUsersTableAsync(db, log);
            await EnsureUserAccessAsync(db, log);
            await SiteSettings.LoadAsync(db);
            log.LogInformation("Bitta qurilma qoidasi (o'quvchilar): {State}", SiteSettings.SingleDevice ? "yoqilgan" : "o'chirilgan");
            var users = scope.ServiceProvider.GetRequiredService<UserService>();
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            await users.EnsureDefaultSuperAdminAsync(config, log);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Foydalanuvchilar jadvalini tayyorlashda xatolik");
        }

        try
        {
            await EnsureResultsTablesAsync(db, log);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Test natijalari jadvallarini tayyorlashda xatolik");
        }

        try
        {
            await EnsureSubscriptionTablesAsync(db, log);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Obuna va to'lov jadvallarini tayyorlashda xatolik");
        }

        try
        {
            await UpgradeSchemaAsync(db, log);
            await MigrateDataAsync(db, log);
        }
        catch (Exception ex)
        {
            // Sayt ishlashda davom etsin, xato jurnalga yoziladi
            log.LogError(ex, "Bazani 3 tilli tuzilishga o'tkazishda xatolik");
        }
    }

    private static async Task EnsureUsersTableAsync(AppDbContext db, ILogger log)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();

        if (await TableExistsAsync(conn, "Users")) return;

        await ExecAsync(conn, @"
CREATE TABLE `Users` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Username` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
    `FullName` varchar(128) CHARACTER SET utf8mb4 NULL,
    `PasswordHash` longtext CHARACTER SET utf8mb4 NOT NULL,
    `Role` varchar(32) CHARACTER SET utf8mb4 NOT NULL,
    `IsActive` tinyint(1) NOT NULL DEFAULT 1,
    `SecurityStamp` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `LastLoginAt` datetime(6) NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `IX_Users_Username` (`Username`)
) CHARACTER SET=utf8mb4");
        log.LogInformation("Users jadvali yaratildi");
    }

    /// <summary>
    /// Kirish muddati va bitta qurilma cheklovi: Users jadvaliga yangi ustunlar (bo'sh — ya'ni cheksiz muddat,
    /// qurilma hali bog'lanmagan) va qurilma so'rovlari jadvali. Mavjud ma'lumotlar o'zgarmaydi.
    /// </summary>
    private static async Task EnsureUserAccessAsync(AppDbContext db, ILogger log)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();

        var columns = new (string name, string type)[]
        {
            ("AccessExpiresAt", "datetime(6) NULL"),
            ("DeviceId", "varchar(64) CHARACTER SET utf8mb4 NULL"),
            ("DeviceInfo", "varchar(255) CHARACTER SET utf8mb4 NULL"),
            ("DeviceBoundAt", "datetime(6) NULL"),
            ("IsComputer", "tinyint(1) NOT NULL DEFAULT 0"),
            ("Phone", "varchar(32) CHARACTER SET utf8mb4 NULL"),
            ("RegisteredVia", "varchar(16) CHARACTER SET utf8mb4 NULL"),
        };
        foreach (var (name, type) in columns)
        {
            if (await ColumnExistsAsync(conn, "Users", name)) continue;
            await ExecAsync(conn, $"ALTER TABLE `Users` ADD COLUMN `{name}` {type}");
            log.LogInformation("Users.{Column} ustuni qo'shildi", name);
        }

        if (!await TableExistsAsync(conn, "AppSettings"))
        {
            await ExecAsync(conn, @"
CREATE TABLE `AppSettings` (
    `Key` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
    `Value` varchar(255) CHARACTER SET utf8mb4 NULL,
    PRIMARY KEY (`Key`)
) CHARACTER SET=utf8mb4");
            log.LogInformation("AppSettings jadvali yaratildi");
        }

        if (!await TableExistsAsync(conn, "UserDeviceRequests"))
        {
            await ExecAsync(conn, @"
CREATE TABLE `UserDeviceRequests` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `UserId` int NOT NULL,
    `DeviceId` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
    `DeviceInfo` varchar(255) CHARACTER SET utf8mb4 NULL,
    `IpAddress` varchar(64) CHARACTER SET utf8mb4 NULL,
    `Status` varchar(16) CHARACTER SET utf8mb4 NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `ResolvedAt` datetime(6) NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_UserDeviceRequests_Status_CreatedAt` (`Status`, `CreatedAt`),
    KEY `IX_UserDeviceRequests_UserId_DeviceId` (`UserId`, `DeviceId`),
    CONSTRAINT `FK_UserDeviceRequests_Users` FOREIGN KEY (`UserId`) REFERENCES `Users` (`Id`) ON DELETE CASCADE
) CHARACTER SET=utf8mb4");
            log.LogInformation("UserDeviceRequests jadvali yaratildi");
        }
    }

    /// <summary>Test natijalari, javoblar va xato savollar jadvallari (foydalanuvchi kabineti uchun).</summary>
    private static async Task EnsureResultsTablesAsync(AppDbContext db, ILogger log)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();

        // Savol o'chirilsa — unga bog'liq javoblar va xatolar ham o'chadi (Questions jadvali bo'lmasa FK qo'yilmaydi)
        bool hasQuestions = await TableExistsAsync(conn, "Questions");
        string qFk(string name) => hasQuestions
            ? $",\n    CONSTRAINT `{name}` FOREIGN KEY (`QuestionId`) REFERENCES `Questions` (`Id`) ON DELETE CASCADE"
            : "";

        if (!await TableExistsAsync(conn, "TestAttempts"))
        {
            await ExecAsync(conn, @"
CREATE TABLE `TestAttempts` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `UserId` int NOT NULL,
    `AttemptKey` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
    `TestType` varchar(32) CHARACTER SET utf8mb4 NOT NULL,
    `TestRef` varchar(255) CHARACTER SET utf8mb4 NULL,
    `IsErrorReview` tinyint(1) NOT NULL DEFAULT 0,
    `Total` int NOT NULL,
    `Correct` int NOT NULL,
    `Wrong` int NOT NULL,
    `Skipped` int NOT NULL,
    `Passed` tinyint(1) NOT NULL,
    `StartedAt` datetime(6) NOT NULL,
    `FinishedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `IX_TestAttempts_AttemptKey` (`AttemptKey`),
    KEY `IX_TestAttempts_UserId_FinishedAt` (`UserId`, `FinishedAt`),
    CONSTRAINT `FK_TestAttempts_Users` FOREIGN KEY (`UserId`) REFERENCES `Users` (`Id`) ON DELETE CASCADE
) CHARACTER SET=utf8mb4");
            log.LogInformation("TestAttempts jadvali yaratildi");
        }

        if (!await TableExistsAsync(conn, "TestAnswers"))
        {
            await CreateWithFallbackAsync(conn, log, qFk("FK_TestAnswers_Questions"), fk => @"
CREATE TABLE `TestAnswers` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `AttemptId` int NOT NULL,
    `QuestionId` int NOT NULL,
    `SelectedOptionId` int NULL,
    `IsCorrect` tinyint(1) NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_TestAnswers_AttemptId` (`AttemptId`),
    KEY `IX_TestAnswers_QuestionId` (`QuestionId`),
    CONSTRAINT `FK_TestAnswers_TestAttempts` FOREIGN KEY (`AttemptId`) REFERENCES `TestAttempts` (`Id`) ON DELETE CASCADE" + fk + @"
) CHARACTER SET=utf8mb4");
            log.LogInformation("TestAnswers jadvali yaratildi");
        }

        if (!await TableExistsAsync(conn, "UserMistakes"))
        {
            await CreateWithFallbackAsync(conn, log, qFk("FK_UserMistakes_Questions"), fk => @"
CREATE TABLE `UserMistakes` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `UserId` int NOT NULL,
    `QuestionId` int NOT NULL,
    `WrongCount` int NOT NULL,
    `LastWrongAt` datetime(6) NOT NULL,
    `ResolvedAt` datetime(6) NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `IX_UserMistakes_UserId_QuestionId` (`UserId`, `QuestionId`),
    CONSTRAINT `FK_UserMistakes_Users` FOREIGN KEY (`UserId`) REFERENCES `Users` (`Id`) ON DELETE CASCADE" + fk + @"
) CHARACTER SET=utf8mb4");
            log.LogInformation("UserMistakes jadvali yaratildi");
        }
    }

    /// <summary>
    /// Mobil ilova obunasi: tariflar va to'lovlar jadvallari. Tariflar bo'sh bo'lsa — 1 oy, 3 oy, 1 yil
    /// boshlang'ich narxlar bilan qo'shiladi (admin ilovada o'zgartiradi).
    /// </summary>
    private static async Task EnsureSubscriptionTablesAsync(AppDbContext db, ILogger log)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();

        if (!await TableExistsAsync(conn, "SubscriptionPlans"))
        {
            await ExecAsync(conn, @"
CREATE TABLE `SubscriptionPlans` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Name` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
    `Months` int NOT NULL,
    `Price` bigint NOT NULL,
    `Badge` varchar(64) CHARACTER SET utf8mb4 NULL,
    `IsActive` tinyint(1) NOT NULL DEFAULT 1,
    `SortOrder` int NOT NULL DEFAULT 0,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4");
            log.LogInformation("SubscriptionPlans jadvali yaratildi");
        }

        if (!await TableExistsAsync(conn, "Payments"))
        {
            await ExecAsync(conn, @"
CREATE TABLE `Payments` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `UserId` int NOT NULL,
    `PlanId` int NULL,
    `Months` int NOT NULL,
    `Days` int NOT NULL,
    `Amount` bigint NOT NULL,
    `Provider` varchar(16) CHARACTER SET utf8mb4 NOT NULL,
    `Status` varchar(16) CHARACTER SET utf8mb4 NOT NULL,
    `ProviderTransId` varchar(64) CHARACTER SET utf8mb4 NULL,
    `ProviderState` int NOT NULL DEFAULT 0,
    `ProviderCreateTime` bigint NOT NULL DEFAULT 0,
    `ProviderPerformTime` bigint NOT NULL DEFAULT 0,
    `ProviderCancelTime` bigint NOT NULL DEFAULT 0,
    `CancelReason` int NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `PaidAt` datetime(6) NULL,
    `PeriodStart` datetime(6) NULL,
    `PeriodEnd` datetime(6) NULL,
    `GrantedByUserId` int NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_Payments_UserId_CreatedAt` (`UserId`, `CreatedAt`),
    KEY `IX_Payments_Status_PaidAt` (`Status`, `PaidAt`),
    KEY `IX_Payments_Provider_ProviderTransId` (`Provider`, `ProviderTransId`),
    CONSTRAINT `FK_Payments_Users` FOREIGN KEY (`UserId`) REFERENCES `Users` (`Id`) ON DELETE CASCADE,
    CONSTRAINT `FK_Payments_SubscriptionPlans` FOREIGN KEY (`PlanId`) REFERENCES `SubscriptionPlans` (`Id`) ON DELETE SET NULL
) CHARACTER SET=utf8mb4");
            log.LogInformation("Payments jadvali yaratildi");
        }

        if (!await db.SubscriptionPlans.AnyAsync())
        {
            var now = DateTime.UtcNow;
            db.SubscriptionPlans.AddRange(
                new SubscriptionPlan { Name = "1 oy", Months = 1, Price = 49_000, SortOrder = 1, UpdatedAt = now },
                new SubscriptionPlan { Name = "3 oy", Months = 3, Price = 129_000, Badge = "Eng ommabop", SortOrder = 2, UpdatedAt = now },
                new SubscriptionPlan { Name = "1 yil", Months = 12, Price = 399_000, Badge = "Eng foydali", SortOrder = 3, UpdatedAt = now });
            await db.SaveChangesAsync();
            log.LogWarning("Boshlang'ich obuna tariflari qo'shildi — narxlarni admin panelida tekshiring!");
        }
    }

    private static async Task UpgradeSchemaAsync(AppDbContext db, ILogger log)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();

        if (!await TableExistsAsync(conn, "Questions")) return; // baza hali yaratilmagan

        bool renameQ = await ColumnExistsAsync(conn, "Questions", "QuestionEN") && !await ColumnExistsAsync(conn, "Questions", "QuestionUZK");
        bool renameO = await ColumnExistsAsync(conn, "Options", "OptionEN") && !await ColumnExistsAsync(conn, "Options", "OptionUZK");

        if (renameQ || renameO)
        {
            // Zaxira nusxa — hech narsa yo'qolmasligi uchun
            if (!await TableExistsAsync(conn, "Questions_backup_i18n"))
                await ExecAsync(conn, "CREATE TABLE `Questions_backup_i18n` AS SELECT * FROM `Questions`");
            if (!await TableExistsAsync(conn, "Options_backup_i18n"))
                await ExecAsync(conn, "CREATE TABLE `Options_backup_i18n` AS SELECT * FROM `Options`");
            log.LogInformation("Zaxira jadvallar yaratildi: Questions_backup_i18n, Options_backup_i18n");
        }

        if (renameQ)
        {
            await ExecAsync(conn, "ALTER TABLE `Questions` CHANGE COLUMN `QuestionEN` `QuestionUZK` longtext CHARACTER SET utf8mb4 NULL");
            log.LogInformation("Questions.QuestionEN → QuestionUZK");
        }
        if (renameO)
        {
            await ExecAsync(conn, "ALTER TABLE `Options` CHANGE COLUMN `OptionEN` `OptionUZK` longtext CHARACTER SET utf8mb4 NULL");
            log.LogInformation("Options.OptionEN → OptionUZK");
        }

        // Ikkala ustun ham bor bo'lsa (qo'lda qo'shilgan) — eski ustundagi matnni bo'sh joyga ko'chiramiz, eskisini o'chirmaymiz
        if (await ColumnExistsAsync(conn, "Questions", "QuestionEN"))
            await ExecAsync(conn, "UPDATE `Questions` SET `QuestionUZK` = `QuestionEN` WHERE (`QuestionUZK` IS NULL OR `QuestionUZK` = '') AND `QuestionEN` IS NOT NULL AND `QuestionEN` <> ''");
        if (await ColumnExistsAsync(conn, "Options", "OptionEN"))
            await ExecAsync(conn, "UPDATE `Options` SET `OptionUZK` = `OptionEN` WHERE (`OptionUZK` IS NULL OR `OptionUZK` = '') AND `OptionEN` IS NOT NULL AND `OptionEN` <> ''");

        // Tarjimalar ixtiyoriy
        await EnsureNullableAsync(conn, "Questions", "QuestionRU");
        await EnsureNullableAsync(conn, "Options", "OptionRU");
        await EnsureNullableAsync(conn, "Questions", "QuestionEN");
        await EnsureNullableAsync(conn, "Options", "OptionEN");

        foreach (var col in new[] { "QuestionUZK", "ExplanationUZ", "ExplanationUZK", "ExplanationRU" })
            if (!await ColumnExistsAsync(conn, "Questions", col))
                await ExecAsync(conn, $"ALTER TABLE `Questions` ADD COLUMN `{col}` longtext CHARACTER SET utf8mb4 NULL");

        if (!await ColumnExistsAsync(conn, "Options", "OptionUZK"))
            await ExecAsync(conn, "ALTER TABLE `Options` ADD COLUMN `OptionUZK` longtext CHARACTER SET utf8mb4 NULL");
    }

    /// <summary>Jadvalni Questions ga FK bilan yaratadi; eski jadval turi (masalan MyISAM) FK ga ruxsat bermasa — FK siz.</summary>
    private static async Task CreateWithFallbackAsync(DbConnection conn, ILogger log, string questionFk, Func<string, string> sql)
    {
        try
        {
            await ExecAsync(conn, sql(questionFk));
        }
        catch (Exception ex) when (questionFk.Length > 0)
        {
            log.LogWarning(ex, "Questions ga FK qo'yib bo'lmadi — jadval FK siz yaratiladi");
            await ExecAsync(conn, sql(""));
        }
    }

    private static async Task MigrateDataAsync(AppDbContext db, ILogger log)
    {
        var questions = await db.Questions.Include(q => q.Options).ToListAsync();
        int changed = 0;

        foreach (var q in questions)
        {
            bool c = false;

            var (uz, uzk, ru) = Normalize(q.QuestionUZ, q.QuestionUZK, q.QuestionRU);
            if (uz != q.QuestionUZ || uzk != q.QuestionUZK || ru != q.QuestionRU)
            {
                q.QuestionUZ = uz ?? ""; q.QuestionUZK = uzk; q.QuestionRU = ru; c = true;
            }

            var (euz, euzk, eru) = Normalize(q.ExplanationUZ, q.ExplanationUZK, q.ExplanationRU);
            if (euz != q.ExplanationUZ || euzk != q.ExplanationUZK || eru != q.ExplanationRU)
            {
                q.ExplanationUZ = euz; q.ExplanationUZK = euzk; q.ExplanationRU = eru; c = true;
            }

            foreach (var o in q.Options)
            {
                var (ouz, ouzk, oru) = Normalize(o.OptionUZ, o.OptionUZK, o.OptionRU);
                if (ouz != o.OptionUZ || ouzk != o.OptionUZK || oru != o.OptionRU)
                {
                    o.OptionUZ = ouz ?? ""; o.OptionUZK = ouzk; o.OptionRU = oru; c = true;
                }
            }

            if (c) changed++;
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync();
            log.LogInformation("{Count} ta savol o'zbek (lotin) tiliga ko'chirildi", changed);
        }
    }

    /// <summary>
    /// Bitta matn uchligini (lotin, kirill, rus) tartibga keltiradi. Qoidalar:
    ///  • lotin maydonida o'zbek kirill matni bo'lsa → kirill maydoniga (bo'sh bo'lsa) o'tadi, lotinga o'giriladi;
    ///  • lotin maydonida rus matni bo'lsa → rus maydoniga (bo'sh bo'lsa) o'tadi, lotin kirilldan olinadi (bo'lsa);
    ///  • lotin bo'sh bo'lsa → kirilldan transliteratsiya, u ham bo'lmasa rus matni nusxalanadi.
    /// Matn hech qachon o'chirilmaydi: agar joy band bo'lsa, asl qiymat o'z joyida qoladi.
    /// </summary>
    internal static (string? uz, string? uzk, string? ru) Normalize(string? uz, string? uzk, string? ru)
    {
        if (!string.IsNullOrWhiteSpace(uz) && Translit.IsMostlyCyrillic(uz))
        {
            if (LooksRussian(uz))
            {
                if (string.IsNullOrWhiteSpace(ru)) ru = uz;
                if (ru == uz && !string.IsNullOrWhiteSpace(uzk))
                    uz = Translit.ToLatin(uzk);
                // aks holda asl matn joyida qoladi (yo'qolmasin)
            }
            else
            {
                if (string.IsNullOrWhiteSpace(uzk)) uzk = uz;
                uz = Translit.ToLatin(uz);
            }
        }

        if (string.IsNullOrWhiteSpace(uz))
        {
            if (!string.IsNullOrWhiteSpace(uzk)) uz = Translit.ToLatin(uzk);
            else if (!string.IsNullOrWhiteSpace(ru)) uz = ru;
        }

        return (uz, uzk, ru);
    }

    /// <summary>Ruscha matnga xos harflar bor, o'zbek kirilliga xos harflar (ў қ ғ ҳ) yo'q.</summary>
    private static bool LooksRussian(string text)
    {
        var lower = text.ToLowerInvariant();
        bool uzbek = lower.IndexOfAny(new[] { 'ў', 'қ', 'ғ', 'ҳ' }) >= 0;
        bool russian = lower.IndexOfAny(new[] { 'ы', 'щ' }) >= 0;
        return russian && !uzbek;
    }

    private static async Task<bool> TableExistsAsync(DbConnection conn, string table) =>
        await ScalarLongAsync(conn,
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t",
            ("@t", table)) > 0;

    private static async Task<bool> ColumnExistsAsync(DbConnection conn, string table, string column) =>
        await ScalarLongAsync(conn,
            "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND COLUMN_NAME = @c",
            ("@t", table), ("@c", column)) > 0;

    private static async Task EnsureNullableAsync(DbConnection conn, string table, string column)
    {
        var notNull = await ScalarLongAsync(conn,
            "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND COLUMN_NAME = @c AND IS_NULLABLE = 'NO'",
            ("@t", table), ("@c", column));
        if (notNull > 0)
            await ExecAsync(conn, $"ALTER TABLE `{table}` MODIFY COLUMN `{column}` longtext CHARACTER SET utf8mb4 NULL");
    }

    private static async Task<long> ScalarLongAsync(DbConnection conn, string sql, params (string name, object value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task ExecAsync(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
