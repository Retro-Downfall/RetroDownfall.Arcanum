using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Runtime projection for content guardrails (Tier 3 Phase 4). Activation comes from
/// <c>Arcanum:Features:Guardrails</c>, authored policy from <c>Arcanum:Security:Guardrails</c>, and
/// streaming mode/file mechanics remain code-owned.
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
    /// Streaming output-filter mode. Default <see cref="GuardrailsStreamingMode.Buffered"/> so that
    /// when <see cref="Enabled"/> is true and the operator has not opted into passthrough, blocked
    /// assistant output does not reach the client. Explicit <see cref="GuardrailsStreamingMode.Passthrough"/>
    /// is still honored (with a warning). Filters assistant output only — not tool side effects.
    /// No effect when <see cref="Enabled"/> is <see langword="false"/>.
    /// </summary>
    public GuardrailsStreamingMode StreamingMode { get; set; } = GuardrailsStreamingMode.Buffered;

}

/// <summary>
/// Runtime projection for the persisted guardrails audit log — a durable, append-only JSONL trail
/// of guardrail violations (input PII, blocked toxicity/topics), one file per UTC day. Enablement
/// and retention come from <c>Arcanum:Security:Guardrails:AuditLog</c>.
/// </summary>
public sealed record GuardrailsAuditLogSettings
{

    /// <summary>
    /// Master toggle. When <see langword="false"/> (default), <c>GuardrailAuditLogger</c> is a
    /// complete no-op and <c>GET /api/guardrails/audit</c> returns an empty list. Ineffective when
    /// <c>Arcanum:Features:Guardrails</c> is <see langword="false"/>.
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

    private static string DefaultFilePath =>
        Path.Combine(ArcanumPaths.GrimoireDirectory, "guardrails.jsonl");

}
