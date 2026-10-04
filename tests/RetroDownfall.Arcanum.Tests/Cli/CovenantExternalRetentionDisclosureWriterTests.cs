using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The shared destructive disclosure, as an erase writes it and as every other destroy surface
/// writes its help targets.
/// </summary>
/// <remarks>
/// The per-channel lines come from a closed map, and the map's two duties are tested apart. One is
/// order and stream: the shared sentence, then one line per channel, then where to go and delete,
/// all on the diagnostic stream. The other is truthfulness: no value reads as "not disclosed", no
/// count is printed, and a receipt window is never described as bounded by when the item was
/// created, because the Lexicon backup channel is not.
/// </remarks>
public sealed class CovenantExternalRetentionDisclosureWriterTests
{
    private const string NotRecordedPhrase =
        "not recorded — Arcanum holds no record that this content was sent there, so a send cannot be ruled out.";

    private const string ReceiptWindowPhrase =
        "possible — Arcanum recorded a send on this channel and cannot rule out that it carried this content.";

    private static readonly MemoryExternalChannel[] Channels =
    [
        MemoryExternalChannel.InferenceProviderAuthorship,
        MemoryExternalChannel.InferenceProviderContext,
        MemoryExternalChannel.EmbeddingProvider,
        MemoryExternalChannel.EncryptedBackup,
        MemoryExternalChannel.OtherExternal,
    ];

    [Fact]
    public void WriteErasure_writes_the_sentence_then_one_line_per_channel_then_help_targets_to_diagnostics_only()
    {
        RecordingDispatcher dispatcher = new();

        Writer(dispatcher).WriteErasure(new MemoryErasureExternalExposureDto(MemoryExternalRevocation.NotPerformed, [
            new(MemoryExternalChannel.InferenceProviderAuthorship, MemoryExternalEvidence.Known),
            new(MemoryExternalChannel.InferenceProviderContext, MemoryExternalEvidence.NotRecorded),
            new(MemoryExternalChannel.EmbeddingProvider, MemoryExternalEvidence.NotApplicable),
            new(MemoryExternalChannel.EncryptedBackup, MemoryExternalEvidence.ReceiptWindow),
            new(MemoryExternalChannel.OtherExternal, MemoryExternalEvidence.NotRecorded)]));

        Assert.Equal(
            [
                CovenantExternalRetentionDisclosure.DestructiveOperationText,
                "Arcanum does not revoke copies that already left this machine:",
                "  Inference provider that wrote it: known — this content was sent there at least once.",
                $"  Inference providers that read it in a turn: {NotRecordedPhrase}",
                "  Embedding provider: not applicable — this store never sends content on this channel.",
                $"  Encrypted backups: {ReceiptWindowPhrase}",
                $"  Other external copies: {NotRecordedPhrase}",
                "  Retention guidance: docs/Arcanum.Engineering.md#covenant-provider-retention-and-deletion",
            ],
            dispatcher.Diagnostics);

        Assert.Empty(dispatcher.Payloads);
    }

    [Theory]
    [InlineData(MemoryExternalEvidence.Known)]
    [InlineData(MemoryExternalEvidence.ReceiptWindow)]
    [InlineData(MemoryExternalEvidence.NotRecorded)]
    [InlineData(MemoryExternalEvidence.NotApplicable)]
    public void WriteErasure_never_says_not_disclosed_and_prints_no_count(MemoryExternalEvidence evidence)
    {
        RecordingDispatcher dispatcher = new();

        Writer(dispatcher).WriteErasure(new MemoryErasureExternalExposureDto(
            MemoryExternalRevocation.NotPerformed,
            [.. Channels.Select(channel => new MemoryExternalExposureChannelDto(channel, evidence))]));

        // One line per channel between the header and the help target, and nothing else.
        Assert.Equal(1 + 1 + Channels.Length + 1, dispatcher.Diagnostics.Count);

        Assert.DoesNotContain(dispatcher.Diagnostics, line => line.Contains("not disclosed", StringComparison.OrdinalIgnoreCase));

        // No providers are configured, so no help URI carries a digit either.
        Assert.DoesNotMatch(@"\d", string.Join('\n', dispatcher.Diagnostics));

        Assert.Empty(dispatcher.Payloads);
    }

