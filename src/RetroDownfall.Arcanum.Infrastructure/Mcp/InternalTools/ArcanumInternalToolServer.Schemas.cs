using System.Buffers;
using System.Text.Json;


namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{

    private static JsonElement BuildReadFileChunkSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "relativePath",
                "Path to the file relative to the workspace root (not an absolute path).");

            WriteIntegerProperty(w, "startLine", "1-based inclusive starting line number.");

            WriteIntegerProperty(w, "endLine", "1-based inclusive ending line number.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("relativePath");

            w.WriteStringValue("startLine");

            w.WriteStringValue("endLine");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildReplaceTextBlockSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "relativePath",
                "Path to the file relative to the workspace root (not an absolute path).");

            WriteStringProperty(w, "exactSearchText", "Verbatim block of text to locate in the file, including whitespace and newlines.");

            WriteStringProperty(w, "replacementText", "Replacement block of text. May be empty to delete the matched block.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("relativePath");

            w.WriteStringValue("exactSearchText");

            w.WriteStringValue("replacementText");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildWriteFileSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "relativePath",
                "Path to the file relative to the workspace root (not an absolute path).");

            WriteStringProperty(w, "content", "Full file contents. Replaces the entire file if it already exists.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("relativePath");

            w.WriteStringValue("content");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildListDirectorySchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "relativePath",
                "Directory path relative to the workspace root (use '.' for the workspace root).");

            WriteBooleanProperty(w, "recursive", "When true, lists entries recursively; node_modules, bin, obj, and .git folders are skipped.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("relativePath");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildExecuteCommandSchema(int timeoutSeconds)
    {
        return BuildSchema(w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "command",
                $"Executable or binary name (no shell). The host enforces a {timeoutSeconds} second timeout.");

            WriteStringProperty(
                w,
                "arguments",
                "Optional command-line arguments as a single string. Tokenized by the host (quoted substrings stay together; whitespace separates tokens). Prefer 'argumentList' when calling from a model SDK.");

            w.WriteStartObject("argumentList");

            w.WriteString("type", "array");

            w.WriteString("description", "Preferred: pre-tokenized argument list. Each entry is passed verbatim to the child process (no shell, no re-parsing).");

            w.WriteStartObject("items");

            w.WriteString("type", "string");

            w.WriteEndObject();

            w.WriteEndObject();

            WriteStringProperty(
                w,
                "workingDirectory",
                "Optional working directory relative to the workspace root. When omitted, the process runs in the workspace root. Must not be an absolute path.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("command");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildAskHumanSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(w, "question", "The question or context to show the human operator.");

            WriteStringProperty(
                w,
                "promptId",
                "Unique correlation id for this prompt. Generate a new random UUID (RFC 4122) for every ask_human call.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("question");

            w.WriteStringValue("promptId");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildReadLoreSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "key",
                "Lore key (e.g. Architecture_State, User_Preferences). Stored in the encrypted Grimoire database.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("key");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildScribeLoreSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "key",
                "Descriptive lore key under which the fact is stored (upsert).");

            WriteStringProperty(w, "value", "Compressed factual summary to persist for later turns.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("key");

            w.WriteStringValue("value");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildDeleteLoreSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(w, "key", "Lore key to remove from the Grimoire.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("key");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildSearchArchivesSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "query",
                "Keywords or FTS5 query text to match against archived chat message content.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("query");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildReadSagaSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "query",
                "Natural-language query to semantically search Saga (long-term associative memory) for relevant past facts, decisions, and preferences.");

            WriteIntegerProperty(
                w,
                "limit",
                "Optional maximum number of memories to return (defaults to Arcanum:Embeddings:MaxResults).");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("query");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildAdjustInitiativeSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "job_name",
                "Unseen Servant job name as configured under Arcanum:Daemon:Jobs (the 'name' field).");

            WriteIntegerProperty(
                w,
                "interval_minutes",
                "New polling interval in minutes (clamped by the host to the allowed range).");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("job_name");

            w.WriteStringValue("interval_minutes");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildUseCommlinkSchema()
    {

        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "title",
                "Short alert title shown to the operator.");

            WriteStringProperty(
                w,
                "body",
                "Alert body with details the operator should read.");

            WriteStringProperty(
                w,
                "severity",
                "One of: Info, Warning, Critical (case-insensitive). Unknown values are treated as Info.");

            WriteStringProperty(
                w,
                "source",
                "Optional origin label (defaults to use_commlink).");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("title");

            w.WriteStringValue("body");

            w.WriteStringValue("severity");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);

        });

    }

    private static JsonElement BuildPetitionDungeonMasterSchema()
    {

        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "reason",
                "Clear explanation of why the Apprentice is stuck and requires Dungeon Master guidance.");

            WriteStringProperty(
                w,
                "source",
                "Optional origin label (defaults to petition_dungeon_master).");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("reason");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);

        });

    }

    private static JsonElement BuildCastSendingSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "goal",
                "The goal for the new child Apprentice. Describe the delegated sub-task clearly and self-containedly.");

            WriteStringProperty(
                w,
                "name",
                "Optional display name for the child Apprentice. A themed default is used when omitted.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("goal");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildSchema(Action<Utf8JsonWriter> writeBody)
    {
        ArrayBufferWriter<byte> buffer = new(512);

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            writeBody(writer);

            writer.WriteEndObject();
        }

        using JsonDocument doc = JsonDocument.Parse(buffer.WrittenMemory);

        return doc.RootElement.Clone();
    }

    private static void WriteStringProperty(Utf8JsonWriter w, string name, string description)
    {
        w.WriteStartObject(name);

        w.WriteString("type", "string");

        w.WriteString("description", description);

        w.WriteEndObject();
    }

    private static void WriteIntegerProperty(Utf8JsonWriter w, string name, string description)
    {
        w.WriteStartObject(name);

        w.WriteString("type", "integer");

        w.WriteString("description", description);

        w.WriteEndObject();
    }

    private static void WriteBooleanProperty(Utf8JsonWriter w, string name, string description)
    {
        w.WriteStartObject(name);

        w.WriteString("type", "boolean");

        w.WriteString("description", description);

        w.WriteEndObject();
    }
}
