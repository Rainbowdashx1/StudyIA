using System.IO;
using Microsoft.Data.Sqlite;

namespace StudyIA;

/// <summary>
/// Base de datos SQLite local.
///
/// Esquema:
///   Settings    → configuración persistente (clave / valor)
///   PdfFiles    → un registro por archivo PDF con hash SHA-256 y nombre
///   Questions   → preguntas vinculadas a un PdfFile concreto (página + contexto)
///   UserAnswers → respuestas del usuario con puntuación y retroalimentación
/// </summary>
public class AppDatabase
{
    private static readonly string DbPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "studyia.db");

    private readonly string _cs = $"Data Source={DbPath}";

    public AppDatabase() => Initialize();

    // ── Inicialización del esquema ─────────────────────────────────────────
    private void Initialize()
    {
        using var conn = Open();

        // Tablas base (CREATE IF NOT EXISTS es idempotente)
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS Settings (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PdfFiles (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                FolderPath TEXT    NOT NULL,
                FilePath   TEXT    NOT NULL UNIQUE,
                FileName   TEXT    NOT NULL DEFAULT '',
                FileHash   TEXT    NOT NULL,
                FileSize   INTEGER NOT NULL,
                LastSeen   TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Questions (
                Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                PdfFileId      INTEGER NOT NULL,
                PdfSectionId   INTEGER,
                PageNumber     INTEGER NOT NULL DEFAULT 0,
                Context        TEXT    NOT NULL DEFAULT '',
                QuestionText   TEXT    NOT NULL,
                ExpectedAnswer TEXT    NOT NULL,
                CreatedAt      TEXT    NOT NULL,
                FOREIGN KEY (PdfFileId)    REFERENCES PdfFiles(Id),
                FOREIGN KEY (PdfSectionId) REFERENCES PdfSections(Id)
            );

            CREATE TABLE IF NOT EXISTS UserAnswers (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                QuestionId INTEGER NOT NULL,
                UserAnswer TEXT    NOT NULL,
                Score      REAL    NOT NULL,
                Feedback   TEXT    NOT NULL DEFAULT '',
                AnsweredAt TEXT    NOT NULL,
                FOREIGN KEY (QuestionId) REFERENCES Questions(Id)
            );

            CREATE TABLE IF NOT EXISTS Folders (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                Name       TEXT    NOT NULL,
                FolderPath TEXT    NOT NULL UNIQUE,
                CreatedAt  TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PdfSections (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                PdfFileId INTEGER NOT NULL,
                Title     TEXT    NOT NULL,
                StartPage INTEGER NOT NULL,
                EndPage   INTEGER NOT NULL,
                FOREIGN KEY (PdfFileId) REFERENCES PdfFiles(Id)
            );
            """);

        // Migraciones incrementales — falla en silencio si la columna ya existe
        TryMigrate(conn, "ALTER TABLE PdfFiles   ADD COLUMN FileName TEXT NOT NULL DEFAULT ''");
        TryMigrate(conn, "ALTER TABLE Questions  ADD COLUMN PdfSectionId INTEGER REFERENCES PdfSections(Id)");

    }

    // ── Folders ────────────────────────────────────────────────────────────

    public List<FolderRecord> GetAllFolders()
    {
        using var conn = Open();
        using var cmd  = Cmd(conn, "SELECT Id, Name, FolderPath, CreatedAt FROM Folders ORDER BY Name");
        using var r    = cmd.ExecuteReader();

        var list = new List<FolderRecord>();
        while (r.Read())
            list.Add(new FolderRecord
            {
                Id         = r.GetInt32(0),
                Name       = r.GetString(1),
                FolderPath = r.GetString(2),
                CreatedAt  = DateTime.Parse(r.GetString(3))
            });
        return list;
    }

    /// <summary>Añade un temario. Si la ruta ya existe, no hace nada y devuelve el Id existente.</summary>
    public int AddFolder(string name, string folderPath)
    {
        using var conn = Open();
        using var cmd  = Cmd(conn, """
            INSERT INTO Folders (Name, FolderPath, CreatedAt)
            VALUES ($name, $path, $at)
            ON CONFLICT(FolderPath) DO NOTHING;
            SELECT Id FROM Folders WHERE FolderPath = $path;
            """);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$path", folderPath);
        cmd.Parameters.AddWithValue("$at",   DateTime.UtcNow.ToString("o"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Elimina el temario y en cascada sus PDFs, preguntas y respuestas.</summary>
    public void DeleteFolder(int folderId)
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();

        // Obtener la ruta del temario
        using var pathCmd = Cmd(conn, "SELECT FolderPath FROM Folders WHERE Id = $id");
        pathCmd.Parameters.AddWithValue("$id", folderId);
        pathCmd.Transaction = tx;
        var folderPath = pathCmd.ExecuteScalar() as string;
        if (folderPath is null) { tx.Rollback(); return; }

        Execute(conn, tx, """
            DELETE FROM UserAnswers
            WHERE QuestionId IN (
                SELECT q.Id FROM Questions q
                JOIN PdfFiles p ON p.Id = q.PdfFileId
                WHERE p.FolderPath = $path
            );
            """, ("$path", folderPath));

        Execute(conn, tx, """
            DELETE FROM Questions
            WHERE PdfFileId IN (
                SELECT Id FROM PdfFiles WHERE FolderPath = $path
            );
            """, ("$path", folderPath));

        Execute(conn, tx, "DELETE FROM PdfFiles WHERE FolderPath = $path",
                ("$path", folderPath));

        Execute(conn, tx, "DELETE FROM Folders WHERE Id = $id",
                ("$id", (object)folderId));

        tx.Commit();
    }

    // ── Settings ───────────────────────────────────────────────────────────
    public string? GetSetting(string key)
    {
        using var conn = Open();
        using var cmd  = Cmd(conn, "SELECT Value FROM Settings WHERE Key = $k");
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var conn = Open();
        using var cmd  = Cmd(conn, """
            INSERT INTO Settings (Key, Value) VALUES ($k, $v)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // ── PdfFiles ───────────────────────────────────────────────────────────

    /// <summary>
    /// Inserta o actualiza el registro del archivo PDF y devuelve su Id.
    /// El Id se usa para vincular preguntas a este archivo específico.
    /// </summary>
    public int UpsertPdfFile(string folderPath, string filePath, string fileName,
                             string hash, long fileSize)
    {
        using var conn = Open();
        using var cmd  = Cmd(conn, """
            INSERT INTO PdfFiles (FolderPath, FilePath, FileName, FileHash, FileSize, LastSeen)
                VALUES ($folder, $file, $name, $hash, $size, $seen)
            ON CONFLICT(FilePath) DO UPDATE SET
                FileName   = excluded.FileName,
                FileHash   = excluded.FileHash,
                FileSize   = excluded.FileSize,
                LastSeen   = excluded.LastSeen;
            SELECT Id FROM PdfFiles WHERE FilePath = $file;
            """);
        cmd.Parameters.AddWithValue("$folder", folderPath);
        cmd.Parameters.AddWithValue("$file",   filePath);
        cmd.Parameters.AddWithValue("$name",   fileName);
        cmd.Parameters.AddWithValue("$hash",   hash);
        cmd.Parameters.AddWithValue("$size",   fileSize);
        cmd.Parameters.AddWithValue("$seen",   DateTime.UtcNow.ToString("o"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public string? GetStoredHash(string filePath)
    {
        using var conn = Open();
        using var cmd  = Cmd(conn, "SELECT FileHash FROM PdfFiles WHERE FilePath = $f");
        cmd.Parameters.AddWithValue("$f", filePath);
        return cmd.ExecuteScalar() as string;
    }

    public List<PdfFileRecord> GetFilesInFolder(string folderPath)
    {
        using var conn = Open();
        using var cmd  = Cmd(conn, """
            SELECT Id, FolderPath, FilePath, FileName, FileHash, FileSize, LastSeen
            FROM PdfFiles WHERE FolderPath = $folder ORDER BY FileName
            """);
        cmd.Parameters.AddWithValue("$folder", folderPath);
        using var r = cmd.ExecuteReader();

        var list = new List<PdfFileRecord>();
        while (r.Read())
            list.Add(new PdfFileRecord
            {
                Id         = r.GetInt32(0),
                FolderPath = r.GetString(1),
                FilePath   = r.GetString(2),
                FileName   = r.GetString(3),
                FileHash   = r.GetString(4),
                FileSize   = r.GetInt64(5),
                LastSeen   = DateTime.Parse(r.GetString(6))
            });
        return list;
    }

    /// <summary>Número de preguntas generadas para un archivo PDF concreto.</summary>
    public int GetQuestionCount(int pdfFileId)
    {
        using var conn = Open();
        using var cmd  = Cmd(conn, "SELECT COUNT(*) FROM Questions WHERE PdfFileId = $pid");
        cmd.Parameters.AddWithValue("$pid", pdfFileId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Devuelve un resumen por archivo: (Id, FileName, número de preguntas).
    /// Incluye archivos sin preguntas (COUNT = 0).
    /// </summary>
    public List<(int PdfFileId, string FileName, int Count)> GetQuestionSummary(string folderPath)
    {
        using var conn = Open();
        using var cmd  = Cmd(conn, """
            SELECT p.Id, p.FileName, COUNT(q.Id)
            FROM   PdfFiles  p
            LEFT   JOIN Questions q ON q.PdfFileId = p.Id
            WHERE  p.FolderPath = $folder
            GROUP  BY p.Id, p.FileName
            ORDER  BY p.FileName
            """);
        cmd.Parameters.AddWithValue("$folder", folderPath);
        using var r = cmd.ExecuteReader();

        var list = new List<(int, string, int)>();
        while (r.Read())
            list.Add((r.GetInt32(0), r.GetString(1), r.GetInt32(2)));
        return list;
    }

    /// <summary>
    /// Devuelve todas las preguntas de todos los PDFs de una carpeta en orden aleatorio.
    /// </summary>
    public List<QuestionRecord> GetAllQuestionsForFolder(string folderPath)
    {
        using var conn = Open();
        using var cmd  = Cmd(conn, """
            SELECT q.Id, q.PdfFileId, q.PageNumber, q.Context,
                   q.QuestionText, q.ExpectedAnswer, q.CreatedAt,
                   q.PdfSectionId, p.FileName
            FROM   Questions q
            JOIN   PdfFiles  p ON p.Id = q.PdfFileId
            WHERE  p.FolderPath = $folder
            ORDER  BY RANDOM()
            """);
        cmd.Parameters.AddWithValue("$folder", folderPath);
        using var r = cmd.ExecuteReader();

        var list = new List<QuestionRecord>();
        while (r.Read())
            list.Add(new QuestionRecord
            {
                Id             = r.GetInt32(0),
                PdfFileId      = r.GetInt32(1),
                PageNumber     = r.GetInt32(2),
                Context        = r.GetString(3),
                QuestionText   = r.GetString(4),
                ExpectedAnswer = r.GetString(5),
                CreatedAt      = DateTime.Parse(r.GetString(6)),
                PdfSectionId   = r.IsDBNull(7) ? null : r.GetInt32(7),
                FileName       = r.GetString(8)
            });
        return list;
    }

    // ── Questions ──────────────────────────────────────────────────────────

    /// <summary>
    /// Guarda una pregunta vinculada a un PDF concreto (por su Id) indicando
    /// la página y el fragmento de texto de donde proviene. Devuelve el Id.
    /// </summary>
    public int SaveQuestion(int pdfFileId, int pageNumber, string context,
                            string questionText, string expectedAnswer)
    {
        using var conn = Open();

        int? pdfSectionId = null;
        using (var secCmd = Cmd(conn, """
            SELECT Id FROM PdfSections
            WHERE PdfFileId = $pid AND StartPage <= $page AND EndPage >= $page
            LIMIT 1
            """))
        {
            secCmd.Parameters.AddWithValue("$pid",  pdfFileId);
            secCmd.Parameters.AddWithValue("$page", pageNumber);
            var secResult = secCmd.ExecuteScalar();
            if (secResult is not null and not DBNull)
                pdfSectionId = Convert.ToInt32(secResult);
        }

        using var cmd  = Cmd(conn, """
            INSERT INTO Questions
                (PdfFileId, PdfSectionId, PageNumber, Context, QuestionText, ExpectedAnswer, CreatedAt)
            VALUES ($pid, $secId, $page, $ctx, $q, $a, $created);
            SELECT last_insert_rowid();
            """);
        cmd.Parameters.AddWithValue("$pid",     pdfFileId);
        cmd.Parameters.AddWithValue("$secId",   pdfSectionId.HasValue ? (object)pdfSectionId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$page",    pageNumber);
        cmd.Parameters.AddWithValue("$ctx",     context);
        cmd.Parameters.AddWithValue("$q",       questionText);
        cmd.Parameters.AddWithValue("$a",       expectedAnswer);
        cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("o"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<QuestionRecord> GetQuestionsForFile(int pdfFileId)
    {
        using var conn = Open();
        using var cmd  = Cmd(conn, """
            SELECT q.Id, q.PdfFileId, q.PageNumber, q.Context,
                   q.QuestionText, q.ExpectedAnswer, q.CreatedAt,
                   q.PdfSectionId, p.FileName
            FROM   Questions q
            JOIN   PdfFiles  p ON p.Id = q.PdfFileId
            WHERE  q.PdfFileId = $pid
            ORDER  BY q.PageNumber, q.Id
            """);
        cmd.Parameters.AddWithValue("$pid", pdfFileId);
        using var r = cmd.ExecuteReader();

        var list = new List<QuestionRecord>();
        while (r.Read())
            list.Add(new QuestionRecord
            {
                Id             = r.GetInt32(0),
                PdfFileId      = r.GetInt32(1),
                PageNumber     = r.GetInt32(2),
                Context        = r.GetString(3),
                QuestionText   = r.GetString(4),
                ExpectedAnswer = r.GetString(5),
                CreatedAt      = DateTime.Parse(r.GetString(6)),
                PdfSectionId   = r.IsDBNull(7) ? null : r.GetInt32(7),
                FileName       = r.GetString(8)
            });
        return list;
    }

    // ── UserAnswers ────────────────────────────────────────────────────────

    /// <summary>Guarda la respuesta del usuario con su puntuación. Devuelve el Id.</summary>
    public int SaveUserAnswer(int questionId, string userAnswer, double score, string feedback)
    {
        using var conn = Open();
        using var cmd  = Cmd(conn, """
            INSERT INTO UserAnswers (QuestionId, UserAnswer, Score, Feedback, AnsweredAt)
            VALUES ($qid, $ans, $score, $fb, $at);
            SELECT last_insert_rowid();
            """);
        cmd.Parameters.AddWithValue("$qid",   questionId);
        cmd.Parameters.AddWithValue("$ans",   userAnswer);
        cmd.Parameters.AddWithValue("$score", score);
        cmd.Parameters.AddWithValue("$fb",    feedback);
        cmd.Parameters.AddWithValue("$at",    DateTime.UtcNow.ToString("o"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<UserAnswerRecord> GetAnswersForQuestion(int questionId)
    {
        using var conn = Open();
        using var cmd  = Cmd(conn, """
            SELECT Id, QuestionId, UserAnswer, Score, Feedback, AnsweredAt
            FROM   UserAnswers
            WHERE  QuestionId = $qid
            ORDER  BY AnsweredAt DESC
            """);
        cmd.Parameters.AddWithValue("$qid", questionId);
        using var r = cmd.ExecuteReader();

        var list = new List<UserAnswerRecord>();
        while (r.Read())
            list.Add(new UserAnswerRecord
            {
                Id         = r.GetInt32(0),
                QuestionId = r.GetInt32(1),
                UserAnswer = r.GetString(2),
                Score      = r.GetDouble(3),
                Feedback   = r.GetString(4),
                AnsweredAt = DateTime.Parse(r.GetString(5))
            });
        return list;
    }

    // ── Helpers privados ───────────────────────────────────────────────────
    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_cs);
        conn.Open();
        return conn;
    }

    private static SqliteCommand Cmd(SqliteConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = Cmd(conn, sql);
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction tx,
                                string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = Cmd(conn, sql);
        cmd.Transaction = tx;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static void TryMigrate(SqliteConnection conn, string sql)
    {
        try   { Execute(conn, sql); }
        catch { /* columna / índice ya existe, se ignora */ }
    }
    public bool HasSections(int pdfFileId)
    {
        using var conn = Open();
        using var cmd = Cmd(conn, "SELECT COUNT(*) FROM PdfSections WHERE PdfFileId = $pid");
        cmd.Parameters.AddWithValue("$pid", pdfFileId);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public void SaveSections(int pdfFileId, List<PdfSection> sections)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        Execute(conn, tx, "DELETE FROM PdfSections WHERE PdfFileId = $pid",
                ("$pid", (object)pdfFileId));
        foreach (var s in sections)
        {
            using var ins = Cmd(conn,
                "INSERT INTO PdfSections (PdfFileId, Title, StartPage, EndPage) " +
                "VALUES ($pid, $title, $start, $end)");
            ins.Transaction = tx;
            ins.Parameters.AddWithValue("$pid", pdfFileId);
            ins.Parameters.AddWithValue("$title", s.Title);
            ins.Parameters.AddWithValue("$start", s.StartPage);
            ins.Parameters.AddWithValue("$end", s.EndPage);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<PdfSection> GetSections(int pdfFileId)
    {
        using var conn = Open();
        using var cmd = Cmd(conn,
            "SELECT Id, PdfFileId, Title, StartPage, EndPage " +
            "FROM PdfSections WHERE PdfFileId = $pid ORDER BY StartPage");
        cmd.Parameters.AddWithValue("$pid", pdfFileId);
        using var r = cmd.ExecuteReader();
        var list = new List<PdfSection>();
        while (r.Read())
            list.Add(new PdfSection
            {
                Id = r.GetInt32(0),
                PdfFileId = r.GetInt32(1),
                Title = r.GetString(2),
                StartPage = r.GetInt32(3),
                EndPage = r.GetInt32(4)
            });
        return list;
    }
}
