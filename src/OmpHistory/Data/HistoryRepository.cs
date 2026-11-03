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
                updated_at TEXT NOT NULL,
                repo_path TEXT
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

        try
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE sessions ADD COLUMN repo_path TEXT;";
            alterCmd.ExecuteNonQuery();
        }
        catch
        {
            // Column already exists
        }
    }

    public void UpsertSession(string sessionId, string repo, string name, string? repoPath = null)
    {
        var now = DateTime.UtcNow.ToString("O");
        var normalizedRepo = string.IsNullOrWhiteSpace(repo) ? "unknown" : repo.Trim();
        var normalizedName = string.IsNullOrWhiteSpace(name) ? sessionId[..Math.Min(8, sessionId.Length)] : name.Trim();
        var normalizedRepoPath = string.IsNullOrWhiteSpace(repoPath) ? null : repoPath.Trim();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (session_id, repo, name, created_at, updated_at, repo_path)
            VALUES ($sessionId, $repo, $name, $now, $now, $repoPath)
            ON CONFLICT(session_id) DO UPDATE SET
                repo = CASE WHEN excluded.repo <> 'unknown' THEN excluded.repo ELSE sessions.repo END,
                name = excluded.name,
                updated_at = excluded.updated_at,
                repo_path = COALESCE(excluded.repo_path, sessions.repo_path);
            """;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        cmd.Parameters.AddWithValue("$repo", normalizedRepo);
        cmd.Parameters.AddWithValue("$name", normalizedName);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$repoPath", (object?)normalizedRepoPath ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        _notifier.NotifyChanged();
    }

    public void UpdateSessionRepoPath(string sessionId, string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(repoPath))
        {
            return;
        }

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions
            SET repo_path = $repoPath
            WHERE session_id = $sessionId AND (repo_path IS NULL OR repo_path <> $repoPath);
            """;
        cmd.Parameters.AddWithValue("$sessionId", sessionId.Trim());
        cmd.Parameters.AddWithValue("$repoPath", repoPath.Trim());
        cmd.ExecuteNonQuery();
    }

    public bool RenameSession(string sessionId, string name, string? repo = null, string? repoPath = null)
    {
        var now = DateTime.UtcNow.ToString("O");
        var normalizedName = name.Trim();
        var normalizedRepoPath = string.IsNullOrWhiteSpace(repoPath) ? null : repoPath.Trim();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions
            SET name = $name,
                updated_at = $now,
                repo_path = COALESCE($repoPath, repo_path)
            WHERE session_id = $sessionId;
            """;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        cmd.Parameters.AddWithValue("$name", normalizedName);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$repoPath", (object?)normalizedRepoPath ?? DBNull.Value);

        var rows = cmd.ExecuteNonQuery();
        if (rows == 0 && !string.IsNullOrWhiteSpace(repo))
        {
            UpsertSession(sessionId, repo, normalizedName, normalizedRepoPath);
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

    public ConversationRecord AddConversation(string sessionId, string prompt, string responseMd, string? repo = null, string? sessionName = null, string? repoPath = null)
    {
        var now = DateTime.UtcNow.ToString("O");
        var normalizedRepoPath = string.IsNullOrWhiteSpace(repoPath) ? null : repoPath.Trim();
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        var fallbackRepo = string.IsNullOrWhiteSpace(repo) ? "unknown" : repo.Trim();
        using (var ensureCmd = conn.CreateCommand())
        {
            ensureCmd.Transaction = tx;
            var fallbackName = !string.IsNullOrWhiteSpace(sessionName)
                ? sessionName.Trim()
                : FirstLinePreview(prompt, 60);

            ensureCmd.CommandText = """
                INSERT INTO sessions (session_id, repo, name, created_at, updated_at, repo_path)
                VALUES ($sessionId, $repo, $name, $now, $now, $repoPath)
                ON CONFLICT(session_id) DO UPDATE SET
                    updated_at = $now,
                    repo = CASE WHEN sessions.repo = 'unknown' AND $repo <> 'unknown' THEN $repo ELSE sessions.repo END,
                    name = CASE WHEN $hasExplicitName = 1 THEN $name ELSE sessions.name END,
                    repo_path = COALESCE(excluded.repo_path, sessions.repo_path);
                """;
            ensureCmd.Parameters.AddWithValue("$sessionId", sessionId);
            ensureCmd.Parameters.AddWithValue("$repo", fallbackRepo);
            ensureCmd.Parameters.AddWithValue("$name", fallbackName);
            ensureCmd.Parameters.AddWithValue("$hasExplicitName", !string.IsNullOrWhiteSpace(sessionName) ? 1 : 0);
            ensureCmd.Parameters.AddWithValue("$now", now);
            ensureCmd.Parameters.AddWithValue("$repoPath", (object?)normalizedRepoPath ?? DBNull.Value);
            ensureCmd.ExecuteNonQuery();
        }

        string effectiveRepo = fallbackRepo;
        using (var repoCmd = conn.CreateCommand())
        {
            repoCmd.Transaction = tx;
            repoCmd.CommandText = "SELECT repo FROM sessions WHERE session_id = $sessionId;";
            repoCmd.Parameters.AddWithValue("$sessionId", sessionId);
            if (repoCmd.ExecuteScalar() is string dbRepo && !string.IsNullOrWhiteSpace(dbRepo))
            {
                effectiveRepo = dbRepo;
            }
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
        _notifier.NotifyConversationAdded(effectiveRepo, sessionId, id);

        return new ConversationRecord(id, sessionId, prompt, responseMd, now);
    }

    public IReadOnlyList<RepoSummary> GetRepos()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT repo,
                   COUNT(*) AS session_count,
                   MAX(updated_at) AS last_updated,
                   MAX(repo_path) AS repo_path
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
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return list;
    }

    public string? GetRepoPath(string repo)
    {
        if (string.IsNullOrWhiteSpace(repo))
        {
            return null;
        }

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT repo_path
            FROM sessions
            WHERE repo = $repo AND repo_path IS NOT NULL AND TRIM(repo_path) <> ''
            ORDER BY updated_at DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$repo", repo.Trim());
        return cmd.ExecuteScalar() as string;
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
