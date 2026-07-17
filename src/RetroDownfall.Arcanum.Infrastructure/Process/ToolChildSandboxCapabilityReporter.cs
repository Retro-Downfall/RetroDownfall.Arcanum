namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Builds <see cref="ToolChildSandboxStatus"/> for the current host (or an explicit platform label for tests).
/// </summary>
public static class ToolChildSandboxCapabilityReporter
{

    public const string LinuxBetaDenialMessage =
        "Linux filesystem jail is not active in this beta. Set Arcanum:Security:AllowUnsandboxedToolChildren=true to run without FS confinement, or use macOS for sandboxed command tools.";

    public const string NetworkIsolationPublicMessage =
        "Network isolation for tool children is not provided.";

    /// <summary>
    /// Platform labels accepted by <see cref="Build"/> for pure unit tests.
    /// </summary>
    public static class Platforms
    {

        public const string MacOs = "macOS";

        public const string Linux = "Linux";

        public const string Windows = "Windows";

        public const string Unknown = "Unknown";

    }

    public static ToolChildSandboxStatus BuildForCurrentHost(bool escapeHatchEnabled) =>
        Build(DetectPlatform(), escapeHatchEnabled, macOsSandboxExecPresent: File.Exists("/usr/bin/sandbox-exec"));

    /// <summary>
    /// Pure status builder. <paramref name="macOsSandboxExecPresent"/> is only consulted for macOS.
    /// </summary>
    public static ToolChildSandboxStatus Build(
        string platform,
        bool escapeHatchEnabled,
        bool macOsSandboxExecPresent = true)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        if (escapeHatchEnabled)
        {

            return new ToolChildSandboxStatus
            {
                Platform = platform,

                FilesystemJailMode = ToolChildFilesystemJailMode.UnsandboxedEscapeHatch,

                ResourceLimitsMode = ResourceLimitsFor(platform),

                NetworkIsolationMode = ToolChildNetworkIsolationMode.NotProvided,

                PublicMessage =
                    "AllowUnsandboxedToolChildren=true: tool children run without filesystem confinement. "
                    + NetworkIsolationPublicMessage,

                OperatorGuidance =
                    "Disable Arcanum:Security:AllowUnsandboxedToolChildren unless you intentionally accept unsandboxed command tools. "
                    + NetworkIsolationPublicMessage,

                IsBetaSafeDefault = false,

                EscapeHatchEnabled = true,

                IsHealthDegraded = true,
            };

        }

        if (string.Equals(platform, Platforms.MacOs, StringComparison.OrdinalIgnoreCase))
        {

            if (macOsSandboxExecPresent)
            {

                return new ToolChildSandboxStatus
                {
                    Platform = Platforms.MacOs,

                    FilesystemJailMode = ToolChildFilesystemJailMode.Active,

                    ResourceLimitsMode = ToolChildResourceLimitsMode.Active,

                    NetworkIsolationMode = ToolChildNetworkIsolationMode.NotProvided,

                    PublicMessage =
                        "macOS filesystem jail is active (deprecated /usr/bin/sandbox-exec Seatbelt). "
                        + NetworkIsolationPublicMessage,

                    OperatorGuidance =
                        "Command tools are filesystem-jailed via Seatbelt. "
                        + NetworkIsolationPublicMessage,

                    IsBetaSafeDefault = true,

                    EscapeHatchEnabled = false,

                    IsHealthDegraded = false,
                };

            }

            return new ToolChildSandboxStatus
            {
                Platform = Platforms.MacOs,

                FilesystemJailMode = ToolChildFilesystemJailMode.InactiveFailClosed,

                ResourceLimitsMode = ToolChildResourceLimitsMode.Active,

                NetworkIsolationMode = ToolChildNetworkIsolationMode.NotProvided,

                PublicMessage =
                    "macOS sandbox-exec is not present on this host; command tools fail closed. "
                    + NetworkIsolationPublicMessage,

                OperatorGuidance =
                    "Install/restore /usr/bin/sandbox-exec, use a host where Seatbelt is available, "
                    + "or set Arcanum:Security:AllowUnsandboxedToolChildren=true knowingly. "
                    + NetworkIsolationPublicMessage,

                IsBetaSafeDefault = false,

                EscapeHatchEnabled = false,

                IsHealthDegraded = true,
            };

        }

