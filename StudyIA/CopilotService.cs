using System.ClientModel;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;

namespace StudyIA;

/// <summary>
/// Representa una pregunta generada por la IA con su respuesta esperada,
/// la página de origen y el fragmento de contexto relevante.
/// </summary>
public record GeneratedQuestion(
    string QuestionText,
    string ExpectedAnswer,
    int    PageNumber,
    string Context);

/// <summary>
/// Descriptor de una sección dentro de un batch de generación multi-sección.
/// </summary>
public record SectionBatchItem(
    int    SectionId,
    string SectionTitle,
    string PageRange,
    int    QuestionCount,
    List<(int Page, string Text)> Pages);

/// <summary>
/// Pregunta generada en un batch multi-sección, con el Id de sección de origen resuelto.
/// </summary>
public record SectionedQuestion(
    int    SectionId,
    string QuestionText,
    string ExpectedAnswer,
    int    PageNumber,
    string Context);

/// <summary>
/// Servicio que se comunica con GitHub Models (endpoint compatible con OpenAI)
/// para generar preguntas de evaluación a partir del texto de un PDF.
/// </summary>
public class CopilotService(string githubToken)
{
    private const string GhModelsEndpoint = "https://models.inference.ai.azure.com";
    private const string Model            = "gpt-4o";

    private static readonly JsonSerializerOptions JsonOpts = new()
        { PropertyNameCaseInsensitive = true };

    private readonly ChatClient _chat = new(
        model: Model,
        credential: new ApiKeyCredential(githubToken),
        options: new OpenAIClientOptions { Endpoint = new Uri(GhModelsEndpoint) });

    /// <summary>
    /// Genera <paramref name="count"/> preguntas de evaluación a partir de las páginas dadas.
    /// </summary>
    public async Task<List<GeneratedQuestion>> GenerateAsync(
        List<(int Page, string Text)> pages,
        int count,
        CancellationToken ct = default)
    {
        var pageContent = string.Join("\n\n",
            pages.Select(p => $"=== Página {p.Page} ===\n{p.Text.Trim()}"));

        List<ChatMessage> messages =
        [
            new SystemChatMessage("""
                Eres un profesor experto en evaluaciones académicas.
                Responde ÚNICAMENTE con un array JSON válido, sin texto adicional ni bloques de código markdown.
                Cada elemento del array debe tener exactamente estas cuatro propiedades:
                  "questionText":   texto de la pregunta, claro y específico (string),
                  "expectedAnswer": respuesta óptima y completa (string),
                  "pageNumber":     número de página de donde proviene la información (integer),
                  "context":        fragmento breve del texto original relevante, máx. 200 caracteres (string).
                """),
            new UserChatMessage($"""
                Genera exactamente {count} preguntas de evaluación basadas en el siguiente contenido del PDF.
                Cubre los conceptos más importantes y variados del documento.

                {pageContent}
                """)
        ];

        var result = await _chat.CompleteChatAsync(messages, cancellationToken: ct);
        var json   = result.Value.Content[0].Text.Trim();

        // Eliminar bloque de código markdown si el modelo lo añadió
        if (json.StartsWith("```"))
        {
            var start = json.IndexOf('\n') + 1;
            var end   = json.LastIndexOf("```");
            if (end > start)
                json = json[start..end].Trim();
        }

        var dtos = JsonSerializer.Deserialize<List<QuestionDto>>(json, JsonOpts)
                   ?? throw new InvalidOperationException("La IA no devolvió un JSON válido.");

        return dtos.Select(d => new GeneratedQuestion(
            d.QuestionText   ?? string.Empty,
            d.ExpectedAnswer ?? string.Empty,
            d.PageNumber,
            d.Context        ?? string.Empty)).ToList();
    }

    private sealed class QuestionDto
    {
        public string? QuestionText   { get; set; }
        public string? ExpectedAnswer { get; set; }
        public int     PageNumber     { get; set; }
        public string? Context        { get; set; }
    }

