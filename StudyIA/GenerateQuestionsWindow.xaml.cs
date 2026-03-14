using System.Windows;
using System.Windows.Controls;

namespace StudyIA;

public partial class GenerateQuestionsWindow : Window
{
    private readonly AppDatabase _db;
    private readonly string      _folderPath;
    private CancellationTokenSource? _cts;

    private const string KeyGithubToken  = "GithubToken";
    private const int    MaxCharsPerFile = 200_000;

    public GenerateQuestionsWindow(string folderPath, AppDatabase db)
    {
        InitializeComponent();
        _folderPath = folderPath;
        _db         = db;
        CountBox.TextChanged += CountBox_TextChanged; // después de InitializeComponent
        Loaded += (_, _) => OnLoaded();
    }

    // ── Carga inicial ──────────────────────────────────────────────────────
    private void OnLoaded()
    {
        var token = _db.GetSetting(KeyGithubToken);
        if (!string.IsNullOrEmpty(token))
            TokenBox.Text = token;

        RefreshFileList();
    }

    // ── Token ──────────────────────────────────────────────────────────────
    private void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Text.Trim();
        if (string.IsNullOrEmpty(token))
        {
            TokenStatus.Text = "⚠️ El token no puede estar vacío.";
            return;
        }
        _db.SetSetting(KeyGithubToken, token);
        TokenStatus.Text = "✓ Token guardado.";
    }

    // ── Tabla de archivos con conteo de preguntas ──────────────────────────
    private void RefreshFileList()
    {
        var summary = _db.GetQuestionSummary(_folderPath);
        FilesGrid.ItemsSource = summary.Select(s => new FileQSummary
        {
            PdfFileId    = s.PdfFileId,
            FileName     = s.FileName,
            QuestionCount = s.Count,
            CountDisplay  = s.Count == 0 ? "Sin preguntas" : $"{s.Count} pregunta(s)"
        }).ToList();

        UpdateSummary();
    }

    // ── Resumen de preguntas existentes vs. necesarias ─────────────────────
    private void UpdateSummary()
    {
        if (FilesGrid.ItemsSource is not List<FileQSummary> items)
        {
            ExistingText.Text = string.Empty;
            NeededText.Text   = string.Empty;
            GenerateBtn.IsEnabled = false;
            return;
        }

        var existing  = items.Sum(x => x.QuestionCount);
        var requested = GetRequestedCount();
        var needed    = Math.Max(0, requested - existing);

        ExistingText.Text = $"Ya existen {existing} pregunta(s) en la base de datos.";
        NeededText.Text   = needed == 0
            ? "✅ Ya tienes suficientes preguntas."
            : $"Se generarán aproximadamente {needed} pregunta(s) nueva(s).";

        GenerateBtn.IsEnabled = needed > 0 && items.Count > 0;
    }

    private void CountBox_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateSummary();

    private int GetRequestedCount() =>
        int.TryParse(CountBox.Text, out var n) ? Math.Max(1, n) : 10;

    // ── Generación ─────────────────────────────────────────────────────────
    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Text.Trim();
        if (string.IsNullOrEmpty(token))
        {
            TokenStatus.Text = "⚠️ Introduce tu token de GitHub antes de generar.";
            return;
        }

        var files = _db.GetFilesInFolder(_folderPath);
        if (files.Count == 0)
        {
            GenStatus.Text = "No hay archivos PDF registrados. Escanea la carpeta primero.";
            return;
        }

        var requested = GetRequestedCount();
        var existing  = _db.GetQuestionSummary(_folderPath);
        var needed    = requested - existing.Sum(s => s.Count);

        if (needed <= 0)
        {
            GenStatus.Text = "✅ Ya tienes suficientes preguntas.";
            return;
        }

        _cts = new CancellationTokenSource();
        SetGenerating(true);

        try
        {
            var service = new CopilotService(token);

            // Distribuir preguntas proporcionalmente entre los archivos
            var perFile   = (int)Math.Ceiling((double)requested / files.Count);
            var generated = 0;

            for (var i = 0; i < files.Count; i++)
            {
                if (_cts.Token.IsCancellationRequested) break;

                var file       = files[i];
                var existCount = existing.FirstOrDefault(s => s.PdfFileId == file.Id).Count;
                var toGenerate = Math.Max(0, perFile - existCount);

                if (toGenerate == 0) continue;

                // Actualizar progreso
                GenProgress.Value = (double)i / files.Count * 100;
                GenStatus.Text    = $"Extrayendo texto de {file.FileName}…";

                var pages = await Task.Run(() => PdfTextService.ExtractPages(file.FilePath));

                if (pages.Count == 0)
                {
                    GenStatus.Text = $"⚠️ {file.FileName}: sin texto extraíble (PDF escaneado), omitiendo.";
                    await Task.Delay(1000, _cts.Token);
                    continue;
                }

                var trimmed = PdfTextService.Trim(pages, MaxCharsPerFile);
                GenStatus.Text = $"Generando {toGenerate} pregunta(s) para {file.FileName}  ({i + 1}/{files.Count})…";

                List<GeneratedQuestion> questions;
                try
                {
                    questions = await service.GenerateAsync(trimmed, toGenerate, _cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    GenStatus.Text = $"⚠️ Error con {file.FileName}: {ex.Message}";
                    await Task.Delay(1500, _cts.Token);
                    continue;
                }

                foreach (var q in questions)
                    _db.SaveQuestion(file.Id, q.PageNumber, q.Context,
                                     q.QuestionText, q.ExpectedAnswer);

                generated += questions.Count;
            }

            GenProgress.Value = 100;
            GenStatus.Text    = $"✅ {generated} pregunta(s) generada(s) correctamente.";
            RefreshFileList();
        }
        catch (OperationCanceledException)
        {
            GenStatus.Text = "Generación cancelada.";
        }
        catch (Exception ex)
        {
            GenStatus.Text = $"Error inesperado: {ex.Message}";
        }
        finally
        {
            SetGenerating(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CancelGen_Click(object sender, RoutedEventArgs e) =>
        _cts?.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e) =>
        Close();

    // ── Helpers ────────────────────────────────────────────────────────────
    private void SetGenerating(bool active)
    {
        GenerateBtn.IsEnabled      = !active;
        SaveTokenBtn.IsEnabled     = !active;
        CancelGenBtn.Visibility    = active ? Visibility.Visible : Visibility.Collapsed;
        GenProgress.Visibility     = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active) GenProgress.Value = 0;
    }
}

internal sealed class FileQSummary
{
    public int    PdfFileId     { get; init; }
    public string FileName      { get; init; } = string.Empty;
    public int    QuestionCount { get; init; }
    public string CountDisplay  { get; init; } = string.Empty;
}
