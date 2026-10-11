/// <summary>Admin bosh sahifasi (Question/Index1): savollar bazasi ko'rsatkichlari.</summary>
public class AdminDashboardModel
{
    /// <summary>Jami savollar — biletlardagi savollar (66 × 20 = 1320).</summary>
    public int TotalQuestions { get; set; }
    public int BiletCount { get; set; }
    public int QuestionsPerBilet { get; set; }
    public int TopicCount { get; set; }

    public int WithImage { get; set; }
    public int WithoutImage { get; set; }

    // Tarjimalar
    public int WithUz { get; set; }
    public int WithRu { get; set; }
    public int WithUzk { get; set; }

    public int Percent(int part) => TotalQuestions == 0 ? 0 : (int)Math.Round(100.0 * part / TotalQuestions);
}
