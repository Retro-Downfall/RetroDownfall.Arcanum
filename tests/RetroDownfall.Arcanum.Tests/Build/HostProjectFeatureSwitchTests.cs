using System.Xml.Linq;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class HostProjectFeatureSwitchTests
{

    /// <summary>
    /// MSBuild properties the SDK turns into runtimeconfig <c>configProperties</c> for every build,
    /// not only for a Native AOT publish: <c>Microsoft.NET.Sdk.targets</c> emits each
    /// <c>RuntimeHostConfigurationOption</c> on the sole condition that the property is set, and
    /// <c>PublishAot</c> pulls in the ILCompiler targets that force <c>DynamicCodeSupport=false</c>,
    /// <c>EventSourceSupport=false</c> and <c>CanEmitObjectArrayDelegate=false</c>. Leaving any of
    /// them ungated by <c>RuntimeIdentifier</c> stamps AOT feature switches into ordinary
    /// Debug/Release hosts, where <c>IsDynamicCodeSupported=false</c> breaks EF Core LINQ (no
    /// precompiled queries; <c>EFPrecompileQueriesStage</c> is <c>none</c>) and
    /// <c>UseSystemResourceKeys=true</c> degrades every BCL exception message to a bare resource key.
    /// </summary>
    private static readonly string[] AotOnlyFeatureSwitches =
    [
        "PublishAot",
        "UseSystemResourceKeys",
        "StackTraceSupport",
        "DebuggerSupport",
        "EventSourceSupport",
        "BuiltInComInteropSupport",
        "UseWindowsThreadPool",
    ];

    [Theory]
    [InlineData("src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj")]
    [InlineData("src/RetroDownfall.Arcanum.Api.DevHost/RetroDownfall.Arcanum.Api.DevHost.csproj")]
    public void Host_project_gates_aot_feature_switches_behind_a_runtime_identifier(string relativeProjectPath)
    {

        string projectPath = Path.Combine(
            FindRepositoryRoot(),
            relativeProjectPath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(projectPath), $"Missing project file: {projectPath}");

        XDocument project = XDocument.Load(projectPath);

        string[] ungated = project
            .Descendants()
            .Where(static element => element.Parent?.Name.LocalName == "PropertyGroup")
            .Where(static element => AotOnlyFeatureSwitches.Contains(element.Name.LocalName, StringComparer.Ordinal))
            .Where(static element => !IsRuntimeIdentifierGated(element))
            .Select(static element => $"{element.Name.LocalName}={element.Value.Trim()}")
            .ToArray();

        Assert.True(
            ungated.Length == 0,
            $"{relativeProjectPath} sets AOT-only feature switches without a RuntimeIdentifier guard, "
            + "so they are stamped into plain `dotnet build` / `dotnet run` runtimeconfig:\n  "
            + string.Join("\n  ", ungated));

    }

    private static bool IsRuntimeIdentifierGated(XElement property)
    {

        string propertyCondition = (string?)property.Attribute("Condition") ?? string.Empty;

        string groupCondition = (string?)property.Parent?.Attribute("Condition") ?? string.Empty;

        return propertyCondition.Contains("$(RuntimeIdentifier", StringComparison.Ordinal)
            || groupCondition.Contains("$(RuntimeIdentifier", StringComparison.Ordinal);

    }

    private static string FindRepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {

            if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
            {

                return directory.FullName;

            }

            directory = directory.Parent;

        }

        throw new InvalidOperationException("Could not locate the repository root.");

    }

}
