using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace StudyIA;

/// <summary>
/// Extracts text and structural information from PDF files using iText7.
/// </summary>
public static class PdfTextService
{
    // ── Page text extraction ───────────────────────────────────────────────

    /// <summary>
    /// Returns (pageNumber, text) pairs, skipping blank or unreadable pages.
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
                catch { /* unreadable page */ }
            }
        }
        catch { /* inaccessible or encrypted */ }

        return pages;
    }

    /// <summary>
    /// Truncates the page list to a maximum total character count while
    /// preserving original page numbers.
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

    /// <summary>
    /// Builds a lightweight summary for topic extraction.
    /// Prioritises short lines (3-80 chars) that are likely headings;
    /// falls back to a mid-page snippet when no short line is found.
    /// </summary>
    public static string BuildLightSummary(
        List<(int Page, string Text)> pages, int budgetPerPage = 280)
    {
        return string.Join("\n", pages.Select(p =>
        {
            var lines   = p.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var heading = lines.FirstOrDefault(l => l.Trim().Length is >= 3 and <= 80)
                          ?? string.Empty;
            var midStart = Math.Max(0, p.Text.Length / 2 - budgetPerPage / 2);
            var snippet  = heading.Length > 0
                ? heading.Trim()
                : p.Text.Substring(midStart,
                      Math.Min(budgetPerPage, p.Text.Length - midStart)).Trim();
            return $"Pag.{p.Page}: {snippet}";
        }));
    }

    /// <summary>
    /// Splits pages into blocks of at most <paramref name="chunkSize"/> characters.
    /// Pages are never cut in half; an oversized single page gets its own block truncated.
    /// </summary>
    public static List<List<(int Page, string Text)>> Chunk(
        List<(int Page, string Text)> pages, int chunkSize = 12_000)
    {
        var chunks  = new List<List<(int Page, string Text)>>();
        var current = new List<(int Page, string Text)>();
        var total   = 0;

        foreach (var (page, text) in pages)
        {
            var safe = text.Length > chunkSize ? text[..chunkSize] : text;

            if (total + safe.Length > chunkSize && current.Count > 0)
            {
                chunks.Add(current);
                current = [];
                total   = 0;
            }

            current.Add((page, safe));
            total += safe.Length;
        }

        if (current.Count > 0)
            chunks.Add(current);

        return chunks;
    }

    // ── Outline / Table of Contents ────────────────────────────────────────

    /// <summary>
    /// Builds a structural outline (index) of a PDF as an ordered list of sections,
    /// each with a title, start page and end page.
    ///
    /// Strategy (applied in order):
    ///   1. Read embedded PDF bookmarks (outline tree) when present.
    ///   2. Otherwise detect headings heuristically: on every page the text
    ///      fragment rendered with the largest font size that is short enough
    ///      to be a heading (3-100 chars) is treated as a section title.
    ///      Adjacent pages sharing the same title are merged into one section.
    /// </summary>
    public static List<PdfSection> ExtractOutline(string filePath)
    {
        try
        {
            using var reader = new PdfReader(filePath);
            using var doc    = new PdfDocument(reader);

            //var bookmarks = TryExtractBookmarks(doc);
            //if (bookmarks.Count > 0)
                //return FinalizeOutline(bookmarks, doc.GetNumberOfPages());

            return ExtractOutlineHeuristic(doc);
        }
        catch
        {
            return [];
        }
    }

    // ── Bookmark-based extraction ──────────────────────────────────────────

    private static List<(string Title, int Page)> TryExtractBookmarks(PdfDocument doc)
    {
        var catalog  = doc.GetCatalog();
        var outlines = catalog.GetPdfObject()
                              .GetAsDictionary(iText.Kernel.Pdf.PdfName.Outlines);
        if (outlines is null) return [];

        var first = outlines.GetAsDictionary(iText.Kernel.Pdf.PdfName.First);
        if (first is null) return [];

        var raw     = new List<(string Title, int Page)>();
        var current = first;

        while (current is not null)
        {
            var titleObj = current.GetAsString(iText.Kernel.Pdf.PdfName.Title);
            var title    = titleObj?.ToUnicodeString() ?? string.Empty;

            var dest = current.Get(iText.Kernel.Pdf.PdfName.Dest);
            var page = ResolveDestPage(dest, doc);

            if (!string.IsNullOrWhiteSpace(title) && page > 0)
                raw.Add((title.Trim(), page));

            current = current.GetAsDictionary(iText.Kernel.Pdf.PdfName.Next);
        }

        return raw;
    }

    private static int ResolveDestPage(
        iText.Kernel.Pdf.PdfObject? dest, PdfDocument doc)
    {
        if (dest is null) return 0;

        iText.Kernel.Pdf.PdfArray? arr = null;

        if (dest is iText.Kernel.Pdf.PdfArray a)
        {
            arr = a;
        }
        else if (dest is iText.Kernel.Pdf.PdfString s)
        {
            var named = doc.GetCatalog()
                           .GetNameTree(iText.Kernel.Pdf.PdfName.Dests)
                           .GetNames();
            if (named.TryGetValue(s, out var v) &&
                v is iText.Kernel.Pdf.PdfArray va)
                arr = va;
        }

        if (arr is null || arr.Size() == 0) return 0;

        var pageRef = arr.Get(0);
        if (pageRef is iText.Kernel.Pdf.PdfDictionary pageDict)
        {
            for (var i = 1; i <= doc.GetNumberOfPages(); i++)
                if (doc.GetPage(i).GetPdfObject() == pageDict)
                    return i;
        }
        return 0;
    }

    // ── Heuristic extraction ───────────────────────────────────────────────

    private static List<PdfSection> ExtractOutlineHeuristic(PdfDocument doc)
    {
        var candidates = new List<(string Title, int Page)>();

        for (var i = 1; i <= doc.GetNumberOfPages(); i++)
        {
            var listener  = new FontSizeListener();
            var processor = new PdfCanvasProcessor(listener);
            try   { processor.ProcessPageContent(doc.GetPage(i)); }
            catch { continue; }

            var heading = listener.GetDominantHeading();
            if (!string.IsNullOrWhiteSpace(heading))
                candidates.Add((heading, i));
        }

        // Merge consecutive pages that share the same heading
        var merged = new List<(string Title, int Page)>();
        foreach (var (title, page) in candidates)
        {
            if (merged.Count > 0 &&
                string.Equals(merged[^1].Title, title,
                              StringComparison.OrdinalIgnoreCase))
                continue;
            merged.Add((title, page));
        }

        return FinalizeOutline(merged, doc.GetNumberOfPages());
    }

    // ── Shared helper ──────────────────────────────────────────────────────

    private static List<PdfSection> FinalizeOutline(
        List<(string Title, int Page)> raw, int totalPages)
    {
        var sections = new List<PdfSection>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            sections.Add(new PdfSection
            {
                Title     = raw[i].Title,
                StartPage = raw[i].Page,
                EndPage   = i + 1 < raw.Count
                    ? raw[i + 1].Page - 1
                    : totalPages
            });
        }
        return sections;
    }
}

