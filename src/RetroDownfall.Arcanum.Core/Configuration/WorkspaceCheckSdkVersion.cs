using System.Globalization;

namespace RetroDownfall.Arcanum.Core.Configuration;

internal readonly record struct WorkspaceCheckSdkVersion(
    int Major,
    int Minor,
    int Patch,
    string? Prerelease,
    string Original) : IComparable<WorkspaceCheckSdkVersion>
{
    internal bool IsPrerelease => Prerelease is not null;

    internal (int Major, int Minor, int Feature) Band => (Major, Minor, Patch / 100);

    public int CompareTo(WorkspaceCheckSdkVersion other)
    {
        int versionOrder = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

        if (versionOrder != 0)
        {
            return versionOrder;
        }

        if (Prerelease is null)
        {
            return other.Prerelease is null ? 0 : 1;
        }

        if (other.Prerelease is null)
        {
            return -1;
        }

        string[] left = Prerelease.Split('.');

        string[] right = other.Prerelease.Split('.');

        for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            bool leftNumeric = left[index].All(char.IsAsciiDigit);

            bool rightNumeric = right[index].All(char.IsAsciiDigit);

            int comparison = rightNumeric.CompareTo(leftNumeric);

            if (leftNumeric && rightNumeric)
            {
                comparison = left[index].Length.CompareTo(right[index].Length);
            }

            if (comparison == 0)
            {
                comparison = string.CompareOrdinal(left[index], right[index]);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    internal static bool TryParse(string? value, out WorkspaceCheckSdkVersion version)
    {
        version = default;

        if (string.IsNullOrEmpty(value) || value.Length > 255)
        {
            return false;
        }

        string[] build = value.Split('+', 2);

        string[] suffix = build[0].Split('-', 2);

        string[] parts = suffix[0].Split('.');

        string? prerelease = suffix.Length == 2 ? suffix[1] : null;

        if (parts.Length != 3
            || !TryParseNumber(parts[0], out int major)
            || !TryParseNumber(parts[1], out int minor)
            || !TryParseNumber(parts[2], out int patch)
            || (prerelease is not null && !ValidIdentifiers(prerelease, allowLeadingZero: false))
            || (build.Length == 2 && !ValidIdentifiers(build[1], allowLeadingZero: true)))
        {
            return false;
        }

        version = new WorkspaceCheckSdkVersion(major, minor, patch, prerelease, value);

        return true;
    }

    private static bool TryParseNumber(string part, out int value) =>
        int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value)
        && part.All(char.IsAsciiDigit)
        && (part.Length == 1 || part[0] != '0');

    private static bool ValidIdentifiers(string suffix, bool allowLeadingZero) =>
        suffix.Split('.').All(identifier =>
            identifier.Length > 0
            && identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            && (allowLeadingZero || identifier.Length == 1
                || identifier[0] != '0' || !identifier.All(char.IsAsciiDigit)));
}
