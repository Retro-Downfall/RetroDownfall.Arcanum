using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Content guardrails settings (Tier 3 Phase 4). When <see cref="Enabled"/> is <see langword="false"/>
/// (the default) the <c>GuardrailsPipeline</c> is a complete pass-through — no input/output scanning,
/// no audit logging — so enabling Arcanum never changes inference behavior until an operator opts in.
/// </summary>
public sealed record GuardrailsSettings
{

    /// <summary>
    /// Master toggle. When <see langword="false"/> (default), <see cref="GuardrailsPipeline"/> returns
    /// success for every input/output without scanning, and the audit logger writes nothing.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When <see langword="true"/> (default), PII (email, phone, SSN, credit-card) in input messages
    /// is detected and the turn is rejected with <c>Guardrails.PiiDetected</c> before inference runs.
    /// </summary>
    public bool DetectPii { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, input or output containing any <see cref="ToxicityBlocklist"/>
    /// keyword is rejected with <c>Guardrails.Blocked</c>. Default <see langword="false"/> — an empty
    /// blocklist is a no-op even when this is true.
    /// </summary>
    public bool BlockToxicity { get; set; }

    /// <summary>
    /// Substring (case-insensitive) blocklist matched against input and output text. Default empty —
    /// no toxicity blocking. Only consulted when <see cref="BlockToxicity"/> is <see langword="true"/>.
    /// </summary>
    public string[] ToxicityBlocklist { get; set; } = [];

    /// <summary>
    /// Optional allow-list of topic patterns (regex). When non-empty, input that fails to match any
    /// pattern is rejected with <c>Guardrails.Blocked</c>. Default empty — all topics allowed.
    /// </summary>
    public string[] AllowedTopics { get; set; } = [];

    /// <summary>
    /// Optional block-list of topic patterns (regex). Input or output matching any pattern is rejected
    /// with <c>Guardrails.Blocked</c>. Default empty — no topics blocked.
    /// </summary>
    public string[] BlockedTopics { get; set; } = [];

    /// <summary>
    /// Persisted guardrails audit log configuration. Disabled by default — no file I/O, no
    /// <c>GET /api/guardrails/audit</c> results — until an operator opts in. Independent of
    /// <c>Host:AuditLog</c> (which records completed inference turns); this records guardrails
    /// violations only, and only when <see cref="Enabled"/> is also <see langword="true"/>.
    /// </summary>
    public GuardrailsAuditLogSettings AuditLog { get; set; } = new();

    /// <summary>
    /// Streaming output-filter mode. <c>passthrough</c> (default) emits tokens in real time and
    /// runs the output filter post-hoc (toxic text may reach the client; only persistence is blocked).
    /// <c>buffered</c> holds tokens server-side, runs the output filter on the full text, and only
    /// releases the content after the filter passes — toxic content never reaches the client, at the
    /// cost of real-time streaming. No effect when <see cref="Enabled"/> is <see langword="false"/>.
    /// </summary>
    public string StreamingMode { get; set; } = "passthrough";

}

/// <summary>
/// Configuration for the persisted guardrails audit log — a durable, append-only JSONL trail of
/// guardrail violations (input PII, blocked toxicity/topics), one file per UTC day. Bound from
/// <c>Arcanum:Guardrails:AuditLog</c>. See DESIGN.md §8.x (guardrails).
/// </summary>
public sealed record GuardrailsAuditLogSettings
{

    /// <summary>
    /// Master toggle. When <see langword="false"/> (default), <c>GuardrailAuditLogger</c> is a
    /// complete no-op and <c>GET /api/guardrails/audit</c> returns an empty list. Ineffective when
    /// <c>Guardrails:Enabled</c> is <see langword="false"/>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Base file path. The directory portion is where dated log files are written; the filename stem
    /// (default <c>guardrails</c>) is combined with a UTC date to produce each day's file, e.g.
    /// <c>guardrails-20260115.jsonl</c> — one file per UTC day, never a single ever-growing file.
    /// </summary>
    public string FilePath { get; set; } = DefaultFilePath;

    /// <summary>
    /// Soft per-file size cap in megabytes. Once a day's file reaches this size, further writes for
    /// that day are dropped (logged once) rather than growing the file unbounded. Default <c>100</c>;
    /// clamped 10–1,000 (reuses <see cref="ArcanumSettingClamps.HostAuditLogMaxSizeMb"/> bounds).
    /// </summary>
    public int MaxSizeMb { get; set; } = 100;

    /// <summary>
    /// Dated log files older than this many days are deleted automatically the first time a new UTC
    /// day's file is created. Default <c>7</c>; clamped 1–365 (reuses
    /// <see cref="ArcanumSettingClamps.HostAuditLogRetentionDays"/> bounds).
    /// </summary>
    public int RetentionDays { get; set; } = 7;

    private static string DefaultFilePath =>
        Path.Combine(ArcanumPaths.GrimoireDirectory, "guardrails.jsonl");

}
