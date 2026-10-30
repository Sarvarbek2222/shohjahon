using System.Collections.Concurrent;

/// <summary>
/// Jonli darslar xonalari (xotirada): hozir kim ulangan, qaysi ulanish qaysi davomat yozuviga tegishli,
/// reaksiyalar soni. Ma'lumotning doimiy qismi (davomat, savollar) bazada — bu faqat jonli holat.
/// </summary>
public class LiveRoom
{
    public sealed record Member(string ConnectionId, int UserId, string Name, bool IsAdmin, DateTime JoinedAt, int? AttendanceId);

    private sealed class Room
    {
        public readonly Dictionary<string, Member> Members = new();
        public int Reactions;
    }

    private readonly ConcurrentDictionary<int, Room> _rooms = new();
    private readonly ConcurrentDictionary<string, int> _connLesson = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastReaction = new();
    private readonly ConcurrentDictionary<(int, int), DateTime> _lastQuestion = new();

    private Room Get(int lessonId) => _rooms.GetOrAdd(lessonId, _ => new Room());

    public void Add(int lessonId, Member m)
    {
        var r = Get(lessonId);
        lock (r) r.Members[m.ConnectionId] = m;
        _connLesson[m.ConnectionId] = lessonId;
    }

    public (int LessonId, Member Member)? Remove(string connectionId)
    {
        if (!_connLesson.TryRemove(connectionId, out var lessonId)) return null;
        _lastReaction.TryRemove(connectionId, out _);
        var r = Get(lessonId);
        lock (r)
        {
            if (r.Members.Remove(connectionId, out var m)) return (lessonId, m);
        }
        return null;
    }

    public void SetAttendance(int lessonId, string connectionId, int attendanceId)
    {
        var r = Get(lessonId);
        lock (r)
        {
            if (r.Members.TryGetValue(connectionId, out var m)) r.Members[connectionId] = m with { AttendanceId = attendanceId };
        }
    }

    /// <summary>O'quvchilar (admin hisobga olinmaydi), bir kishi bir nechta oynada bo'lsa — bir marta.</summary>
    public List<Member> Students(int lessonId)
    {
        var r = Get(lessonId);
        lock (r)
        {
            return r.Members.Values.Where(m => !m.IsAdmin)
                .GroupBy(m => m.UserId).Select(g => g.OrderBy(m => m.JoinedAt).First())
                .OrderBy(m => m.JoinedAt).ToList();
        }
    }

    public List<Member> Connections(int lessonId)
    {
        var r = Get(lessonId);
        lock (r) return r.Members.Values.ToList();
    }

    public bool IsUserConnected(int lessonId, int userId)
    {
        var r = Get(lessonId);
        lock (r) return r.Members.Values.Any(m => m.UserId == userId);
    }

    public int AddReaction(int lessonId) => Interlocked.Increment(ref Get(lessonId).Reactions);
    public int Reactions(int lessonId) => Get(lessonId).Reactions;

    /// <summary>Reaksiya: bitta ulanishdan sekundiga ko'pi bilan 3 ta.</summary>
    public bool AllowReaction(string connectionId)
    {
        var now = DateTime.UtcNow;
        var last = _lastReaction.GetValueOrDefault(connectionId);
        if (now - last < TimeSpan.FromMilliseconds(330)) return false;
        _lastReaction[connectionId] = now;
        return true;
    }

    /// <summary>Savol: bitta o'quvchidan 15 soniyada ko'pi bilan bitta.</summary>
    public TimeSpan? QuestionCooldown(int lessonId, int userId)
    {
        var now = DateTime.UtcNow;
        if (_lastQuestion.TryGetValue((lessonId, userId), out var last) && now - last < TimeSpan.FromSeconds(15))
            return TimeSpan.FromSeconds(15) - (now - last);
        _lastQuestion[(lessonId, userId)] = now;
        return null;
    }

    public void Close(int lessonId) => _rooms.TryRemove(lessonId, out _);

    public static string Group(int lessonId) => "live-" + lessonId;
    public static string AdminGroup(int lessonId) => "live-admin-" + lessonId;
}
