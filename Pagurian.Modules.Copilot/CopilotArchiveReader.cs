using Microsoft.Data.Sqlite;

namespace Pagurian.Modules.Copilot;

// These are observed internal App tables, not a public integration contract.
// One transaction sees workspace mappings and archive flags in the same WAL view.
internal sealed class CopilotArchiveReader(string database, Action<string>? diagnostic = null) : ICopilotArchiveReader
{
    public IReadOnlyDictionary<string, CopilotArchiveState> Read(IReadOnlyCollection<string> ids,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var result = new Dictionary<string, CopilotArchiveState>(StringComparer.Ordinal);
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database, Mode = SqliteOpenMode.ReadOnly,
                Pooling = false, DefaultTimeout = 1,
            }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            bool sessions = HasColumns(connection, transaction, "sessions", "id", "session_type", "archived_at");
            bool workspaces = HasColumns(connection, transaction, "workspaces", "id", "session_id", "archived_at");
            bool aliases = HasColumns(connection, transaction, "workspace_session_aliases", "session_id", "workspace_id");
            if (!sessions || !workspaces || !aliases)
            {
                diagnostic?.Invoke("app-database-schema-unavailable");
                return result;
            }
            foreach (var id in ids)
            {
                cancellation.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT id, archived_at FROM workspaces WHERE session_id = $id
                    UNION
                    SELECT w.id, w.archived_at FROM workspace_session_aliases a
                    JOIN workspaces w ON w.id = a.workspace_id WHERE a.session_id = $id;
                    """;
                command.Parameters.AddWithValue("$id", id);
                string? workspace = null;
                CopilotArchiveState state = CopilotArchiveState.Unknown;
                bool conflict = false;
                using (var rows = command.ExecuteReader())
                {
                    while (rows.Read())
                    {
                        if (workspace is not null) conflict = true;
                        workspace = rows.GetString(0);
                        state = Archive(rows, 1);
                    }
                }
                // A dangling alias is incomplete evidence, even alongside a valid direct mapping.
                command.CommandText = """
                    SELECT COUNT(*) FROM workspace_session_aliases a
                    LEFT JOIN workspaces w ON w.id = a.workspace_id
                    WHERE a.session_id = $id AND w.id IS NULL;
                    """;
                if (Convert.ToInt64(command.ExecuteScalar()) != 0) conflict = true;
                if (conflict) state = CopilotArchiveState.Unknown;
                else if (workspace is null)
                {
                    command.CommandText = "SELECT session_type, archived_at FROM sessions WHERE id = $id;";
                    using var row = command.ExecuteReader();
                    // Only explicitly standalone chats may use the session flag.
                    if (row.Read() && !row.IsDBNull(0) && row.GetString(0) == "chat")
                        state = Archive(row, 1);
                    if (row.Read()) state = CopilotArchiveState.Unknown;
                }
                result[id] = state;
            }
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or InvalidCastException
            or DllNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            // Do not publish a partial transaction on a failed read.
            result.Clear();
            diagnostic?.Invoke("app-database-unavailable");
        }
        return result;
    }

    private static CopilotArchiveState Archive(SqliteDataReader row, int column)
    {
        if (row.IsDBNull(column)) return CopilotArchiveState.Live;
        var value = row.GetString(column);
        return value.Length >= 19 && value[4] == '-' && value[7] == '-' &&
            DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out _)
            ? CopilotArchiveState.Archived : CopilotArchiveState.Unknown;
    }

    private static bool HasColumns(SqliteConnection connection, SqliteTransaction transaction,
        string table, params string[] required)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table});";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return required.All(columns.Contains);
    }
}
