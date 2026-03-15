namespace StudyIA;

public class QuestionRecord
{
    public int      Id             { get; set; }
    public int      PdfFileId      { get; set; }
    public int?     PdfSectionId   { get; set; }
    public int      PageNumber     { get; set; }
    /// <summary>Fragmento de texto del PDF del que se originó la pregunta.</summary>
    public string   Context        { get; set; } = string.Empty;
    public string   QuestionText   { get; set; } = string.Empty;
    public string   ExpectedAnswer { get; set; } = string.Empty;
    public DateTime CreatedAt      { get; set; }

    // Dato desnormalizado para mostrar en UI sin JOIN adicional
    public string   FileName       { get; set; } = string.Empty;
}
