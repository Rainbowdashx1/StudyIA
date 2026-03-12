namespace StudyIA;

public class UserAnswerRecord
{
    public int      Id         { get; set; }
    public int      QuestionId { get; set; }
    public string   UserAnswer { get; set; } = string.Empty;
    /// <summary>Puntuación de 0 a 100 asignada por la IA.</summary>
    public double   Score      { get; set; }
    public string   Feedback   { get; set; } = string.Empty;
    public DateTime AnsweredAt { get; set; }
}
