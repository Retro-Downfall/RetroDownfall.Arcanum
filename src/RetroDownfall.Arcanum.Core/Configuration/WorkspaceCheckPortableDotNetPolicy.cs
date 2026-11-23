using System.Text.RegularExpressions;

namespace RetroDownfall.Arcanum.Core.Configuration;

internal static partial class WorkspaceCheckPortableDotNetPolicy
{
    private const string ConfigPath = "integrations.workspaceChecks.portableDotNet";

    private const string RegistryLabel = "[a-z0-9]+(?:-+[a-z0-9]+)*";

    private const string RepositoryComponent = "[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*";

    internal static void Validate(
        WorkspaceCheckPortableDotNetSettings settings,
        List<ConfigurationValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(settings.OfflinePackageFeed)
            || settings.OfflinePackageFeed.Any(char.IsControl)
            || !Path.IsPathFullyQualified(settings.OfflinePackageFeed))
        {
            errors.Add(new ConfigurationValidationError(
                $"{ConfigPath}.offlinePackageFeed",
                "Configure an absolute path to the operator-approved offline NuGet feed."));
        }

        if (!IsDigestPinnedImage(settings.InitImageReference))
        {
            errors.Add(new ConfigurationValidationError(
                $"{ConfigPath}.initImageReference",
                "Configure a fully qualified init image reference ending in @sha256 and its 64-digit lowercase digest."));
        }

        WorkspaceCheckPortableImageSettings[] images = settings.Images ?? [];

        if (images.Length == 0)
        {
            errors.Add(new ConfigurationValidationError(
                $"{ConfigPath}.images",
                "Configure at least one exact .NET SDK image with a digest-pinned reference."));
        }

        HashSet<string> versions = new(StringComparer.Ordinal);

        for (int index = 0; index < images.Length; index++)
        {
            WorkspaceCheckPortableImageSettings? image = images[index];

            string pointer = $"{ConfigPath}.images[{index}]";

            if (!WorkspaceCheckSdkVersion.TryParse(image?.SdkVersion, out WorkspaceCheckSdkVersion version)
                || version.Band.Feature < 1)
            {
                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.sdkVersion",
                    "Configure an exact .NET SDK version with a feature band."));
            }
            else if (!versions.Add(version.Original))
            {
                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.sdkVersion",
                    "Configure each exact .NET SDK version only once."));
            }

            if (!IsDigestPinnedImage(image?.ImageReference))
            {
                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.imageReference",
                    "Configure a fully qualified SDK image reference ending in @sha256 and its 64-digit lowercase digest."));
            }
        }
    }

    internal static bool IsDigestPinnedImage(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return false;
        }

        int digestSeparator = reference.IndexOf('@');

        if (digestSeparator is < 0 or > 255)
        {
            return false;
        }

        Match match = DigestPinnedImageRegex().Match(reference);

        if (!match.Success)
        {
            return false;
        }

        Group registry = match.Groups["registry"];

        Group port = match.Groups["port"];

        return (registry.ValueSpan.Contains('.') || registry.ValueSpan.SequenceEqual("localhost") || port.Success)
            && registry.Value.Split('.').All(static label => label.Length <= 63)
            && (!port.Success || (int.TryParse(port.ValueSpan, out int number) && number is >= 1 and <= 65535));
    }

    [GeneratedRegex(
        @"\A(?<registry>" + RegistryLabel + @"(?:\." + RegistryLabel + @")*)(?::(?<port>[0-9]{1,5}))?/"
        + RepositoryComponent + "(?:/" + RepositoryComponent + @")*@sha256:[0-9a-f]{64}\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex DigestPinnedImageRegex();
}
