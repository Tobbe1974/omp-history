using Microsoft.Data.Sqlite;
using OmpHistory.Models;

namespace OmpHistory.Data;

public sealed class HistoryRepository
{
    private readonly string _connectionString;
    private readonly OmpHistory.Services.HistoryNotifier _notifier;

    public HistoryRepository(string dbPath, OmpHistory.Services.HistoryNotifier notifier)
    {
        var fullPath = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            ForeignKeys = true
        }.ToString();

        _notifier = notifier;
        InitializeDatabase();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void InitializeDatabase()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                session_id TEXT PRIMARY KEY,
                repo TEXT NOT NULL,
                name TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_sessions_repo ON sessions(repo);

            CREATE TABLE IF NOT EXISTS conversations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                prompt TEXT NOT NULL,
                response_md TEXT NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_conversations_session_id ON conversations(session_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public void UpsertSession(string sessionId, string repo, string name)
    {
        var now = DateTime.UtcNow.ToString("O");
        var normalizedRepo = string.IsNullOrWhiteSpace(repo) ? "unknown" : repo.Trim();
        var normalizedName = string.IsNullOrWhiteSpace(name) ? sessionId[..Math.Min(8, sessionId.Length)] : name.Trim();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (session_id, repo, name, created_at, updated_at)
            VALUES ($sessionId, $repo, $name, $now, $now)
            ON CONFLICT(session_id) DO UPDATE SET
                repo = CASE WHEN excluded.repo <> 'unknown' THEN excluded.repo ELSE sessions.repo END,
                name = excluded.name,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        cmd.Parameters.AddWithValue("$repo", normalizedRepo);
        cmd.Parameters.AddWithValue("$name", normalizedName);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        _notifier.NotifyChanged();
    }

    public bool RenameSession(string sessionId, string name, string? repo = null)
    {
        var now = DateTime.UtcNow.ToString("O");
        var normalizedName = name.Trim();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions
            SET name = $name,
                updated_at = $now
            WHERE session_id = $sessionId;
            """;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        cmd.Parameters.AddWithValue("$name", normalizedName);
        cmd.Parameters.AddWithValue("$now", now);

        var rows = cmd.ExecuteNonQuery();
        if (rows == 0 && !string.IsNullOrWhiteSpace(repo))
        {
            UpsertSession(sessionId, repo, normalizedName);
            return true;
        }

        if (rows > 0)
        {
            _notifier.NotifyChanged();
            return true;
        }

        return false;
    }

    public bool DeleteSession(string sessionId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE session_id = $sessionId;";
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        var rows = cmd.ExecuteNonQuery();
        if (rows > 0)
        {
            _notifier.NotifyChanged();
            return true;
        }
        return false;
    }

    public ConversationRecord AddConversation(string sessionId, string prompt, string responseMd, string? repo = null, string? sessionName = null)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var ensureCmd = conn.CreateCommand())
        {
            ensureCmd.Transaction = tx;
            var fallbackRepo = string.IsNullOrWhiteSpace(repo) ? "unknown" : repo.Trim();
            var fallbackName = !string.IsNullOrWhiteSpace(sessionName)
                ? sessionName.Trim()
                : FirstLinePreview(prompt, 60);

            ensureCmd.CommandText = """
                INSERT INTO sessions (session_id, repo, name, created_at, updated_at)
                VALUES ($sessionId, $repo, $name, $now, $now)
                ON CONFLICT(session_id) DO UPDATE SET
                    updated_at = $now,
                    repo = CASE WHEN sessions.repo = 'unknown' AND $repo <> 'unknown' THEN $repo ELSE sessions.repo END,
                    name = CASE WHEN $hasExplicitName = 1 THEN $name ELSE sessions.name END;
                """;
            ensureCmd.Parameters.AddWithValue("$sessionId", sessionId);
            ensureCmd.Parameters.AddWithValue("$repo", fallbackRepo);
            ensureCmd.Parameters.AddWithValue("$name", fallbackName);
            ensureCmd.Parameters.AddWithValue("$hasExplicitName", !string.IsNullOrWhiteSpace(sessionName) ? 1 : 0);
            ensureCmd.Parameters.AddWithValue("$now", now);
            ensureCmd.ExecuteNonQuery();
        }

        long id;
        using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO conversations (session_id, prompt, response_md, created_at)
                VALUES ($sessionId, $prompt, $responseMd, $now);
                SELECT last_insert_rowid();
                """;
            insertCmd.Parameters.AddWithValue("$sessionId", sessionId);
            insertCmd.Parameters.AddWithValue("$prompt", prompt);
            insertCmd.Parameters.AddWithValue("$responseMd", responseMd);
            insertCmd.Parameters.AddWithValue("$now", now);
            id = (long)insertCmd.ExecuteScalar()!;
        }

        tx.Commit();
        _notifier.NotifyChanged();

        return new ConversationRecord(id, sessionId, prompt, responseMd, now);
    }

    public IReadOnlyList<RepoSummary> GetRepos()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT repo, COUNT(*) AS session_count, MAX(updated_at) AS last_updated
            FROM sessions
            GROUP BY repo
            ORDER BY last_updated DESC, repo ASC;
            """;

        var list = new List<RepoSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new RepoSummary(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2)));
        }
        return list;
    }

    public IReadOnlyList<SessionRecord> GetSessionsByRepo(string repo)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.session_id,
                   s.repo,
                   s.name,
                   s.created_at,
                   s.updated_at,
                   COUNT(c.id) AS conversation_count
            FROM sessions s
            LEFT JOIN conversations c ON c.session_id = s.session_id
            WHERE s.repo = $repo
            GROUP BY s.session_id, s.repo, s.name, s.created_at, s.updated_at
            ORDER BY s.updated_at DESC, s.created_at DESC;
            """;
        cmd.Parameters.AddWithValue("$repo", repo);

        var list = new List<SessionRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new SessionRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5)));
        }
        return list;
    }

    public IReadOnlyList<ConversationRecord> GetConversations(string sessionId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, prompt, response_md, created_at
            FROM conversations
            WHERE session_id = $sessionId
            ORDER BY id ASC;
            """;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);

        var list = new List<ConversationRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ConversationRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }
        return list;
    }

    private static string FirstLinePreview(string text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Untitled Session";
        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "Untitled Session";
        return firstLine.Length <= maxLen ? firstLine : firstLine[..maxLen] + "…";
    }
}