    /// <summary>
    /// Evalúa la respuesta del usuario y devuelve (puntuación 0-100, retroalimentación).
    /// </summary>
    public async Task<(int Score, string Feedback)> EvaluateAnswerAsync(
        string questionText,
        string expectedAnswer,
        string userAnswer,
        CancellationToken ct = default)
    {
        List<ChatMessage> messages =
        [
            new SystemChatMessage("""
                Eres un evaluador académico experto.
                Evalúa la respuesta del estudiante comparándola con la respuesta esperada.
                Responde ÚNICAMENTE con un JSON válido con dos propiedades:
                  "score":    puntuación de 0 a 100 (integer), siendo 100 una respuesta perfecta,
                  "feedback": explicación breve en español de por qué obtuvo esa puntuación (string, máx. 180 caracteres).
                """),
            new UserChatMessage($"""
                Pregunta: {questionText}

                Respuesta esperada: {expectedAnswer}

                Respuesta del estudiante: {userAnswer}
                """)
        ];

        var result = await _chat.CompleteChatAsync(messages, cancellationToken: ct);
        var json   = result.Value.Content[0].Text.Trim();

        if (json.StartsWith("```"))
        {
            var s = json.IndexOf('\n') + 1;
            var e = json.LastIndexOf("```");
            if (e > s) json = json[s..e].Trim();
        }

        var dto = JsonSerializer.Deserialize<EvalDto>(json, JsonOpts)
                  ?? throw new InvalidOperationException("Respuesta de evaluación no válida.");

        return (Math.Clamp(dto.Score, 0, 100), dto.Feedback ?? string.Empty);
    }

    private sealed class EvalDto
    {
        public int     Score    { get; set; }
        public string? Feedback { get; set; }
    }

    // ── Experimental: generación multi-sección en un solo request ─────────

    /// <summary>
    /// Genera preguntas para varias secciones en una única llamada a la API,
    /// aprovechando mejor la ventana de contexto del modelo.
    /// Cada pregunta del resultado ya viene con el <see cref="SectionBatchItem.SectionId"/>
    /// de la sección a la que pertenece.
    /// </summary>
    public async Task<List<SectionedQuestion>> GenerateForSectionsAsync(
        IReadOnlyList<SectionBatchItem> batch,
        CancellationToken ct = default)
    {
        var sb         = new System.Text.StringBuilder();
        var totalCount = 0;

        for (var i = 0; i < batch.Count; i++)
        {
            var item    = batch[i];
            totalCount += item.QuestionCount;

            var content = string.Join("\n\n",
                item.Pages.Select(p => $"=== Página {p.Page} ===\n{p.Text.Trim()}"));

            sb.AppendLine($"""
                ## Sección {i}: "{item.SectionTitle}" ({item.PageRange})
                Genera exactamente {item.QuestionCount} pregunta(s).
                {content}

                """);
        }

        List<ChatMessage> messages =
        [
            new SystemChatMessage("""
                Eres un profesor experto en evaluaciones académicas.
                Responde ÚNICAMENTE con un array JSON válido, sin texto adicional ni bloques de código markdown.
                Cada elemento del array debe tener exactamente estas cinco propiedades:
                  "sectionIndex":  índice 0-based de la sección de la que proviene la pregunta (integer),
                  "questionText":   texto de la pregunta, claro y específico (string),
                  "expectedAnswer": respuesta óptima y completa (string),
                  "pageNumber":     número de página de donde proviene la información (integer),
                  "context":        fragmento breve del texto original relevante, máx. 200 caracteres (string).
                """),
            new UserChatMessage($"""
                Genera las preguntas de evaluación indicadas para cada sección del PDF.
                Respeta el número exacto de preguntas por sección y asigna el "sectionIndex" correcto en cada elemento.

                {sb}
                Total de preguntas a generar: {totalCount}
                """)
        ];

        var result = await _chat.CompleteChatAsync(messages, cancellationToken: ct);
        var json   = result.Value.Content[0].Text.Trim();

        if (json.StartsWith("```"))
        {
            var start = json.IndexOf('\n') + 1;
            var end   = json.LastIndexOf("```");
            if (end > start)
                json = json[start..end].Trim();
        }

        var dtos = JsonSerializer.Deserialize<List<SectionedQuestionDto>>(json, JsonOpts)
                   ?? throw new InvalidOperationException("La IA no devolvió un JSON válido.");

        return dtos
            .Where(d => d.SectionIndex >= 0 && d.SectionIndex < batch.Count)
            .Select(d => new SectionedQuestion(
                batch[d.SectionIndex].SectionId,
                d.QuestionText   ?? string.Empty,
                d.ExpectedAnswer ?? string.Empty,
                d.PageNumber,
                d.Context        ?? string.Empty))
            .ToList();
    }

    private sealed class SectionedQuestionDto
    {
        public int     SectionIndex   { get; set; }
        public string? QuestionText   { get; set; }
        public string? ExpectedAnswer { get; set; }
        public int     PageNumber     { get; set; }
        public string? Context        { get; set; }
    }
}