        if (string.Equals(platform, Platforms.Linux, StringComparison.OrdinalIgnoreCase))
        {

            return new ToolChildSandboxStatus
            {
                Platform = Platforms.Linux,

                FilesystemJailMode = ToolChildFilesystemJailMode.InactiveFailClosed,

                ResourceLimitsMode = ToolChildResourceLimitsMode.Active,

                NetworkIsolationMode = ToolChildNetworkIsolationMode.NotProvided,

                PublicMessage = LinuxBetaDenialMessage + " " + NetworkIsolationPublicMessage,

                OperatorGuidance = LinuxBetaDenialMessage,

                IsBetaSafeDefault = false,

                EscapeHatchEnabled = false,

                IsHealthDegraded = true,
            };

        }

        if (string.Equals(platform, Platforms.Windows, StringComparison.OrdinalIgnoreCase))
        {

            return new ToolChildSandboxStatus
            {
                Platform = Platforms.Windows,

                FilesystemJailMode = ToolChildFilesystemJailMode.NotAvailable,

                ResourceLimitsMode = ToolChildResourceLimitsMode.Partial,

                NetworkIsolationMode = ToolChildNetworkIsolationMode.NotProvided,

                PublicMessage =
                    "Windows: no filesystem jail; Job Objects / resource limits only. "
                    + "When Sanctum Enabled+EnforcePathBoundary, command tools are denied "
                    + "(AllowUnsandboxedToolChildren does not bypass that denial). "
                    + NetworkIsolationPublicMessage,

                OperatorGuidance =
                    "Do not expect FS confinement on Windows in this beta. Prefer macOS for sandboxed command tools, "
                    + "or keep Sanctum path-boundary off only if Job Objects alone are acceptable. "
                    + NetworkIsolationPublicMessage,

                IsBetaSafeDefault = false,

                EscapeHatchEnabled = false,

                // Documented ≠ Healthy — Windows no-FS-jail is always Degraded for beta honesty.
                IsHealthDegraded = true,
            };

        }

        return new ToolChildSandboxStatus
        {
            Platform = Platforms.Unknown,

            FilesystemJailMode = ToolChildFilesystemJailMode.InactiveFailClosed,

            ResourceLimitsMode = ToolChildResourceLimitsMode.NotAvailable,

            NetworkIsolationMode = ToolChildNetworkIsolationMode.NotProvided,

            PublicMessage =
                "Unsupported OS for child-process filesystem jail; command tools fail closed. "
                + NetworkIsolationPublicMessage,

            OperatorGuidance =
                "Use macOS for sandboxed command tools, or set AllowUnsandboxedToolChildren=true knowingly. "
                + NetworkIsolationPublicMessage,

            IsBetaSafeDefault = false,

            EscapeHatchEnabled = false,

            IsHealthDegraded = true,
        };

    }

    private static string DetectPlatform()
    {

        if (OperatingSystem.IsMacOS())
        {

            return Platforms.MacOs;

        }

        if (OperatingSystem.IsLinux())
        {

            return Platforms.Linux;

        }

        if (OperatingSystem.IsWindows())
        {

            return Platforms.Windows;

        }

        return Platforms.Unknown;

    }

    private static ToolChildResourceLimitsMode ResourceLimitsFor(string platform)
    {

        if (string.Equals(platform, Platforms.Windows, StringComparison.OrdinalIgnoreCase))
        {

            return ToolChildResourceLimitsMode.Partial;

        }

        if (string.Equals(platform, Platforms.MacOs, StringComparison.OrdinalIgnoreCase)
            || string.Equals(platform, Platforms.Linux, StringComparison.OrdinalIgnoreCase))
        {

            return ToolChildResourceLimitsMode.Active;

        }

        return ToolChildResourceLimitsMode.NotAvailable;

    }

}
