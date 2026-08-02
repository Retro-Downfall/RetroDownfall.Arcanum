namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// One typed retention rule. Disabling a rule prevents policy sweeps from selecting that class;
/// it does not disable status, dry-run, or explicit item-scoped deletion.
/// </summary>
public sealed record RetentionRuleSettings
{

    public bool Enabled { get; set; }

    public int Days { get; set; } = 30;

}

/// <summary>
/// Unified data-lifecycle policy under <c>Arcanum:Retention</c>.
/// Automatic sweeps are opt-in, and session-bearing classes are disabled by default.
/// </summary>
public sealed record RetentionSettings
{

    public bool AutomaticSweepsEnabled { get; set; }

    public int SweepIntervalHours { get; set; } = 24;

    public int MaxItemsPerSweep { get; set; } = 500;

    public int CheckpointInterval { get; set; } = 50;

    public int AccountingMinimumDays { get; set; } = 365;

    public RetentionRuleSettings ActiveSessions { get; set; } =
        new() { Enabled = false, Days = 365 };

    public RetentionRuleSettings ArchivedSessions { get; set; } =
        new() { Enabled = false, Days = 180 };

    public RetentionRuleSettings Entries { get; set; } =
        new() { Enabled = false, Days = 180 };

    public RetentionRuleSettings Attachments { get; set; } =
        new() { Enabled = false, Days = 180 };

    public RetentionRuleSettings UploadedFiles { get; set; } =
        new() { Enabled = true, Days = 30 };

    public RetentionRuleSettings CompletedBatches { get; set; } =
        new() { Enabled = true, Days = 30 };

    public RetentionRuleSettings SagaMemories { get; set; } =
        new() { Enabled = false, Days = 365 };

    public RetentionRuleSettings LexiconEntries { get; set; } =
        new() { Enabled = false, Days = 365 };

    public RetentionRuleSettings WorkspaceIndexes { get; set; } =
        new() { Enabled = true, Days = 30 };

    public RetentionRuleSettings SessionEntryEmbeddings { get; set; } =
        new() { Enabled = true, Days = 30 };

    public RetentionRuleSettings AuditLogs { get; set; } =
        new() { Enabled = true, Days = 30 };

    public RetentionRuleSettings GuardrailLogs { get; set; } =
        new() { Enabled = true, Days = 30 };

    public RetentionRuleSettings IdempotencyClaims { get; set; } =
        new() { Enabled = true, Days = 7 };

    public RetentionRuleSettings Accounting { get; set; } =
        new() { Enabled = true, Days = 365 };

    public RetentionRuleSettings LongRunningOperations { get; set; } =
        new() { Enabled = true, Days = 90 };

    public RetentionRuleSettings SanctumBreaches { get; set; } =
        new() { Enabled = true, Days = 90 };

    public RetentionRuleSettings DaemonHistory { get; set; } =
        new() { Enabled = true, Days = 30 };

    /// <summary>
    /// Explicit operator holds. These session ids remain visible in plans as blocked candidates.
    /// </summary>
    public Guid[] ProtectedSessionIds { get; set; } = [];

}
