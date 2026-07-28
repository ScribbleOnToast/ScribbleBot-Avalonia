using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ScribbleBot.Models;

namespace ScribbleBot.Services;

public class DatabaseService
{
    private readonly ILogger<DatabaseService> _logger;
    private readonly string _dbPath;
    private readonly string _connectionString;

    public bool IsAvailable { get; private set; } = false;

    #region Constructor and Initialization
    public DatabaseService(ILogger<DatabaseService> logger)
    {
        _logger = logger;

        string appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScribbleBot");
        Directory.CreateDirectory(appDataFolder);

        _dbPath = Path.Combine(appDataFolder, "scribble.db");
        _connectionString = $"Data Source={_dbPath};";
    }

    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing database at {Path}...", _dbPath);

        if (!File.Exists(_dbPath))
        {
            _logger.LogWarning("Database file was not found. Creating a new one.");
        }

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = GetSchemaSql();
            await command.ExecuteNonQueryAsync();

            IsAvailable = true;
            _logger.LogInformation("Database initialized successfully.");
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            _logger.LogError(ex, "Failed to initialize or create SQLite database.");
            throw;
        }
    }
    #endregion

    #region Schema Definition
    private static string GetSchemaSql()
    {
        return @"
            PRAGMA foreign_keys = ON;
            ------------------------------------------------
            -- 1. Tables and Triggers for holding LLM threads and messages
            -------------------------------------------------

            CREATE TABLE IF NOT EXISTS threads (
                id text PRIMARY KEY,
                title text NOT NULL,
                created_at TEXT NOT NULL,
                last_updated_at TEXT NOT NULL,
                system_summary TEXT
            );

            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                thread_id text NOT NULL,
                role TEXT NOT NULL,                
                timestamp TEXT NOT NULL,
                content TEXT NOT NULL,
                FOREIGN KEY(thread_id) REFERENCES threads(id) ON DELETE CASCADE
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
                content, 
                content=messages, 
                content_rowid=id
            );

            CREATE TRIGGER IF NOT EXISTS messages_ai AFTER INSERT ON messages BEGIN
                INSERT INTO messages_fts(rowid, content) VALUES (new.id, new.content);
            END;

            CREATE TRIGGER IF NOT EXISTS messages_ad AFTER DELETE ON messages BEGIN
                INSERT INTO messages_fts(messages_fts, rowid, content) VALUES('delete', old.id, old.content);
            END;

            -------------------------------
            -- END LLM threads and messages
            -------------------------------

            -------------------------------------------------------------------------------
            -- 2.  Tables and Triggers for holding Source Code Analysis and Reviews
            -- 2a. STRUCTURAL NODES (Files, Classes, Interfaces, Methods, Properties)
            -------------------------------------------------------------------------------
            CREATE TABLE IF NOT EXISTS code_symbols (
                id TEXT PRIMARY KEY,
                parent_id TEXT,               -- Self-reference (e.g., Method -> Class -> File)
                project_name TEXT NOT NULL,
                file_path TEXT NOT NULL,
                symbol_type TEXT NOT NULL,     -- 'File', 'Class', 'Interface', 'Method', 'Property'
                symbol_name TEXT NOT NULL,
                signature TEXT,                -- Full declaration signature
                start_line INTEGER NOT NULL,
                end_line INTEGER NOT NULL,
                content TEXT,                  -- Raw source text of the symbol
                FOREIGN KEY (parent_id) REFERENCES code_symbols(id) ON DELETE CASCADE
            );

            -------------------------------------------------------------------------------
            -- 2b. GRAPH EDGES (Relationships & Call Graphs)
            -------------------------------------------------------------------------------
            CREATE TABLE IF NOT EXISTS symbol_relationships (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_id TEXT NOT NULL,        -- Caller / Implementation / Derived
                target_id TEXT NOT NULL,        -- Callee / Interface / Base Class
                relation_type TEXT NOT NULL,    -- 'CALLS', 'IMPLEMENTS', 'INHERITS', 'CONTAINS'
                FOREIGN KEY (source_id) REFERENCES code_symbols(id) ON DELETE CASCADE,
                FOREIGN KEY (target_id) REFERENCES code_symbols(id) ON DELETE CASCADE
            );

            -------------------------------------------------------------------------------
            -- 2c. FULL-TEXT SEARCH (FTS5 Trigram Matching)
            -------------------------------------------------------------------------------
            CREATE VIRTUAL TABLE IF NOT EXISTS code_symbols_fts USING fts5(
                project_name,
                symbol_name,
                signature,
                content,
                content='code_symbols',
                tokenize='trigram'
            );

            -- Triggers to sync code_symbols with FTS index automatically
            DROP TRIGGER IF EXISTS code_symbols_ai;
            DROP TRIGGER IF EXISTS code_symbols_ad;
            DROP TRIGGER IF EXISTS code_symbols_au;

            CREATE TRIGGER code_symbols_ai AFTER INSERT ON code_symbols BEGIN
                INSERT INTO code_symbols_fts(rowid, project_name, symbol_name, signature, content) 
                VALUES (new.rowid, new.project_name, new.symbol_name, new.signature, new.content);
            END;

            CREATE TRIGGER code_symbols_ad AFTER DELETE ON code_symbols BEGIN
                INSERT INTO code_symbols_fts(code_symbols_fts, rowid, project_name, symbol_name, signature, content) 
                VALUES('delete', old.rowid, old.project_name, old.symbol_name, old.signature, old.content);
            END;

            CREATE TRIGGER code_symbols_au AFTER UPDATE ON code_symbols BEGIN
                INSERT INTO code_symbols_fts(code_symbols_fts, rowid, project_name, symbol_name, signature, content) 
                VALUES('delete', old.rowid, old.project_name, old.symbol_name, old.signature, old.content);
                INSERT INTO code_symbols_fts(rowid, project_name, symbol_name, signature, content) 
                VALUES (new.rowid, new.project_name, new.symbol_name, new.signature, new.content);
            END;
            
            ------------------------------------------------
            -- END Source Code Analysis and Reviews
            ------------------------------------------------
        ";
    }
    #endregion

    #region Thread & Message Operations
    public async Task<List<ChatThreadEntity>> GetAllThreadsAsync()
    {
        _logger.LogInformation("Fetching all chat threads from the database.");
        var threads = new List<ChatThreadEntity>();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT id, title, created_at, last_updated_at, system_summary FROM threads ORDER BY last_updated_at DESC;";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                threads.Add(new ChatThreadEntity
                {
                    Id = reader.GetString(0),
                    Title = reader.GetString(1),
                    CreatedAt = DateTime.Parse(reader.GetString(2)),
                    LastUpdatedAt = DateTime.Parse(reader.GetString(3)),
                    SystemSummary = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
                });
            }
        }
        catch(Exception ex)
        {
            _logger.LogError($"{ex.Message}, Failed to retrieve thread list");
        }
        return threads;
    }

    public async Task SaveThreadAsync(ChatThreadEntity thread)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
            INSERT INTO threads (id, title, created_at, last_updated_at, system_summary)
            VALUES ($id, $title, $created_at, $last_updated_at, $system_summary)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                last_updated_at = excluded.last_updated_at,
                system_summary = excluded.system_summary;
        ";
            command.Parameters.AddWithValue("$id", thread.Id);
            command.Parameters.AddWithValue("$title", thread.Title);
            command.Parameters.AddWithValue("$created_at", thread.CreatedAt.ToString("o"));
            command.Parameters.AddWithValue("$last_updated_at", DateTime.Now.ToString("o"));
            command.Parameters.AddWithValue("$system_summary", thread.SystemSummary ?? (object)DBNull.Value);

            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save thread");
        }
    }

    public async Task AddMessageAsync(string threadId, ChatMessageEntity message)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var transaction = connection.BeginTransaction();

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
            INSERT INTO messages (thread_id, role, timestamp, content)
            VALUES ($threadId, $role, $timestamp, $content);

            UPDATE threads SET last_updated_at = $timestamp WHERE id = $threadId;
            ";
            command.Parameters.AddWithValue("$threadId", message.ThreadId);
            command.Parameters.AddWithValue("$role", message.Role);
            command.Parameters.AddWithValue("$content", message.RichContentJson);
            command.Parameters.AddWithValue("$timestamp", message.Timestamp.ToString("o"));

            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        catch(Exception ex)
        {
            _logger.LogError(ex, "Failed to save message");
        }
    }

    public async Task<List<ChatMessageEntity>> GetMessagesForThreadAsync(string threadId)
    {

        var messages = new List<ChatMessageEntity>();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT role, content, timestamp FROM messages WHERE thread_id = $threadId ORDER BY id ASC;";
            command.Parameters.AddWithValue("$threadId", threadId);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                messages.Add(new ChatMessageEntity
                {
                    Role = reader.GetString(0),
                    RichContentJson = reader.GetString(1),
                    Timestamp = DateTime.Parse(reader.GetString(2))
                });
            }
        }
        catch (Exception ex) 
        {
            _logger.LogError(ex, "Failed to retrieve Messages for thread {threadId}", threadId);
        }
        return messages;
    }

    public async Task DeleteThreadAsync(string threadId)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM threads WHERE id = $id;";
            command.Parameters.AddWithValue("$id", threadId);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete thread {id}", threadId);
        }
    }

    public async Task UpdateThreadSummaryAsync(string threadId, string summary)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE threads 
                SET system_summary = $summary, last_updated_at = $updatedAt 
                WHERE id = $id;";

            command.Parameters.AddWithValue("$summary", summary);
            command.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("o"));
            command.Parameters.AddWithValue("$id", threadId);

            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update thread summary for thread {id}", threadId);
        }
    }
    #endregion

    #region Code Analysis and Review Persistence Methods
    public async Task SaveCodeSymbolsAsync(IEnumerable<CodeSymbolModel> symbols, string projectName)
    {
        if (symbols == null || !symbols.Any()) return;

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        try
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO code_symbols (id, parent_id, project_name, file_path, symbol_type, symbol_name, signature, start_line, end_line, content)
                VALUES ($id, $parentId, $projectName, $filePath, $symbolType, $symbolName, $signature, $startLine, $endLine, $content)
                ON CONFLICT(id) DO UPDATE SET
                    parent_id = excluded.parent_id,
                    project_name = excluded.project_name,
                    file_path = excluded.file_path,
                    symbol_type = excluded.symbol_type,
                    symbol_name = excluded.symbol_name,
                    signature = excluded.signature,
                    start_line = excluded.start_line,
                    end_line = excluded.end_line,
                    content = excluded.content;";

            var pId = command.Parameters.Add("$id", SqliteType.Text);
            var pParentId = command.Parameters.Add("$parentId", SqliteType.Text);
            var pProject = command.Parameters.Add("$projectName", SqliteType.Text);
            var pPath = command.Parameters.Add("$filePath", SqliteType.Text);
            var pType = command.Parameters.Add("$symbolType", SqliteType.Text);
            var pName = command.Parameters.Add("$symbolName", SqliteType.Text);
            var pSig = command.Parameters.Add("$signature", SqliteType.Text);
            var pStart = command.Parameters.Add("$startLine", SqliteType.Integer);
            var pEnd = command.Parameters.Add("$endLine", SqliteType.Integer);
            var pContent = command.Parameters.Add("$content", SqliteType.Text);

            foreach (var symbol in symbols)
            {
                pId.Value = symbol.Id;
                pParentId.Value = (object?)symbol.ParentId ?? DBNull.Value;
                pProject.Value = projectName;
                pPath.Value = symbol.FilePath;
                pType.Value = symbol.SymbolType;
                pName.Value = symbol.SymbolName;
                pSig.Value = (object?)symbol.Signature ?? DBNull.Value;
                pStart.Value = symbol.StartLine;
                pEnd.Value = symbol.EndLine;
                pContent.Value = (object?)symbol.Content ?? DBNull.Value;

                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            _logger.LogInformation("Saved {Count} symbols for project {Project}", symbols.Count(), projectName);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to save code symbols for project {Project}", projectName);
            throw;
        }
    }

    public async Task SaveCodeRelationshipsAsync(IEnumerable<CodeEdgeModel> edges)
    {
        if (edges == null || !edges.Any()) return;

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        try
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;

            // Insert edge ONLY if both source and target IDs exist in code_symbols
            command.CommandText = @"
            INSERT INTO symbol_relationships (source_id, target_id, relation_type)
            SELECT $sourceId, $targetId, $relationType
            WHERE EXISTS (SELECT 1 FROM code_symbols WHERE id = $sourceId)
              AND EXISTS (SELECT 1 FROM code_symbols WHERE id = $targetId);";

            var pSource = command.Parameters.Add("$sourceId", SqliteType.Text);
            var pTarget = command.Parameters.Add("$targetId", SqliteType.Text);
            var pRelation = command.Parameters.Add("$relationType", SqliteType.Text);

            foreach (var edge in edges)
            {
                pSource.Value = edge.SourceId;
                pTarget.Value = edge.TargetId;
                pRelation.Value = edge.RelationType;

                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            _logger.LogInformation("Saved relationships for valid local symbols.");
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to save code relationships.");
            throw;
        }
    }

    public async Task ClearProjectDataAsync(string projectName)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM code_symbols WHERE project_name = $projectName;";
        command.Parameters.AddWithValue("$projectName", projectName);

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Cleared existing index data for project {Project}", projectName);
    }

    public async Task<List<string>> GetIndexedProjectNamesAsync()
    {
        var projects = new List<string>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
        SELECT DISTINCT project_name 
        FROM code_symbols 
        WHERE project_name IS NOT NULL AND project_name != ''
        ORDER BY project_name;";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            projects.Add(reader.GetString(0));
        }

        return projects;
    }
    #endregion

    #region Code Analysis and Review Retrieval Methods
    public async Task<List<CodeSymbolModel>> GetProjectOverviewAsync(string projectName)
    {
        var symbols = new List<CodeSymbolModel>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        // Fetch non-method structural nodes (Files, Classes, Interfaces, Configs) to build macro summary
        command.CommandText = @"
        SELECT id, parent_id, file_path, symbol_type, symbol_name, signature, start_line, end_line
        FROM code_symbols 
        WHERE project_name = $projectName AND symbol_type != 'Method'
        ORDER BY symbol_type, symbol_name;";
        command.Parameters.AddWithValue("$projectName", projectName);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            symbols.Add(new CodeSymbolModel
            {
                Id = reader.GetString(0),
                ParentId = reader.IsDBNull(1) ? null : reader.GetString(1),
                FilePath = reader.GetString(2),
                SymbolType = reader.GetString(3),
                SymbolName = reader.GetString(4),
                Signature = reader.IsDBNull(5) ? null : reader.GetString(5),
                StartLine = reader.GetInt32(6),
                EndLine = reader.GetInt32(7)
            });
        }

        return symbols;
    }

    public async Task<List<CodeSymbolModel>> SearchSymbolsFtsAsync(string projectName, string queryTerm, int limit = 20)
    {
        var results = new List<CodeSymbolModel>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
        SELECT cs.id, cs.parent_id, cs.file_path, cs.symbol_type, cs.symbol_name, cs.signature, cs.start_line, cs.end_line, cs.content
        FROM code_symbols_fts fts
        JOIN code_symbols cs ON fts.rowid = cs.rowid
        WHERE fts.project_name = $projectName AND code_symbols_fts MATCH $query
        LIMIT $limit;";

        command.Parameters.AddWithValue("$projectName", projectName);
        // Sanitize and format for FTS5 trigram match
        command.Parameters.AddWithValue("$query", $"\"{queryTerm}\"");
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new CodeSymbolModel
            {
                Id = reader.GetString(0),
                ParentId = reader.IsDBNull(1) ? null : reader.GetString(1),
                FilePath = reader.GetString(2),
                SymbolType = reader.GetString(3),
                SymbolName = reader.GetString(4),
                Signature = reader.IsDBNull(5) ? null : reader.GetString(5),
                StartLine = reader.GetInt32(6),
                EndLine = reader.GetInt32(7),
                Content = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }

        return results;
    }

    public async Task<CodeSymbolModel?> GetSymbolByIdOrNameAsync(string projectName, string symbolIdentifier)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
        SELECT id, parent_id, file_path, symbol_type, symbol_name, signature, start_line, end_line, content
        FROM code_symbols
        WHERE project_name = $projectName AND (id = $idOrName OR symbol_name = $idOrName)
        LIMIT 1;";

        command.Parameters.AddWithValue("$projectName", projectName);
        command.Parameters.AddWithValue("$idOrName", symbolIdentifier);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new CodeSymbolModel
            {
                Id = reader.GetString(0),
                ParentId = reader.IsDBNull(1) ? null : reader.GetString(1),
                FilePath = reader.GetString(2),
                SymbolType = reader.GetString(3),
                SymbolName = reader.GetString(4),
                Signature = reader.IsDBNull(5) ? null : reader.GetString(5),
                StartLine = reader.GetInt32(6),
                EndLine = reader.GetInt32(7),
                Content = reader.IsDBNull(8) ? null : reader.GetString(8)
            };
        }

        return null;
    }

    public async Task<List<CodeEdgeModel>> GetRelationshipsForSymbolAsync(string symbolIdOrName)
    {
        var edges = new List<CodeEdgeModel>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        // Retrieve incoming or outgoing relationships (CALLS, IMPLEMENTS, INHERITS, USES_CODEBEHIND)
        command.CommandText = @"
        SELECT source_id, target_id, relation_type 
        FROM symbol_relationships 
        WHERE source_id = $id OR target_id = $id;";

        command.Parameters.AddWithValue("$id", symbolIdOrName);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            edges.Add(new CodeEdgeModel
            {
                SourceId = reader.GetString(0),
                TargetId = reader.GetString(1),
                RelationType = reader.GetString(2)
            });
        }

        return edges;
    }
    #endregion
}