using System.Globalization;
using System.Text;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal static class InternalCodingToolSettingsFingerprint
{
    internal static string Build(
        CodingToolsSettings? configured)
    {
        CodingToolsSettings settings =
            configured ?? ArcanumRuntimeDefaults.CodingTools;
        WorkspaceSearchSettings search =
            settings.Search ?? new WorkspaceSearchSettings();
        WorkspacePatchSettings patch =
            settings.Patch ?? new WorkspacePatchSettings();
        WorkspaceCheckSettings check =
            settings.WorkspaceCheck
            ?? new WorkspaceCheckSettings();
        Builder fingerprint = new();

        fingerprint.Append(
            ArcanumSettingClamps.WorkspaceSearchMaxPatternChars(
                search.MaxPatternChars));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspaceSearchRegexTimeoutMilliseconds(
                search.RegexTimeoutMilliseconds));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspaceSearchMaxElapsedMilliseconds(
                search.MaxElapsedMilliseconds));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspaceSearchMaxFiles(
                search.MaxFiles));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspaceSearchMaxBytes(
                search.MaxBytes));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspaceSearchMaxTraversalSteps(
                search.MaxTraversalSteps));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspaceSearchMaxMatches(
                search.MaxMatches));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspaceSearchMaxPreviewChars(
                search.MaxPreviewChars));

        fingerprint.Append(
            ArcanumSettingClamps.WorkspacePatchMaxPatchBytes(
                patch.MaxPatchBytes));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspacePatchMaxInputBytesPerFile(
                patch.MaxInputBytesPerFile));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspacePatchMaxTotalInputBytes(
                patch.MaxTotalInputBytes));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspacePatchMaxOutputBytesPerFile(
                patch.MaxOutputBytesPerFile));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspacePatchMaxTotalOutputBytes(
                patch.MaxTotalOutputBytes));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspacePatchMaxStagingBytesPerFile(
                patch.MaxStagingBytesPerFile));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspacePatchMaxTotalStagingBytes(
                patch.MaxTotalStagingBytes));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspacePatchMaxElapsedMilliseconds(
                patch.MaxElapsedMilliseconds));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspacePatchRollbackReserveMilliseconds(
                patch.RollbackReserveMilliseconds));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspacePatchMaxFiles(
                patch.MaxFiles));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspacePatchMaxHunks(
                patch.MaxHunks));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspacePatchMaxLinesPerHunk(
                patch.MaxLinesPerHunk));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspacePatchFuzzyMatchWindowLines(
                patch.FuzzyMatchWindowLines));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspacePatchMaxResultItems(
                patch.MaxResultItems));

        AppendWorkspaceCheck(fingerprint, check);
        return fingerprint.ToString();
    }

    internal static string BuildWorkspaceCheck(
        WorkspaceCheckSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Builder fingerprint = new();
        AppendWorkspaceCheck(fingerprint, settings);
        return fingerprint.ToString();
    }

    private static void AppendWorkspaceCheck(
        Builder fingerprint,
        WorkspaceCheckSettings value)
    {
        fingerprint.Append(value.Enabled);
        fingerprint.Append(
            ArcanumSettingClamps.WorkspaceCheckTimeoutSeconds(
                value.TimeoutSeconds));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspaceCheckMaxCustomProfiles(
                value.MaxCustomProfiles));
        fingerprint.Append(
            ArcanumSettingClamps
                .WorkspaceCheckMaxFixedArgumentsPerProfile(
                    value.MaxFixedArgumentsPerProfile));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspaceCheckMaxArgumentTokenChars(
                value.MaxArgumentTokenChars));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspaceCheckMaxOptionsPerProfile(
                value.MaxOptionsPerProfile));
        fingerprint.Append(
            ArcanumSettingClamps
                .WorkspaceCheckMaxAllowedValuesPerOption(
                    value.MaxAllowedValuesPerOption));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspaceCheckMaxDiagnostics(
                value.MaxDiagnostics));
        fingerprint.Append(
            ArcanumSettingClamps.WorkspaceCheckMaxOutputBytes(
                value.MaxOutputBytes));
        fingerprint.Append(
            value.ExecutableCatalog?.DotNet?.Path
            ?? string.Empty);

        KeyValuePair<string, WorkspaceCheckProfileSettings>[]
            profiles = (value.CustomProfiles
                ?? new Dictionary<
                    string,
                    WorkspaceCheckProfileSettings>())
                .OrderBy(
                    static pair => pair.Key,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        fingerprint.Append(profiles.Length);

        foreach ((string profileId, WorkspaceCheckProfileSettings profile)
                 in profiles)
        {
            fingerprint.AppendCaseInsensitive(profileId);

            if (profile is null)
            {
                fingerprint.Append("<null-profile>");
                continue;
            }

            fingerprint.AppendCaseInsensitive(
                profile.ExecutableId);
            fingerprint.Append((int)profile.Kind);
            fingerprint.Append((int)profile.Parser);
            fingerprint.Append(profile.Target);

            string[] fixedArguments =
                profile.FixedArguments ?? [];
            fingerprint.Append(fixedArguments.Length);

            foreach (string argument in fixedArguments)
            {
                fingerprint.Append(argument);
            }

            KeyValuePair<
                string,
                WorkspaceCheckProfileOptionSettings>[]
                options = (profile.Options
                    ?? new Dictionary<
                        string,
                        WorkspaceCheckProfileOptionSettings>())
                    .OrderBy(
                        static pair => pair.Key,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            fingerprint.Append(options.Length);

            foreach ((
                         string optionId,
                         WorkspaceCheckProfileOptionSettings option)
                     in options)
            {
                fingerprint.AppendCaseInsensitive(optionId);

                if (option is null)
                {
                    fingerprint.Append("<null-option>");
                    continue;
                }

                KeyValuePair<string, string[]>[] allowedValues =
                    (option.AllowedValues
                        ?? new Dictionary<string, string[]>())
                    .OrderBy(
                        static pair => pair.Key,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                fingerprint.Append(allowedValues.Length);

                foreach ((string valueId, string[] arguments)
                         in allowedValues)
                {
                    fingerprint.AppendCaseInsensitive(valueId);
                    string[] rendering = arguments ?? [];
                    fingerprint.Append(rendering.Length);

                    foreach (string argument in rendering)
                    {
                        fingerprint.Append(argument);
                    }
                }
            }
        }
    }

    private sealed class Builder
    {
        private readonly StringBuilder _value = new();

        internal void Append(bool value) =>
            Append(value ? "1" : "0");

        internal void Append(int value) =>
            Append(value.ToString(
                CultureInfo.InvariantCulture));

        internal void Append(long value) =>
            Append(value.ToString(
                CultureInfo.InvariantCulture));

        internal void AppendCaseInsensitive(string? value) =>
            Append((value ?? string.Empty).ToUpperInvariant());

        internal void Append(string? value)
        {
            string text = value ?? string.Empty;
            _ = _value
                .Append(text.Length)
                .Append(':')
                .Append(text);
        }

        public override string ToString() =>
            _value.ToString();
    }
}
