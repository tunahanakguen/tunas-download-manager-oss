using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TunasDM.Core.Management;

/// <summary>Download-Liste und Einstellungen in einer SQLite-Datei.</summary>
public sealed class DownloadDatabase
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _connectionString;
    private readonly object _lock = new();

    public DownloadDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS downloads (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                url TEXT NOT NULL,
                file_name TEXT,
                directory TEXT NOT NULL,
                file_path TEXT,
                category TEXT NOT NULL,
                status INTEGER NOT NULL,
                total_size INTEGER NOT NULL,
                downloaded INTEGER NOT NULL,
                supports_ranges INTEGER NOT NULL,
                error TEXT,
                added_at TEXT NOT NULL,
                completed_at TEXT,
                referrer TEXT,
                cookies TEXT,
                user_agent TEXT,
                description TEXT
            );
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
        Migrate();
    }

    /// <summary>Spalten nachrüsten, die spätere Versionen brauchen (bestehende Listen bleiben erhalten).</summary>
    private void Migrate()
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(downloads)";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(1));
        }
        if (!columns.Contains("kind"))
            Execute("ALTER TABLE downloads ADD COLUMN kind INTEGER NOT NULL DEFAULT 0");
        if (!columns.Contains("media_format"))
            Execute("ALTER TABLE downloads ADD COLUMN media_format TEXT");
        if (!columns.Contains("queue_id"))
            Execute($"ALTER TABLE downloads ADD COLUMN queue_id INTEGER NOT NULL DEFAULT {DownloadQueue.MainId}");
        if (!columns.Contains("etag"))
            Execute("ALTER TABLE downloads ADD COLUMN etag TEXT");
        if (!columns.Contains("last_modified"))
            Execute("ALTER TABLE downloads ADD COLUMN last_modified TEXT");
        if (!columns.Contains("replace_existing"))
            Execute("ALTER TABLE downloads ADD COLUMN replace_existing INTEGER NOT NULL DEFAULT 0");
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TUNAS Download Manager", "tunasdm.db");

    public List<DownloadItem> LoadAll()
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM downloads ORDER BY id";
            using var reader = command.ExecuteReader();
            var items = new List<DownloadItem>();
            while (reader.Read())
            {
                items.Add(new DownloadItem
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    Url = Secrets.UnprotectUrl(reader.GetString(reader.GetOrdinal("url"))),
                    FileName = Text(reader, "file_name"),
                    Directory = reader.GetString(reader.GetOrdinal("directory")),
                    FilePath = Text(reader, "file_path"),
                    Category = reader.GetString(reader.GetOrdinal("category")),
                    Status = (DownloadStatus)reader.GetInt32(reader.GetOrdinal("status")),
                    TotalSize = reader.GetInt64(reader.GetOrdinal("total_size")),
                    Downloaded = reader.GetInt64(reader.GetOrdinal("downloaded")),
                    SupportsRanges = reader.GetInt64(reader.GetOrdinal("supports_ranges")) != 0,
                    Error = Text(reader, "error"),
                    AddedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("added_at")), CultureInfo.InvariantCulture),
                    CompletedAt = Text(reader, "completed_at") is { } completed
                        ? DateTimeOffset.Parse(completed, CultureInfo.InvariantCulture) : null,
                    Referrer = Text(reader, "referrer"),
                    Cookies = Secrets.Unprotect(Text(reader, "cookies")),
                    UserAgent = Text(reader, "user_agent"),
                    Description = Text(reader, "description"),
                    Kind = (DownloadKind)reader.GetInt32(reader.GetOrdinal("kind")),
                    MediaFormat = Text(reader, "media_format"),
                    QueueId = reader.GetInt64(reader.GetOrdinal("queue_id")),
                    ETag = Text(reader, "etag"),
                    LastModified = Text(reader, "last_modified") is { } modified
                        ? DateTimeOffset.Parse(modified, CultureInfo.InvariantCulture) : null,
                    ReplaceOnComplete = reader.GetInt64(reader.GetOrdinal("replace_existing")) != 0,
                });
            }
            return items;
        }
    }

    public void Insert(DownloadItem item)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO downloads (url, file_name, directory, file_path, category, status, total_size, downloaded,
                    supports_ranges, error, added_at, completed_at, referrer, cookies, user_agent, description, kind, media_format, queue_id, etag, last_modified, replace_existing)
                VALUES ($url, $file_name, $directory, $file_path, $category, $status, $total_size, $downloaded,
                    $supports_ranges, $error, $added_at, $completed_at, $referrer, $cookies, $user_agent, $description, $kind, $media_format, $queue_id, $etag, $last_modified, $replace_existing);
                SELECT last_insert_rowid();
                """;
            Bind(command, item);
            item.Id = (long)command.ExecuteScalar()!;
        }
    }

    public void Update(DownloadItem item)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE downloads SET url = $url, file_name = $file_name, directory = $directory, file_path = $file_path,
                    category = $category, status = $status, total_size = $total_size, downloaded = $downloaded,
                    supports_ranges = $supports_ranges, error = $error, added_at = $added_at, completed_at = $completed_at,
                    referrer = $referrer, cookies = $cookies, user_agent = $user_agent, description = $description,
                    kind = $kind, media_format = $media_format, queue_id = $queue_id,
                    etag = $etag, last_modified = $last_modified, replace_existing = $replace_existing
                WHERE id = $id
                """;
            Bind(command, item);
            command.Parameters.AddWithValue("$id", item.Id);
            command.ExecuteNonQuery();
        }
    }

    public void Delete(long id)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM downloads WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public AppSettings LoadSettings()
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = 'app'";
            if (command.ExecuteScalar() is string json)
            {
                try
                {
                    return JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
                }
                catch (JsonException)
                {
                    // Unlesbare Einstellungen: mit Standardwerten weiter.
                }
            }
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO settings (key, value) VALUES ('app', $value) ON CONFLICT(key) DO UPDATE SET value = $value";
            command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(settings, Json));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Warteschlangen mit Zeitplan (JSON in der Tabelle settings); leer, wenn noch keine gespeichert sind.</summary>
    public List<DownloadQueue> LoadQueues()
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = 'queues'";
            if (command.ExecuteScalar() is string json)
            {
                try
                {
                    return JsonSerializer.Deserialize<List<DownloadQueue>>(json, Json) ?? new();
                }
                catch (JsonException)
                {
                    // Unlesbar: mit der Hauptwarteschlange weiter.
                }
            }
            return new();
        }
    }

    public void SaveQueues(IEnumerable<DownloadQueue> queues)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO settings (key, value) VALUES ('queues', $value) ON CONFLICT(key) DO UPDATE SET value = $value";
            command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(queues.ToList(), Json));
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand command, DownloadItem item)
    {
        void Add(string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        Add("$url", Secrets.ProtectUrl(item.Url));
        Add("$file_name", item.FileName);
        Add("$directory", item.Directory);
        Add("$file_path", item.FilePath);
        Add("$category", item.Category);
        Add("$status", (int)item.Status);
        Add("$total_size", item.TotalSize);
        Add("$downloaded", item.Downloaded);
        Add("$supports_ranges", item.SupportsRanges ? 1 : 0);
        Add("$error", item.Error);
        Add("$added_at", item.AddedAt.ToString("O", CultureInfo.InvariantCulture));
        Add("$completed_at", item.CompletedAt?.ToString("O", CultureInfo.InvariantCulture));
        Add("$referrer", item.Referrer);
        Add("$cookies", Secrets.Protect(item.Cookies));
        Add("$user_agent", item.UserAgent);
        Add("$description", item.Description);
        Add("$kind", (int)item.Kind);
        Add("$media_format", item.MediaFormat);
        Add("$queue_id", item.QueueId);
        Add("$etag", item.ETag);
        Add("$last_modified", item.LastModified?.ToString("O", CultureInfo.InvariantCulture));
        Add("$replace_existing", item.ReplaceOnComplete ? 1 : 0);
    }

    private static string? Text(SqliteDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
