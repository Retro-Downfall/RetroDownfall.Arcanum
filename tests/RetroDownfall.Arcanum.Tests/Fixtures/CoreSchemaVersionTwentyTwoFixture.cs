using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>Version 22, captured before retention identity and accounting indexes were added.</summary>
internal static class CoreSchemaVersionTwentyTwoFixture
{
    internal const string PublishedFingerprint = "7777286981B12C5727713341CF25E4112124FD0CBC52B611A648FB31281AEA43";

    private static readonly IReadOnlyDictionary<string, string> FrozenTables = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Entries"] =
            """
            CREATE TABLE IF NOT EXISTS "Entries" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Entries" PRIMARY KEY,
                "SessionId" TEXT NOT NULL,
                "Role" INTEGER NOT NULL,
                "Content" TEXT NOT NULL,
                "ModelUsed" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "Sequence" INTEGER NOT NULL,
                "ToolCallId" TEXT NULL,
                "ToolName" TEXT NULL,
                "ToolArguments" TEXT NULL,
                "IsPinned" INTEGER NOT NULL DEFAULT 0,
                CONSTRAINT "FK_Entries_Sessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES "Sessions" ("Id") ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS "IX_Entries_SessionId_CreatedAt" ON "Entries" ("SessionId", "CreatedAt");

            -- Authoritative intra-session chronological order. Unique so a lost per-session allocation races
            -- into a write failure instead of silently reordering a transcript.
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Entries_SessionId_Sequence" ON "Entries" ("SessionId", "Sequence");

            CREATE INDEX IF NOT EXISTS "IX_Entries_Role" ON "Entries" ("Role");

            CREATE INDEX IF NOT EXISTS "IX_Entries_SessionId_IsPinned" ON "Entries" ("SessionId", "IsPinned");

            -- Retention compares this column normalized - lower(replace(SessionId, '-', '')) - because a stored
            -- identity once had two spellings and an exact equality would have missed one of them. SQLite cannot
            -- answer a function-wrapped column from IX_Entries_SessionId_CreatedAt or from any other index above,
            -- so every one of those comparisons was a full scan of this table, once per candidate Session in the
            -- planning pass and again per candidate in the apply pass. The expression here has to stay character
            -- for character the shape the predicate has, because that is how SQLite decides the index applies.
            CREATE INDEX IF NOT EXISTS "IX_Entries_SessionId_Norm"
              ON "Entries" (lower(replace("SessionId", '-', '')));
            """,
        ["Sessions"] =
            """
            -- TotalCostUsd is laid out exactly as SQLite splices an added column into the stored table
            -- declaration. Version 8 replaces the former NUMERIC-affinity column because SQLite coerced EF's
            -- exact decimal text to an IEEE-754 REAL before storing it. The replacement TEXT column keeps the
            -- decimal(18,8) value exact, and fresh and evolved installations must normalize to the same DDL.
            CREATE TABLE IF NOT EXISTS "Sessions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Sessions" PRIMARY KEY,
                "CampaignId" TEXT NULL,
                "Title" TEXT NULL,
                "Status" TEXT NOT NULL DEFAULT 'active',
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "Summary" TEXT NULL,
                "LastSummarizedMessageAt" TEXT NULL,
                "TotalTokensUsed" INTEGER NOT NULL DEFAULT 0,
                "UnsummarizedEntryCount" INTEGER NOT NULL DEFAULT 0,
                "ForkedFromSessionId" TEXT NULL
            , "TotalCostUsd" TEXT NOT NULL DEFAULT 0);

            CREATE INDEX IF NOT EXISTS "IX_Sessions_CreatedAt" ON "Sessions" ("CreatedAt");

            CREATE INDEX IF NOT EXISTS "IX_Sessions_UpdatedAt" ON "Sessions" ("UpdatedAt");

            CREATE INDEX IF NOT EXISTS "IX_Sessions_Status" ON "Sessions" ("Status");

            CREATE INDEX IF NOT EXISTS "IX_Sessions_CampaignId" ON "Sessions" ("CampaignId");

            CREATE INDEX IF NOT EXISTS "IX_Sessions_Status_UpdatedAt" ON "Sessions" ("Status", "UpdatedAt");

            CREATE INDEX IF NOT EXISTS "IX_Sessions_CampaignId_Status_UpdatedAt" ON "Sessions" ("CampaignId", "Status", "UpdatedAt");

            CREATE INDEX IF NOT EXISTS "IX_Sessions_UnsummarizedEntryCount" ON "Sessions" ("UnsummarizedEntryCount");

            CREATE INDEX IF NOT EXISTS "IX_Sessions_ForkedFromSessionId" ON "Sessions" ("ForkedFromSessionId");
            """,
        ["IdempotencyClaims"] =
            """
            CREATE TABLE IF NOT EXISTS "IdempotencyClaims" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_IdempotencyClaims" PRIMARY KEY,
                "ClaimKeyHash" TEXT NOT NULL,
                "FingerprintHash" TEXT NOT NULL,
                "State" INTEGER NOT NULL,
                "OwnerId" TEXT NOT NULL,
                "LeaseExpiresAt" TEXT NOT NULL,
                "HeartbeatAt" TEXT NOT NULL,
                "RunId" TEXT NULL,
                "StatusCode" INTEGER NULL,
                "ContentType" TEXT NULL,
                "ResponseBody" TEXT NULL,
                "TerminalStreamComplete" INTEGER NOT NULL DEFAULT 0,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_IdempotencyClaims_ClaimKeyHash" ON "IdempotencyClaims" ("ClaimKeyHash");
            CREATE INDEX IF NOT EXISTS "IX_IdempotencyClaims_State_LeaseExpiresAt" ON "IdempotencyClaims" ("State", "LeaseExpiresAt");
            """,
        ["InferenceRuns"] =
            """
            CREATE TABLE IF NOT EXISTS "InferenceRuns" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_InferenceRuns" PRIMARY KEY,
                "RequestId" TEXT NOT NULL,
                "SessionId" TEXT NULL,
                "Surface" TEXT NOT NULL,
                "Purpose" TEXT NOT NULL,
                "StartedAt" TEXT NOT NULL,
                "CompletedAt" TEXT NULL,
                "Status" INTEGER NOT NULL,
                "IdempotencyClaimId" TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS "IX_InferenceRuns_StartedAt" ON "InferenceRuns" ("StartedAt");
            CREATE INDEX IF NOT EXISTS "IX_InferenceRuns_IdempotencyClaimId" ON "InferenceRuns" ("IdempotencyClaimId");

            -- Retention compares SessionId normalized - lower(replace(SessionId, '-', '')) - because TurnRunWriter writes
            -- this column dash-free while Sessions.Id is dashed, once per candidate Session in the planning pass, again
            -- in the apply pass, and in the post-commit counts that prove a Session delete. SQLite cannot answer a
            -- function-wrapped column from an ordinary column index, so each of those was a scan of the run ledger,
            -- which gains a row per turn. The expression here has to stay character for character the shape the
            -- predicate has, because that is how SQLite decides the index applies.
            CREATE INDEX IF NOT EXISTS "IX_InferenceRuns_SessionId_Norm"
              ON "InferenceRuns" (lower(replace("SessionId", '-', '')));
            """,
        ["LongRunningOperations"] =
            """
            CREATE TABLE IF NOT EXISTS "LongRunningOperations" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_LongRunningOperations" PRIMARY KEY,
                "Kind" TEXT NOT NULL,
                "State" INTEGER NOT NULL,
                "RecoveryPolicy" INTEGER NOT NULL,
                "RootOperationId" TEXT NULL,
                "ParentOperationId" TEXT NULL,
                "SessionId" TEXT NULL,
                "RunId" TEXT NULL,
                "InferenceRunId" TEXT NULL,
                "BudgetReservationId" TEXT NULL,
                "IdempotencyClaimId" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "StartedAt" TEXT NULL,
                "HeartbeatAt" TEXT NULL,
                "CompletedAt" TEXT NULL,
                "LeaseOwner" TEXT NULL,
                "LeaseExpiresAt" TEXT NULL,
                "AttemptCount" INTEGER NOT NULL DEFAULT 0,
                "CheckpointVersion" INTEGER NOT NULL DEFAULT 0,
                "CheckpointPayload" BLOB NULL,
                "CheckpointReference" TEXT NULL,
                "PublicSummary" TEXT NOT NULL,
                "TerminalErrorCode" TEXT NULL,
                "Revision" INTEGER NOT NULL DEFAULT 0,
                CONSTRAINT "FK_LongRunningOperations_Root"
                    FOREIGN KEY ("RootOperationId") REFERENCES "LongRunningOperations" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_LongRunningOperations_Parent"
                    FOREIGN KEY ("ParentOperationId") REFERENCES "LongRunningOperations" ("Id") ON DELETE RESTRICT
            );

            CREATE INDEX IF NOT EXISTS "IX_LongRunningOperations_State_LeaseExpiresAt"
                ON "LongRunningOperations" ("State", "LeaseExpiresAt");

            CREATE INDEX IF NOT EXISTS "IX_LongRunningOperations_Kind_State"
                ON "LongRunningOperations" ("Kind", "State");

            CREATE INDEX IF NOT EXISTS "IX_LongRunningOperations_ParentOperationId"
                ON "LongRunningOperations" ("ParentOperationId");

            CREATE INDEX IF NOT EXISTS "IX_LongRunningOperations_RootOperationId"
                ON "LongRunningOperations" ("RootOperationId");

            CREATE INDEX IF NOT EXISTS "IX_LongRunningOperations_SessionId"
                ON "LongRunningOperations" ("SessionId");

            CREATE INDEX IF NOT EXISTS "IX_LongRunningOperations_RunId"
                ON "LongRunningOperations" ("RunId");

            CREATE INDEX IF NOT EXISTS "IX_LongRunningOperations_BudgetReservationId"
                ON "LongRunningOperations" ("BudgetReservationId");
            """,
        ["Batches"] =
            """
            CREATE TABLE IF NOT EXISTS "Batches" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Batches" PRIMARY KEY,
                "InputFileId" TEXT NOT NULL,
                "Endpoint" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "CompletedAt" TEXT NULL,
                "OutputFileId" TEXT NULL,
                "ErrorFileId" TEXT NULL,
                "TotalRequestCount" INTEGER NOT NULL DEFAULT 0,
                "CompletedRequestCount" INTEGER NOT NULL DEFAULT 0,
                "FailedRequestCount" INTEGER NOT NULL DEFAULT 0,
                CONSTRAINT "CK_Batches_RequestCounts" CHECK (
                    "TotalRequestCount" >= 0
                    AND "CompletedRequestCount" >= 0
                    AND "FailedRequestCount" >= 0
                    AND "CompletedRequestCount" + "FailedRequestCount" <= "TotalRequestCount"
                )
            );

            CREATE INDEX IF NOT EXISTS "IX_Batches_Status" ON "Batches" ("Status");

            CREATE INDEX IF NOT EXISTS "IX_Batches_CreatedAt" ON "Batches" ("CreatedAt");

            CREATE INDEX IF NOT EXISTS "IX_Batches_CreatedAt_Id" ON "Batches" ("CreatedAt" DESC, "Id" DESC);

            -- Each of the three file roles a batch names is looked up by the file, from the file's side: deleting an
            -- uploaded file, and the retention sweep's reference check, ask whether any batch still names it. Without
            -- an index per column each of those asks scans every batch the installation ever held. The value stored
            -- here is the canonical uppercase dashed identity, so the lookup is a closed list of exact equalities (the
            -- canonical spelling plus the two pre-version-15 lowercase spellings) and these ordinary column indexes answer
            -- each one.
            CREATE INDEX IF NOT EXISTS "IX_Batches_InputFileId" ON "Batches" ("InputFileId");

            CREATE INDEX IF NOT EXISTS "IX_Batches_OutputFileId" ON "Batches" ("OutputFileId");

            CREATE INDEX IF NOT EXISTS "IX_Batches_ErrorFileId" ON "Batches" ("ErrorFileId");

            -- The retention sweep names the same three roles normalized - lower(replace(col, '-', '')) - because it has to
            -- stay correct for whatever spelling a file identity was ever written in, and it asks the file's question from
            -- two sides: whether a batch still names an aged upload, and which batches name it. SQLite cannot answer a
            -- function-wrapped column from the plain indexes above, so without an index on each wrapped expression every
            -- one of those asks is a scan of every batch the installation ever held, once per candidate file. Each
            -- expression here has to stay character for character the shape the predicate has, because that is how SQLite
            -- decides the index applies.
            CREATE INDEX IF NOT EXISTS "IX_Batches_InputFileId_Norm"
              ON "Batches" (lower(replace("InputFileId", '-', '')));

            CREATE INDEX IF NOT EXISTS "IX_Batches_OutputFileId_Norm"
              ON "Batches" (lower(replace("OutputFileId", '-', '')));

            CREATE INDEX IF NOT EXISTS "IX_Batches_ErrorFileId_Norm"
              ON "Batches" (lower(replace("ErrorFileId", '-', '')));
            """,
        ["CostAdjustments"] =
            """
            -- AmountUsd is at the version-9 ADD COLUMN tail so fresh and evolved catalogs converge.
            CREATE TABLE IF NOT EXISTS "CostAdjustments" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CostAdjustments" PRIMARY KEY,
                "BillableOperationId" TEXT NULL,
                "RunId" TEXT NULL,
                "Reason" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            , "AmountUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("AmountUsd") = 'text'));
            """,
        ["BudgetAlerts"] =
            """
            -- Exact USD columns are at the version-9 ADD COLUMN tail so fresh and evolved catalogs converge.
            CREATE TABLE IF NOT EXISTS "BudgetAlerts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_BudgetAlerts" PRIMARY KEY,
                "Threshold" INTEGER NOT NULL,
                "AlertedAt" TEXT NOT NULL,
                "SpendUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("SpendUsd") = 'text'),
                "DailyLimitUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("DailyLimitUsd") = 'text'));

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_BudgetAlerts_Threshold_Date" ON "BudgetAlerts" ("Threshold", date("AlertedAt"));
            """,
        ["SanctumBreaches"] =
            """
            CREATE TABLE IF NOT EXISTS "SanctumBreaches" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SanctumBreaches" PRIMARY KEY,
                "CampaignId" TEXT NOT NULL,
                "OccurredAt" TEXT NOT NULL,
                "ToolName" TEXT NOT NULL,
                "BreachType" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "DetailsJson" TEXT NULL,
                CONSTRAINT "FK_SanctumBreaches_Campaigns_CampaignId"
                    FOREIGN KEY ("CampaignId") REFERENCES "Campaigns"("Id") ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS "IX_SanctumBreaches_CampaignId_OccurredAt"
                ON "SanctumBreaches" ("CampaignId", "OccurredAt" DESC);
            """,
        ["SessionAttachments"] =
            """
            CREATE TABLE IF NOT EXISTS "SessionAttachments" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SessionAttachments" PRIMARY KEY,
                "SessionId" TEXT NULL,
                "EntryId" TEXT NULL,
                "PendingTurnId" TEXT NULL,
                "State" TEXT NOT NULL,
                "LogicalKey" TEXT NOT NULL,
                "OriginalFileName" TEXT NOT NULL,
                "Version" INTEGER NOT NULL,
                "RelativePath" TEXT NOT NULL,
                "ContentSha256" TEXT NOT NULL,
                "MimeType" TEXT NOT NULL,
                "ByteLength" INTEGER NOT NULL,
                "Kind" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "SourceKind" TEXT NOT NULL DEFAULT 'SnapshotOnly',
                "SourceWorkspaceIdentity" TEXT NULL,
                "SourceRelativePath" TEXT NULL,
                "SourceCanonicalPath" TEXT NULL,
                "SourceContentSha256" TEXT NULL,
                "SourceFileIdentity" TEXT NULL,
                "SourceLastWriteAt" TEXT NULL,
                "SourceByteLength" INTEGER NULL,
                "SourceStatus" TEXT NOT NULL DEFAULT 'NotApplicable',
                "SourceDiagnosticReason" TEXT NULL,
                "EncryptionVersion" INTEGER NOT NULL DEFAULT 0,
                "EncryptionKeyId" TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_Session_Logical_Version"
              ON "SessionAttachments" ("SessionId", "LogicalKey", "Version");

            CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_Session_CreatedAt"
              ON "SessionAttachments" ("SessionId", "CreatedAt");

            CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_EntryId"
              ON "SessionAttachments" ("EntryId");

            CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_PendingTurnId"
              ON "SessionAttachments" ("PendingTurnId");

            CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_State"
              ON "SessionAttachments" ("State");

            CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_SourceWorkspace_Path"
              ON "SessionAttachments" ("SourceWorkspaceIdentity", "SourceRelativePath");

            CREATE UNIQUE INDEX IF NOT EXISTS UX_SessionAttachments_Bound
              ON SessionAttachments(SessionId, LogicalKey, Version)
              WHERE State = 'Bound';

            CREATE UNIQUE INDEX IF NOT EXISTS UX_SessionAttachments_Pending
              ON SessionAttachments(PendingTurnId, LogicalKey, Version)
              WHERE State = 'Pending';

            -- The two columns retention compares normalized. The attachment candidate scan reads SessionId that
            -- way for every candidate Session, and the pin check reads Id that way for every candidate attachment;
            -- neither can be answered from the indexes above, which key the unwrapped columns.
            CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_SessionId_Norm"
              ON "SessionAttachments" (lower(replace("SessionId", '-', '')));

            CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_Id_Norm"
              ON "SessionAttachments" (lower(replace("Id", '-', '')));
            """,
        ["BudgetReservations"] =
            """
            -- The two exact USD columns are at the version-9 ADD COLUMN tail so fresh and evolved catalogs
            -- converge to the same normalized SQLite definition.
            CREATE TABLE IF NOT EXISTS "BudgetReservations" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_BudgetReservations" PRIMARY KEY,
                "RunId" TEXT NOT NULL,
                "BudgetPeriod" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                "ExpiresAt" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            , "ReservedUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("ReservedUsd") = 'text'),
                "ReconciledUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("ReconciledUsd") = 'text'));

            CREATE INDEX IF NOT EXISTS "IX_BudgetReservations_BudgetPeriod_Status" ON "BudgetReservations" ("BudgetPeriod", "Status");
            CREATE INDEX IF NOT EXISTS "IX_BudgetReservations_ExpiresAt" ON "BudgetReservations" ("ExpiresAt");

            CREATE INDEX IF NOT EXISTS "IX_BudgetReservations_Status" ON "BudgetReservations" ("Status");
            """,
        ["BillableOperations"] =
            """
            -- ActualCostUsd is laid out where SQLite's version-9 ADD COLUMN splice leaves it: immediately
            -- before the table constraint. Keeping the fresh source in that exact form makes a fresh and an
            -- evolved catalog normalize to one definition.
            CREATE TABLE IF NOT EXISTS "BillableOperations" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_BillableOperations" PRIMARY KEY,
                "RunId" TEXT NOT NULL,
                "OperationType" INTEGER NOT NULL,
                "Provider" TEXT NOT NULL,
                "Model" TEXT NOT NULL,
                "Purpose" TEXT NOT NULL,
                "StartedAt" TEXT NOT NULL,
                "CompletedAt" TEXT NOT NULL,
                "InputTokens" INTEGER NOT NULL,
                "OutputTokens" INTEGER NOT NULL,
                "ReasoningTokens" INTEGER NOT NULL DEFAULT 0,
                "CachedTokens" INTEGER NOT NULL,
                "PricingSnapshotJson" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                "ProviderRequestId" TEXT NULL,
                "ActualCostUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("ActualCostUsd") = 'text'),
                CONSTRAINT "FK_BillableOperations_InferenceRuns_RunId" FOREIGN KEY ("RunId") REFERENCES "InferenceRuns" ("Id") ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS "IX_BillableOperations_CompletedAt" ON "BillableOperations" ("CompletedAt");
            CREATE INDEX IF NOT EXISTS "IX_BillableOperations_RunId" ON "BillableOperations" ("RunId");
            """,
        ["UploadedFiles"] =
            """
            CREATE TABLE IF NOT EXISTS "UploadedFiles" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_UploadedFiles" PRIMARY KEY,
                "Filename" TEXT NOT NULL,
                "Bytes" INTEGER NOT NULL,
                "Purpose" TEXT NOT NULL,
                "MimeType" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "EncryptionVersion" INTEGER NOT NULL DEFAULT 0,
                "EncryptionKeyId" TEXT NULL,
                "PlaintextSha256" TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS "IX_UploadedFiles_Purpose" ON "UploadedFiles" ("Purpose");

            CREATE INDEX IF NOT EXISTS "IX_UploadedFiles_CreatedAt" ON "UploadedFiles" ("CreatedAt");
            """,
    };

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. CoreSchemaVersionTwentyThreeFixture.Objects.Select(item => FrozenTables.TryGetValue(item.Name, out string? sql)
            ? item with { Sql = sql + "\n" }
            : item),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        new GrimoireSchemaVersionChain(
            GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 22, Fingerprint, Objects),
            Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(step => step.ToVersion <= 22)]),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
    ]);
}
