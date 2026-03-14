using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace StudyIA;

public partial class MainWindow : Window
{
    private FolderRecord?    _selectedFolder;
    private readonly AppDatabase _db = new();

    public MainWindow()
    {
        InitializeComponent();
        LoadAllFolders(autoSelectFirst: true);
    }

    // ── Lista de temarios ──────────────────────────────────────────────────

    private void LoadAllFolders(bool autoSelectFirst = false)
    {
        var folders = _db.GetAllFolders();
        FoldersList.ItemsSource = folders;

        if (autoSelectFirst && folders.Count > 0)
            FoldersList.SelectedIndex = 0;
    }

    private async void FoldersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FoldersList.SelectedItem is not FolderRecord folder) return;

        _selectedFolder           = folder;
        FolderNameText.Text       = folder.Name;
        FolderNameText.Foreground = System.Windows.Media.Brushes.Black;
        ScanBtn.IsEnabled         = true;
        QuestionsBtn.IsEnabled    = true;
        PracticeBtn.IsEnabled     = true;
        RemoveFolderBtn.IsEnabled = true;
        await ScanFolderAsync();
    }

    // ── Añadir temario ─────────────────────────────────────────────────────

    private async void AddFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Seleccionar carpeta con archivos PDF"
        };

        if (dialog.ShowDialog() != true) return;

        var path = dialog.FolderName;
        var name = Path.GetFileName(
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = path;

        _db.AddFolder(name, path);
        LoadAllFolders();

        // Seleccionar el temario recién añadido
        var folders = FoldersList.ItemsSource as List<FolderRecord>;
        var added   = folders?.FirstOrDefault(f => f.FolderPath == path);
        if (added is not null)
            FoldersList.SelectedItem = added;
    }

    // ── Eliminar temario ───────────────────────────────────────────────────

    private void RemoveFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFolder is null) return;

        var confirm = MessageBox.Show(
            $"¿Eliminar el temario \"{_selectedFolder.Name}\"?\n\n" +
            "Se eliminarán también todos sus PDFs y preguntas generadas.",
            "Confirmar eliminación",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        _db.DeleteFolder(_selectedFolder.Id);
        _selectedFolder = null;

        FolderNameText.Text       = "← Selecciona o añade un temario";
        FolderNameText.Foreground = System.Windows.Media.Brushes.Gray;
        ScanBtn.IsEnabled         = false;
        QuestionsBtn.IsEnabled    = false;
        PracticeBtn.IsEnabled     = false;
        RemoveFolderBtn.IsEnabled = false;
        PdfGrid.Visibility        = Visibility.Collapsed;
        PlaceholderText.Visibility = Visibility.Visible;
        PlaceholderText.Text      = "Selecciona un temario para ver sus archivos PDF.";
        StatusText.Text           = "Listo";

        LoadAllFolders();
    }

    // ── Escanear ───────────────────────────────────────────────────────────

    private async void ScanBtn_Click(object sender, RoutedEventArgs e) =>
        await ScanFolderAsync();

    private async Task ScanFolderAsync()
    {
        if (_selectedFolder is null) return;

        var folderPath = _selectedFolder.FolderPath;
        var pdfs       = GetPdfs(folderPath);

        if (pdfs.Length == 0)
        {
            PlaceholderText.Text       = "No se encontraron archivos PDF en esta carpeta.";
            PlaceholderText.Visibility = Visibility.Visible;
            PdfGrid.Visibility         = Visibility.Collapsed;
            StatusText.Text            = "Sin archivos PDF.";
            return;
        }

        AddFolderBtn.IsEnabled    = false;
        RemoveFolderBtn.IsEnabled = false;
        ScanBtn.IsEnabled         = false;
        QuestionsBtn.IsEnabled    = false;
        PracticeBtn.IsEnabled     = false;
        ProgressBar.Visibility    = Visibility.Visible;
        StatusText.Text           = "Escaneando...";

        var results = new List<PdfScanResult>();

        try
        {
            for (var i = 0; i < pdfs.Length; i++)
            {
                var pdf = pdfs[i];
                StatusText.Text = $"Procesando {Path.GetFileName(pdf)}  ({i + 1}/{pdfs.Length})...";
                var result = await Task.Run(() => ProcessPdf(pdf, folderPath));
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
            AddFolderBtn.IsEnabled    = true;
            RemoveFolderBtn.IsEnabled = _selectedFolder is not null;
            ScanBtn.IsEnabled         = true;
            QuestionsBtn.IsEnabled    = true;
            PracticeBtn.IsEnabled     = true;
            ProgressBar.Visibility    = Visibility.Collapsed;
        }
    }

    private PdfScanResult ProcessPdf(string filePath, string folderPath)
    {
        var fileName = Path.GetFileName(filePath);
        try
        {
            var fileSize     = new FileInfo(filePath).Length;
            using var stream = File.OpenRead(filePath);
            var hash         = Convert.ToHexString(SHA256.HashData(stream));

            var storedHash = _db.GetStoredHash(filePath);
            var status = storedHash is null ? PdfStatus.New
                       : storedHash == hash ? PdfStatus.Unchanged
                                            : PdfStatus.Modified;

            _db.UpsertPdfFile(folderPath, filePath, fileName, hash, fileSize);
            return new PdfScanResult(fileName, fileSize, hash, status);
        }
        catch (Exception ex)
        {
            return new PdfScanResult(fileName, 0, string.Empty, PdfStatus.New, ex.Message);
        }
    }

    private static string[] GetPdfs(string folderPath) =>
        Directory.Exists(folderPath)
            ? Directory.GetFiles(folderPath, "*.pdf", SearchOption.AllDirectories)
            : [];

    // ── Ventanas de preguntas y práctica ───────────────────────────────────

    private void QuestionsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFolder is null) return;
        var win = new GenerateQuestionsWindow(_selectedFolder.FolderPath, _db) { Owner = this };
        win.ShowDialog();
    }

    private void PracticeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFolder is null) return;
        var win = new QuizWindow(_selectedFolder.FolderPath, _db) { Owner = this };
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
        Status = error is not null            ? "Error"
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
