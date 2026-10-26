using System.ClientModel.Primitives;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Rechecks the first-party build observation before every physical SDK attempt.</summary>
internal sealed class TapestryProviderPolicy(ITapestryProviderValidator? validator,
    string provider, string model, bool embedding) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ValidateAsync(message.CancellationToken).GetAwaiter().GetResult();

        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ValidateAsync(message.CancellationToken).ConfigureAwait(false);

        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private async Task ValidateAsync(CancellationToken token)
    {
        if (TapestryBuildContext.Current is null) return;

        token.ThrowIfCancellationRequested();

        Result current = validator is null
            ? Result.Failure(new Error(ErrorCodes.Hub.InvalidAdmission, "The Tapestry provider effect has no source validator."))
            : await validator.ValidateAsync(provider, model, embedding, token).ConfigureAwait(false);

        if (current.IsFailure) throw new TapestryProviderRefusalException(current.Error);
    }
}

internal sealed class TapestryProviderRefusalException(Error error) : Exception(error.Message)
{
    internal Error Error { get; } = error;
}
