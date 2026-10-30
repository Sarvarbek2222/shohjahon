using System.Collections.Concurrent;

/// <summary>
/// Dastur ichidagi jonli efir (YouTube'siz). Admin brauzeri kamera/ekranni ~2 soniyalik mustaqil WebM bo'laklarga
/// yozib yuboradi (har biri o'z sarlavhasi va kalit kadri bilan — o'quvchi istalgan bo'lakdan qo'shila oladi).
/// Server oxirgi bo'laklarni FAQAT XOTIRADA ushlaydi (diskka yozilmaydi — efir yozib olinmaydi) va o'quvchilarga
/// beradi; yangi bo'lakni kutayotgan so'rovlar bo'lak kelishi bilan javob oladi (long-poll). Dars tugagach — o'chiriladi.
/// Bo'lak ~250 KB: Nginx ning standart 1 MB cheklovidan o'tadi, qo'shimcha sozlash kerak emas.
/// </summary>
public class LiveStreamStore
{
    private const int KeepInMemory = 40; // ~80 soniya
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(12);

    private sealed class Stream
    {
        public readonly object Lock = new();
        public readonly LinkedList<(int Seq, byte[] Data)> Recent = new();
        public int Last = -1;
        public bool Active;
        public DateTime LastAt;
        public string Mime = "video/webm";
        public TaskCompletionSource Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<int, Stream> _streams = new();

    private Stream Get(int lessonId) => _streams.GetOrAdd(lessonId, _ => new Stream());

    /// <summary>Yangi bo'lak. Raqamni server beradi (admin sahifani yangilasa ham ketma-ketlik buzilmaydi).</summary>
    public (int Seq, bool BecameActive) Push(int lessonId, byte[] data, string mime)
    {
        var s = Get(lessonId);
        int seq; bool became; TaskCompletionSource old;
        lock (s.Lock)
        {
            seq = ++s.Last;
            s.Recent.AddLast((seq, data));
            while (s.Recent.Count > KeepInMemory) s.Recent.RemoveFirst();
            became = !s.Active || DateTime.UtcNow - s.LastAt > ActiveWindow;
            s.Active = true;
            s.LastAt = DateTime.UtcNow;
            s.Mime = mime;
            old = s.Signal;
            s.Signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult();
        return (seq, became);
    }

    public void Stop(int lessonId)
    {
        var s = Get(lessonId);
        lock (s.Lock) s.Active = false;
    }

    /// <summary>Dars tugadi — efirning barcha bo'laklari xotiradan o'chiriladi.</summary>
    public void Clear(int lessonId)
    {
        if (_streams.TryRemove(lessonId, out var s))
            lock (s.Lock) { s.Recent.Clear(); s.Active = false; s.Signal.TrySetResult(); }
    }

    public (bool Active, int Last, string Mime) State(int lessonId)
    {
        var s = Get(lessonId);
        lock (s.Lock) return (s.Active && DateTime.UtcNow - s.LastAt < ActiveWindow, s.Last, s.Mime);
    }

    /// <summary>Bo'lak xotiradan; hali kelmagan bo'lsa — kelguncha kutadi (ko'pi bilan timeout). Eskirgan bo'lak — yo'q.</summary>
    public async Task<byte[]?> GetAsync(int lessonId, int seq, TimeSpan timeout, CancellationToken ct)
    {
        var s = Get(lessonId);
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            Task wait;
            lock (s.Lock)
            {
                foreach (var r in s.Recent) if (r.Seq == seq) return r.Data;
                if (seq <= s.Last) return null;
                wait = s.Signal.Task;
            }
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            try { await wait.WaitAsync(left, ct); }
            catch (TimeoutException) { return null; }
        }
    }
}