// ── FontSizeListener ──────────────────────────────────────────────────────────

/// <summary>
/// iText7 event listener that collects every text fragment together with its
/// rendered font size. Used to identify the dominant heading on a page.
/// </summary>
internal sealed class FontSizeListener : IEventListener
{
    private readonly record struct Fragment(string Text, float FontSize, float StartX, float EndX);

    private readonly List<Fragment> _fragments = [];

    public void EventOccurred(IEventData data, EventType type)
    {
        if (type != EventType.RENDER_TEXT) return;
        if (data is not TextRenderInfo tri)  return;

        var text = tri.GetText();
        if (string.IsNullOrEmpty(text) || text.Contains('\n')) return;

        // Use the ascent-to-descent distance as a stable font-size proxy.
        // This is consistent across all glyphs of the same font/size, including
        // accented characters rendered via composite/CID fonts where the text
        // matrix element [0] can vary per glyph and produce misleading values.
        float fontSize;
        try
        {
            var ascentY  = tri.GetAscentLine().GetStartPoint().Get(1);
            var descentY = tri.GetDescentLine().GetStartPoint().Get(1);
            fontSize     = MathF.Abs(ascentY - descentY);
        }
        catch
        {
            var scale = tri.GetTextMatrix().Get(0);
            fontSize  = MathF.Abs(tri.GetFontSize() * (scale != 0f ? scale : 1f));
        }

        if (fontSize <= 0) return;

        var startX = tri.GetBaseline().GetStartPoint().Get(0);
        var endX   = tri.GetBaseline().GetEndPoint().Get(0);

        _fragments.Add(new Fragment(text, fontSize, startX, endX));
    }

    public ICollection<EventType> GetSupportedEvents() =>
        [EventType.RENDER_TEXT];

    /// <summary>
    /// Returns the text of the largest-font fragment that qualifies as a heading
    /// (3–100 characters). Returns empty string when nothing qualifies.
    /// </summary>
    public string GetDominantHeading()
    {
        if (_fragments.Count == 0) return string.Empty;

        var maxSize = _fragments.Max(f => f.FontSize);

        // A heading must be rendered at least 10 % larger than the median font size.
        var sorted = _fragments.Select(f => f.FontSize).Order().ToList();
        var median = sorted[sorted.Count / 2];

        // NOTE: length is NOT filtered per-fragment here.
        // iText7 can split a single word into many single-character render events
        // (especially for accented chars in CID fonts). Filtering by length would
        // silently drop those characters, e.g. "Introducción" → "Introduc".
        var candidates = _fragments
            .Where(f => f.FontSize >= maxSize * 0.97f &&
                        f.FontSize >  median * 1.10f)
            .ToList();

        if (candidates.Count == 0) return string.Empty;

        // Smart join: inspect the X-axis gap between consecutive fragments.
        // If the gap is larger than ~25 % of the rendered font size it is a real
        // word-space; otherwise the fragments belong to the same word and are
        // concatenated directly (no inserted space).
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < candidates.Count; i++)
        {
            var frag = candidates[i];
            if (i > 0)
            {
                var prev = candidates[i - 1];
                if (frag.StartX - prev.EndX > frag.FontSize * 0.25f)
                    sb.Append(' ');
            }
            sb.Append(frag.Text);
        }

        var joined = sb.ToString().Trim();
        return joined.Length is >= 3 and <= 100 ? joined : string.Empty;
    }
}
