using System.Globalization;
using Microsoft.Data.Sqlite;
using Pillion.Contracts;

namespace Pillion.LocalStore.Conversations;

/// <summary>An <see cref="IConversationRepository"/> over an open <c>conversations.db</c>.</summary>
public sealed class ConversationRepository : IConversationRepository
{
    private const string OutboundColumns =
        "id, time, request_id, provider, model, task_class, locality, prompt_tokens, prompt_tokens_counted, completion_tokens, "
        + "cache_hit, first_token_ms, duration_ms, outcome, error_code, redactions, context_items, context_trimmed, spans";

    private const string MessageColumns =
        "id, conversation_id, role, content, created, model, provider, tokens_in, tokens_out, cost, citations, cancelled";

    private readonly EncryptedDatabase _database;

    /// <summary>Initializes a new instance of the <see cref="ConversationRepository"/> class.</summary>
    /// <param name="database">The open database, created with <see cref="ConversationsSchema.Schema"/>. The caller owns it.</param>
    public ConversationRepository(EncryptedDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc/>
    public Task SaveConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrEmpty(conversation.Id);
        return _database.WriteAsync(
            (connection, transaction, token) => ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO conversations (id, title, created, updated, model) VALUES ($id, $title, $created, $updated, $model)
                ON CONFLICT (id) DO UPDATE SET title = excluded.title, updated = excluded.updated, model = excluded.model
                """,
                token,
                ("$id", conversation.Id),
                ("$title", conversation.Title ?? string.Empty),
                ("$created", ToUnixMs(conversation.Created)),
                ("$updated", ToUnixMs(conversation.Updated)),
                ("$model", conversation.Model)),
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> TryCreateConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrEmpty(conversation.Id);
        return _database.WriteAsync(
            async (connection, transaction, token) =>
                await ExecuteAsync(
                    connection,
                    transaction,
                    "INSERT INTO conversations (id, title, created, updated, model) VALUES ($id, $title, $created, $updated, $model) ON CONFLICT (id) DO NOTHING",
                    token,
                    ("$id", conversation.Id),
                    ("$title", conversation.Title ?? string.Empty),
                    ("$created", ToUnixMs(conversation.Created)),
                    ("$updated", ToUnixMs(conversation.Updated)),
                    ("$model", conversation.Model)).ConfigureAwait(false) > 0,
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> RenameConversationAsync(string id, string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(title);
        return _database.WriteAsync(
            async (connection, transaction, token) =>
                await ExecuteAsync(connection, transaction, "UPDATE conversations SET title = $title WHERE id = $id", token, ("$id", id), ("$title", title)).ConfigureAwait(false) > 0,
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> SetConversationModelAsync(string id, string? model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _database.WriteAsync(
            async (connection, transaction, token) =>
                await ExecuteAsync(connection, transaction, "UPDATE conversations SET model = $model WHERE id = $id", token, ("$id", id), ("$model", model)).ConfigureAwait(false) > 0,
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ConversationListItem>> ListConversationPageAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        return QueryAsync(
            """
            SELECT c.id, c.title, c.created, c.updated, c.model, (SELECT count(*) FROM messages m WHERE m.conversation_id = c.id)
            FROM conversations c ORDER BY c.updated DESC, c.id LIMIT $limit OFFSET $offset
            """,
            reader => new ConversationListItem(ReadConversation(reader), reader.GetInt32(5)),
            cancellationToken,
            ("$limit", limit),
            ("$offset", offset));
    }

    /// <inheritdoc/>
    public async Task<ConversationRecord?> GetConversationAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        var rows = await QueryAsync(
            "SELECT id, title, created, updated, model FROM conversations WHERE id = $id",
            ReadConversation,
            cancellationToken,
            ("$id", id)).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        return QueryAsync(
            "SELECT id, title, created, updated, model FROM conversations ORDER BY updated DESC, id LIMIT $limit",
            ReadConversation,
            cancellationToken,
            ("$limit", limit));
    }

    /// <inheritdoc/>
    public Task<bool> DeleteConversationAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _database.WriteAsync(
            async (connection, transaction, token) =>
                await ExecuteAsync(connection, transaction, "DELETE FROM conversations WHERE id = $id", token, ("$id", id)).ConfigureAwait(false) > 0,
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<MessageRecord> AddMessageAsync(NewMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.ConversationId);
        ArgumentException.ThrowIfNullOrEmpty(message.Role);
        ArgumentNullException.ThrowIfNull(message.Content);

        return _database.WriteAsync(
            async (connection, transaction, token) =>
            {
                var created = ToUnixMs(message.Created);
                var updated = await ExecuteAsync(
                    connection,
                    transaction,
                    "UPDATE conversations SET updated = max(updated, $created) WHERE id = $id",
                    token,
                    ("$id", message.ConversationId),
                    ("$created", created)).ConfigureAwait(false);
                if (updated == 0)
                {
                    throw new KeyNotFoundException($"There is no conversation '{message.ConversationId}'.");
                }

                using var command = Command(
                    connection,
                    transaction,
                    """
                    INSERT INTO messages (conversation_id, role, content, model, provider, tokens_in, tokens_out, cost, created, citations, cancelled)
                    VALUES ($conversation, $role, $content, $model, $provider, $in, $out, $cost, $created, $citations, $cancelled)
                    RETURNING id
                    """,
                    ("$conversation", message.ConversationId),
                    ("$role", message.Role),
                    ("$content", message.Content),
                    ("$model", message.Model),
                    ("$provider", message.Provider),
                    ("$in", message.TokensIn),
                    ("$out", message.TokensOut),
                    ("$cost", message.Cost),
                    ("$created", created),
                    ("$citations", message.Citations),
                    ("$cancelled", message.Cancelled));
                var id = Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);

                return new MessageRecord(
                    id,
                    message.ConversationId,
                    message.Role,
                    message.Content,
                    FromUnixMs(created),
                    message.Model,
                    message.Provider,
                    message.TokensIn,
                    message.TokensOut,
                    message.Cost,
                    message.Citations,
                    message.Cancelled);
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MessageRecord>> GetMessagesAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        return QueryAsync(
            $"SELECT {MessageColumns} FROM messages WHERE conversation_id = $id ORDER BY id",
            ReadMessage,
            cancellationToken,
            ("$id", conversationId));
    }

    /// <inheritdoc/>
    public async Task<MessageRecord?> GetLastMessageAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        var rows = await QueryAsync(
            $"SELECT {MessageColumns} FROM messages WHERE conversation_id = $id ORDER BY id DESC LIMIT 1",
            ReadMessage,
            cancellationToken,
            ("$id", conversationId)).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc/>
    public Task ReplacePinsAsync(string conversationId, IReadOnlyList<PinRecord> pins, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(pins);

        return _database.WriteAsync(
            async (connection, transaction, token) =>
            {
                var exists = await ScalarAsync(connection, transaction, "SELECT count(*) FROM conversations WHERE id = $id", token, ("$id", conversationId)).ConfigureAwait(false);
                if (Convert.ToInt64(exists, CultureInfo.InvariantCulture) == 0)
                {
                    throw new KeyNotFoundException($"There is no conversation '{conversationId}'.");
                }

                await ExecuteAsync(connection, transaction, "DELETE FROM pins WHERE conversation_id = $id", token, ("$id", conversationId)).ConfigureAwait(false);
                var ordinal = 0;
                foreach (var pin in pins)
                {
                    ArgumentNullException.ThrowIfNull(pin);

                    // A repeated kind and target keeps the last one's values, in the position of the first.
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO pins (conversation_id, kind, target, token_count, display_name, ordinal) VALUES ($id, $kind, $target, $tokens, $name, $ordinal)
                        ON CONFLICT (conversation_id, kind, target) DO UPDATE SET token_count = excluded.token_count, display_name = excluded.display_name
                        """,
                        token,
                        ("$id", conversationId),
                        ("$kind", pin.Kind),
                        ("$target", pin.Target),
                        ("$tokens", pin.TokenCount),
                        ("$name", pin.DisplayName ?? string.Empty),
                        ("$ordinal", ordinal++)).ConfigureAwait(false);
                }
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PinRecord>> GetPinsAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        return QueryAsync(
            "SELECT kind, target, token_count, display_name FROM pins WHERE conversation_id = $id ORDER BY ordinal, kind, target",
            reader => new PinRecord(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)),
            cancellationToken,
            ("$id", conversationId));
    }

    /// <inheritdoc/>
    public Task<long> AppendOutboundAsync(OutboundLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _database.WriteAsync(
            async (connection, transaction, token) =>
            {
                using var command = Command(
                    connection,
                    transaction,
                    """
                    INSERT INTO outbound_log (time, request_id, provider, model, task_class, locality, prompt_tokens, prompt_tokens_counted,
                        completion_tokens, cache_hit, first_token_ms, duration_ms, outcome, error_code, redactions, context_items, context_trimmed, spans)
                    VALUES ($time, $request, $provider, $model, $task, $locality, $prompt, $counted,
                        $completion, $cache, $first, $duration, $outcome, $error, $redactions, $items, $trimmed, $spans)
                    RETURNING id
                    """,
                    ("$time", ToUnixMs(entry.Timestamp)),
                    ("$request", entry.RequestId ?? string.Empty),
                    ("$provider", entry.ProviderId ?? string.Empty),
                    ("$model", entry.ModelId ?? string.Empty),
                    ("$task", entry.TaskClass.ToString()),
                    ("$locality", entry.IsLocal ? "local" : "cloud"),
                    ("$prompt", entry.PromptTokens),
                    ("$counted", entry.PromptTokensCounted),
                    ("$completion", entry.CompletionTokens),
                    ("$cache", entry.CacheHit),
                    ("$first", entry.TimeToFirstToken is { } first ? (long)first.TotalMilliseconds : null),
                    ("$duration", (long)entry.Duration.TotalMilliseconds),
                    ("$outcome", entry.Outcome ?? string.Empty),
                    ("$error", entry.ErrorCode?.ToString()),
                    ("$redactions", entry.RedactionCount),
                    ("$items", entry.ContextItems),
                    ("$trimmed", entry.ContextTrimmed),
                    ("$spans", entry.Spans));
                return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<OutboundLogEntry>> GetRecentOutboundAsync(int limit = 500, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        return QueryAsync(
            $"SELECT {OutboundColumns} FROM (SELECT {OutboundColumns} FROM outbound_log ORDER BY id DESC LIMIT $limit) ORDER BY id",
            ReadOutbound,
            cancellationToken,
            ("$limit", limit));
    }

    /// <inheritdoc/>
    public Task<int> DeleteConversationsUpdatedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction, token) => ExecuteAsync(connection, transaction, "DELETE FROM conversations WHERE updated < $cutoff", token, ("$cutoff", ToUnixMs(cutoff))),
            cancellationToken);

    /// <inheritdoc/>
    public Task<int> DeleteOutboundBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction, token) => ExecuteAsync(connection, transaction, "DELETE FROM outbound_log WHERE time < $cutoff", token, ("$cutoff", ToUnixMs(cutoff))),
            cancellationToken);

    /// <inheritdoc/>
    public async Task<DeleteAllResult> DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        var counts = await _database.WriteAsync(
            async (connection, transaction, token) =>
            {
                // Pins and messages first, so the counts are not hidden by the cascade.
                var pins = await ExecuteAsync(connection, transaction, "DELETE FROM pins", token).ConfigureAwait(false);
                var messages = await ExecuteAsync(connection, transaction, "DELETE FROM messages", token).ConfigureAwait(false);
                var conversations = await ExecuteAsync(connection, transaction, "DELETE FROM conversations", token).ConfigureAwait(false);
                var outbound = await ExecuteAsync(connection, transaction, "DELETE FROM outbound_log", token).ConfigureAwait(false);
                return (conversations, messages, pins, outbound);
            },
            cancellationToken).ConfigureAwait(false);

        // The delete is committed; compacting and removing old backups must finish even if the caller gives up now.
        await _database.VacuumAsync(CancellationToken.None).ConfigureAwait(false);
        var backups = _database.DeleteBackups();
        return new DeleteAllResult(counts.conversations, counts.messages, counts.pins, counts.outbound, backups);
    }

    private static ConversationRecord ReadConversation(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        FromUnixMs(reader.GetInt64(2)),
        FromUnixMs(reader.GetInt64(3)),
        NullableString(reader, 4));

    private static MessageRecord ReadMessage(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        FromUnixMs(reader.GetInt64(4)),
        NullableString(reader, 5),
        NullableString(reader, 6),
        reader.IsDBNull(7) ? null : reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetInt32(8),
        reader.IsDBNull(9) ? null : reader.GetDouble(9),
        NullableString(reader, 10),
        reader.GetBoolean(11));

    private static OutboundLogEntry ReadOutbound(SqliteDataReader reader) => new(
        FromUnixMs(reader.GetInt64(1)),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        Enum.TryParse<TaskClass>(reader.GetString(5), out var taskClass) ? taskClass : default,
        string.Equals(reader.GetString(6), "local", StringComparison.Ordinal),
        reader.GetInt32(7),
        reader.GetInt32(9),
        reader.GetInt32(15),
        TimeSpan.FromMilliseconds(reader.GetInt64(12)),
        reader.GetString(13),
        reader.IsDBNull(14) ? null : Enum.TryParse<PillionErrorCode>(reader.GetString(14), out var code) ? code : null,
        reader.GetBoolean(8),
        reader.GetBoolean(10),
        reader.IsDBNull(11) ? null : TimeSpan.FromMilliseconds(reader.GetInt64(11)),
        reader.GetInt32(16),
        reader.GetInt32(17),
        NullableString(reader, 18))
    {
        Id = reader.GetInt64(0),
    };

    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long ToUnixMs(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromUnixMs(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<IReadOnlyList<T>> QueryAsync<T>(string sql, Func<SqliteDataReader, T> read, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters) =>
        _database.ReadAsync<IReadOnlyList<T>>(
            async (connection, token) =>
            {
                using var command = Command(connection, null, sql, parameters);
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var rows = new List<T>();
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    rows.Add(read(reader));
                }

                return rows;
            },
            cancellationToken);
}
