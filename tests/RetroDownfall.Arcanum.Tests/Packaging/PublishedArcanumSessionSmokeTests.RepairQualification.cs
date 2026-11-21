using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed partial class PublishedArcanumSessionSmokeTests
{
    private static ProcessStartInfo ConfineRepairApphost(
        ProcessStartInfo start, string executable, PublishedTestProcessIsolation isolation)
    {
        if (global::System.Environment.GetEnvironmentVariable("ARCANUM_PUBLISHED_REPAIR_CONFINEMENT") != "1") return start;

        const string sandbox = "/usr/bin/sandbox-exec";

        if (!OperatingSystem.IsMacOS() || !File.Exists(sandbox))
            throw new InvalidOperationException("Independent repair qualification requires the native apphost confinement boundary.");

        string configurationPath = Path.Combine(isolation.TestHome, ".config", "arcanum", "arcanum.json");

        ArcanumConfigurationFile configuration = JsonSerializer.Deserialize(File.ReadAllText(configurationPath),
            ConfigurationJsonContext.Default.ArcanumConfigurationFile)
            ?? throw new InvalidOperationException("The local provider fixture configuration is unavailable.");

        Uri provider = new(configuration.Arcanum.Providers.Single().Endpoint);

        if (!provider.IsLoopback || configuration.Arcanum.Host.Port is <= 0 or > 65535)
            throw new InvalidOperationException("Repair qualification requires isolated loopback endpoints.");

        string hostPort = configuration.Arcanum.Host.Port.ToString(CultureInfo.InvariantCulture);

        string providerPort = provider.Port.ToString(CultureInfo.InvariantCulture);

        string temporary = NoFollowPathTopology.NormalizeMacOsSystemAlias(isolation.ProcessTemporaryDirectory);

        string binaryDirectory = NoFollowPathTopology.NormalizeMacOsSystemAlias(Path.GetDirectoryName(Path.GetFullPath(executable))!);

        string profile = MacOsSandboxExecProfileBuilder.Build([temporary], [binaryDirectory], temporary,
            denyProcessFork: true, confineMetadataToRoots: true, confineProcessInformation: true);

        const string defaultNetwork = "(allow network*)";

        if (!profile.Contains(defaultNetwork, StringComparison.Ordinal))
            throw new InvalidOperationException("The native apphost confinement network boundary could not be narrowed.");

        string network = $"(allow network-bind network-inbound (local ip \"localhost:{hostPort}\"))\n"
            + $"(allow network-outbound (remote ip \"localhost:{hostPort}\") (remote ip \"localhost:{providerPort}\"))";

        StringBuilder confinement = new(profile.Replace(defaultNetwork, network, StringComparison.Ordinal));

        // The installation lock writes the host name into the isolated profile's own lock marker.
        confinement.AppendLine("(allow sysctl-read (sysctl-name \"kern.hostname\"))");

        // The protected blob inventory opens each known ancestor by handle before inspecting the profile.
        // Permit the directory entry itself. File contents and writes outside the test roots remain unavailable.
        for (DirectoryInfo? ancestor = Directory.GetParent(temporary); ancestor?.Parent is not null; ancestor = ancestor.Parent)
        {
            string literal = ancestor.FullName.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);

            confinement.AppendLine($"(allow file-read-data (literal \"{literal}\"))");
        }

        string[] arguments = [.. start.ArgumentList];

        start.FileName = sandbox;

        start.Environment["DOTNET_EnableDiagnostics"] = "0";

        start.ArgumentList.Clear();

        start.ArgumentList.Add("-p");

        start.ArgumentList.Add(confinement.ToString());

        start.ArgumentList.Add(executable);

        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        return start;
    }
}
