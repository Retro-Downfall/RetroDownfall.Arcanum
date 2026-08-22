using RetroDownfall.Arcanum.Core.Configuration.Presets;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Compendium.Ux.Services;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

[Collection("EnvVarSensitive")]
public sealed class CompendiumConfigurationPresetServiceTests
{

    [Theory]
    [InlineData(false, ErrorCodes.Data.FileLocked)]
    [InlineData(true, ErrorCodes.Data.ControlPathUnavailable)]
    public async Task Refused_client_mutation_never_enters_the_preset_writer(
        bool unsafeDisposition,
        string expectedCode)
    {

        Error error = new(expectedCode, "refused for test");

        RefusingBoundary boundary = new(error, unsafeDisposition);

        RecordingPresetService inner = new();

        CompendiumConfigurationPresetService service = new(inner, boundary);

        Result<ConfigurationPresetApplyResult> apply =
            await service.ApplyAsync("general-assistant");

        Result<ConfigurationPresetResetResult> reset =
            await service.ResetAsync();

        Result<ConfigurationPresetPlan> diff =
            await service.DiffAsync("general-assistant");

        Result<ConfigurationPresetInspection> inspect =
            await service.InspectAsync();

        Assert.True(apply.IsFailure);

        Assert.Equal(expectedCode, apply.Error.Code);

        Assert.True(reset.IsFailure);

        Assert.Equal(expectedCode, reset.Error.Code);

        Assert.True(diff.IsFailure);

        Assert.Equal(expectedCode, diff.Error.Code);

        Assert.True(inspect.IsFailure);

        Assert.Equal(expectedCode, inspect.Error.Code);

        Assert.Equal(0, inner.ApplyCallCount);

        Assert.Equal(0, inner.ResetCallCount);

        Assert.Equal(0, inner.DiffCallCount);

        Assert.Equal(0, inner.InspectCallCount);

    }

    [Theory]
    [InlineData(false, ErrorCodes.Data.FileLocked)]
    [InlineData(true, ErrorCodes.Data.ControlPathUnavailable)]
    public async Task Refused_diff_and_inspect_leave_a_prepared_journal_byte_identical(
        bool unsafeDisposition,
        string expectedCode)
    {

        using ArcanumTestHomeScope home = new("compendium-preset-prepared-journal");

        string journalPath = ArcanumPaths.ConfigurationPresetJournalFile;

        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);

        byte[] preparedJournal = "prepared-transaction-must-not-recover"u8.ToArray();

        await File.WriteAllBytesAsync(journalPath, preparedJournal);

        Error error = new(expectedCode, "refused for test");

        RefusingBoundary boundary = new(error, unsafeDisposition);

        RecordingPresetService inner = new(journalPath);

        CompendiumConfigurationPresetService service = new(inner, boundary);

        Result<ConfigurationPresetPlan> diff =
            await service.DiffAsync("general-assistant");

        Result<ConfigurationPresetInspection> inspect =
            await service.InspectAsync();

        Assert.True(diff.IsFailure);

        Assert.Equal(expectedCode, diff.Error.Code);

        Assert.True(inspect.IsFailure);

        Assert.Equal(expectedCode, inspect.Error.Code);

        Assert.Equal(preparedJournal, await File.ReadAllBytesAsync(journalPath));

        Assert.Equal(0, inner.DiffCallCount);

        Assert.Equal(0, inner.InspectCallCount);

    }

    private sealed class RefusingBoundary(
        Error error,
        bool unsafeDisposition) : IArcanumClientMutationBoundary
    {

        public Task<ArcanumClientMutationResult<T>> RunAsync<T>(
            Func<T> mutation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Refusal<T>());

        public Task<ArcanumClientMutationResult<T>> RunAsync<T>(
            Func<CancellationToken, Task<T>> mutation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Refusal<T>());

        private ArcanumClientMutationResult<T> Refusal<T>() =>
            unsafeDisposition
                ? ArcanumClientMutationResult<T>.Unsafe(error)
                : ArcanumClientMutationResult<T>.Blocked(error);

    }

    private sealed class RecordingPresetService(
        string? preparedJournalPath = null) : IConfigurationPresetService
    {

        public int ApplyCallCount { get; private set; }

        public int ResetCallCount { get; private set; }

        public int DiffCallCount { get; private set; }

        public int InspectCallCount { get; private set; }

        public IReadOnlyList<ConfigurationPresetDefinition> List() => [];

        public IReadOnlyList<ConfigurationPresetGlossaryEntry> Glossary() => [];

        public ConfigurationPresetDefinition? Find(string idOrName) => null;

        public Task<Result<ConfigurationPresetPlan>> DiffAsync(
            string idOrName,
            CancellationToken cancellationToken = default)
        {

            DiffCallCount++;

            RecoverPreparedJournal();

            throw new InvalidOperationException("The refused boundary must not invoke recovery-capable inspection.");

        }

        public Task<Result<ConfigurationPresetApplyResult>> ApplyAsync(
            string idOrName,
            CancellationToken cancellationToken = default)
        {

            ApplyCallCount++;

            throw new InvalidOperationException("The refused boundary must not invoke the writer.");

        }

        public Task<Result<ConfigurationPresetResetResult>> ResetAsync(
            CancellationToken cancellationToken = default)
        {

            ResetCallCount++;

            throw new InvalidOperationException("The refused boundary must not invoke the writer.");

        }

        public Task<Result<ConfigurationPresetInspection>> InspectAsync(
            CancellationToken cancellationToken = default)
        {

            InspectCallCount++;

            RecoverPreparedJournal();

            throw new InvalidOperationException("The refused boundary must not invoke recovery-capable inspection.");

        }

        private void RecoverPreparedJournal()
        {

            if (preparedJournalPath is not null)
            {

                File.Delete(preparedJournalPath);

            }

        }

    }

}
