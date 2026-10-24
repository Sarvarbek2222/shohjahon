using Microsoft.EntityFrameworkCore;

/// <summary>
/// Admin qo'shgan savollarni biletlarga joylash. Asosiy bazadagi (ptest) har bir savol biror biletda turadi:
/// yangi savol oxirgi biletga qo'shiladi, oxirgi bilet to'lgan bo'lsa (20 ta) — yangi bilet ochiladi.
/// Savolning mavzusi QuestionTopics jadvaliga yoziladi (Mavzular bo'limi shundan tuziladi).
/// Shu tufayli savol hamma joyda chiqadi: biletlar, mavzular, test turlari va savollar soni.
/// </summary>
public static class BiletPlacement
{
    /// <summary>Bazada ptest biletlari bormi (yo'q bo'lsa — eski tuzilishdagi baza, biletlar boshqacha tuziladi).</summary>
    public static Task<bool> HasBiletsAsync(AppDbContext db) =>
        db.Questions.AnyAsync(q => q.Source == QuestionSources.Ptest && q.BiletNumber != null);

    /// <summary>Keyingi yangi savol qaysi biletga tushadi (ptest biletlari bo'lmasa — null).</summary>
    public static async Task<int?> NextBiletAsync(AppDbContext db)
    {
        var last = await db.Questions.AsNoTracking()
            .Where(q => q.Source == QuestionSources.Ptest && q.BiletNumber != null)
            .GroupBy(q => q.BiletNumber!.Value)
            .Select(g => new { Bilet = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Bilet)
            .FirstOrDefaultAsync();
        if (last == null) return null;
        return last.Count >= QuestionBank.QuestionsPerBilet ? last.Bilet + 1 : last.Bilet;
    }

    /// <summary>Biletsiz ptest savollarini (Id tartibida) biletlarga joylaydi. Nechta joylanganini qaytaradi.</summary>
    public static async Task<int> PlaceUnassignedAsync(AppDbContext db)
    {
        var pending = await db.Questions
            .Where(q => q.Source == QuestionSources.Ptest && q.BiletNumber == null)
            .OrderBy(q => q.Id)
            .ToListAsync();
        if (pending.Count == 0) return 0;

        var last = await db.Questions.AsNoTracking()
            .Where(q => q.Source == QuestionSources.Ptest && q.BiletNumber != null)
            .GroupBy(q => q.BiletNumber!.Value)
            .Select(g => new { Bilet = g.Key, Count = g.Count(), MaxOrder = g.Max(q => q.BiletOrder ?? 0) })
            .OrderByDescending(x => x.Bilet)
            .FirstOrDefaultAsync();
        if (last == null) return 0;

        int bilet = last.Bilet, count = last.Count, order = last.MaxOrder;
        foreach (var q in pending)
        {
            if (count >= QuestionBank.QuestionsPerBilet) { bilet++; count = 0; order = 0; }
            q.BiletNumber = bilet;
            q.BiletOrder = ++order;
            count++;
        }
        await db.SaveChangesAsync();

        foreach (var q in pending)
            await SyncTopicAsync(db, q, null, save: false);
        await db.SaveChangesAsync();
        return pending.Count;
    }

    /// <summary>
    /// Biletdagi savol mavzusini QuestionTopics jadvaliga moslaydi: mavzu o'zgargan bo'lsa eski bog'lanish
    /// olib tashlanadi, yangi mavzu bog'lanishi mavzu oxiriga qo'shiladi. Boshqa mavzu bog'lanishlariga tegilmaydi.
    /// </summary>
    public static async Task SyncTopicAsync(AppDbContext db, Question q, string? oldTopic, bool save = true)
    {
        if (q.Source != QuestionSources.Ptest || q.BiletNumber == null) return;

        var links = await db.QuestionTopics.Where(l => l.QuestionId == q.Id).ToListAsync();
        if (!string.IsNullOrEmpty(oldTopic) && oldTopic != q.Topic)
            db.QuestionTopics.RemoveRange(links.Where(l => l.Topic == oldTopic));

        if (!string.IsNullOrEmpty(q.Topic) && !links.Any(l => l.Topic == q.Topic))
        {
            var topic = q.Topic;
            var dbMax = await db.QuestionTopics.Where(l => l.Topic == topic).MaxAsync(l => (int?)l.SortOrder) ?? 0;
            // Shu so'rovda qo'shilgan, hali saqlanmagan bog'lanishlar ham hisobga olinadi
            var localMax = db.QuestionTopics.Local.Where(l => l.Topic == topic).Select(l => l.SortOrder).DefaultIfEmpty(0).Max();
            db.QuestionTopics.Add(new QuestionTopic { QuestionId = q.Id, Topic = topic, SortOrder = Math.Max(dbMax, localMax) + 1 });
        }

        if (save) await db.SaveChangesAsync();
    }
}
