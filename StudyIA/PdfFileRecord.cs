namespace StudyIA;

public class PdfFileRecord
{
    public int      Id         { get; set; }
    public string   FolderPath { get; set; } = string.Empty;
    public string   FilePath   { get; set; } = string.Empty;
    public string   FileName   { get; set; } = string.Empty;
    public string   FileHash   { get; set; } = string.Empty;
    public long     FileSize   { get; set; }
    public DateTime LastSeen   { get; set; }
}
