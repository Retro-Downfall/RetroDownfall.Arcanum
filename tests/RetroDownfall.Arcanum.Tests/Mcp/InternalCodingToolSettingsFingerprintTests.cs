using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class InternalCodingToolSettingsFingerprintTests
{
    [Fact]
    public void Fingerprint_null_fallback_matches_resolved_code_owned_defaults()
    {
        Assert.Equal(
            InternalCodingToolSettingsFingerprint.Build(null),
            InternalCodingToolSettingsFingerprint.Build(
                new ArcanumSettings().ResolveCodingTools()));
    }

    [Fact]
    public void Fingerprint_uses_normalized_workspace_patch_deadline_relation()
    {
        CodingToolsSettings first = new()
        {
            Patch = new WorkspacePatchSettings
            {
                MaxElapsedMilliseconds = 100,
                RollbackReserveMilliseconds = 50_000,
            },
        };
        CodingToolsSettings second = new()
        {
            Patch = new WorkspacePatchSettings
            {
                MaxElapsedMilliseconds = 100,
                RollbackReserveMilliseconds = 60_000,
            },
        };

        Assert.Equal(
            InternalCodingToolSettingsFingerprint.Build(first),
            InternalCodingToolSettingsFingerprint.Build(second));
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
        new ArcanumSettings
        {
            Integrations = new IntegrationSettings
            {
                WorkspaceChecks = new WorkspaceCheckIntegrationSettings
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
            },
        }.ResolveCodingTools();
}
