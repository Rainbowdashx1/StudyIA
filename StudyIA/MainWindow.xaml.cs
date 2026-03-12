using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Win32;

namespace StudyIA;

public partial class MainWindow : Window
{
    private string _folderPath = string.Empty;
    private readonly AppDatabase _db = new();

    private const string KeyLastFolder = "LastFolder";

    public MainWindow()
    {
        InitializeComponent();
        LoadSavedFolder();
    }

    // Restaurar carpeta guardada al iniciar
    private async void LoadSavedFolder()
    {
        var saved = _db.GetSetting(KeyLastFolder);
        if (string.IsNullOrEmpty(saved) || !Directory.Exists(saved)) return;

        _folderPath            = saved;
        FolderPathBox.Text     = _folderPath;
        ScanBtn.IsEnabled      = true;
        QuestionsBtn.IsEnabled = true;
        PracticeBtn.IsEnabled  = true;
        await ScanFolderAsync();
    }

    // Seleccionar carpeta
    private async void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Seleccionar carpeta con archivos PDF"
        };

        if (dialog.ShowDialog() != true) return;

        _folderPath            = dialog.FolderName;
        FolderPathBox.Text     = _folderPath;
        ScanBtn.IsEnabled      = true;
        QuestionsBtn.IsEnabled = true;
        PracticeBtn.IsEnabled  = true;
        _db.SetSetting(KeyLastFolder, _folderPath);
        await ScanFolderAsync();
    }

    // Escanear manualmente
    private async void ScanBtn_Click(object sender, RoutedEventArgs e) =>
        await ScanFolderAsync();

    // Escanear carpeta: calcular hash y registrar en BD
    private async Task ScanFolderAsync()
    {
        var pdfs = GetPdfs();

        if (pdfs.Length == 0)
        {
            PlaceholderText.Text       = "No se encontraron archivos PDF en esta carpeta.";
            PlaceholderText.Visibility = Visibility.Visible;
            PdfGrid.Visibility         = Visibility.Collapsed;
            StatusText.Text            = "Sin archivos PDF.";
            return;
        }

        BrowseBtn.IsEnabled     = false;
        ScanBtn.IsEnabled       = false;
        QuestionsBtn.IsEnabled  = false;
        PracticeBtn.IsEnabled   = false;
        ProgressBar.Visibility  = Visibility.Visible;
        StatusText.Text         = "Escaneando...";

        var results = new List<PdfScanResult>();

        try
        {
            for (var i = 0; i < pdfs.Length; i++)
            {
                var pdf = pdfs[i];
                StatusText.Text = $"Procesando {Path.GetFileName(pdf)}  ({i + 1}/{pdfs.Length})...";
                var result = await Task.Run(() => ProcessPdf(pdf));
                results.Add(result);
            }

            PdfGrid.ItemsSource        = results;
            PdfGrid.Visibility         = Visibility.Visible;
            PlaceholderText.Visibility = Visibility.Collapsed;

            var newCount      = results.Count(r => r.RawStatus == PdfStatus.New);
            var modifiedCount = results.Count(r => r.RawStatus == PdfStatus.Modified);
            StatusText.Text = $"{results.Count} PDF(s) escaneado(s)" +
                              (newCount      > 0 ? $"  -  {newCount} nuevo(s)"           : "") +
                              (modifiedCount > 0 ? $"  -  {modifiedCount} modificado(s)" : "") +
                              ".";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error al escanear: {ex.Message}";
        }
        finally
        {
            BrowseBtn.IsEnabled    = true;
            ScanBtn.IsEnabled      = true;
            QuestionsBtn.IsEnabled = true;
            PracticeBtn.IsEnabled  = true;
            ProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    // Calcular SHA-256 y registrar un solo registro por archivo en la BD
    private PdfScanResult ProcessPdf(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        try
        {
            var fileSize     = new FileInfo(filePath).Length;
            using var stream = File.OpenRead(filePath);
            var hash         = Convert.ToHexString(SHA256.HashData(stream));

            var storedHash = _db.GetStoredHash(filePath);
            var status = storedHash is null       ? PdfStatus.New
                       : storedHash == hash       ? PdfStatus.Unchanged
                                                  : PdfStatus.Modified;

            _db.UpsertPdfFile(_folderPath, filePath, fileName, hash, fileSize);
            return new PdfScanResult(fileName, fileSize, hash, status);
        }
        catch (Exception ex)
        {
            return new PdfScanResult(fileName, 0, string.Empty, PdfStatus.New, ex.Message);
        }
    }

    private string[] GetPdfs() =>
        Directory.Exists(_folderPath)
            ? Directory.GetFiles(_folderPath, "*.pdf", SearchOption.AllDirectories)
            : [];

    // Abrir ventana de generación de preguntas
    private void QuestionsBtn_Click(object sender, RoutedEventArgs e)
    {
        var win = new GenerateQuestionsWindow(_folderPath, _db) { Owner = this };
        win.ShowDialog();
    }

    // Abrir ventana de evaluación
    private void PracticeBtn_Click(object sender, RoutedEventArgs e)
    {
        var win = new QuizWindow(_folderPath, _db) { Owner = this };
        win.ShowDialog();
    }
}

public enum PdfStatus { New, Unchanged, Modified }

public class PdfScanResult
{
    public string    FileName        { get; }
    public string    FileSizeDisplay { get; }
    public string    HashShort       { get; }
    public string    Status          { get; }
    public string    LastSeenDisplay { get; }
    public PdfStatus RawStatus       { get; }

    public PdfScanResult(string fileName, long fileSize, string hash,
                         PdfStatus status, string? error = null)
    {
        FileName        = fileName;
        FileSizeDisplay = FormatSize(fileSize);
        HashShort       = hash.Length >= 8 ? hash[..8] + "..." : hash;
        RawStatus       = status;
        LastSeenDisplay = DateTime.Now.ToString("dd/MM/yyyy HH:mm");
        Status = error is not null          ? "Error"
               : status == PdfStatus.New      ? "Nuevo"
               : status == PdfStatus.Modified ? "Modificado"
                                              : "Sin cambios";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576     => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024         => $"{bytes / 1_024.0:F1} KB",
        _                => $"{bytes} B"
    };
}
