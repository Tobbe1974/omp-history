using System.Text;
using System.Text.Json;
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
                diff_patch TEXT,
                git_tree_before TEXT,
                git_tree_after TEXT,
                FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_conversations_session_id ON conversations(session_id);

            CREATE TABLE IF NOT EXISTS deleted_sessions (
                session_id TEXT PRIMARY KEY,
                deleted_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS suggestions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                repo TEXT NOT NULL,
                repo_path TEXT,
                session_id TEXT,
                conversation_id INTEGER,
                title TEXT NOT NULL,
                human_summary TEXT NOT NULL,
                agent_context TEXT NOT NULL,
                file_paths TEXT,
                severity TEXT,
                status TEXT NOT NULL DEFAULT 'open',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_suggestions_repo ON suggestions(repo);
            CREATE INDEX IF NOT EXISTS idx_suggestions_conversation_id ON suggestions(conversation_id);
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

        foreach (var alterSql in new[]
        {
            "ALTER TABLE conversations ADD COLUMN diff_patch TEXT;",
            "ALTER TABLE conversations ADD COLUMN git_tree_before TEXT;",
            "ALTER TABLE conversations ADD COLUMN git_tree_after TEXT;",
            "ALTER TABLE conversations ADD COLUMN credits_used REAL;",
            "ALTER TABLE conversations ADD COLUMN cost_usd REAL;",
            "ALTER TABLE conversations ADD COLUMN total_tokens INTEGER;",
            "ALTER TABLE conversations ADD COLUMN model TEXT;"
        })
        {
            try
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = alterSql;
                alterCmd.ExecuteNonQuery();
            }
            catch
            {
                // Column already exists
            }
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
            DELETE FROM deleted_sessions WHERE session_id = $sessionId;
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
        var now = DateTime.UtcNow.ToString("O");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE session_id = $sessionId;";
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        var rows = cmd.ExecuteNonQuery();

        using var tombCmd = conn.CreateCommand();
        tombCmd.CommandText = """
            INSERT INTO deleted_sessions (session_id, deleted_at)
            VALUES ($sessionId, $now)
            ON CONFLICT(session_id) DO UPDATE SET deleted_at = excluded.deleted_at;
            """;
        tombCmd.Parameters.AddWithValue("$sessionId", sessionId);
        tombCmd.Parameters.AddWithValue("$now", now);
        tombCmd.ExecuteNonQuery();

        if (rows > 0)
        {
            _notifier.NotifyChanged();
            return true;
        }
        return false;
    }

    public IReadOnlyList<string> GetDeletedSessionIds()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT session_id
            FROM deleted_sessions
            ORDER BY deleted_at DESC
            LIMIT 500;
            """;

        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    public ConversationRecord AddConversation(
        string sessionId,
        string prompt,
        string responseMd,
        string? repo = null,
        string? sessionName = null,
        string? repoPath = null,
        string? diffPatch = null,
        string? gitTreeBefore = null,
        string? gitTreeAfter = null,
        double? creditsUsed = null,
        double? costUsd = null,
        long? totalTokens = null,
        string? model = null,
        IReadOnlyList<long>? suggestionIds = null)
    {
        var now = DateTime.UtcNow.ToString("O");
        var normalizedRepoPath = string.IsNullOrWhiteSpace(repoPath) ? null : repoPath.Trim();
        var normalizedDiffPatch = string.IsNullOrWhiteSpace(diffPatch) ? null : diffPatch;
        var normalizedTreeBefore = string.IsNullOrWhiteSpace(gitTreeBefore) ? null : gitTreeBefore.Trim();
        var normalizedTreeAfter = string.IsNullOrWhiteSpace(gitTreeAfter) ? null : gitTreeAfter.Trim();
        var normalizedModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
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
                INSERT INTO conversations (session_id, prompt, response_md, created_at, diff_patch, git_tree_before, git_tree_after, credits_used, cost_usd, total_tokens, model)
                VALUES ($sessionId, $prompt, $responseMd, $now, $diffPatch, $gitTreeBefore, $gitTreeAfter, $creditsUsed, $costUsd, $totalTokens, $model);
                SELECT last_insert_rowid();
                """;
            insertCmd.Parameters.AddWithValue("$sessionId", sessionId);
            insertCmd.Parameters.AddWithValue("$prompt", prompt);
            insertCmd.Parameters.AddWithValue("$responseMd", responseMd);
            insertCmd.Parameters.AddWithValue("$now", now);
            insertCmd.Parameters.AddWithValue("$diffPatch", (object?)normalizedDiffPatch ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$gitTreeBefore", (object?)normalizedTreeBefore ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$gitTreeAfter", (object?)normalizedTreeAfter ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$creditsUsed", (object?)creditsUsed ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$costUsd", (object?)costUsd ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$totalTokens", (object?)totalTokens ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$model", (object?)normalizedModel ?? DBNull.Value);
            id = (long)insertCmd.ExecuteScalar()!;
        }
        var linkedSuggestions = 0;
        if (suggestionIds is { Count: > 0 })
        {
            foreach (var sugId in suggestionIds.Distinct())
            {
                if (sugId <= 0) continue;
                using var linkCmd = conn.CreateCommand();
                linkCmd.Transaction = tx;
                linkCmd.CommandText = """
                    UPDATE suggestions
                    SET conversation_id = $convId,
                        session_id = COALESCE(session_id, $sessionId),
                        updated_at = $now
                    WHERE id = $sugId;
                    """;
                linkCmd.Parameters.AddWithValue("$convId", id);
                linkCmd.Parameters.AddWithValue("$sessionId", sessionId);
                linkCmd.Parameters.AddWithValue("$now", now);
                linkCmd.Parameters.AddWithValue("$sugId", sugId);
                linkedSuggestions += linkCmd.ExecuteNonQuery();
            }
        }


        tx.Commit();
        _notifier.NotifyConversationAdded(effectiveRepo, sessionId, id);

        return new ConversationRecord(
            id,
            sessionId,
            prompt,
            responseMd,
            now,
            normalizedDiffPatch,
            normalizedTreeBefore,
            normalizedTreeAfter,
            creditsUsed,
            costUsd,
            totalTokens,
            normalizedModel,
            linkedSuggestions);
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
                   COUNT(c.id) AS conversation_count,
                   SUM(c.credits_used) AS total_credits,
                   SUM(c.cost_usd) AS total_cost_usd,
                   SUM(c.total_tokens) AS total_tokens
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
            list.Add(ReadSessionRecord(reader));
        }
        return list;
    }

    public IReadOnlyList<SessionRecord> GetAllSessions(int limit = 100)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.session_id,
                   s.repo,
                   s.name,
                   s.created_at,
                   s.updated_at,
                   COUNT(c.id) AS conversation_count,
                   SUM(c.credits_used) AS total_credits,
                   SUM(c.cost_usd) AS total_cost_usd,
                   SUM(c.total_tokens) AS total_tokens
            FROM sessions s
            LEFT JOIN conversations c ON c.session_id = s.session_id
            GROUP BY s.session_id, s.repo, s.name, s.created_at, s.updated_at
            ORDER BY s.updated_at DESC, s.created_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<SessionRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadSessionRecord(reader));
        }
        return list;
    }

    public SessionRecord? GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.session_id,
                   s.repo,
                   s.name,
                   s.created_at,
                   s.updated_at,
                   COUNT(c.id) AS conversation_count,
                   SUM(c.credits_used) AS total_credits,
                   SUM(c.cost_usd) AS total_cost_usd,
                   SUM(c.total_tokens) AS total_tokens
            FROM sessions s
            LEFT JOIN conversations c ON c.session_id = s.session_id
            WHERE s.session_id = $sessionId
            GROUP BY s.session_id, s.repo, s.name, s.created_at, s.updated_at
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$sessionId", sessionId.Trim());

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return ReadSessionRecord(reader);
        }
        return null;
    }

    private static SessionRecord ReadSessionRecord(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            reader.IsDBNull(7) ? null : reader.GetDouble(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8));

    public string? ExportSessionJsonl(string sessionId, string? targetCwd = null, string? model = null)
    {
        var session = GetSession(sessionId);
        if (session is null)
        {
            return null;
        }

        var conversations = GetConversations(session.SessionId);
        var resolvedCwd = !string.IsNullOrWhiteSpace(targetCwd)
            ? targetCwd.Trim()
            : (GetRepoPath(session.Repo) ?? "/work");
        var normalizedModel = !string.IsNullOrWhiteSpace(model) ? model.Trim() : null;
        string? providerPart = null;
        string? modelIdPart = null;
        if (!string.IsNullOrWhiteSpace(normalizedModel))
        {
            var slashIdx = normalizedModel.IndexOf('/');
            if (slashIdx > 0 && slashIdx < normalizedModel.Length - 1)
            {
                providerPart = normalizedModel[..slashIdx];
                modelIdPart = normalizedModel[(slashIdx + 1)..];
            }
            else
            {
                modelIdPart = normalizedModel;
            }
        }

        var sb = new StringBuilder();

        var sessionHeader = new
        {
            type = "session",
            version = 3,
            id = session.SessionId,
            timestamp = session.CreatedAt,
            cwd = resolvedCwd,
            title = session.Name,
            titleSource = "user"
        };
        sb.AppendLine(JsonSerializer.Serialize(sessionHeader));

        string? parentId = null;
        if (!string.IsNullOrWhiteSpace(normalizedModel))
        {
            var modelChangeId = "m0000001";
            var modelChangeEntry = new
            {
                type = "model_change",
                id = modelChangeId,
                parentId,
                timestamp = session.CreatedAt,
                model = normalizedModel,
                role = "default",
                resolvedModelIsFallback = false
            };
            sb.AppendLine(JsonSerializer.Serialize(modelChangeEntry));
            parentId = modelChangeId;
        }

        for (var i = 0; i < conversations.Count; i++)
        {
            var conv = conversations[i];
            var turnNum = i + 1;
            var userEntryId = $"u{turnNum:x7}";
            var asstEntryId = $"a{turnNum:x7}";
            var tsMs = DateTimeOffset.TryParse(conv.CreatedAt, out var dto)
                ? dto.ToUnixTimeMilliseconds()
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var userEntry = new
            {
                type = "message",
                id = userEntryId,
                parentId,
                timestamp = conv.CreatedAt,
                message = new
                {
                    role = "user",
                    content = new[]
                    {
                        new { type = "text", text = conv.Prompt ?? string.Empty }
                    },
                    attribution = "user",
                    timestamp = tsMs
                }
            };
            sb.AppendLine(JsonSerializer.Serialize(userEntry));

            object asstMessage = (!string.IsNullOrWhiteSpace(providerPart) && !string.IsNullOrWhiteSpace(modelIdPart))
                ? new
                {
                    role = "assistant",
                    provider = providerPart,
                    model = modelIdPart,
                    content = new[]
                    {
                        new { type = "text", text = conv.ResponseMd ?? string.Empty }
                    },
                    stopReason = "stop",
                    timestamp = tsMs + 1
                }
                : new
                {
                    role = "assistant",
                    content = new[]
                    {
                        new { type = "text", text = conv.ResponseMd ?? string.Empty }
                    },
                    stopReason = "stop",
                    timestamp = tsMs + 1
                };

            var asstEntry = new
            {
                type = "message",
                id = asstEntryId,
                parentId = userEntryId,
                timestamp = conv.CreatedAt,
                message = asstMessage
            };
            sb.AppendLine(JsonSerializer.Serialize(asstEntry));
            parentId = asstEntryId;
        }

        return sb.ToString();
    }

    public IReadOnlyList<ConversationRecord> GetConversations(string sessionId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.id,
                   c.session_id,
                   c.prompt,
                   c.response_md,
                   c.created_at,
                   c.diff_patch,
                   c.git_tree_before,
                   c.git_tree_after,
                   c.credits_used,
                   c.cost_usd,
                   c.total_tokens,
                   c.model,
                   (SELECT COUNT(*) FROM suggestions sg WHERE sg.conversation_id = c.id) AS suggestion_count
            FROM conversations c
            WHERE c.session_id = $sessionId
            ORDER BY c.id ASC;
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
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetDouble(8),
                reader.IsDBNull(9) ? null : reader.GetDouble(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetInt32(12)));
        }
        return list;
    }

    public TurnDetailsRecord? GetTurnDetails(long conversationId)
    {
        ConversationRecord? target = null;
        string? sessionId = null;

        using (var conn = OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT c.id,
                       c.session_id,
                       c.prompt,
                       c.response_md,
                       c.created_at,
                       c.diff_patch,
                       c.git_tree_before,
                       c.git_tree_after,
                       c.credits_used,
                       c.cost_usd,
                       c.total_tokens,
                       c.model,
                       (SELECT COUNT(*) FROM suggestions sg WHERE sg.conversation_id = c.id) AS suggestion_count
                FROM conversations c
                WHERE c.id = $id
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$id", conversationId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                sessionId = reader.GetString(1);
                target = new ConversationRecord(
                    reader.GetInt64(0),
                    sessionId,
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetDouble(8),
                    reader.IsDBNull(9) ? null : reader.GetDouble(9),
                    reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.GetInt32(12));
            }
        }

        if (target is null || string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var session = GetSession(sessionId);
        if (session is null)
        {
            return null;
        }

        var allInSession = GetConversations(sessionId);
        var turnNumber = 1;
        for (var i = 0; i < allInSession.Count; i++)
        {
            if (allInSession[i].Id == conversationId)
            {
                turnNumber = i + 1;
                break;
            }
        }

        return new TurnDetailsRecord(target, session, turnNumber, Math.Max(allInSession.Count, turnNumber));
    }

    public AppSettingsRecord GetAppSettings()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var conn = OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value FROM app_settings;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                dict[reader.GetString(0)] = reader.GetString(1);
            }
        }

        var autoSelect = !dict.TryGetValue("auto_select_new_conversation", out var autoStr)
            || !bool.TryParse(autoStr, out var parsedAuto)
            || parsedAuto;
        var pinLists = !dict.TryGetValue("pin_lists", out var pinStr)
            || !bool.TryParse(pinStr, out var parsedPin)
            || parsedPin;
        var ntfyEnabled = dict.TryGetValue("ntfy_enabled", out var ntfyEnStr)
            && bool.TryParse(ntfyEnStr, out var parsedNtfy)
            && parsedNtfy;
        var ntfyServerUrl = dict.TryGetValue("ntfy_server_url", out var srv) && !string.IsNullOrWhiteSpace(srv)
            ? srv.Trim().TrimEnd('/')
            : "https://skegriebyavag13.duckdns.org";
        var ntfyTopic = dict.TryGetValue("ntfy_topic", out var top) && !string.IsNullOrWhiteSpace(top)
            ? top.Trim().Trim('/')
            : "omp-history";
        var askBeforeDeleteSession = !dict.TryGetValue("ask_before_delete_session", out var askSessStr)
            || !bool.TryParse(askSessStr, out var parsedAskSess)
            || parsedAskSess;
        var askBeforeDeleteSuggestion = !dict.TryGetValue("ask_before_delete_suggestion", out var askSugStr)
            || !bool.TryParse(askSugStr, out var parsedAskSug)
            || parsedAskSug;

        return new AppSettingsRecord(
            AutoSelectNewConversation: autoSelect,
            PinLists: pinLists,
            NtfyEnabled: ntfyEnabled,
            NtfyServerUrl: ntfyServerUrl,
            NtfyTopic: ntfyTopic,
            AskBeforeDeleteSession: askBeforeDeleteSession,
            AskBeforeDeleteSuggestion: askBeforeDeleteSuggestion);
    }

    public AppSettingsRecord SaveAppSettings(AppSettingsRecord settings)
    {
        var normalizedServer = string.IsNullOrWhiteSpace(settings.NtfyServerUrl)
            ? "https://skegriebyavag13.duckdns.org"
            : settings.NtfyServerUrl.Trim().TrimEnd('/');
        var normalizedTopic = string.IsNullOrWhiteSpace(settings.NtfyTopic)
            ? "omp-history"
            : settings.NtfyTopic.Trim().Trim('/');

        var normalized = new AppSettingsRecord(
            AutoSelectNewConversation: settings.AutoSelectNewConversation,
            PinLists: settings.PinLists,
            NtfyEnabled: settings.NtfyEnabled,
            NtfyServerUrl: normalizedServer,
            NtfyTopic: normalizedTopic,
            AskBeforeDeleteSession: settings.AskBeforeDeleteSession,
            AskBeforeDeleteSuggestion: settings.AskBeforeDeleteSuggestion);

        var pairs = new (string Key, string Value)[]
        {
            ("auto_select_new_conversation", normalized.AutoSelectNewConversation ? "true" : "false"),
            ("pin_lists", normalized.PinLists ? "true" : "false"),
            ("ntfy_enabled", normalized.NtfyEnabled ? "true" : "false"),
            ("ntfy_server_url", normalized.NtfyServerUrl),
            ("ntfy_topic", normalized.NtfyTopic),
            ("ask_before_delete_session", normalized.AskBeforeDeleteSession ? "true" : "false"),
            ("ask_before_delete_suggestion", normalized.AskBeforeDeleteSuggestion ? "true" : "false")
        };

        using (var conn = OpenConnection())
        using (var tx = conn.BeginTransaction())
        {
            foreach (var (key, val) in pairs)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO app_settings (key, value)
                    VALUES ($key, $val)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                    """;
                cmd.Parameters.AddWithValue("$key", key);
                cmd.Parameters.AddWithValue("$val", val);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        _notifier.NotifyChanged();
        return normalized;
    }

    public SuggestionRecord AddSuggestion(CreateSuggestionRequest req)
    {
        var now = DateTime.UtcNow.ToString("O");
        var normalizedRepo = string.IsNullOrWhiteSpace(req.Repo) ? "unknown" : req.Repo.Trim();
        var normalizedRepoPath = string.IsNullOrWhiteSpace(req.RepoPath) ? null : req.RepoPath.Trim();
        var normalizedSessionId = string.IsNullOrWhiteSpace(req.SessionId) ? null : req.SessionId.Trim();
        var normalizedTitle = string.IsNullOrWhiteSpace(req.Title) ? "Code improvement suggestion" : req.Title.Trim();
        var normalizedHuman = req.HumanSummary?.Trim() ?? string.Empty;
        var normalizedAgent = req.AgentContext?.Trim() ?? string.Empty;
        var normalizedFiles = string.IsNullOrWhiteSpace(req.FilePaths) ? null : req.FilePaths.Trim();
        var normalizedSeverity = string.IsNullOrWhiteSpace(req.Severity) ? null : req.Severity.Trim().ToLowerInvariant();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO suggestions (repo, repo_path, session_id, conversation_id, title, human_summary, agent_context, file_paths, severity, status, created_at, updated_at)
            VALUES ($repo, $repoPath, $sessionId, $convId, $title, $humanSummary, $agentContext, $filePaths, $severity, 'open', $now, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$repo", normalizedRepo);
        cmd.Parameters.AddWithValue("$repoPath", (object?)normalizedRepoPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sessionId", (object?)normalizedSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$convId", (object?)req.ConversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", normalizedTitle);
        cmd.Parameters.AddWithValue("$humanSummary", normalizedHuman);
        cmd.Parameters.AddWithValue("$agentContext", normalizedAgent);
        cmd.Parameters.AddWithValue("$filePaths", (object?)normalizedFiles ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$severity", (object?)normalizedSeverity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);

        var id = (long)cmd.ExecuteScalar()!;
        _notifier.NotifyChanged();

        return new SuggestionRecord(
            id,
            normalizedRepo,
            normalizedRepoPath,
            normalizedSessionId,
            req.ConversationId,
            normalizedTitle,
            normalizedHuman,
            normalizedAgent,
            normalizedFiles,
            normalizedSeverity,
            "open",
            now,
            now);
    }

    public IReadOnlyList<SuggestionRecord> GetSuggestionsByRepo(string repo)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, repo, repo_path, session_id, conversation_id, title, human_summary, agent_context, file_paths, severity, status, created_at, updated_at
            FROM suggestions
            WHERE repo = $repo
            ORDER BY CASE WHEN status = 'open' THEN 0 ELSE 1 END, id DESC;
            """;
        cmd.Parameters.AddWithValue("$repo", repo);

        var list = new List<SuggestionRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadSuggestionRecord(reader));
        }
        return list;
    }

    public SuggestionRecord? GetSuggestion(long id)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, repo, repo_path, session_id, conversation_id, title, human_summary, agent_context, file_paths, severity, status, created_at, updated_at
            FROM suggestions
            WHERE id = $id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadSuggestionRecord(reader) : null;
    }

    public bool UpdateSuggestionStatus(long id, string status)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE suggestions
            SET status = $status, updated_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$now", now);
        var rows = cmd.ExecuteNonQuery();
        if (rows > 0)
        {
            _notifier.NotifyChanged();
            return true;
        }
        return false;
    }

    public bool DeleteSuggestion(long id)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM suggestions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        var rows = cmd.ExecuteNonQuery();
        if (rows > 0)
        {
            _notifier.NotifyChanged();
            return true;
        }
        return false;
    }

    private static SuggestionRecord ReadSuggestionRecord(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetInt64(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.GetString(10),
        reader.GetString(11),
        reader.GetString(12));

    private static string FirstLinePreview(string text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Untitled Session";
        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "Untitled Session";
        return firstLine.Length <= maxLen ? firstLine : firstLine[..maxLen] + "…";
    }
}
