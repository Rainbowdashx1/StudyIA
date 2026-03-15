namespace StudyIA;

/// <summary>
/// Represents a logical section of a PDF document identified by its title
/// and the range of pages it spans.
/// </summary>
public sealed class PdfSection
{
    public int    Id        { get; set; }
    public int    PdfFileId { get; set; }
    public string Title     { get; set; } = string.Empty;
    public int    StartPage { get; set; }
    public int    EndPage   { get; set; }

    /// <summary>Human-readable page range, e.g. "pp. 3-7" or "p. 12".</summary>
    public string PageRange => StartPage == EndPage
        ? $"p. {StartPage}"
        : $"pp. {StartPage}\u2013{EndPage}";
}