    /// <summary>
    /// A receipt window is described without a creation bound, on every channel (R-T12-C1).
    /// </summary>
    /// <remarks>
    /// The writer is told the evidence, not the store, so the one phrase it has must be true for the
    /// store whose window is widest. The Lexicon backup channel reports a window whenever any backup was
    /// ever recorded, because an entry keeps no creation time, so "while this item existed" would claim
    /// a bound that store never measured.
    /// </remarks>
    [Fact]
    public void WriteErasure_receipt_window_wording_claims_no_creation_bound_on_any_channel()
    {
        RecordingDispatcher dispatcher = new();

        Writer(dispatcher).WriteErasure(new MemoryErasureExternalExposureDto(
            MemoryExternalRevocation.NotPerformed,
            [.. Channels.Select(channel => new MemoryExternalExposureChannelDto(channel, MemoryExternalEvidence.ReceiptWindow))]));

        string[] channelLines = [.. dispatcher.Diagnostics.Skip(2).Take(Channels.Length)];

        Assert.All(channelLines, line => Assert.EndsWith($": {ReceiptWindowPhrase}", line, StringComparison.Ordinal));

        string lexiconBackupLine = Assert.Single(channelLines, line => line.StartsWith("  Encrypted backups:", StringComparison.Ordinal));

        foreach (string bound in new[] { "existed", "created", "creation", "after", "since" })
        {
            Assert.DoesNotContain(bound, lexiconBackupLine, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Write_and_backup_restore_share_WriteHelpTargets()
    {
        IOptions<ArcanumSettings> settings = Options.Create(new ArcanumSettings
        {
            Providers =
            [
                new ProviderSettings { Name = "openai", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://api.openai.com/v1" },
            ],
        });

        RecordingDispatcher afterWrite = new();

        new CovenantExternalRetentionDisclosureWriter(afterWrite, settings)
            .Write(new DataRetentionCovenantInventory(1, 0, 0, 0, 0, CovenantDisclosureCountKind.Exact));

        RecordingDispatcher afterWriteHelpTargets = new();

        new CovenantExternalRetentionDisclosureWriter(afterWriteHelpTargets, settings).WriteHelpTargets();

        RecordingDispatcher afterRestore = new();

        int exit = await DeclinedProtectedStateRestoreAsync(afterRestore, settings);

        Assert.Equal((int)CliExitCode.Success, exit);

        Assert.Equal(HelpLines(afterWrite), HelpLines(afterWriteHelpTargets));

        Assert.Equal(HelpLines(afterWrite), HelpLines(afterRestore));

        Assert.Equal(afterWriteHelpTargets.Diagnostics, HelpLines(afterWriteHelpTargets));

        Assert.Contains(HelpLines(afterWrite), line => line.StartsWith("  Retention guidance (", StringComparison.Ordinal));
    }

    private static string[] HelpLines(RecordingDispatcher dispatcher) =>
        [.. dispatcher.Diagnostics.Where(static line => line.StartsWith("  Retention guidance", StringComparison.Ordinal))];

    private static CovenantExternalRetentionDisclosureWriter Writer(IConsoleDispatcher dispatcher) =>
        new(dispatcher, Options.Create(new ArcanumSettings()));

    /// <summary>
    /// Drives a protected-state restore to its disclosure and declines, so the restore's own help
    /// lines are read from the command rather than from a copy of its loop.
    /// </summary>
    private static Task<int> DeclinedProtectedStateRestoreAsync(RecordingDispatcher dispatcher, IOptions<ArcanumSettings> settings)
    {
        BackupCommands commands = new(
            new ThrowingGrimoireInitialization(),
            new ThrowingScopeFactory(),
            new PlanningRestoreService(),
            new FixedPassphraseReader(),
            new DecliningPrompt(),
            dispatcher,
            new PlainInvocationContext(),
            settings);

        return commands.Restore(
            "/tmp/protected.arcbackup",
            conflictMode: null,
            destinationRoot: null,
            sessionIds: [],
            mappings: [],
            campaignMappings: [],
            "purge-protected-state",
            restoreMasterApiKey: false,
            dryRun: false,
            skipSafetyBackup: true,
            passphraseEnvironmentVariable: null,
            passphraseFileDescriptor: null,
            CancellationToken.None);
    }

    private sealed class RecordingDispatcher : IConsoleDispatcher
    {
        internal List<string> Diagnostics { get; } = [];

        internal List<string> Payloads { get; } = [];

        public void WritePayload(string value) => Payloads.Add(value);

        public void WriteDiagnostic(string value) => Diagnostics.Add(value);

        public void WriteVerbose(string value) => Diagnostics.Add(value);

        public void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo) => Payloads.Add(JsonSerializer.Serialize(value, typeInfo));

        public void WriteJson(JsonElement value) => Payloads.Add(value.GetRawText());

        public void BeginJsonStream()
        {
        }
    }

    private sealed class DecliningPrompt : IConfirmationPrompt
    {
        public Task<bool> PromptForConfirmationAsync(string question, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class PlainInvocationContext : ICliInvocationContext
    {
        public CliInvocationOptions Options { get; } = new(Json: false, Plain: true, Yes: false);
    }

    private sealed class FixedPassphraseReader : IBackupPassphraseReader
    {
        public ValueTask<SensitiveBackupPassphrase?> ReadAsync(BackupPassphraseReadRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<SensitiveBackupPassphrase?>(new SensitiveBackupPassphrase("restore secret".ToCharArray()));
    }

    private sealed class ThrowingGrimoireInitialization : IGrimoireCliInitialization
    {
        public Task<T> RunExclusiveAsync<T>(Func<IServiceProvider, CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A restore never enters the CLI backup boundary.");

        public Task<T> RunExclusiveWithBootstrapAsync<T>(Func<IServiceProvider, CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
            RunExclusiveAsync(operation, cancellationToken);
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("A restore never creates a backup-service scope.");
    }

    private sealed class PlanningRestoreService : IBackupRestoreService
    {
        public Task<BackupRestorePlan> PlanAsync(BackupRestoreRequest request, ReadOnlyMemory<char> recoveryPassphrase, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupRestorePlan(
                new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero),
                request.ArchivePath,
                BackupArchiveFormat.CurrentVersion,
                request.ConflictMode,
                "/tmp/arcanum",
                [BackupComponent.GrimoireDatabase],
                Entries: 3,
                RestoredBytes: 2048,
                RequiredBytes: 4096,
                AvailableBytes: 1_000_000,
                "sha256-source",
                "sha256-destination",
                SchemaMigrationRequired: false,
                SelectedSessionIds: [],
                PathMappings: [],
                UnmappedNonportablePaths: [],
                RequiresConfirmation: true,
                SafetyBackupPlanned: false,
                Warnings: [],
                Blockers: [],
                request.ProtectedStateMode,
                new BackupRestoreDisclosureExposure(EverOccurred: true, PossibleAttempts: 2, CovenantDisclosureCountKind.LowerBound)));

        public Task<BackupRestoreResult> RestoreAsync(BackupRestoreRequest request, ReadOnlyMemory<char> recoveryPassphrase, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A declined restore restores nothing.");

        public Task<BackupMigrateResult> MigrateAsync(BackupMigrateRequest request, ReadOnlyMemory<char> recoveryPassphrase, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This suite migrates nothing.");
    }
}
