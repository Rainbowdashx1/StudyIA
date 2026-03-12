using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace StudyIA;

public partial class QuizWindow : Window
{
    private readonly AppDatabase     _db;
    private readonly string          _folderPath;
    private readonly CopilotService? _copilot;

    private List<QuestionRecord>      _questions = [];
    private readonly List<AttemptData> _attempts  = [];
    private int                        _current   = 0;
    private CancellationTokenSource?   _cts;

    private const string KeyGithubToken = "GithubToken";

    public QuizWindow(string folderPath, AppDatabase db)
    {
        InitializeComponent();
        _folderPath = folderPath;
        _db         = db;

        var token = _db.GetSetting(KeyGithubToken);
        if (!string.IsNullOrEmpty(token))
            _copilot = new CopilotService(token);

        Loaded += (_, _) => OnLoaded();
    }

    // ── Carga inicial ──────────────────────────────────────────────────────
    private void OnLoaded()
    {
        _questions = _db.GetAllQuestionsForFolder(_folderPath);

        if (_questions.Count == 0)
        {
            QuestionText.Text   = "No hay preguntas disponibles. Genera preguntas primero desde '📝 Preguntas'.";
            AnswerBox.IsEnabled = false;
            AnswerBtn.IsEnabled = false;
            ProgressText.Text   = "0 preguntas";
            return;
        }

        ShowQuestion(0);
    }

    // ── Mostrar una pregunta ───────────────────────────────────────────────
    private void ShowQuestion(int index)
    {
        var q = _questions[index];

        ProgressText.Text  = $"Pregunta {index + 1} de {_questions.Count}";
        QuizProgress.Value = (double)index / _questions.Count * 100;
        SourceText.Text    = $"📄 {q.FileName}  ·  Página {q.PageNumber}";
        QuestionText.Text  = q.QuestionText;

        AnswerBox.Text       = string.Empty;
        AnswerBox.IsEnabled  = true;
        AnswerBox.Focus();

        AnswerBtn.IsEnabled        = true;
        AnswerBtn.Visibility       = Visibility.Visible;
        EvaluatingPanel.Visibility = Visibility.Collapsed;
        FeedbackPanel.Visibility   = Visibility.Collapsed;
    }

    // ── Ctrl+Enter para enviar ─────────────────────────────────────────────
    private void AnswerBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
        {
            Answer_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    // ── Evaluar respuesta ──────────────────────────────────────────────────
    private async void Answer_Click(object sender, RoutedEventArgs e)
    {
        var answer = AnswerBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(answer)) return;

        AnswerBtn.Visibility       = Visibility.Collapsed;
        EvaluatingPanel.Visibility = Visibility.Visible;
        AnswerBox.IsEnabled        = false;

        var q        = _questions[_current];
        var score    = 0;
        var feedback = string.Empty;

        _cts = new CancellationTokenSource();
        try
        {
            if (_copilot is null)
            {
                feedback = "⚠️ Sin token configurado. Configura tu GitHub PAT en '📝 Preguntas'.";
                score    = 0;
            }
            else
            {
                (score, feedback) = await _copilot.EvaluateAnswerAsync(
                    q.QuestionText, q.ExpectedAnswer, answer, _cts.Token);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            feedback = $"Error al evaluar: {ex.Message}";
            score    = 0;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }

        // Persistir en BD
        _db.SaveUserAnswer(q.Id, answer, score / 100.0, feedback);

        // Acumular para el resumen final
        _attempts.Add(new AttemptData(q, answer, score, feedback));

        ShowFeedback(score, feedback);
    }

    // ── Mostrar resultado de la evaluación ────────────────────────────────
    private void ShowFeedback(int score, string feedback)
    {
        EvaluatingPanel.Visibility = Visibility.Collapsed;

        var color = score >= 80 ? Color.FromRgb(0x10, 0x7C, 0x10)   // verde
                  : score >= 60 ? Color.FromRgb(0xD8, 0x7C, 0x00)   // naranja
                                : Color.FromRgb(0xC4, 0x28, 0x28);  // rojo

        ScoreBadge.Background = new SolidColorBrush(color);
        ScoreText.Text        = $"{score}";
        FeedbackText.Text     = feedback;

        var isLast = _current == _questions.Count - 1;
        NextBtn.Content    = isLast ? "Ver resultados  ✔" : "Siguiente  →";
        NextBtn.Background = new SolidColorBrush(
            isLast ? Color.FromRgb(0x10, 0x7C, 0x10)
                   : Color.FromRgb(0x00, 0x78, 0xD4));

        FeedbackPanel.Visibility = Visibility.Visible;
    }

    // ── Siguiente pregunta ─────────────────────────────────────────────────
    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_current < _questions.Count - 1)
        {
            _current++;
            ShowQuestion(_current);
        }
        else
        {
            ShowResults();
        }
    }

    // ── Panel de resultados ────────────────────────────────────────────────
    private void ShowResults()
    {
        QuizPanel.Visibility    = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Visible;

        var avg = _attempts.Count > 0
            ? (int)Math.Round(_attempts.Average(a => a.Score))
            : 0;

        var color = avg >= 80 ? Color.FromRgb(0x10, 0x7C, 0x10)
                  : avg >= 60 ? Color.FromRgb(0xD8, 0x7C, 0x00)
                              : Color.FromRgb(0xC4, 0x28, 0x28);

        AvgBadge.Background = new SolidColorBrush(color);
        AvgText.Text        = $"{avg} / 100";

        ResultsGrid.ItemsSource = _attempts
            .Select((a, i) => new QuizAttemptResult
            {
                Number       = i + 1,
                Question     = Trim(a.Q.QuestionText,  90),
                UserAnswer   = Trim(a.Answer,           90),
                ScoreDisplay = $"{a.Score} / 100",
                Feedback     = a.Feedback,
                RawScore     = a.Score
            })
            .ToList();
    }

    private void CloseResult_Click(object sender, RoutedEventArgs e) => Close();

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}

// ── Modelos internos ──────────────────────────────────────────────────────
internal record AttemptData(
    QuestionRecord Q,
    string         Answer,
    int            Score,
    string         Feedback);

internal sealed class QuizAttemptResult
{
    public int    Number       { get; init; }
    public string Question     { get; init; } = string.Empty;
    public string UserAnswer   { get; init; } = string.Empty;
    public string ScoreDisplay { get; init; } = string.Empty;
    public string Feedback     { get; init; } = string.Empty;
    public int    RawScore     { get; init; }
}
