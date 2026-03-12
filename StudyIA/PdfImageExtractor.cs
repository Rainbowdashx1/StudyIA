using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace StudyIA;

public class ExtractedImage
{
    public int PageNumber { get; set; }
    public int ImageIndex { get; set; }
    public byte[] Bytes { get; set; } = [];
    public string MimeType { get; set; } = "image/jpeg";
    public string Base64 => Convert.ToBase64String(Bytes);
    public string DataUrl => $"data:{MimeType};base64,{Base64}";
}

public class PdfImageExtractor : IEventListener
{
    private readonly List<ExtractedImage> _images = new();
    private int _pageNumber;
    private int _imageIndex;

    public static List<ExtractedImage> ExtractFromFile(string pdfPath)
    {
        var allImages = new List<ExtractedImage>();
        using var reader = new PdfReader(pdfPath);
        using var pdfDoc = new PdfDocument(reader);

        for (int page = 1; page <= pdfDoc.GetNumberOfPages(); page++)
        {
            var extractor = new PdfImageExtractor { _pageNumber = page, _imageIndex = 1 };
            var processor = new PdfCanvasProcessor(extractor);
            processor.ProcessPageContent(pdfDoc.GetPage(page));
            allImages.AddRange(extractor._images);
        }

        return allImages;
    }

    public void EventOccurred(IEventData data, EventType type)
    {
        if (type != EventType.RENDER_IMAGE) return;

        try
        {
            var renderInfo = (ImageRenderInfo)data;
            var image = renderInfo.GetImage();
            var bytes = image.GetImageBytes(true);

            var mimeType = image.IdentifyImageFileExtension() switch
            {
                "png"           => "image/png",
                "jpg" or "jpeg" => "image/jpeg",
                "gif"           => "image/gif",
                _               => "image/jpeg"
            };

            _images.Add(new ExtractedImage
            {
                PageNumber = _pageNumber,
                ImageIndex = _imageIndex++,
                Bytes      = bytes,
                MimeType   = mimeType
            });
        }
        catch { /* Ignorar imagenes que no se puedan extraer */ }
    }

    public ICollection<EventType> GetSupportedEvents() =>
        new HashSet<EventType> { EventType.RENDER_IMAGE };
}