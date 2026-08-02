using System.Diagnostics;

using System.Text;

namespace RetroDownfall.Arcanum.Core.Desktop;

/// <summary>Starts application processes directly and converts start failures to safe results.</summary>
public sealed class ApplicationProcessStarter : IApplicationProcessStarter
{

    public bool TryStart(ProcessStartInfo startInfo)
    {

        ArgumentNullException.ThrowIfNull(startInfo);

        try
        {

            using Process? process = Process.Start(startInfo);

            return process is not null;

        }
        catch
        {

            return false;

        }

    }

}

/// <summary>
/// Launches discovered applications with structured process arguments and reports safe fallbacks.
/// </summary>
public sealed class ApplicationLauncher : IApplicationLauncher
{

    private readonly IApplicationDiscoveryService _discovery;

    private readonly IApplicationProcessStarter _processStarter;

    public ApplicationLauncher(
        IApplicationDiscoveryService discovery,
        IApplicationProcessStarter processStarter)
    {

        _discovery = discovery;

        _processStarter = processStarter;

    }

    public ApplicationLaunchResult TryLaunch(ApplicationLaunchRequest request)
    {

        ArgumentNullException.ThrowIfNull(request);

        if (request.DeepLink is { } deepLink
            && deepLink.TargetApplication != request.Application)
        {

            return Failure(
                request,
                [],
                "The deep-link target does not match the requested application.");

        }

        string? deepLinkPayload;

        try
        {

            deepLinkPayload = request.DeepLink is null
                ? null
                : ApplicationDeepLinkCodec.Encode(request.DeepLink);

        }
        catch
        {

            return Failure(
                request,
                [],
                "The deep-link payload could not be encoded.");

        }

        IReadOnlyList<ApplicationDiscoveryCandidate> candidates;

        try
        {

            candidates = _discovery.Discover(request.Application);

        }
        catch
        {

            return Failure(
                request,
                [],
                "Application discovery failed.");

        }

        List<ApplicationDiscoveryCandidate> triedCandidates = [];

        bool foundExistingCandidate = false;

        foreach (ApplicationDiscoveryCandidate candidate in candidates)
        {

            triedCandidates.Add(candidate);

            if (!candidate.Exists)
            {

                continue;

            }

            foundExistingCandidate = true;

            ProcessStartInfo startInfo = CreateStartInfo(candidate, deepLinkPayload);

            bool started;

            try
            {

                started = _processStarter.TryStart(startInfo);

            }
            catch
            {

                started = false;

            }

            if (started)
            {

                return new ApplicationLaunchResult(
                    ApplicationLaunchStatus.Started,
                    triedCandidates.ToArray(),
                    candidate,
                    $"Started {GetDisplayName(request.Application)}.",
                    DevelopmentFallbackCommand: null,
                    request.CliFallbackCommand);

            }

        }

        ApplicationLaunchStatus status = foundExistingCandidate
            ? ApplicationLaunchStatus.Failed
            : ApplicationLaunchStatus.Unavailable;

        string? developmentFallback = CreateDevelopmentFallback(
            candidates,
            deepLinkPayload);

        return new ApplicationLaunchResult(
            status,
            triedCandidates.ToArray(),
            SelectedCandidate: null,
            BuildFailureMessage(
                request.Application,
                status,
                triedCandidates),
            developmentFallback,
            request.CliFallbackCommand);

    }

    private static ProcessStartInfo CreateStartInfo(
        ApplicationDiscoveryCandidate candidate,
        string? deepLinkPayload)
    {

        ProcessStartInfo startInfo = new()
        {

            UseShellExecute = false,

            CreateNoWindow = true,

        };

        switch (candidate.Kind)
        {

            case ApplicationCandidateKind.Executable:

                startInfo.FileName = candidate.LaunchPath;

                break;

            case ApplicationCandidateKind.ApplicationBundle:

                startInfo.FileName = "/usr/bin/open";

                startInfo.ArgumentList.Add("-n");

                startInfo.ArgumentList.Add(candidate.LaunchPath);

                if (deepLinkPayload is not null)
                {

                    startInfo.ArgumentList.Add("--args");

                }

                break;

            case ApplicationCandidateKind.DevelopmentProject:

                startInfo.FileName = "dotnet";

                startInfo.ArgumentList.Add("run");

                startInfo.ArgumentList.Add("--project");

                startInfo.ArgumentList.Add(candidate.LaunchPath);

                startInfo.ArgumentList.Add("--");

                break;

            default:

                throw new InvalidOperationException(
                    "The application discovery candidate kind is unknown.");

        }

        if (deepLinkPayload is not null)
        {

            startInfo.ArgumentList.Add(ApplicationDeepLinkCodec.ArgumentName);

            startInfo.ArgumentList.Add(deepLinkPayload);

        }

        return startInfo;

    }

    private static string? CreateDevelopmentFallback(
        IReadOnlyList<ApplicationDiscoveryCandidate> candidates,
        string? deepLinkPayload)
    {

        ApplicationDiscoveryCandidate? project = candidates.FirstOrDefault(
            candidate =>
                candidate.Kind == ApplicationCandidateKind.DevelopmentProject
                && !string.IsNullOrWhiteSpace(candidate.ProjectRelativePath));

        if (project?.ProjectRelativePath is not { } projectRelativePath)
        {

            return null;

        }

        StringBuilder command = new();

        command.Append("dotnet run --project ");

        command.Append(projectRelativePath);

        if (deepLinkPayload is not null)
        {

            command.Append(" -- ");

            command.Append(ApplicationDeepLinkCodec.ArgumentName);

            command.Append(' ');

            command.Append(
                CommandDisplayFormatter.QuoteArgumentForCurrentPlatform(
                    deepLinkPayload));

        }

        return command.ToString();

    }

    private static string BuildFailureMessage(
        DesktopApplication application,
        ApplicationLaunchStatus status,
        IReadOnlyList<ApplicationDiscoveryCandidate> candidates)
    {

        string outcome = status == ApplicationLaunchStatus.Unavailable
            ? "was not found"
            : "could not be started";

        string locations = candidates.Count == 0
            ? "none"
            : string.Join(
                ", ",
                candidates.Select(candidate => candidate.DisplayPath));

        return $"{GetDisplayName(application)} {outcome}. Discovery locations tried: {locations}.";

    }

    private static ApplicationLaunchResult Failure(
        ApplicationLaunchRequest request,
        IReadOnlyList<ApplicationDiscoveryCandidate> triedCandidates,
        string message) =>
        new(
            ApplicationLaunchStatus.Failed,
            triedCandidates,
            SelectedCandidate: null,
            message,
            DevelopmentFallbackCommand: null,
            request.CliFallbackCommand);

    private static string GetDisplayName(DesktopApplication application) =>
        application switch
        {

            DesktopApplication.CommandCenter => "Command Center",

            DesktopApplication.TheForge => "The Forge",

            DesktopApplication.Compendium => "Compendium",

            _ => "Application",

        };

}
