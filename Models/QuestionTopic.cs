using System.ComponentModel.DataAnnotations;

/// <summary>
/// Savol qaysi mavzu(lar)ga tegishli. ptest'da bitta savol bir nechta mavzuda uchraydi, shuning uchun
/// alohida jadval: bir savol — bir nechta mavzu. Topic — Topics.All dagi kirill kalit.
/// Question.Topic ustunida esa birinchi (asosiy) mavzu turadi — statistika va kuchsiz mavzular shuni ishlatadi.
/// </summary>
public class QuestionTopic
{
    public int Id { get; set; }
    public int QuestionId { get; set; }

    [MaxLength(255)]
    public string Topic { get; set; } = "";

    /// <summary>Savolning mavzu ichidagi tartibi (ptest'dagi tartib).</summary>
    public int SortOrder { get; set; }
}
