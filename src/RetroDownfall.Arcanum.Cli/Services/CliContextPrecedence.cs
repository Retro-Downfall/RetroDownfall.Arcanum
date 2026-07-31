namespace RetroDownfall.Arcanum.Cli.Services;

public enum CliContextSource
{

    ExplicitOption,

    ActiveContext,

    CurrentDirectory,

    ServerDefault,

}

public readonly record struct CliContextValue<T>(
    T? Value,
    CliContextSource Source);

public sealed record CliEffectiveContext(
    CliContextValue<Guid?> Campaign,
    CliContextValue<string?> Workspace,
    CliContextValue<string?> Model,
    CliContextValue<Guid?> Session);

public sealed record CliContextResolutionRequest(
    Guid? ExplicitCampaignId,
    string? ExplicitWorkspacePath,
    string? ExplicitModel,
    Guid? ExplicitSessionId,
    CliContextDocument Active,
    Guid? DetectedCampaignId,
    string? DetectedCampaignPath,
    string? ServerDefaultModel,
    bool NoContext);

public static class CliContextPrecedence
{

    public static CliEffectiveContext Resolve(CliContextResolutionRequest request)
    {

        ArgumentNullException.ThrowIfNull(request);

        CliContextDocument active = request.NoContext
            ? CliContextDocument.Empty
            : request.Active;

        CliContextValue<Guid?> campaign = Select(
            request.ExplicitCampaignId,
            active.CampaignId,
            request.DetectedCampaignId,
            default(Guid?));

        CliContextValue<string?> workspace = Select(
            Normalize(request.ExplicitWorkspacePath),
            Normalize(active.WorkspacePath),
            Normalize(request.DetectedCampaignPath),
            default(string));

        CliContextValue<string?> model = Select(
            Normalize(request.ExplicitModel),
            Normalize(active.Model),
            default(string),
            Normalize(request.ServerDefaultModel));

        CliContextValue<Guid?> session = Select(
            request.ExplicitSessionId,
            active.SessionId,
            default(Guid?),
            default(Guid?));

        return new CliEffectiveContext(
            campaign,
            workspace,
            model,
            session);

    }

    private static CliContextValue<T?> Select<T>(
        T? explicitValue,
        T? activeValue,
        T? detectedValue,
        T? serverDefault)
    {

        if (explicitValue is not null)
        {

            return new CliContextValue<T?>(
                explicitValue,
                CliContextSource.ExplicitOption);

        }

        if (activeValue is not null)
        {

            return new CliContextValue<T?>(
                activeValue,
                CliContextSource.ActiveContext);

        }

        if (detectedValue is not null)
        {

            return new CliContextValue<T?>(
                detectedValue,
                CliContextSource.CurrentDirectory);

        }

        return new CliContextValue<T?>(
            serverDefault,
            CliContextSource.ServerDefault);

    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}
