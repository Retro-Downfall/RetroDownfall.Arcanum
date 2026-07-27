namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Tokens reserved to the workspace-check runtime. Operator profiles cannot enable restore,
/// redirect trusted roots, or select their own result/cache/log destinations.
/// </summary>
public static class WorkspaceCheckArgumentPolicy
{

    public static bool IsRuntimeReservedToken(string token)
    {

        if (string.IsNullOrWhiteSpace(token))
        {

            return false;
        }

        string normalized = token.Trim().ToLowerInvariant();

        if (normalized == "--no-restore")
        {

            return false;
        }

        if (normalized.Contains("restore", StringComparison.Ordinal)
            || normalized.Contains("outputpath", StringComparison.Ordinal)
            || normalized.Contains("outdir", StringComparison.Ordinal)
            || normalized.Contains("intermediateoutputpath", StringComparison.Ordinal)
            || normalized.Contains("projectextensionspath", StringComparison.Ordinal)
            || normalized.Contains("artifactspath", StringComparison.Ordinal)
            || normalized.Contains("useartifactsoutput", StringComparison.Ordinal)
            || normalized.Contains("nuget_packages", StringComparison.Ordinal)
            || normalized.Contains("nugetpackages", StringComparison.Ordinal)
            || normalized.Contains("vstest_results", StringComparison.Ordinal)
            || normalized.Contains("resultsdirectory", StringComparison.Ordinal)
            || normalized.Contains("resultspath", StringComparison.Ordinal)
            || normalized.Contains("logfilename", StringComparison.Ordinal))
        {

            return true;
        }

        return normalized is
            "--output" or "-o"
            or "--results-directory"
            or "--artifacts-path"
            or "--packages"
            or "--interactive"
            or "--force"
            or "--force-evaluate"
            or "--logger"
            or "--binarylog"
            or "--report"
            || normalized.StartsWith("--output=", StringComparison.Ordinal)
            || normalized.StartsWith("--results-directory=", StringComparison.Ordinal)
            || normalized.StartsWith("--artifacts-path=", StringComparison.Ordinal)
            || normalized.StartsWith("--packages=", StringComparison.Ordinal)
            || normalized.StartsWith("-bl", StringComparison.Ordinal)
            || normalized.StartsWith("/bl", StringComparison.Ordinal);
    }
}
