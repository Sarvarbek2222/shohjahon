using System.Collections.Concurrent;

/// <summary>
/// Parolni tanlab topishdan (brute-force) himoya. Faqat NOTO'G'RI urinishlar sanaladi:
///  • bitta login uchun 15 daqiqada 5 ta xato → shu login 15 daqiqaga bloklanadi;
///  • bitta IP manzildan 15 daqiqada 40 ta xato → shu IP 15 daqiqaga bloklanadi.
/// To'g'ri kirish login hisoblagichini tozalaydi. Oddiy foydalanuvchilar sezmaydi.
/// </summary>
public class LoginGuard
{
    public const int MaxPerUser = 5;
    public const int MaxPerIp = 40;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private sealed class Bucket
    {
        public readonly object Lock = new();
        public readonly Queue<DateTime> Fails = new();
        public DateTime LockedUntil;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private DateTime _lastSweep = DateTime.UtcNow;

    private static string UserKey(string? username) => "u:" + (username ?? "").Trim().ToLowerInvariant();
    private static string IpKey(string? ip) => "i:" + (ip ?? "?");

    /// <summary>Bloklangan bo'lsa — qancha kutish kerakligi; aks holda null.</summary>
    public TimeSpan? Blocked(string? username, string? ip)
    {
        var now = DateTime.UtcNow;
        foreach (var key in new[] { UserKey(username), IpKey(ip) })
        {
            if (_buckets.TryGetValue(key, out var b))
                lock (b.Lock)
                    if (b.LockedUntil > now) return b.LockedUntil - now;
        }
        return null;
    }

    public void Fail(string? username, string? ip)
    {
        Hit(UserKey(username), MaxPerUser);
        Hit(IpKey(ip), MaxPerIp);
        Sweep();
    }

    public void Success(string? username) => _buckets.TryRemove(UserKey(username), out _);

    private void Hit(string key, int max)
    {
        var now = DateTime.UtcNow;
        var b = _buckets.GetOrAdd(key, _ => new Bucket());
        lock (b.Lock)
        {
            while (b.Fails.Count > 0 && now - b.Fails.Peek() > Window) b.Fails.Dequeue();
            b.Fails.Enqueue(now);
            if (b.Fails.Count >= max)
            {
                b.LockedUntil = now + Window;
                b.Fails.Clear();
            }
        }
    }

    /// <summary>Eski yozuvlar xotirada to'planib qolmasin.</summary>
    private void Sweep()
    {
        var now = DateTime.UtcNow;
        if (now - _lastSweep < TimeSpan.FromMinutes(5)) return;
        _lastSweep = now;
        foreach (var kv in _buckets)
            lock (kv.Value.Lock)
                if (kv.Value.LockedUntil < now && (kv.Value.Fails.Count == 0 || now - kv.Value.Fails.Last() > Window))
                    _buckets.TryRemove(kv.Key, out _);
    }
}
