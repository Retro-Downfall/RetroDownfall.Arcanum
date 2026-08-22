using RetroDownfall.Arcanum.Core.Configuration.Presets;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Coordination;

namespace RetroDownfall.Compendium.Ux.Services;

internal sealed class CompendiumConfigurationPresetService(
    IConfigurationPresetService inner,
    IArcanumClientMutationBoundary mutationBoundary) : IConfigurationPresetService
{

    public IReadOnlyList<ConfigurationPresetDefinition> List() => inner.List();

    public IReadOnlyList<ConfigurationPresetGlossaryEntry> Glossary() => inner.Glossary();

    public ConfigurationPresetDefinition? Find(string idOrName) => inner.Find(idOrName);

    public Task<Result<ConfigurationPresetPlan>> DiffAsync(
        string idOrName,
        CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            admittedCancellationToken => inner.DiffAsync(
                idOrName,
                admittedCancellationToken),
            cancellationToken);

    public Task<Result<ConfigurationPresetApplyResult>> ApplyAsync(
        string idOrName,
        CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            admittedCancellationToken => inner.ApplyAsync(
                idOrName,
                admittedCancellationToken),
            cancellationToken);

    public Task<Result<ConfigurationPresetResetResult>> ResetAsync(
        CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            inner.ResetAsync,
            cancellationToken);

    public Task<Result<ConfigurationPresetInspection>> InspectAsync(
        CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            inner.InspectAsync,
            cancellationToken);

    private async Task<Result<T>> RunMutationAsync<T>(
        Func<CancellationToken, Task<Result<T>>> mutation,
        CancellationToken cancellationToken)
    {

        ArcanumClientMutationResult<Result<T>> admitted = await mutationBoundary
            .RunAsync(mutation, cancellationToken)
            .ConfigureAwait(false);

        return admitted.IsCompleted
            ? admitted.Value
            : Result<T>.Failure(admitted.Error);

    }

}
