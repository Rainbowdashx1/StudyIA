using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;

namespace StudyIA;

/// <summary>
/// Extrae el texto de cada página de un PDF usando iText7.
/// </summary>
public static class PdfTextService
{
    /// <summary>
    /// Devuelve la lista de (número de página, texto) omitiendo páginas vacías.
    /// Si el PDF no es accesible o está cifrado, devuelve lista vacía.
    /// </summary>
    public static List<(int Page, string Text)> ExtractPages(string filePath)
    {
        var pages = new List<(int, string)>();
        try
        {
            using var reader = new PdfReader(filePath);
            using var doc    = new PdfDocument(reader);

            for (var i = 1; i <= doc.GetNumberOfPages(); i++)
            {
                try
                {
                    var text = PdfTextExtractor.GetTextFromPage(doc.GetPage(i));
                    if (!string.IsNullOrWhiteSpace(text))
                        pages.Add((i, text));
                }
                catch { /* página no legible */ }
            }
        }
        catch { /* PDF inaccesible o cifrado */ }

        return pages;
    }

    /// <summary>
    /// Devuelve las mismas páginas pero truncadas a un máximo de caracteres total.
    /// Se conservan los números de página originales.
    /// </summary>
    public static List<(int Page, string Text)> Trim(
        List<(int Page, string Text)> pages, int maxChars)
    {
        var result = new List<(int, string)>();
        var total  = 0;

        foreach (var (page, text) in pages)
        {
            if (total >= maxChars) break;
            var take = Math.Min(text.Length, maxChars - total);
            result.Add((page, text[..take]));
            total += take;
        }

        return result;
    }
}
