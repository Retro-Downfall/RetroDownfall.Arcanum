using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class InternalCodingToolSettingsFingerprintTests
{
    [Fact]
    public void Fingerprint_changes_for_every_scalar_tool_surface_setting()
    {
        string baseline =
            InternalCodingToolSettingsFingerprint.Build(
                new CodingToolsSettings());
        (string Name, Action<CodingToolsSettings> Mutate)[] mutations =
        [
            ("search.maxPatternChars", value =>
                value.Search.MaxPatternChars++),
            ("search.regexTimeoutMilliseconds", value =>
                value.Search.RegexTimeoutMilliseconds++),
            ("search.maxElapsedMilliseconds", value =>
                value.Search.MaxElapsedMilliseconds++),
            ("search.maxFiles", value =>
                value.Search.MaxFiles++),
            ("search.maxBytes", value =>
                value.Search.MaxBytes++),
            ("search.maxTraversalSteps", value =>
                value.Search.MaxTraversalSteps++),
            ("search.maxMatches", value =>
                value.Search.MaxMatches++),
            ("search.maxPreviewChars", value =>
                value.Search.MaxPreviewChars++),
            ("patch.maxPatchBytes", value =>
                value.Patch.MaxPatchBytes++),
            ("patch.maxInputBytesPerFile", value =>
                value.Patch.MaxInputBytesPerFile++),
            ("patch.maxTotalInputBytes", value =>
                value.Patch.MaxTotalInputBytes++),
            ("patch.maxOutputBytesPerFile", value =>
                value.Patch.MaxOutputBytesPerFile++),
            ("patch.maxTotalOutputBytes", value =>
                value.Patch.MaxTotalOutputBytes++),
            ("patch.maxStagingBytesPerFile", value =>
                value.Patch.MaxStagingBytesPerFile++),
            ("patch.maxTotalStagingBytes", value =>
                value.Patch.MaxTotalStagingBytes++),
            ("patch.maxElapsedMilliseconds", value =>
                value.Patch.MaxElapsedMilliseconds++),
            ("patch.rollbackReserveMilliseconds", value =>
                value.Patch.RollbackReserveMilliseconds++),
            ("patch.maxFiles", value =>
                value.Patch.MaxFiles++),
            ("patch.maxHunks", value =>
                value.Patch.MaxHunks++),
            ("patch.maxLinesPerHunk", value =>
                value.Patch.MaxLinesPerHunk++),
            ("patch.fuzzyMatchWindowLines", value =>
                value.Patch.FuzzyMatchWindowLines++),
            ("patch.maxResultItems", value =>
                value.Patch.MaxResultItems++),
            ("workspaceCheck.enabled", value =>
                value.WorkspaceCheck.Enabled =
                    !value.WorkspaceCheck.Enabled),
            ("workspaceCheck.timeoutSeconds", value =>
                value.WorkspaceCheck.TimeoutSeconds++),
            ("workspaceCheck.maxCustomProfiles", value =>
                value.WorkspaceCheck.MaxCustomProfiles++),
            ("workspaceCheck.maxFixedArgumentsPerProfile", value =>
                value.WorkspaceCheck
                    .MaxFixedArgumentsPerProfile++),
            ("workspaceCheck.maxArgumentTokenChars", value =>
                value.WorkspaceCheck.MaxArgumentTokenChars++),
            ("workspaceCheck.maxOptionsPerProfile", value =>
                value.WorkspaceCheck.MaxOptionsPerProfile++),
            ("workspaceCheck.maxAllowedValuesPerOption", value =>
                value.WorkspaceCheck
                    .MaxAllowedValuesPerOption++),
            ("workspaceCheck.maxDiagnostics", value =>
                value.WorkspaceCheck.MaxDiagnostics++),
            ("workspaceCheck.maxOutputBytes", value =>
                value.WorkspaceCheck.MaxOutputBytes++),
            ("workspaceCheck.executableCatalog.dotNet.path", value =>
                value.WorkspaceCheck.ExecutableCatalog.DotNet.Path =
                    "/trusted/dotnet"),
        ];

        foreach ((string name, Action<CodingToolsSettings> mutate)
                 in mutations)
        {
            CodingToolsSettings changed = new();
            mutate(changed);

            Assert.NotEqual(
                baseline,
                InternalCodingToolSettingsFingerprint.Build(
                    changed));
        }
    }

    [Fact]
    public void Fingerprint_tracks_nested_check_profiles_but_keeps_semantically_equivalent_objects()
    {
        CodingToolsSettings first = SettingsWithProfile(
            profileId: "Custom-Profile",
            optionId: "Configuration",
            valueId: "Release");
        CodingToolsSettings equivalent = SettingsWithProfile(
            profileId: "custom-profile",
            optionId: "configuration",
            valueId: "release");
        string baseline =
            InternalCodingToolSettingsFingerprint.Build(first);

        Assert.Equal(
            baseline,
            InternalCodingToolSettingsFingerprint.Build(
                equivalent));

        equivalent.WorkspaceCheck.CustomProfiles[
            "custom-profile"].Target = "src/App.csproj";
        Assert.NotEqual(
            baseline,
            InternalCodingToolSettingsFingerprint.Build(
                equivalent));
        equivalent.WorkspaceCheck.CustomProfiles[
            "custom-profile"].Target = string.Empty;

        equivalent.WorkspaceCheck.CustomProfiles[
            "custom-profile"].Options["configuration"]
            .AllowedValues["release"] =
            ["--configuration", "Debug"];

        Assert.NotEqual(
            baseline,
            InternalCodingToolSettingsFingerprint.Build(
                equivalent));
    }

    private static CodingToolsSettings SettingsWithProfile(
        string profileId,
        string optionId,
        string valueId) =>
        new()
        {
            WorkspaceCheck = new WorkspaceCheckSettings
            {
                CustomProfiles =
                    new Dictionary<
                        string,
                        WorkspaceCheckProfileSettings>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        [profileId] =
                            new WorkspaceCheckProfileSettings
                            {
                                ExecutableId = "dotnet",
                                Kind = WorkspaceCheckKind.Build,
                                Parser =
                                    WorkspaceCheckDiagnosticParserKind
                                        .MsBuild,
                                FixedArguments =
                                    ["build", "--no-restore"],
                                Options =
                                    new Dictionary<
                                        string,
                                        WorkspaceCheckProfileOptionSettings>(
                                        StringComparer.OrdinalIgnoreCase)
                                    {
                                        [optionId] =
                                            new WorkspaceCheckProfileOptionSettings
                                            {
                                                AllowedValues =
                                                    new Dictionary<
                                                        string,
                                                        string[]>(
                                                        StringComparer
                                                            .OrdinalIgnoreCase)
                                                    {
                                                        [valueId] =
                                                        [
                                                            "--configuration",
                                                            "Release",
                                                        ],
                                                    },
                                            },
                                    },
                            },
                    },
            },
        };
}
