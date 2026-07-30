using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Telemetry;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

[Collection("Grimoire")]
public sealed class MandatoryGrimoireRepositoryTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public MandatoryGrimoireRepositoryTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        SessionEntryPersistence.AfterMandatoryCommitForTests = null;

        SessionEntryPersistence.AfterMandatoryTransactionBeganForTests = null;

        SessionEntryPersistence.AfterMandatoryTransactionBeganAsyncForTests = null;

        SessionEntryPersistence.BeforeMandatoryCancellationClassificationLockForTests = null;

        SessionEntryPersistence.MandatoryCancellationClassificationTimeoutForTests = null;

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

    }

    [SkippableFact]
    public async Task New_then_recovered_append_is_idempotent_without_duplicate_counters()
    {

        SkipUnavailable();

        GrimoireRepository repository = CreateRepository(_db!);

        Guid sessionId = await CreateSessionAsync(repository, "mandatory receipt");

        DateTimeOffset timestamp = DateTimeOffset.UtcNow.AddMinutes(1);

        MandatoryToolInteraction interaction = CreateInteraction(
            sessionId,
            round: 0,
            call: 0,
            timestamp);

        MandatoryToolInteractionAppendResult first =
            await repository.AppendMandatoryToolInteractionAsync(
                interaction,
                CancellationToken.None);

        Assert.Equal(MandatoryToolInteractionAppendOutcome.NewlyCommitted, first.Outcome);

        _db!.ChangeTracker.Clear();

        Entry callRow = await _db.Entries
            .AsNoTracking()
            .SingleAsync(
                entry => entry.Id == interaction.Receipt.CallEntryId,
                CancellationToken.None);

        Entry resultRow = await _db.Entries
            .AsNoTracking()
            .SingleAsync(
                entry => entry.Id == interaction.Receipt.ResultEntryId,
                CancellationToken.None);

        Assert.Equal(MessageRole.Assistant, callRow.Role);

        Assert.Equal("[ToolCall: apply_patch({\"patch\":\"bounded\"})]", callRow.Content);

        Assert.Equal("provider-call", callRow.ToolCallId);

        Assert.Equal("apply_patch", callRow.ToolName);

        Assert.Equal("{\"patch\":\"bounded\"}", callRow.ToolArguments);

        Assert.Equal(MessageRole.System, resultRow.Role);

        Assert.Equal("[ToolResult: {\"status\":\"ok\"}]", resultRow.Content);

        Assert.Equal(timestamp, callRow.CreatedAt);

        Assert.Equal(timestamp, resultRow.CreatedAt);

        Session firstSession = await LoadSessionAsync(_db, sessionId);

        Assert.Equal(4, firstSession.UnsummarizedEntryCount);

        Assert.Equal(timestamp, firstSession.UpdatedAt);

        MandatoryToolInteractionAppendResult recovered =
            await repository.AppendMandatoryToolInteractionAsync(
                interaction,
                CancellationToken.None);

        Assert.Equal(MandatoryToolInteractionAppendOutcome.RecoveredCommitted, recovered.Outcome);

        _db.ChangeTracker.Clear();

        Assert.Equal(
            2,
            await CountReceiptRowsAsync(_db, interaction.Receipt));

        Session recoveredSession = await LoadSessionAsync(_db, sessionId);

        Assert.Equal(4, recoveredSession.UnsummarizedEntryCount);

        Assert.Equal(firstSession.UpdatedAt, recoveredSession.UpdatedAt);

    }

    [SkippableFact]
    public async Task Duplicate_provider_ids_across_rounds_both_commit()
    {

        SkipUnavailable();

        GrimoireRepository repository = CreateRepository(_db!);

        Guid sessionId = await CreateSessionAsync(repository, "duplicate provider ids");

        DateTimeOffset timestamp = DateTimeOffset.UtcNow.AddMinutes(1);

        MandatoryToolInteraction first = CreateInteraction(
            sessionId,
            round: 0,
            call: 0,
            timestamp);

        MandatoryToolInteraction second = CreateInteraction(
            sessionId,
            round: 1,
            call: 0,
            timestamp.AddSeconds(1));

        Assert.Equal(first.ToolCallId, second.ToolCallId);

        Assert.NotEqual(first.Receipt.Id, second.Receipt.Id);

        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.NewlyCommitted,
            (await repository.AppendMandatoryToolInteractionAsync(
                first,
                CancellationToken.None)).Outcome);

        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.NewlyCommitted,
            (await repository.AppendMandatoryToolInteractionAsync(
                second,
                CancellationToken.None)).Outcome);

        _db!.ChangeTracker.Clear();

        Assert.Equal(2, await CountReceiptRowsAsync(_db, first.Receipt));

        Assert.Equal(2, await CountReceiptRowsAsync(_db, second.Receipt));

        Assert.Equal(
            2,
            await _db.Entries
                .AsNoTracking()
                .CountAsync(
                    entry => entry.ToolCallId == "provider-call",
                    CancellationToken.None));

        Assert.Equal(
            6,
            (await LoadSessionAsync(_db, sessionId)).UnsummarizedEntryCount);

    }

    [SkippableFact]
    public async Task Multiple_patch_calls_in_one_round_both_commit()
    {

        SkipUnavailable();

        GrimoireRepository repository = CreateRepository(_db!);

        Guid sessionId = await CreateSessionAsync(repository, "multiple patches");

        DateTimeOffset timestamp = DateTimeOffset.UtcNow.AddMinutes(1);

        MandatoryToolInteraction first = CreateInteraction(
            sessionId,
            round: 0,
            call: 0,
            timestamp);

        MandatoryToolInteraction second = CreateInteraction(
            sessionId,
            round: 0,
            call: 1,
            timestamp.AddSeconds(1));

        Assert.NotEqual(first.Receipt.Id, second.Receipt.Id);

        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.NewlyCommitted,
            (await repository.AppendMandatoryToolInteractionAsync(
                first,
                CancellationToken.None)).Outcome);

        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.NewlyCommitted,
            (await repository.AppendMandatoryToolInteractionAsync(
                second,
                CancellationToken.None)).Outcome);

        _db!.ChangeTracker.Clear();

        Assert.Equal(2, await CountReceiptRowsAsync(_db, first.Receipt));

        Assert.Equal(2, await CountReceiptRowsAsync(_db, second.Receipt));

    }

    [SkippableFact]
    public async Task Injected_lost_commit_response_recovers_by_reading_durable_rows()
    {

        SkipUnavailable();

        GrimoireRepository repository = CreateRepository(_db!);

        Guid sessionId = await CreateSessionAsync(repository, "lost commit response");

        MandatoryToolInteraction interaction = CreateInteraction(
            sessionId,
            round: 0,
            call: 0,
            DateTimeOffset.UtcNow.AddMinutes(1));

        SessionEntryPersistence.AfterMandatoryCommitForTests =
            _ => new IOException("injected lost commit response");

        MandatoryToolInteractionAppendResult initial =
            await repository.AppendMandatoryToolInteractionAsync(
            interaction,
            CancellationToken.None);

        Assert.Equal(MandatoryToolInteractionAppendOutcome.RecoveredCommitted, initial.Outcome);

        SessionEntryPersistence.AfterMandatoryCommitForTests = null;

        await using ArcanumDbContext freshContext = _fixture.CreateContext(_dbPath);

        GrimoireRepository freshRepository = CreateRepository(freshContext);

        MandatoryToolInteractionAppendResult recovered =
            await freshRepository.AppendMandatoryToolInteractionAsync(
                interaction,
                CancellationToken.None);

        Assert.Equal(MandatoryToolInteractionAppendOutcome.RecoveredCommitted, recovered.Outcome);

        Assert.Equal(2, await CountReceiptRowsAsync(freshContext, interaction.Receipt));

        Assert.Equal(
            4,
            (await LoadSessionAsync(freshContext, sessionId)).UnsummarizedEntryCount);

    }

    [SkippableFact]
    public async Task Precancelled_recovery_classifies_existing_rows_before_rethrowing()
    {

        SkipUnavailable();

        GrimoireRepository repository = CreateRepository(_db!);

        Guid sessionId = await CreateSessionAsync(repository, "cancelled recovery");

        MandatoryToolInteraction interaction = CreateInteraction(
            sessionId,
            round: 0,
            call: 0,
            DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.NewlyCommitted,
            (await repository.AppendMandatoryToolInteractionAsync(
                interaction,
                CancellationToken.None)).Outcome);

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => repository.AppendMandatoryToolInteractionAsync(
                    interaction,
                    cancellation.Token));

        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.RecoveredCommitted,
            exception.Data[nameof(MandatoryToolInteractionAppendOutcome)]);

        _db!.ChangeTracker.Clear();

        Assert.Equal(2, await CountReceiptRowsAsync(_db, interaction.Receipt));

        Assert.Equal(
            4,
            (await LoadSessionAsync(_db, sessionId)).UnsummarizedEntryCount);

    }

    [SkippableFact]
    public async Task Precancelled_append_with_confirmed_no_rows_is_failed()
    {

        SkipUnavailable();

        GrimoireRepository repository = CreateRepository(_db!);

        Guid sessionId = await CreateSessionAsync(repository, "cancelled before transaction");

        MandatoryToolInteraction interaction = CreateInteraction(
            sessionId,
            round: 0,
            call: 0,
            DateTimeOffset.UtcNow.AddMinutes(1));

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => repository.AppendMandatoryToolInteractionAsync(
                    interaction,
                    cancellation.Token));

        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.Failed,
            exception.Data[nameof(MandatoryToolInteractionAppendOutcome)]);

        Assert.Equal(0, await CountReceiptRowsAsync(_db!, interaction.Receipt));

    }

    [SkippableFact]
    public async Task Cancellation_after_transaction_start_and_definitive_rollback_is_failed()
    {

        SkipUnavailable();

        GrimoireRepository repository = CreateRepository(_db!);

        Guid sessionId = await CreateSessionAsync(repository, "cancelled transaction");

        MandatoryToolInteraction interaction = CreateInteraction(
            sessionId,
            round: 0,
            call: 0,
            DateTimeOffset.UtcNow.AddMinutes(1));

        using CancellationTokenSource cancellation = new();

        SessionEntryPersistence.AfterMandatoryTransactionBeganForTests =
            _ => new OperationCanceledException(cancellation.Token);

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => repository.AppendMandatoryToolInteractionAsync(
                    interaction,
                    cancellation.Token));

        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.Failed,
            exception.Data[nameof(MandatoryToolInteractionAppendOutcome)]);

        Assert.Equal(0, await CountReceiptRowsAsync(_db!, interaction.Receipt));

        Assert.Equal(
            2,
            (await LoadSessionAsync(_db!, sessionId)).UnsummarizedEntryCount);

    }

    [SkippableFact]
    public async Task Cancellation_classification_waits_for_competing_append_then_reads_under_lock()
    {

        SkipUnavailable();

        GrimoireRepository cancelledRepository = CreateRepository(_db!);

        Guid sessionId = await CreateSessionAsync(
            cancelledRepository,
            "classification race");

        MandatoryToolInteraction interaction = CreateInteraction(
            sessionId,
            round: 0,
            call: 0,
            DateTimeOffset.UtcNow.AddMinutes(1));

        TaskCompletionSource writerEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseWriter = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource classifierAttemptedLock = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        SessionEntryPersistence.AfterMandatoryTransactionBeganAsyncForTests =
            async (receipt, cancellationToken) =>
            {

                if (receipt.Id == interaction.Receipt.Id)
                {

                    writerEntered.TrySetResult();

                    await releaseWriter.Task.WaitAsync(cancellationToken);

                }

            };

        SessionEntryPersistence.BeforeMandatoryCancellationClassificationLockForTests =
            receipt =>
            {
                if (receipt.Id == interaction.Receipt.Id)
                {

                    classifierAttemptedLock.TrySetResult();

                }
            };

        await using ArcanumDbContext writerContext = _fixture.CreateContext(_dbPath);

        GrimoireRepository writerRepository = CreateRepository(writerContext);

        Task<MandatoryToolInteractionAppendResult> writer = writerRepository
            .AppendMandatoryToolInteractionAsync(
                interaction,
                CancellationToken.None);

        await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        Task<Exception?> classification = Record.ExceptionAsync(
            () => cancelledRepository.AppendMandatoryToolInteractionAsync(
                interaction,
                cancellation.Token));

        await classifierAttemptedLock.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {

            Task first = await Task.WhenAny(
                classification,
                Task.Delay(TimeSpan.FromMilliseconds(150)));

            Assert.NotSame(classification, first);

        }
        finally
        {

            releaseWriter.TrySetResult();

        }

        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.NewlyCommitted,
            (await writer).Outcome);

        OperationCanceledException exception =
            Assert.IsAssignableFrom<OperationCanceledException>(
                await classification);

        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.RecoveredCommitted,
            exception.Data[nameof(MandatoryToolInteractionAppendOutcome)]);

        Assert.Equal(2, await CountReceiptRowsAsync(_db!, interaction.Receipt));

    }

    [SkippableFact]
    public async Task Cancellation_classification_lock_timeout_is_ambiguous()
    {

        SkipUnavailable();

        GrimoireRepository repository = CreateRepository(_db!);

        Guid sessionId = await CreateSessionAsync(repository, "classification timeout");

        MandatoryToolInteraction interaction = CreateInteraction(
            sessionId,
            round: 0,
            call: 0,
            DateTimeOffset.UtcNow.AddMinutes(1));

        using IDisposable heldLock =
            await SessionEntryPersistence.AcquireWriteLockAsync(
                sessionId,
                CancellationToken.None);

        SessionEntryPersistence.MandatoryCancellationClassificationTimeoutForTests =
            TimeSpan.FromMilliseconds(100);

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => repository.AppendMandatoryToolInteractionAsync(
                    interaction,
                    cancellation.Token));

        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.Ambiguous,
            exception.Data[nameof(MandatoryToolInteractionAppendOutcome)]);

        Assert.Equal(0, await CountReceiptRowsAsync(_db!, interaction.Receipt));

    }

    [SkippableFact]
    public async Task Partial_receipt_is_ambiguous()
    {

        SkipUnavailable();

        GrimoireRepository repository = CreateRepository(_db!);

        Guid sessionId = await CreateSessionAsync(repository, "partial receipt");

        MandatoryToolInteraction interaction = CreateInteraction(
            sessionId,
            round: 0,
            call: 0,
            DateTimeOffset.UtcNow.AddMinutes(1));

        _db!.Entries.Add(BuildCallEntry(interaction, sequence: 3));

        await _db.SaveChangesAsync(CancellationToken.None);

        MandatoryToolInteractionAppendResult result =
            await repository.AppendMandatoryToolInteractionAsync(
                interaction,
                CancellationToken.None);

        Assert.Equal(MandatoryToolInteractionAppendOutcome.Ambiguous, result.Outcome);

        _db.ChangeTracker.Clear();

        Assert.Equal(1, await CountReceiptRowsAsync(_db, interaction.Receipt));

        Assert.Equal(
            2,
            (await LoadSessionAsync(_db, sessionId)).UnsummarizedEntryCount);

    }

    [SkippableFact]
    public async Task Mismatched_receipt_is_ambiguous()
    {

        SkipUnavailable();

        GrimoireRepository repository = CreateRepository(_db!);

        Guid sessionId = await CreateSessionAsync(repository, "mismatched receipt");

        MandatoryToolInteraction interaction = CreateInteraction(
            sessionId,
            round: 0,
            call: 0,
            DateTimeOffset.UtcNow.AddMinutes(1));

        Entry mismatchedCall = BuildCallEntry(interaction, sequence: 3);

        mismatchedCall.Content = "[ToolCall: apply_patch({\"patch\":\"different\"})]";

        _db!.Entries.Add(mismatchedCall);

        _db.Entries.Add(BuildResultEntry(interaction, sequence: 4));

        await _db.SaveChangesAsync(CancellationToken.None);

        MandatoryToolInteractionAppendResult result =
            await repository.AppendMandatoryToolInteractionAsync(
                interaction,
                CancellationToken.None);

        Assert.Equal(MandatoryToolInteractionAppendOutcome.Ambiguous, result.Outcome);

        _db.ChangeTracker.Clear();

        Assert.Equal(2, await CountReceiptRowsAsync(_db, interaction.Receipt));

        Assert.Equal(
            2,
            (await LoadSessionAsync(_db, sessionId)).UnsummarizedEntryCount);

    }

    [SkippableFact]
    public async Task Definitive_rollback_returns_failed_without_rows()
    {

        SkipUnavailable();

        GrimoireRepository repository = CreateRepository(_db!);

        MandatoryToolInteraction interaction = CreateInteraction(
            Guid.NewGuid(),
            round: 0,
            call: 0,
            DateTimeOffset.UtcNow);

        MandatoryToolInteractionAppendResult result =
            await repository.AppendMandatoryToolInteractionAsync(
                interaction,
                CancellationToken.None);

        Assert.Equal(MandatoryToolInteractionAppendOutcome.Failed, result.Outcome);

        Assert.Equal(0, await CountReceiptRowsAsync(_db!, interaction.Receipt));

    }

    [SkippableFact]
    public async Task Apply_patch_production_sink_persists_exact_new_and_recovered_receipts()
    {

        SkipUnavailable();
        GrimoireRepository repository = CreateRepository(_db!);
        (Guid sessionId, Guid assistantEntryId) =
            await repository.BeginAssistantReplyAsync(
                sessionId: null,
                prompt: "patch integration",
                model: "test-model",
                cancellationToken: CancellationToken.None);
        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        workspace.WriteFile("target.txt", "before\n");
        ApplyPatchParams request = ModifyRequest("target.txt", "before", "after");
        string exactArguments =
            "{\"dryRun\":false,\"patch\":"
            + JsonSerializer.Serialize(request.Patch)
            + "}";
        SessionEventHub hub = CreateSessionEventHub();
        GrimoireTurnWriter writer =
            CreateWriter(
                repository,
                logger: null,
                hub: hub);
        using CancellationTokenSource eventTimeout =
            new(TimeSpan.FromSeconds(5));
        Task<Entry[]> firstPublished = ReadEntriesAsync(
            hub,
            sessionId,
            count: 2,
            eventTimeout.Token);
        ApplyPatchInvocationContext context = PatchContext(
            sessionId,
            assistantEntryId,
            exactArguments,
            new WriterPendingReceiptSink(writer));
        ApplyPatchToolExecutionService executor = CreatePatchExecutor(workspace.Root);

        ApplyPatchToolExecutionResponse first = await executor.ExecuteAsync(
            request,
            context,
            CancellationToken.None);
        Entry[] firstEvents = await firstPublished;

        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.NewlyCommitted,
            context.HandoffOutcome);
        Assert.True(context.ReceiptHandled);
        Assert.Equal("after\n", await File.ReadAllTextAsync(
            Path.Combine(workspace.Root, "target.txt")));
        Assert.Empty(Directory.GetFiles(
            workspace.Root,
            "*.arcanum-*",
            SearchOption.AllDirectories));

        ToolInteractionReceipt receipt =
            ToolInteractionReceiptDerivation.Derive(context.Identity);
        Assert.Equal(
            [receipt.CallEntryId, receipt.ResultEntryId],
            firstEvents.Select(static entry => entry.Id));
        _db!.ChangeTracker.Clear();
        Entry call = await _db.Entries
            .AsNoTracking()
            .SingleAsync(entry => entry.Id == receipt.CallEntryId);
        Entry result = await _db.Entries
            .AsNoTracking()
            .SingleAsync(entry => entry.Id == receipt.ResultEntryId);
        Assert.Equal(exactArguments, call.ToolArguments);
        Assert.Equal(
            $"[ToolCall: apply_patch({exactArguments})]",
            call.Content);
        Assert.Equal($"[ToolResult: {first.SerializedResult}]", result.Content);

        workspace.WriteFile("target.txt", "before\n");
        Task<Entry[]> recoveredPublished = ReadEntriesAsync(
            hub,
            sessionId,
            count: 2,
            eventTimeout.Token);
        ApplyPatchToolExecutionResponse recovered = await executor.ExecuteAsync(
            request,
            context,
            CancellationToken.None);
        Entry[] recoveredEvents = await recoveredPublished;

        Assert.Equal(first.SerializedResult, recovered.SerializedResult);
        Assert.Equal("before\n", await File.ReadAllTextAsync(
            Path.Combine(workspace.Root, "target.txt")));
        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.RecoveredCommitted,
            context.HandoffOutcome);
        Assert.Equal(
            [receipt.CallEntryId, receipt.ResultEntryId],
            recoveredEvents.Select(static entry => entry.Id));
        _db.ChangeTracker.Clear();
        Assert.Equal(2, await CountReceiptRowsAsync(_db, receipt));

    }

    [SkippableFact]
    public async Task Apply_patch_production_sink_rolls_back_definitive_failure()
    {

        SkipUnavailable();
        GrimoireRepository repository = CreateRepository(_db!);
        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        workspace.WriteFile("failed.txt", "before\n");
        ApplyPatchParams request = ModifyRequest("failed.txt", "before", "after");
        Guid sessionId = Guid.NewGuid();
        SessionEventHub hub = CreateSessionEventHub();
        ApplyPatchInvocationContext context = PatchContext(
            sessionId,
            Guid.NewGuid(),
            "{\"patch\":\"failed\"}",
            new WriterPendingReceiptSink(
                CreateWriter(
                    repository,
                    logger: null,
                    hub: hub)));

        ApplyPatchToolExecutionResponse response =
            await ExecuteWithoutPublishedEntriesAsync(
                hub,
                sessionId,
                () => CreatePatchExecutor(workspace.Root)
                    .ExecuteAsync(
                        request,
                        context,
                        CancellationToken.None));

        using JsonDocument payload = JsonDocument.Parse(response.SerializedResult);
        Assert.Equal("conflict", payload.RootElement.GetProperty("status").GetString());
        Assert.Equal("receipt_capacity", payload.RootElement.GetProperty("code").GetString());
        Assert.Null(context.HandoffOutcome);
        Assert.Equal("before\n", await File.ReadAllTextAsync(
            Path.Combine(workspace.Root, "failed.txt")));
        Assert.Empty(Directory.GetFiles(
            workspace.Root,
            "*.arcanum-*",
            SearchOption.AllDirectories));

    }

    [SkippableFact]
    public async Task Apply_patch_preflights_exact_entry_size_before_filesystem_mutation()
    {

        SkipUnavailable();

        GrimoireRepository repository = CreateRepository(_db!);

        (Guid sessionId, Guid assistantEntryId) =
            await repository.BeginAssistantReplyAsync(
                sessionId: null,
                prompt: "capacity preflight",
                model: "test-model",
                cancellationToken: CancellationToken.None);

        await using TempWorkspace workspace = new();

        await workspace.InitializeAsync();

        workspace.WriteFile("capacity.txt", "before\n");

        int maxEntryBytes = ArcanumSettingClamps.MaxEntryContentBytes(
            ArcanumRuntimeDefaults.Sessions.MaxEntryContentBytes);
        ApplyPatchInvocationContext context = PatchContext(
            sessionId,
            assistantEntryId,
            new string('x', maxEntryBytes + 100),
            CreateWriterSink(repository));

        ApplyPatchToolExecutionResponse response =
            await CreatePatchExecutor(workspace.Root).ExecuteAsync(
                ModifyRequest("capacity.txt", "before", "after"),
                context,
                CancellationToken.None);

        using JsonDocument payload = JsonDocument.Parse(
            response.SerializedResult);

        Assert.Equal(
            "receipt_capacity",
            payload.RootElement.GetProperty("code").GetString());

        Assert.Equal(
            "before\n",
            await File.ReadAllTextAsync(
                Path.Combine(workspace.Root, "capacity.txt")));

        ToolInteractionReceipt receipt =
            ToolInteractionReceiptDerivation.Derive(context.Identity);

        _db!.ChangeTracker.Clear();

        Assert.Equal(0, await CountReceiptRowsAsync(_db, receipt));

    }

    [SkippableFact]
    public async Task Apply_patch_production_sink_rejects_partial_receipt_before_mutation()
    {

        SkipUnavailable();
        GrimoireRepository repository = CreateRepository(_db!);
        (Guid sessionId, Guid assistantEntryId) =
            await repository.BeginAssistantReplyAsync(
                sessionId: null,
                prompt: "ambiguous patch",
                model: "test-model",
                cancellationToken: CancellationToken.None);
        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        workspace.WriteFile("ambiguous.txt", "before\n");
        const string exactArguments = "{\"patch\":\"ambiguous\"}";
        CapturingGrimoireWriterLogger writerLogger = new();
        SessionEventHub hub = CreateSessionEventHub();
        GrimoireTurnWriter writer = CreateWriter(
            repository,
            writerLogger,
            hub);
        ApplyPatchInvocationContext context = PatchContext(
            sessionId,
            assistantEntryId,
            exactArguments,
            new WriterPendingReceiptSink(writer));
        MandatoryToolInteraction partial = new(
            sessionId,
            ToolInteractionReceiptDerivation.Derive(context.Identity),
            context.Identity.ProviderToolCallId,
            context.Identity.ToolName,
            exactArguments,
            "{\"status\":\"seed\"}",
            context.ModelUsed,
            context.CreatedAt);
        _db!.Entries.Add(BuildCallEntry(partial, sequence: 3));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        ApplyPatchToolExecutionResponse response =
            await ExecuteWithoutPublishedEntriesAsync(
                hub,
                sessionId,
                () => CreatePatchExecutor(workspace.Root)
                    .ExecuteAsync(
                        ModifyRequest(
                            "ambiguous.txt",
                            "before",
                            "after"),
                        context,
                        CancellationToken.None));

        using JsonDocument payload = JsonDocument.Parse(
            response.SerializedResult);

        Assert.Equal(
            "receipt_mismatch",
            payload.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(
            writerLogger.Messages,
            message => message.Contains(
                workspace.Root,
                StringComparison.Ordinal));
        Assert.Equal("before\n", await File.ReadAllTextAsync(
            Path.Combine(workspace.Root, "ambiguous.txt")));
        Assert.Empty(Directory.GetFiles(
            workspace.Root,
            "*.arcanum-*",
            SearchOption.AllDirectories));

    }

    [SkippableFact]
    public async Task Apply_patch_rollback_incomplete_persists_exact_recovery_receipt_and_replays()
    {

        SkipUnavailable();
        GrimoireRepository repository = CreateRepository(_db!);
        (Guid sessionId, Guid assistantEntryId) =
            await repository.BeginAssistantReplyAsync(
                sessionId: null,
                prompt: "rollback incomplete",
                model: "test-model",
                cancellationToken: CancellationToken.None);
        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        workspace.WriteFile("first.txt", "one\n");
        workspace.WriteFile("second.txt", "two\n");
        ApplyPatchParams request = new(
            """
            --- a/first.txt
            +++ b/first.txt
            @@ -1 +1 @@
            -one
            +ONE
            --- a/second.txt
            +++ b/second.txt
            @@ -1 +1 @@
            -two
            +TWO
            """,
            DryRun: false);
        const string exactArguments =
            "{\"dryRun\":false,\"patch\":\"rollback-incomplete\"}";
        SessionEventHub hub = CreateSessionEventHub();
        GrimoireTurnWriter writer = CreateWriter(
            repository,
            logger: null,
            hub: hub);
        ApplyPatchInvocationContext context = PatchContext(
            sessionId,
            assistantEntryId,
            exactArguments,
            new WriterPendingReceiptSink(writer));
        int coordinatorCreations = 0;
        ApplyPatchToolExecutionService executor = CreatePatchExecutor(
            workspace.Root,
            root =>
            {
                coordinatorCreations++;
                return new MultiFileCommitCoordinator(
                    root,
                    new MultiFileCommitCoordinatorOptions
                    {
                        AfterCommitStepAsync = (step, _) =>
                        {
                            if (step.Index == 0)
                            {
                                File.WriteAllText(
                                    Path.Combine(root, "first.txt"),
                                    "external\n");
                            }

                            return ValueTask.CompletedTask;
                        },
                        BeforeCommitStepAsync = (step, _) =>
                            step.Index == 1
                                ? ValueTask.FromException(
                                    new IOException("injected failure"))
                                : ValueTask.CompletedTask,
                    });
            });
        using CancellationTokenSource eventTimeout =
            new(TimeSpan.FromSeconds(5));
        Task<Entry[]> firstPublished = ReadEntriesAsync(
            hub,
            sessionId,
            count: 2,
            eventTimeout.Token);

        ApplyPatchToolExecutionResponse first = await executor.ExecuteAsync(
            request,
            context,
            CancellationToken.None);

        ToolInteractionReceipt receipt =
            ToolInteractionReceiptDerivation.Derive(context.Identity);
        _db!.ChangeTracker.Clear();
        Assert.Equal(2, await CountReceiptRowsAsync(_db, receipt));
        Entry[] firstEvents = await firstPublished;
        Entry call = await _db.Entries
            .AsNoTracking()
            .SingleAsync(entry => entry.Id == receipt.CallEntryId);
        Entry result = await _db.Entries
            .AsNoTracking()
            .SingleAsync(entry => entry.Id == receipt.ResultEntryId);

        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.NewlyCommitted,
            context.HandoffOutcome);
        Assert.True(context.ReceiptHandled);
        Assert.Equal(exactArguments, call.ToolArguments);
        Assert.Equal(
            $"[ToolCall: apply_patch({exactArguments})]",
            call.Content);
        Assert.Equal($"[ToolResult: {first.SerializedResult}]", result.Content);
        Assert.Equal(call.Content, firstEvents[0].Content);
        Assert.Equal(result.Content, firstEvents[1].Content);
        using (JsonDocument payload = JsonDocument.Parse(first.SerializedResult))
        {
            Assert.Equal(
                "rollback_incomplete",
                payload.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                "rollback_incomplete",
                payload.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                ["first.txt"],
                payload.RootElement.GetProperty("affectedPaths")
                    .EnumerateArray()
                    .Select(static path => path.GetString()!)
                    .ToArray());
            Assert.All(
                payload.RootElement.GetProperty("recoveryArtifactPaths")
                    .EnumerateArray()
                    .Select(static path => path.GetString()!),
                AssertRelativeRecoveryPath);
        }

        Assert.Equal(
            "external\n",
            await File.ReadAllTextAsync(
                Path.Combine(workspace.Root, "first.txt")));
        Assert.Equal(
            "two\n",
            await File.ReadAllTextAsync(
                Path.Combine(workspace.Root, "second.txt")));

        ApplyPatchInvocationContext retry = PatchContext(
            sessionId,
            assistantEntryId,
            exactArguments,
            new WriterPendingReceiptSink(writer));
        Task<Entry[]> replayPublished = ReadEntriesAsync(
            hub,
            sessionId,
            count: 2,
            eventTimeout.Token);

        ApplyPatchToolExecutionResponse replay = await executor.ExecuteAsync(
            request,
            retry,
            CancellationToken.None);
        Entry[] replayEvents = await replayPublished;

        Assert.Equal(first.SerializedResult, replay.SerializedResult);
        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.RecoveredCommitted,
            retry.HandoffOutcome);
        Assert.True(retry.ReceiptHandled);
        Assert.Equal(1, coordinatorCreations);
        Assert.Equal(
            [receipt.CallEntryId, receipt.ResultEntryId],
            replayEvents.Select(static entry => entry.Id));
        Assert.Equal(call.Content, replayEvents[0].Content);
        Assert.Equal(result.Content, replayEvents[1].Content);
        _db.ChangeTracker.Clear();
        Assert.Equal(2, await CountReceiptRowsAsync(_db, receipt));

    }

    [SkippableFact]
    public async Task Apply_patch_cancellation_persists_exact_recovery_receipt_before_propagating()
    {

        SkipUnavailable();
        GrimoireRepository repository = CreateRepository(_db!);
        (Guid sessionId, Guid assistantEntryId) =
            await repository.BeginAssistantReplyAsync(
                sessionId: null,
                prompt: "cancel rollback incomplete",
                model: "test-model",
                cancellationToken: CancellationToken.None);
        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        workspace.WriteFile("cancel-incomplete.txt", "before\n");
        ApplyPatchParams request = ModifyRequest(
            "cancel-incomplete.txt",
            "before",
            "after");
        const string exactArguments =
            "{\"dryRun\":false,\"patch\":\"cancel-rollback-incomplete\"}";
        SessionEventHub hub = CreateSessionEventHub();
        GrimoireTurnWriter writer = CreateWriter(
            repository,
            logger: null,
            hub: hub);
        ApplyPatchInvocationContext context = PatchContext(
            sessionId,
            assistantEntryId,
            exactArguments,
            new WriterPendingReceiptSink(writer));
        using CancellationTokenSource callerCancellation = new();
        int coordinatorCreations = 0;
        ApplyPatchToolExecutionService executor = CreatePatchExecutor(
            workspace.Root,
            root =>
            {
                coordinatorCreations++;
                return new MultiFileCommitCoordinator(
                    root,
                    new MultiFileCommitCoordinatorOptions
                    {
                        AfterDestinationMutation = _ =>
                        {
                            File.WriteAllText(
                                Path.Combine(
                                    root,
                                    "cancel-incomplete.txt"),
                                "external\n");
                            callerCancellation.Cancel();
                        },
                    });
            });
        using CancellationTokenSource eventTimeout =
            new(TimeSpan.FromSeconds(5));
        Task<Entry[]> firstPublished = ReadEntriesAsync(
            hub,
            sessionId,
            count: 2,
            eventTimeout.Token);

        OperationCanceledException cancellation =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => executor.ExecuteAsync(
                    request,
                    context,
                    callerCancellation.Token));

        ToolInteractionReceipt receipt =
            ToolInteractionReceiptDerivation.Derive(context.Identity);
        _db!.ChangeTracker.Clear();
        Assert.Equal(2, await CountReceiptRowsAsync(_db, receipt));
        Entry[] firstEvents = await firstPublished;
        Entry call = await _db.Entries
            .AsNoTracking()
            .SingleAsync(entry => entry.Id == receipt.CallEntryId);
        Entry result = await _db.Entries
            .AsNoTracking()
            .SingleAsync(entry => entry.Id == receipt.ResultEntryId);
        WorkspaceCommitRecovery recovery =
            Assert.IsType<WorkspaceCommitRecovery>(
                cancellation.Data[nameof(WorkspaceCommitRecovery)]);
        const string resultPrefix = "[ToolResult: ";
        Assert.StartsWith(resultPrefix, result.Content, StringComparison.Ordinal);
        Assert.EndsWith("]", result.Content, StringComparison.Ordinal);
        string exactResult = result.Content[resultPrefix.Length..^1];

        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.NewlyCommitted,
            context.HandoffOutcome);
        Assert.True(context.ReceiptHandled);
        Assert.True(context.CancellationClassified);
        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.NewlyCommitted,
            cancellation.Data[
                nameof(MandatoryToolInteractionAppendOutcome)]);
        Assert.Equal(exactArguments, call.ToolArguments);
        Assert.Equal(
            $"[ToolCall: apply_patch({exactArguments})]",
            call.Content);
        Assert.Equal(call.Content, firstEvents[0].Content);
        Assert.Equal(result.Content, firstEvents[1].Content);
        using (JsonDocument payload = JsonDocument.Parse(exactResult))
        {
            Assert.Equal(
                "rollback_incomplete",
                payload.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                "rollback_incomplete",
                payload.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                recovery.AffectedPaths,
                payload.RootElement.GetProperty("affectedPaths")
                    .EnumerateArray()
                    .Select(static path => path.GetString()!)
                    .ToArray());
            Assert.Equal(
                recovery.ArtifactPaths,
                payload.RootElement.GetProperty("recoveryArtifactPaths")
                    .EnumerateArray()
                    .Select(static path => path.GetString()!)
                    .ToArray());
            Assert.All(recovery.AffectedPaths, AssertRelativeRecoveryPath);
            Assert.All(recovery.ArtifactPaths, AssertRelativeRecoveryPath);
        }

        Assert.Equal(
            "external\n",
            await File.ReadAllTextAsync(
                Path.Combine(workspace.Root, "cancel-incomplete.txt")));

        ApplyPatchInvocationContext retry = PatchContext(
            sessionId,
            assistantEntryId,
            exactArguments,
            new WriterPendingReceiptSink(writer));
        Task<Entry[]> replayPublished = ReadEntriesAsync(
            hub,
            sessionId,
            count: 2,
            eventTimeout.Token);

        ApplyPatchToolExecutionResponse replay = await executor.ExecuteAsync(
            request,
            retry,
            CancellationToken.None);
        Entry[] replayEvents = await replayPublished;

        Assert.Equal(exactResult, replay.SerializedResult);
        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.RecoveredCommitted,
            retry.HandoffOutcome);
        Assert.True(retry.ReceiptHandled);
        Assert.Equal(1, coordinatorCreations);
        Assert.Equal(
            [receipt.CallEntryId, receipt.ResultEntryId],
            replayEvents.Select(static entry => entry.Id));
        Assert.Equal(call.Content, replayEvents[0].Content);
        Assert.Equal(result.Content, replayEvents[1].Content);
        _db.ChangeTracker.Clear();
        Assert.Equal(2, await CountReceiptRowsAsync(_db, receipt));

    }

    [SkippableFact]
    public async Task Apply_patch_ambiguous_recovery_receipt_retains_artifacts_and_fails()
    {

        SkipUnavailable();
        GrimoireRepository repository = CreateRepository(_db!);
        (Guid sessionId, Guid assistantEntryId) =
            await repository.BeginAssistantReplyAsync(
                sessionId: null,
                prompt: "ambiguous recovery receipt",
                model: "test-model",
                cancellationToken: CancellationToken.None);
        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        workspace.WriteFile("first.txt", "one\n");
        workspace.WriteFile("second.txt", "two\n");
        const string exactArguments =
            "{\"dryRun\":false,\"patch\":\"ambiguous-recovery\"}";
        SessionEventHub hub = CreateSessionEventHub();
        ApplyPatchInvocationContext context = PatchContext(
            sessionId,
            assistantEntryId,
            exactArguments,
            new WriterPendingReceiptSink(
                CreateWriter(
                    repository,
                    logger: null,
                    hub: hub)));
        ToolInteractionReceipt receipt =
            ToolInteractionReceiptDerivation.Derive(context.Identity);
        MandatoryToolInteraction partial = new(
            sessionId,
            receipt,
            context.Identity.ProviderToolCallId,
            context.Identity.ToolName,
            exactArguments,
            "{\"status\":\"partial\"}",
            context.ModelUsed,
            context.CreatedAt);
        ApplyPatchToolExecutionService executor = CreatePatchExecutor(
            workspace.Root,
            root =>
                new MultiFileCommitCoordinator(
                    root,
                    new MultiFileCommitCoordinatorOptions
                    {
                        AfterCommitStepAsync = (step, _) =>
                        {
                            if (step.Index == 0)
                            {
                                File.WriteAllText(
                                    Path.Combine(root, "first.txt"),
                                    "external\n");
                            }

                            return ValueTask.CompletedTask;
                        },
                        BeforeCommitStepAsync = (step, _) =>
                        {
                            if (step.Index != 1)
                            {
                                return ValueTask.CompletedTask;
                            }

                            _db!.Entries.Add(BuildCallEntry(partial, sequence: 3));
                            _db.SaveChanges();
                            return ValueTask.FromException(
                                new IOException("injected failure"));
                        },
                    }));
        ApplyPatchParams request = new(
            """
            --- a/first.txt
            +++ b/first.txt
            @@ -1 +1 @@
            -one
            +ONE
            --- a/second.txt
            +++ b/second.txt
            @@ -1 +1 @@
            -two
            +TWO
            """,
            DryRun: false);

        ApplyPatchReceiptHandoffException exception =
            await ExecuteWithoutPublishedEntriesAsync(
                hub,
                sessionId,
                () => Assert.ThrowsAsync<ApplyPatchReceiptHandoffException>(
                    () => executor.ExecuteAsync(
                        request,
                        context,
                        CancellationToken.None)));

        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.Ambiguous,
            exception.Outcome);
        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.Ambiguous,
            context.HandoffOutcome);
        Assert.True(context.RequiresTurnFailure);
        Assert.False(context.ReceiptHandled);
        Assert.All(exception.AffectedPaths, AssertRelativeRecoveryPath);
        Assert.All(
            exception.RecoveryArtifactPaths,
            AssertRelativeRecoveryPath);
        Assert.Equal(
            "external\n",
            await File.ReadAllTextAsync(
                Path.Combine(workspace.Root, "first.txt")));
        Assert.NotEmpty(Directory.GetFiles(
            workspace.Root,
            "*.arcanum-*",
            SearchOption.AllDirectories));
        _db!.ChangeTracker.Clear();
        Assert.Equal(1, await CountReceiptRowsAsync(_db, receipt));

    }

    [SkippableFact]
    public async Task Apply_patch_complete_cancellation_rollback_does_not_persist_receipt()
    {

        SkipUnavailable();
        GrimoireRepository repository = CreateRepository(_db!);
        (Guid sessionId, Guid assistantEntryId) =
            await repository.BeginAssistantReplyAsync(
                sessionId: null,
                prompt: "cancel complete rollback",
                model: "test-model",
                cancellationToken: CancellationToken.None);
        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        workspace.WriteFile("cancel-complete.txt", "before\n");
        using CancellationTokenSource callerCancellation = new();
        SessionEventHub hub = CreateSessionEventHub();
        ApplyPatchInvocationContext context = PatchContext(
            sessionId,
            assistantEntryId,
            "{\"patch\":\"cancel-complete\"}",
            new WriterPendingReceiptSink(
                CreateWriter(
                    repository,
                    logger: null,
                    hub: hub)));
        ApplyPatchToolExecutionService executor = CreatePatchExecutor(
            workspace.Root,
            root =>
                new MultiFileCommitCoordinator(
                    root,
                    new MultiFileCommitCoordinatorOptions
                    {
                        AfterDestinationMutation =
                            _ => callerCancellation.Cancel(),
                    }));

        _ = await ExecuteWithoutPublishedEntriesAsync(
            hub,
            sessionId,
            () => Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => executor.ExecuteAsync(
                    ModifyRequest(
                        "cancel-complete.txt",
                        "before",
                        "after"),
                    context,
                    callerCancellation.Token)));

        ToolInteractionReceipt receipt =
            ToolInteractionReceiptDerivation.Derive(context.Identity);
        _db!.ChangeTracker.Clear();
        Assert.Equal(0, await CountReceiptRowsAsync(_db, receipt));
        Assert.Null(context.HandoffOutcome);
        Assert.False(context.ReceiptHandled);
        Assert.Equal(
            "before\n",
            await File.ReadAllTextAsync(
                Path.Combine(workspace.Root, "cancel-complete.txt")));
        Assert.Empty(Directory.GetFiles(
            workspace.Root,
            "*.arcanum-*",
            SearchOption.AllDirectories));

    }

    [SkippableFact]
    public async Task Apply_patch_cancellation_finishes_failed_rollback_before_propagating()
    {

        SkipUnavailable();
        GrimoireRepository repository = CreateRepository(_db!);
        (Guid sessionId, Guid assistantEntryId) =
            await repository.BeginAssistantReplyAsync(
                sessionId: null,
                prompt: "cancel failed",
                model: "test-model",
                cancellationToken: CancellationToken.None);
        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        workspace.WriteFile("cancel-failed.txt", "before\n");
        using CancellationTokenSource cancellation = new();
        SessionEntryPersistence.AfterMandatoryTransactionBeganForTests =
            _ => new OperationCanceledException(cancellation.Token);
        ApplyPatchInvocationContext context = PatchContext(
            sessionId,
            assistantEntryId,
            "{\"patch\":\"cancel-failed\"}",
            CreateWriterSink(repository));

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => CreatePatchExecutor(workspace.Root).ExecuteAsync(
                    ModifyRequest("cancel-failed.txt", "before", "after"),
                    context,
                    cancellation.Token));

        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.Failed,
            exception.Data[nameof(MandatoryToolInteractionAppendOutcome)]);
        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.Failed,
            context.HandoffOutcome);
        Assert.Equal("before\n", await File.ReadAllTextAsync(
            Path.Combine(workspace.Root, "cancel-failed.txt")));
        Assert.Empty(Directory.GetFiles(
            workspace.Root,
            "*.arcanum-*",
            SearchOption.AllDirectories));

    }

    [SkippableFact]
    public async Task Apply_patch_retry_replays_before_cancellation_handoff_or_mutation()
    {

        SkipUnavailable();
        GrimoireRepository repository = CreateRepository(_db!);
        (Guid sessionId, Guid assistantEntryId) =
            await repository.BeginAssistantReplyAsync(
                sessionId: null,
                prompt: "cancel recovered",
                model: "test-model",
                cancellationToken: CancellationToken.None);
        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        workspace.WriteFile("cancel-recovered.txt", "before\n");
        ApplyPatchParams request = ModifyRequest(
            "cancel-recovered.txt",
            "before",
            "after");
        const string exactArguments = "{\"patch\":\"cancel-recovered\"}";
        ApplyPatchInvocationContext initial = PatchContext(
            sessionId,
            assistantEntryId,
            exactArguments,
            CreateWriterSink(repository));
        ApplyPatchToolExecutionService executor =
            CreatePatchExecutor(workspace.Root);
        _ = await executor.ExecuteAsync(
            request,
            initial,
            CancellationToken.None);
        workspace.WriteFile("cancel-recovered.txt", "before\n");
        using CancellationTokenSource cancellation = new();
        ApplyPatchInvocationContext retry = PatchContext(
            sessionId,
            assistantEntryId,
            exactArguments,
            new CancellingWriterPendingReceiptSink(
                CreateWriter(repository),
                cancellation));

        ApplyPatchToolExecutionResponse replay = await executor.ExecuteAsync(
            request,
            retry,
            cancellation.Token);

        Assert.NotEmpty(replay.SerializedResult);

        Assert.False(cancellation.IsCancellationRequested);
        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.RecoveredCommitted,
            retry.HandoffOutcome);
        Assert.Equal("before\n", await File.ReadAllTextAsync(
            Path.Combine(workspace.Root, "cancel-recovered.txt")));
        Assert.Empty(Directory.GetFiles(
            workspace.Root,
            "*.arcanum-*",
            SearchOption.AllDirectories));

    }

    [SkippableFact]
    public async Task Apply_patch_retained_cleanup_artifact_emits_metric_and_warning()
    {

        const string secretMatchText = "MATCH_TEXT_MUST_NOT_REACH_LOGS";
        SkipUnavailable();
        GrimoireRepository repository = CreateRepository(_db!);
        (Guid sessionId, Guid assistantEntryId) =
            await repository.BeginAssistantReplyAsync(
                sessionId: null,
                prompt: "cleanup telemetry",
                model: "test-model",
                cancellationToken: CancellationToken.None);
        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        workspace.WriteFile("cleanup.txt", secretMatchText + "\n");
        CapturingGrimoireWriterLogger logger = new();
        GrimoireTurnWriter writer = CreateWriter(repository, logger);
        ConcurrentQueue<long> retainedMeasurements = new();
        ConcurrentQueue<KeyValuePair<string, object?>[]> cleanupMetricTags =
            new();
        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) =>
                activeListener.EnableMeasurementEvents(instrument),
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, _) =>
            {
                if (instrument.Name
                        == "arcanum_apply_patch_artifact_cleanup_total"
                    && HasMetricTag(tags, "outcome", "retained"))
                {
                    retainedMeasurements.Enqueue(measurement);
                    cleanupMetricTags.Enqueue(tags.ToArray());
                }
            });
        listener.Start();
        ApplyPatchInvocationContext context = PatchContext(
            sessionId,
            assistantEntryId,
            $"{{\"patch\":\"{secretMatchText}\"}}",
            new TamperingWriterPendingReceiptSink(
                writer,
                workspace.Root));

        ApplyPatchToolExecutionResponse response =
            await CreatePatchExecutor(workspace.Root).ExecuteAsync(
                ModifyRequest(
                    "cleanup.txt",
                    secretMatchText,
                    "replacement"),
                context,
                CancellationToken.None);

        Assert.Equal(1, Assert.Single(retainedMeasurements));
        KeyValuePair<string, object?> metricTag =
            Assert.Single(Assert.Single(cleanupMetricTags));
        Assert.Equal("outcome", metricTag.Key);
        Assert.Equal("retained", metricTag.Value);
        Assert.Contains(
            logger.Messages,
            static message =>
                message.Contains(
                    "retained",
                    StringComparison.OrdinalIgnoreCase));
        string retainedArtifact = Assert.Single(Directory.GetFiles(
            workspace.Root,
            "*.arcanum-*",
            SearchOption.AllDirectories));
        string relativeArtifact = Path.GetRelativePath(
                workspace.Root,
                retainedArtifact)
            .Replace('\\', '/');
        Assert.Contains(
            logger.Messages,
            message => message.Contains(
                relativeArtifact,
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains(
                workspace.Root,
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains(
                secretMatchText,
                StringComparison.Ordinal));
        ToolInteractionReceipt receipt =
            ToolInteractionReceiptDerivation.Derive(context.Identity);
        _db!.ChangeTracker.Clear();
        Entry result = await _db.Entries
            .AsNoTracking()
            .SingleAsync(entry => entry.Id == receipt.ResultEntryId);
        Assert.Equal(
            $"[ToolResult: {response.SerializedResult}]",
            result.Content);

    }

    private static ApplyPatchToolExecutionService CreatePatchExecutor(
        string workspaceRoot,
        Func<string, MultiFileCommitCoordinator>? coordinatorFactory = null) =>
        new(
            workspaceRoot,
            new WorkspacePatchSettings(),
            outputBudgetBytes: 1024 * 1024,
            McpJsonSerializerContext.Default,
            coordinatorFactory);

    private static bool HasMetricTag(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        string key,
        string value)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == key && Equals(tag.Value, value))
            {
                return true;
            }
        }

        return false;
    }

    private static ApplyPatchInvocationContext PatchContext(
        Guid sessionId,
        Guid assistantEntryId,
        string serializedArguments,
        IApplyPatchPendingReceiptSink sink) =>
        new(
            sessionId,
            assistantEntryId,
            new ToolInvocationIdentity(
                InvocationId: "integration-turn",
                ProviderToolCallId: "provider-call",
                ToolRoundOrdinal: 0,
                CallOrdinal: 0,
                ToolName: "apply_patch"),
            serializedArguments,
            ModelUsed: "test-model",
            CreatedAt: DateTimeOffset.Parse(
                "2026-07-26T12:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture),
            sink);

    private static IApplyPatchPendingReceiptSink CreateWriterSink(
        GrimoireRepository repository) =>
        new WriterPendingReceiptSink(CreateWriter(repository));

    private static GrimoireTurnWriter CreateWriter(
        GrimoireRepository repository,
        ILogger<GrimoireTurnWriter>? logger = null,
        SessionEventHub? hub = null)
    {
        return new GrimoireTurnWriter(
            repository,
            hub ?? CreateSessionEventHub(),
            logger ?? NullLogger<GrimoireTurnWriter>.Instance);

    }

    private static SessionEventHub CreateSessionEventHub() =>
        new(NullLogger<SessionEventHub>.Instance);

    private static async Task<Entry[]> ReadEntriesAsync(
        SessionEventHub hub,
        Guid sessionId,
        int count,
        CancellationToken cancellationToken)
    {
        List<Entry> entries = [];

        await foreach (Entry entry in hub
                           .SubscribeAsync(
                               sessionId,
                               cancellationToken))
        {
            entries.Add(entry);

            if (entries.Count == count)
            {
                break;
            }
        }

        return entries.ToArray();
    }

    private static async Task<T> ExecuteWithoutPublishedEntriesAsync<T>(
        SessionEventHub hub,
        Guid sessionId,
        Func<Task<T>> action)
    {
        using CancellationTokenSource cancellation = new();
        await using IAsyncEnumerator<Entry> subscription =
            hub.SubscribeAsync(
                    sessionId,
                    cancellation.Token)
                .GetAsyncEnumerator(
                    cancellation.Token);
        Task<bool> pending =
            subscription.MoveNextAsync().AsTask();

        T result = await action();

        Assert.False(
            pending.IsCompleted,
            "A failed or ambiguous mandatory receipt published a session event.");
        cancellation.Cancel();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => _ = await pending);
        return result;
    }

    private static ApplyPatchParams ModifyRequest(
        string path,
        string oldText,
        string newText) =>
        new(
            $"""
             --- a/{path}
             +++ b/{path}
             @@ -1 +1 @@
             -{oldText}
             +{newText}
             """,
            DryRun: false);

    private static MandatoryToolInteraction CreateInteraction(
        Guid sessionId,
        int round,
        int call,
        DateTimeOffset timestamp)
    {

        ToolInteractionReceipt receipt = ToolInteractionReceiptDerivation.Derive(
            new ToolInvocationIdentity(
                InvocationId: "turn-1",
                ProviderToolCallId: "provider-call",
                ToolRoundOrdinal: round,
                CallOrdinal: call,
                ToolName: "apply_patch"));

        return new MandatoryToolInteraction(
            SessionId: sessionId,
            Receipt: receipt,
            ToolCallId: "provider-call",
            ToolName: "apply_patch",
            Arguments: "{\"patch\":\"bounded\"}",
            Result: "{\"status\":\"ok\"}",
            ModelUsed: "test-model",
            CreatedAt: timestamp);

    }

    /// <summary>
    /// Mirrors the production builder. <paramref name="sequence"/> is only needed when the entry is
    /// seeded directly, because direct seeding bypasses the repository's per-session allocation and
    /// the unique <c>(SessionId, Sequence)</c> index rejects duplicates.
    /// </summary>
    private static Entry BuildCallEntry(MandatoryToolInteraction interaction, long sequence = 0L) =>
        new()
        {
            Id = interaction.Receipt.CallEntryId,
            SessionId = interaction.SessionId,
            Role = MessageRole.Assistant,
            Content = $"[ToolCall: {interaction.ToolName}({interaction.Arguments})]",
            ModelUsed = interaction.ModelUsed,
            CreatedAt = interaction.CreatedAt,
            Sequence = sequence,
            ToolCallId = interaction.ToolCallId,
            ToolName = interaction.ToolName,
            ToolArguments = interaction.Arguments,
        };

    private static Entry BuildResultEntry(MandatoryToolInteraction interaction, long sequence = 0L) =>
        new()
        {
            Id = interaction.Receipt.ResultEntryId,
            SessionId = interaction.SessionId,
            Role = MessageRole.System,
            Content = $"[ToolResult: {interaction.Result}]",
            ModelUsed = interaction.ModelUsed,
            CreatedAt = interaction.CreatedAt,
            Sequence = sequence,
        };

    private static async Task<Guid> CreateSessionAsync(
        GrimoireRepository repository,
        string prompt)
    {

        (Guid sessionId, _) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt,
            model: "test-model",
            cancellationToken: CancellationToken.None);

        return sessionId;

    }

    private static Task<int> CountReceiptRowsAsync(
        ArcanumDbContext db,
        ToolInteractionReceipt receipt) =>
        db.Entries
            .AsNoTracking()
            .CountAsync(
                entry =>
                    entry.Id == receipt.CallEntryId
                    || entry.Id == receipt.ResultEntryId,
                CancellationToken.None);

    private static void AssertRelativeRecoveryPath(string path)
    {

        Assert.False(Path.IsPathRooted(path));
        Assert.DoesNotContain("..", path, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', path);

    }

    private static Task<Session> LoadSessionAsync(
        ArcanumDbContext db,
        Guid sessionId) =>
        db.Sessions
            .AsNoTracking()
            .SingleAsync(
                session => session.Id == sessionId,
                CancellationToken.None);

    private static GrimoireRepository CreateRepository(ArcanumDbContext db)
    {
        return new GrimoireRepository(
            db,
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()));

    }

    private static void SkipUnavailable() =>
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

    private sealed class WriterPendingReceiptSink(
        GrimoireTurnWriter writer) : IApplyPatchPendingReceiptSink
    {
        public ValueTask<ApplyPatchReceiptProbeResult> ProbeAsync(
            ApplyPatchReceiptProbe probe,
            CancellationToken cancellationToken) =>
            writer.ProbeApplyPatchReceiptAsync(probe, cancellationToken);

        public ValueTask<ApplyPatchReceiptPreflightResult> PreflightAsync(
            ApplyPatchReceiptPreflight preflight,
            CancellationToken cancellationToken) =>
            writer.PreflightApplyPatchReceiptAsync(preflight, cancellationToken);

        public ValueTask<MandatoryToolInteractionAppendOutcome>
            PersistRecoveryReceiptAsync(
                ApplyPatchRecoveryReceipt receipt,
                CancellationToken cancellationToken) =>
            writer.PersistApplyPatchRecoveryReceiptAsync(
                receipt,
                cancellationToken);

        public ValueTask<ApplyPatchPendingReceiptHandoffResult> HandoffAsync(
            PendingApplyPatchReceipt receipt,
            CancellationToken cancellationToken) =>
            writer.HandlePendingApplyPatchReceiptAsync(
                receipt,
                cancellationToken);
    }

    private sealed class CancellingWriterPendingReceiptSink(
        GrimoireTurnWriter writer,
        CancellationTokenSource cancellation)
        : IApplyPatchPendingReceiptSink
    {
        public ValueTask<ApplyPatchReceiptProbeResult> ProbeAsync(
            ApplyPatchReceiptProbe probe,
            CancellationToken cancellationToken) =>
            writer.ProbeApplyPatchReceiptAsync(probe, cancellationToken);

        public ValueTask<ApplyPatchReceiptPreflightResult> PreflightAsync(
            ApplyPatchReceiptPreflight preflight,
            CancellationToken cancellationToken) =>
            writer.PreflightApplyPatchReceiptAsync(preflight, cancellationToken);

        public ValueTask<MandatoryToolInteractionAppendOutcome>
            PersistRecoveryReceiptAsync(
                ApplyPatchRecoveryReceipt receipt,
                CancellationToken cancellationToken) =>
            writer.PersistApplyPatchRecoveryReceiptAsync(
                receipt,
                cancellationToken);

        public ValueTask<ApplyPatchPendingReceiptHandoffResult> HandoffAsync(
            PendingApplyPatchReceipt receipt,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();

            return writer.HandlePendingApplyPatchReceiptAsync(
                receipt,
                cancellationToken);
        }
    }

    private sealed class TamperingWriterPendingReceiptSink(
        GrimoireTurnWriter writer,
        string workspaceRoot)
        : IApplyPatchPendingReceiptSink
    {
        public ValueTask<ApplyPatchReceiptProbeResult> ProbeAsync(
            ApplyPatchReceiptProbe probe,
            CancellationToken cancellationToken) =>
            writer.ProbeApplyPatchReceiptAsync(probe, cancellationToken);

        public ValueTask<ApplyPatchReceiptPreflightResult> PreflightAsync(
            ApplyPatchReceiptPreflight preflight,
            CancellationToken cancellationToken) =>
            writer.PreflightApplyPatchReceiptAsync(preflight, cancellationToken);

        public ValueTask<MandatoryToolInteractionAppendOutcome>
            PersistRecoveryReceiptAsync(
                ApplyPatchRecoveryReceipt receipt,
                CancellationToken cancellationToken) =>
            writer.PersistApplyPatchRecoveryReceiptAsync(
                receipt,
                cancellationToken);

        public ValueTask<ApplyPatchPendingReceiptHandoffResult> HandoffAsync(
            PendingApplyPatchReceipt receipt,
            CancellationToken cancellationToken)
        {
            string artifact = Assert.Single(receipt.Recovery!.ArtifactPaths);
            File.AppendAllText(
                Path.Combine(
                    workspaceRoot,
                    artifact.Replace('/', Path.DirectorySeparatorChar)),
                "tampered");

            return writer.HandlePendingApplyPatchReceiptAsync(
                receipt,
                cancellationToken);
        }
    }

    private sealed class CapturingGrimoireWriterLogger
        : ILogger<GrimoireTurnWriter>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        internal IReadOnlyCollection<string> Messages => _messages.ToArray();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _messages.Enqueue(formatter(state, exception));
    }

}
