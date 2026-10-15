namespace Pillion.LocalStore.Conversations;

/// <summary>
/// The schema of <c>conversations.db</c> (design section 10, Conversations, journal and audit trail). Times are Unix
/// milliseconds in UTC. Agent ledgers, changesets and the apply journal arrive with the agent runtime as later migrations.
/// </summary>
public static class ConversationsSchema
{
    /// <summary>The database kind recorded in <c>meta</c>.</summary>
    public const string Name = "conversations";

    /// <summary>Version 1: conversations, messages, pins and the Outbound log.</summary>
    internal const string Version1 = """
        CREATE TABLE conversations (
            id          TEXT    NOT NULL PRIMARY KEY,
            title       TEXT    NOT NULL DEFAULT '',
            created     INTEGER NOT NULL,
            updated     INTEGER NOT NULL,
            model       TEXT    NULL
        ) STRICT;
        CREATE INDEX ix_conversations_updated ON conversations (updated DESC);

        CREATE TABLE messages (
            id              INTEGER NOT NULL PRIMARY KEY,
            conversation_id TEXT    NOT NULL REFERENCES conversations (id) ON DELETE CASCADE,
            role            TEXT    NOT NULL,
            content         TEXT    NOT NULL,
            model           TEXT    NULL,
            provider        TEXT    NULL,
            tokens_in       INTEGER NULL,
            tokens_out      INTEGER NULL,
            cost            REAL    NULL,
            created         INTEGER NOT NULL
        ) STRICT;
        CREATE INDEX ix_messages_conversation ON messages (conversation_id, id);

        CREATE TABLE pins (
            conversation_id TEXT    NOT NULL REFERENCES conversations (id) ON DELETE CASCADE,
            kind            TEXT    NOT NULL,
            target          TEXT    NOT NULL,
            token_count     INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (conversation_id, kind, target)
        ) STRICT, WITHOUT ROWID;

        CREATE TABLE outbound_log (
            id                    INTEGER NOT NULL PRIMARY KEY,
            time                  INTEGER NOT NULL,
            request_id            TEXT    NOT NULL,
            provider              TEXT    NOT NULL,
            model                 TEXT    NOT NULL,
            task_class            TEXT    NOT NULL,
            locality              TEXT    NOT NULL CHECK (locality IN ('local', 'cloud')),
            prompt_tokens         INTEGER NOT NULL,
            prompt_tokens_counted INTEGER NOT NULL DEFAULT 0,
            completion_tokens     INTEGER NOT NULL,
            cache_hit             INTEGER NOT NULL DEFAULT 0,
            first_token_ms        INTEGER NULL,
            duration_ms           INTEGER NOT NULL,
            outcome               TEXT    NOT NULL,
            error_code            TEXT    NULL,
            redactions            INTEGER NOT NULL DEFAULT 0,
            context_items         INTEGER NOT NULL DEFAULT 0,
            context_trimmed       INTEGER NOT NULL DEFAULT 0,
            spans                 TEXT    NULL
        ) STRICT;
        CREATE INDEX ix_outbound_log_time ON outbound_log (time);
        """;

    /// <summary>
    /// Version 2 (work item 2.2): a reply's citations (JSON) and whether it was stopped, and the pins' order and short names.
    /// All additive, so older builds can keep reading the file (reader version stays 1).
    /// </summary>
    internal const string Version2 = """
        ALTER TABLE messages ADD COLUMN citations TEXT NULL;
        ALTER TABLE messages ADD COLUMN cancelled INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE pins ADD COLUMN ordinal INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE pins ADD COLUMN display_name TEXT NOT NULL DEFAULT '';
        """;

    /// <summary>Gets the schema: every migration of <c>conversations.db</c>, in order.</summary>
    public static StoreSchema Schema { get; } = new(
        Name,
        [
            StoreMigration.Sql(1, 1, "conversations, messages, pins and the Outbound log", Version1),
            StoreMigration.Sql(2, 1, "citations and the stopped marker on messages, order and names on pins", Version2),
        ]);
}
