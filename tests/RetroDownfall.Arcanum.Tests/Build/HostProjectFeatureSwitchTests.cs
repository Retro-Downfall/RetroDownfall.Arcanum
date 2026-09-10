using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class HostProjectFeatureSwitchTests
{
    [Fact]
    public void Shipping_cli_uses_native_aot_for_every_explicit_runtime_identifier()
    {
        string projectPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Cli",
            "RetroDownfall.Arcanum.Cli.csproj");

        XDocument project = XDocument.Load(projectPath);

        XElement publishAot = Assert.Single(
            project.Descendants(),
            static element => element.Name.LocalName == "PublishAot");

        Assert.Equal("true", publishAot.Value.Trim(), ignoreCase: true);
        Assert.True(IsRuntimeIdentifierGated(publishAot));
        Assert.DoesNotContain(
            project.Descendants(),
            static element => element.Name.LocalName == "PublishReadyToRun");
        Assert.DoesNotContain(
            project.Descendants(),
            static element => string.Equals(
                (string?)element.Attribute("Name"),
                "RejectUnsupportedArcanumNativeAotPublish",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Shipping_aot_uses_trim_visible_mvc_metadata_configuration_without_native_debug_symbols()
    {
        string repositoryRoot = FindRepositoryRoot();
        string projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "RetroDownfall.Arcanum.Cli",
            "RetroDownfall.Arcanum.Cli.csproj");
        XDocument project = XDocument.Load(projectPath);
        XElement nativeDebugSymbols = Assert.Single(
            project.Descendants(),
            static element => element.Name.LocalName == "NativeDebugSymbols");
        XElement debugType = Assert.Single(
            project.Descendants(),
            static element => element.Name.LocalName == "DebugType");

        Assert.Equal("false", nativeDebugSymbols.Value.Trim(), ignoreCase: true);
        Assert.Equal("none", debugType.Value.Trim(), ignoreCase: true);
        Assert.True(IsRuntimeIdentifierGated(nativeDebugSymbols));
        Assert.True(IsRuntimeIdentifierGated(debugType));

        XElement mvcSwitch = Assert.Single(
            project.Descendants(),
            static element =>
                element.Name.LocalName == "RuntimeHostConfigurationOption"
                && string.Equals(
                    (string?)element.Attribute("Include"),
                    "Microsoft.AspNetCore.Mvc.ApiExplorer.IsEnhancedModelMetadataSupported",
                    StringComparison.Ordinal));

        Assert.Equal("false", (string?)mvcSwitch.Attribute("Value"), ignoreCase: true);
        Assert.Equal("true", (string?)mvcSwitch.Attribute("Trim"), ignoreCase: true);
        Assert.True(IsRuntimeIdentifierGated(mvcSwitch));

        string program = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "RetroDownfall.Arcanum.Cli",
            "Program.cs"));

        Assert.Contains(
            "Microsoft.AspNetCore.Mvc.ApiExplorer.IsEnhancedModelMetadataSupported",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain("IsEnhancedModelMetadataSupportEnabled", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Shipping_aot_suppresses_only_dependency_diagnostics_that_the_audit_reenables()
    {
        string repositoryRoot = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "RetroDownfall.Arcanum.Cli",
            "RetroDownfall.Arcanum.Cli.csproj"));
        XElement noWarn = Assert.Single(
            project.Descendants(),
            static element =>
                element.Name.LocalName == "NoWarn"
                && element.Value.Contains("IL2104", StringComparison.Ordinal));
        string condition = (string?)noWarn.Parent?.Attribute("Condition") ?? string.Empty;
        string[] diagnostics = noWarn.Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static diagnostic => diagnostic.StartsWith("IL", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["IL2104", "IL3002", "IL3053"], diagnostics);
        Assert.Contains("$(ArcanumAotDiagnosticAudit)", condition, StringComparison.Ordinal);
        Assert.Contains("$(RuntimeIdentifier)", condition, StringComparison.Ordinal);

        string audit = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "scripts",
            "verify-aot-il-warnings.sh"));

        Assert.Contains("-p:ArcanumAotDiagnosticAudit=true", audit, StringComparison.Ordinal);
    }

    [Fact]
    public void MacOS_native_aot_uses_the_official_portable_runtime_pack()
    {
        string repositoryRoot = FindRepositoryRoot();
        string buildPropertiesPath = Path.Combine(repositoryRoot, "Directory.Build.props");
        string buildTargetsPath = Path.Combine(repositoryRoot, "Directory.Build.targets");
        XDocument buildProperties = XDocument.Load(buildPropertiesPath);
        XDocument buildTargets = XDocument.Load(buildTargetsPath);

        XElement portablePack = Assert.Single(
            buildTargets.Descendants(),
            static element =>
                element.Name.LocalName == "PackageDownload"
                && string.Equals(
                    (string?)element.Attribute("Include"),
                    "Microsoft.NETCore.App.Runtime.NativeAOT.osx-arm64",
                    StringComparison.Ordinal));

        Assert.Equal(
            "[$(ArcanumPortableMacOsNativeAotPackageVersion)]",
            (string?)portablePack.Attribute("Version"));
        Assert.True(IsMacOsNativeAotGated(portablePack));

        XElement bundledPackPath = Assert.Single(
            buildTargets.Descendants(),
            static element => element.Name.LocalName == "_ArcanumBundledMacOsNativeAotPath");
        XElement[] bridgeAssignments = buildTargets
            .Descendants()
            .Where(static element => element.Name.LocalName == "_ArcanumRequiresPortableMacOsNativeAotBridge")
            .ToArray();
        XElement[] legacyLldAssignments = buildTargets
            .Descendants()
            .Where(static element => element.Name.LocalName == "_ArcanumRequiresLegacyMacOsLld")
            .ToArray();
        XElement bridgeRequired = Assert.Single(
            bridgeAssignments,
            static element => element.Value.Trim() == "true");
        XElement legacyLldRequired = Assert.Single(
            legacyLldAssignments,
            static element => element.Value.Trim() == "true");

        Assert.Contains("$(NetCoreRoot)packs/", bundledPackPath.Value, StringComparison.Ordinal);
        Assert.Contains("$(BundledNETCoreAppPackageVersion)", bundledPackPath.Value, StringComparison.Ordinal);
        Assert.Contains(
            bridgeAssignments,
            static element => element.Value.Trim() == "false" && element.Attribute("Condition") is null);
        Assert.Equal("true", bridgeRequired.Value.Trim());
        Assert.Contains("nonportable.txt", (string?)bridgeRequired.Attribute("Condition"), StringComparison.Ordinal);
        Assert.Contains(
            legacyLldAssignments,
            static element => element.Value.Trim() == "false" && element.Attribute("Condition") is null);
        Assert.Equal("true", legacyLldRequired.Value.Trim());
        Assert.Contains(
            "VersionLessThan('$(BundledNETCoreAppPackageVersion)', '10.0.12')",
            (string?)legacyLldRequired.Attribute("Condition"),
            StringComparison.Ordinal);

        XElement auditedPackageVersion = Assert.Single(
            buildTargets.Descendants(),
            static element =>
                element.Name.LocalName == "ArcanumPortableMacOsNativeAotPackageVersion");
        XElement[] auditedPackageHashes = buildTargets
            .Descendants()
            .Where(static element =>
                element.Name.LocalName == "ArcanumPortableMacOsNativeAotPackageSha512")
            .ToArray();

        Assert.Equal("$(BundledNETCoreAppPackageVersion)", auditedPackageVersion.Value.Trim());
        Assert.Equal(3, auditedPackageHashes.Length);
        Assert.Contains(
            auditedPackageHashes,
            static element => string.IsNullOrEmpty(element.Value) && element.Attribute("Condition") is null);
        Assert.Contains(
            auditedPackageHashes,
            static element =>
                ((string?)element.Attribute("Condition") ?? string.Empty).Contains(
                    "10.0.11",
                    StringComparison.Ordinal)
                && element.Value.Trim()
                    == "NIe+WI0m5L4HrZ9b+wOhCUkrT3WZyIC8MQuHGzkvJyfUyR8ImMXYJIu3TiJsdLHUkgkqPK92AGQt7CQIc6vydQ==");
        Assert.Contains(
            auditedPackageHashes,
            static element =>
                ((string?)element.Attribute("Condition") ?? string.Empty).Contains(
                    "10.0.12",
                    StringComparison.Ordinal)
                && element.Value.Trim()
                    == "Qt7NCMotrQFrpukryPBpKeF/AawvptJv+v+c0Q3L29Y/ptFO8nFor+g8xKasWq4XNLL/hT/x+QBC7gacjeiIUQ==");
        Assert.True(IsMacOsNativeAotGated(auditedPackageVersion));

        foreach (XElement auditedPackageHash in auditedPackageHashes)
        {
            Assert.True(IsMacOsNativeAotGated(auditedPackageHash));
        }

        string[] localProperties = ((string?)buildTargets.Root?.Attribute("TreatAsLocalProperty") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] expectedLocalProperties =
        [
            "ArcanumPortableMacOsNativeAotPackageSha512",
            "ArcanumPortableMacOsNativeAotPackageVersion",
            "IlcFrameworkNativePath",
            "IlcSdkPath",
            "_ArcanumBundledMacOsNativeAotPath",
            "_ArcanumCryptoArchivePrepared",
            "_ArcanumCryptoArchiveSource",
            "_ArcanumMacOsSdkPath",
            "_ArcanumPortableMacOsNativeAotPackagePath",
            "_ArcanumPortableMacOsNativeAotPackageRoot",
            "_ArcanumPortableMacOsNativeAotPackageSha512Path",
            "_ArcanumPortableNativeAotPath",
            "_ArcanumPreparedNativeAotPath",
            "_ArcanumRequiresLegacyMacOsLld",
            "_ArcanumRequiresPortableMacOsNativeAotBridge",
            "_ArcanumVerifiedPortableNativeAotContents",
            "_ArcanumVerifiedPortableNativeAotPackage",
            "_ArcanumVerifiedPortableNativeAotRoot",
        ];

        Assert.Equal(
            expectedLocalProperties.Order(StringComparer.Ordinal),
            localProperties.Order(StringComparer.Ordinal));
        XElement collectPortablePack = Assert.Single(
            portablePack.Ancestors(),
            static element => string.Equals(
                (string?)element.Attribute("Name"),
                "CollectArcanumPortableMacOsNativeAotPack",
                StringComparison.Ordinal));
        Assert.Equal("CollectPackageDownloads", (string?)collectPortablePack.Attribute("BeforeTargets"));
        Assert.Contains(
            "$(_ArcanumRequiresPortableMacOsNativeAotBridge)' == 'true'",
            (string?)collectPortablePack.Attribute("Condition"),
            StringComparison.Ordinal);

        XElement rejectUnauditedPackage = Assert.Single(
            collectPortablePack.Elements(),
            static element => string.Equals(
                (string?)element.Attribute("Code"),
                "ARCAOT005",
                StringComparison.Ordinal));
        XElement collectPackageDownload = Assert.Single(
            collectPortablePack.Elements(),
            static element => element.Descendants().Any(
                descendant => descendant.Name.LocalName == "PackageDownload"));

        Assert.True(
            Array.IndexOf(collectPortablePack.Elements().ToArray(), rejectUnauditedPackage)
                < Array.IndexOf(collectPortablePack.Elements().ToArray(), collectPackageDownload));
        Assert.DoesNotContain(
            buildProperties
                .Descendants()
                .Concat(buildTargets.Descendants()),
            static element =>
                element.Name.LocalName == "PackageReference"
                && string.Equals(
                    (string?)element.Attribute("Include"),
                    "Microsoft.NETCore.App.Runtime.NativeAOT.osx-arm64",
                    StringComparison.Ordinal));

        XElement[] portablePaths = buildTargets
            .Descendants()
            .Where(static element => element.Name.LocalName == "_ArcanumPortableNativeAotPath")
            .ToArray();
        XElement officialPortablePath = Assert.Single(
            portablePaths,
            static element => element.Value.Trim() == "$(IlcFrameworkNativePath)");
        XElement verifiedPortablePath = Assert.Single(
            portablePaths,
            static element => element.Value.Trim()
                == "$(_ArcanumVerifiedPortableNativeAotContents)runtimes/osx-arm64/native/");
        XElement packageRoot = Assert.Single(
            buildTargets.Descendants(),
            static element =>
                element.Name.LocalName == "_ArcanumPortableMacOsNativeAotPackageRoot");

        Assert.Contains(
            "$(NuGetPackageRoot)microsoft.netcore.app.runtime.nativeaot.osx-arm64/$(ArcanumPortableMacOsNativeAotPackageVersion)",
            packageRoot.Value,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$(_ArcanumPortableMacOsNativeAotPackageRoot)",
            verifiedPortablePath.Value,
            StringComparison.Ordinal);
        Assert.Contains(
            "$(_ArcanumRequiresPortableMacOsNativeAotBridge)' != 'true'",
            (string?)officialPortablePath.Attribute("Condition"),
            StringComparison.Ordinal);
        Assert.Equal(
            "PrepareArcanumMacOsCryptoArchive",
            (string?)officialPortablePath.Parent?.Parent?.Attribute("Name"));
        Assert.True(IsMacOsNativeAotGated(officialPortablePath));
        Assert.True(IsMacOsNativeAotGated(verifiedPortablePath));
        Assert.True(IsMacOsNativeAotGated(packageRoot));

        string[] linkerArguments = buildProperties
            .Descendants()
            .Where(static element => element.Name.LocalName == "LinkerArg")
            .Concat(buildTargets
                .Descendants()
                .Where(static element => element.Name.LocalName == "LinkerArg"))
            .Select(static element => (string?)element.Attribute("Include") ?? string.Empty)
            .ToArray();

        Assert.Contains("-Wl,-Z", linkerArguments, StringComparer.Ordinal);
        Assert.Contains(
            linkerArguments,
            static argument => argument.Contains("$(_ArcanumMacOsSdkPath)/usr/lib", StringComparison.Ordinal));
        Assert.Contains(
            linkerArguments,
            static argument => argument.Contains(
                "$(_ArcanumMacOsSdkPath)/System/Library/Frameworks",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            buildProperties.Descendants(),
            static element =>
                element.Name.LocalName == "LinkerArg"
                && ((string?)element.Attribute("Include") ?? string.Empty).Contains(
                    "ld64.lld",
                    StringComparison.Ordinal));

        XElement[] legacyLinkerArguments = buildTargets
            .Descendants()
            .Where(static element =>
                element.Name.LocalName == "LinkerArg"
                && ((string?)element.Attribute("Include") ?? string.Empty).Contains(
                    "ld64.lld",
                    StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, legacyLinkerArguments.Length);

        foreach (XElement legacyLinkerArgument in legacyLinkerArguments)
        {
            Assert.Contains(
                "$(_ArcanumRequiresLegacyMacOsLld)' == 'true'",
                (string?)legacyLinkerArgument.Attribute("Condition"),
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            linkerArguments,
            static argument =>
                argument.Contains("openssl", StringComparison.OrdinalIgnoreCase)
                || argument.Contains("brotli", StringComparison.OrdinalIgnoreCase));

        string targetsText = File.ReadAllText(buildTargetsPath);

        Assert.Contains("ARCAOT001", targetsText, StringComparison.Ordinal);
        Assert.Contains("ARCAOT002", targetsText, StringComparison.Ordinal);
        Assert.Contains("ARCAOT003", targetsText, StringComparison.Ordinal);
        Assert.Contains("ARCAOT005", targetsText, StringComparison.Ordinal);
        Assert.Contains("ARCAOT006", targetsText, StringComparison.Ordinal);
        Assert.Contains("ARCAOT007", targetsText, StringComparison.Ordinal);
        Assert.Contains("ARCAOT008", targetsText, StringComparison.Ordinal);
        Assert.Contains("ReadLinesFromFile", targetsText, StringComparison.Ordinal);
        Assert.Contains("VerifyFileHash", targetsText, StringComparison.Ordinal);
        Assert.Contains("HashEncoding=\"base64\"", targetsText, StringComparison.Ordinal);
        Assert.Contains(".nupkg.sha512", targetsText, StringComparison.Ordinal);
        Assert.Contains(
            "$(ArcanumPortableMacOsNativeAotPackageSha512)' == ''",
            targetsText,
            StringComparison.Ordinal);
        Assert.Contains(
            "@(_ArcanumPortableMacOsNativeAotPackageHash)' != '$(ArcanumPortableMacOsNativeAotPackageSha512)",
            targetsText,
            StringComparison.Ordinal);
        Assert.Contains("libbrotlicommon.a", targetsText, StringComparison.Ordinal);
        Assert.Contains("libz.a", targetsText, StringComparison.Ordinal);
        Assert.Contains("nonportable.txt", targetsText, StringComparison.Ordinal);

        XElement requirePortablePack = Assert.Single(
            buildTargets.Descendants(),
            static element => string.Equals(
                (string?)element.Attribute("Name"),
                "RequireArcanumPortableMacOsNativeAotPack",
                StringComparison.Ordinal));
        Assert.Contains(
            "$(_ArcanumRequiresPortableMacOsNativeAotBridge)' == 'true'",
            (string?)requirePortablePack.Attribute("Condition"),
            StringComparison.Ordinal);
        XElement verifiedRoot = Assert.Single(
            requirePortablePack.Descendants(),
            static element => element.Name.LocalName == "_ArcanumVerifiedPortableNativeAotRoot");
        XElement verifiedPackage = Assert.Single(
            requirePortablePack.Descendants(),
            static element => element.Name.LocalName == "_ArcanumVerifiedPortableNativeAotPackage");
        XElement verifiedContents = Assert.Single(
            requirePortablePack.Descendants(),
            static element => element.Name.LocalName == "_ArcanumVerifiedPortableNativeAotContents");
        XElement cleanVerifiedRoot = Assert.Single(
            requirePortablePack.Elements(),
            static element =>
                element.Name.LocalName == "RemoveDir"
                && string.Equals(
                    (string?)element.Attribute("Directories"),
                    "$(_ArcanumVerifiedPortableNativeAotRoot)",
                    StringComparison.Ordinal));
        XElement stageVerifiedPackage = Assert.Single(
            requirePortablePack.Elements(),
            static element =>
                element.Name.LocalName == "Copy"
                && string.Equals(
                    (string?)element.Attribute("SourceFiles"),
                    "$(_ArcanumPortableMacOsNativeAotPackagePath)",
                    StringComparison.Ordinal));
        XElement[] packageHashChecks = requirePortablePack
            .Elements()
            .Where(static element => element.Name.LocalName == "VerifyFileHash")
            .ToArray();
        XElement cachedPackageHashCheck = Assert.Single(
            packageHashChecks,
            static element => string.Equals(
                (string?)element.Attribute("File"),
                "$(_ArcanumPortableMacOsNativeAotPackagePath)",
                StringComparison.Ordinal));
        XElement stagedPackageHashCheck = Assert.Single(
            packageHashChecks,
            static element => string.Equals(
                (string?)element.Attribute("File"),
                "$(_ArcanumVerifiedPortableNativeAotPackage)",
                StringComparison.Ordinal));
        XElement extractVerifiedPackage = Assert.Single(
            requirePortablePack.Elements(),
            static element => element.Name.LocalName == "Unzip");
        XElement validateExtractedRuntime = Assert.Single(
            requirePortablePack.Elements(),
            static element => string.Equals(
                (string?)element.Attribute("Code"),
                "ARCAOT001",
                StringComparison.Ordinal));

        Assert.Contains("$(NativeIntermediateOutputPath)", verifiedRoot.Value, StringComparison.Ordinal);
        Assert.Equal(
            "$(_ArcanumVerifiedPortableNativeAotRoot)runtime-pack.nupkg",
            verifiedPackage.Value.Trim());
        Assert.Equal(
            "$(_ArcanumVerifiedPortableNativeAotRoot)contents/",
            verifiedContents.Value.Trim());
        Assert.Equal(
            "$(_ArcanumVerifiedPortableNativeAotPackage)",
            (string?)stageVerifiedPackage.Attribute("DestinationFiles"));
        Assert.Equal(
            "$(_ArcanumVerifiedPortableNativeAotPackage)",
            (string?)extractVerifiedPackage.Attribute("SourceFiles"));
        Assert.Equal(
            "$(_ArcanumVerifiedPortableNativeAotContents)",
            (string?)extractVerifiedPackage.Attribute("DestinationFolder"));

        XElement[] requireSteps = requirePortablePack.Elements().ToArray();

        Assert.True(Array.IndexOf(requireSteps, cachedPackageHashCheck) < Array.IndexOf(requireSteps, cleanVerifiedRoot));
        Assert.True(Array.IndexOf(requireSteps, cleanVerifiedRoot) < Array.IndexOf(requireSteps, stageVerifiedPackage));
        Assert.True(Array.IndexOf(requireSteps, stageVerifiedPackage) < Array.IndexOf(requireSteps, stagedPackageHashCheck));
        Assert.True(Array.IndexOf(requireSteps, stagedPackageHashCheck) < Array.IndexOf(requireSteps, extractVerifiedPackage));
        Assert.True(Array.IndexOf(requireSteps, extractVerifiedPackage) < Array.IndexOf(requireSteps, validateExtractedRuntime));

        XElement prepareCryptoArchive = Assert.Single(
            buildTargets.Descendants(),
            static element => string.Equals(
                (string?)element.Attribute("Name"),
                "PrepareArcanumMacOsCryptoArchive",
                StringComparison.Ordinal));

        Assert.DoesNotContain(
            "$(_ArcanumRequiresPortableMacOsNativeAotBridge)",
            (string?)prepareCryptoArchive.Attribute("Condition"),
            StringComparison.Ordinal);

        Assert.True(IsMacOsNativeAotGated(prepareCryptoArchive));
        Assert.Equal("SetupOSSpecificProps", (string?)prepareCryptoArchive.Attribute("BeforeTargets"));
        Assert.Equal(
            "SetupProperties;RequireArcanumPortableMacOsNativeAotPack",
            (string?)prepareCryptoArchive.Attribute("DependsOnTargets"));
        Assert.Null(prepareCryptoArchive.Attribute("AfterTargets"));
        XElement cryptoArchiveSource = Assert.Single(
            prepareCryptoArchive.Descendants(),
            static element => element.Name.LocalName == "_ArcanumCryptoArchiveSource");
        XElement frameworkPath = Assert.Single(
            prepareCryptoArchive.Descendants(),
            static element => element.Name.LocalName == "IlcFrameworkNativePath");
        XElement sdkPath = Assert.Single(
            prepareCryptoArchive.Descendants(),
            static element => element.Name.LocalName == "IlcSdkPath");

        Assert.Equal(
            "$(_ArcanumPortableNativeAotPath)libSystem.Security.Cryptography.Native.Apple.a",
            cryptoArchiveSource.Value.Trim());
        Assert.Equal("$(_ArcanumPreparedNativeAotPath)", frameworkPath.Value.Trim());
        Assert.Equal(frameworkPath.Value.Trim(), sdkPath.Value.Trim());
        Assert.True(IsMacOsNativeAotGated(frameworkPath));
        Assert.True(IsMacOsNativeAotGated(sdkPath));
        Assert.Contains("libSystem.Security.Cryptography.Native.Apple.a", targetsText, StringComparison.Ordinal);
        Assert.Contains("<Copy", targetsText, StringComparison.Ordinal);
        Assert.Contains("strip -S", targetsText, StringComparison.Ordinal);
        Assert.Contains("_ArcanumPortableNativeAotFile", targetsText, StringComparison.Ordinal);
        Assert.Contains(
            "<IlcFrameworkNativePath>$(_ArcanumPreparedNativeAotPath)</IlcFrameworkNativePath>",
            targetsText,
            StringComparison.Ordinal);
        Assert.Contains(
            "<IlcSdkPath>$(_ArcanumPreparedNativeAotPath)</IlcSdkPath>",
            targetsText,
            StringComparison.Ordinal);

        XDocument cliProject = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "RetroDownfall.Arcanum.Cli",
            "RetroDownfall.Arcanum.Cli.csproj"));

        Assert.DoesNotContain(
            cliProject.Descendants(),
            static element =>
                element.Name.LocalName == "_AppleLldPath"
                || string.Equals((string?)element.Attribute("Code"), "ARC0002", StringComparison.Ordinal));

        XElement stripSymbols = Assert.Single(
            cliProject.Descendants(),
            static element => element.Name.LocalName == "StripSymbols");
        string stripCondition = (string?)stripSymbols.Parent?.Attribute("Condition") ?? string.Empty;

        Assert.Contains("$(RuntimeIdentifier)", stripCondition, StringComparison.Ordinal);
        Assert.Contains("StartsWith('osx')", stripCondition, StringComparison.Ordinal);
        Assert.Contains("$(PublishAot)", stripCondition, StringComparison.Ordinal);
        Assert.DoesNotContain("$(_TargetsApple)", stripCondition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MacOS_native_aot_linker_and_pack_selection_ignore_caller_global_overrides()
    {
        string temporaryRoot = Directory
            .CreateTempSubdirectory("arcanum-nativeaot-selection-")
            .FullName;

        try
        {
            string projectPath = Path.Combine(temporaryRoot, "Selection.proj");
            XDocument project = new(
                new XElement(
                    "Project",
                    new XElement(
                        "PropertyGroup",
                        new XElement("RuntimeIdentifier", "osx-arm64"),
                        new XElement("PublishAot", "true"),
                        new XElement("BundledNETCoreAppPackageVersion", "10.0.99"),
                        new XElement("NetCoreRoot", temporaryRoot + Path.DirectorySeparatorChar),
                        new XElement("NuGetPackageRoot", temporaryRoot + Path.DirectorySeparatorChar),
                        new XElement("NativeIntermediateOutputPath", temporaryRoot + Path.DirectorySeparatorChar)),
                    new XElement(
                        "Import",
                        new XAttribute("Project", Path.Combine(FindRepositoryRoot(), "Directory.Build.targets"))),
                    new XElement(
                        "Target",
                        new XAttribute("Name", "AssertSelection"),
                        new XElement(
                            "Error",
                            new XAttribute(
                                "Condition",
                                "'$(_ArcanumRequiresPortableMacOsNativeAotBridge)' != 'false'"),
                            new XAttribute("Text", "The bridge decision remained caller-controlled.")),
                        new XElement(
                            "Error",
                            new XAttribute(
                                "Condition",
                                "'$(_ArcanumRequiresLegacyMacOsLld)' != 'false'"),
                            new XAttribute("Text", "The linker decision remained caller-controlled.")),
                        new XElement(
                            "Error",
                            new XAttribute(
                                "Condition",
                                "'$(ArcanumPortableMacOsNativeAotPackageVersion)' != '10.0.99'"),
                            new XAttribute("Text", "The package version remained caller-controlled.")),
                        new XElement(
                            "Error",
                            new XAttribute(
                                "Condition",
                                "'$(ArcanumPortableMacOsNativeAotPackageSha512)' != ''"),
                            new XAttribute("Text", "The package hash remained caller-controlled.")))));

            project.Save(projectPath);

            global::System.Diagnostics.ProcessStartInfo start = new("dotnet")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            start.ArgumentList.Add("msbuild");
            start.ArgumentList.Add(projectPath);
            start.ArgumentList.Add("-t:AssertSelection");
            start.ArgumentList.Add("-p:_ArcanumRequiresPortableMacOsNativeAotBridge=true");
            start.ArgumentList.Add("-p:_ArcanumRequiresLegacyMacOsLld=true");
            start.ArgumentList.Add("-p:ArcanumPortableMacOsNativeAotPackageVersion=CALLER");
            start.ArgumentList.Add("-p:ArcanumPortableMacOsNativeAotPackageSha512=CALLER");
            start.ArgumentList.Add("-nodeReuse:false");
            start.ArgumentList.Add("-v:minimal");
            start.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";

            using global::System.Diagnostics.Process process = new() { StartInfo = start };

            Assert.True(process.Start());

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

            await process.WaitForExitAsync(timeout.Token);

            string output = await standardOutput + await standardError;

            Assert.True(process.ExitCode == 0, output);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public Task MacOS_native_aot_hashes_the_package_bytes_instead_of_trusting_nugets_record() =>
        AssertTamperedPackageRejectedAsync(
            recordedHash:
                "NIe+WI0m5L4HrZ9b+wOhCUkrT3WZyIC8MQuHGzkvJyfUyR8ImMXYJIu3TiJsdLHUkgkqPK92AGQt7CQIc6vydQ==",
            overriddenHash: null,
            expectedDiagnostic: "MSB3952");

    [Fact]
    public Task MacOS_native_aot_package_hash_pin_cannot_be_overridden_by_the_caller() =>
        AssertTamperedPackageRejectedAsync(
            recordedHash:
                "scNTItQptu9RlVE4qf6hke/EcUc3CdkHaTCsgjdltzBvaYi6oo/VKckg9zzdQlCGqNH596bKGeEpTcW1ojKHag==",
            overriddenHash:
                "scNTItQptu9RlVE4qf6hke/EcUc3CdkHaTCsgjdltzBvaYi6oo/VKckg9zzdQlCGqNH596bKGeEpTcW1ojKHag==",
            expectedDiagnostic: "ARCAOT007");

    [Fact]
    public async Task MacOS_native_aot_ignores_caller_path_redirects_and_uses_the_verified_package()
    {
        const string version = "10.0.11";
        byte[] verifiedArchive = Encoding.UTF8.GetBytes("archive from the verified package");
        byte[] tamperedArchive = Encoding.UTF8.GetBytes("tampered global package-cache extraction");
        string temporaryRoot = Directory
            .CreateTempSubdirectory("arcanum-nativeaot-consumption-")
            .FullName;

        try
        {
            string packageContentRoot = Path.Combine(temporaryRoot, "package-content");
            string netCoreRoot = Path.Combine(temporaryRoot, "dotnet");
            string bundledNativeRoot = Path.Combine(
                netCoreRoot,
                "packs",
                "Microsoft.NETCore.App.Runtime.NativeAOT.osx-arm64",
                version,
                "runtimes",
                "osx-arm64",
                "native");
            string packageContentNativeRoot = Path.Combine(
                packageContentRoot,
                "runtimes",
                "osx-arm64",
                "native");
            string packageRoot = Path.Combine(
                temporaryRoot,
                "packages",
                "microsoft.netcore.app.runtime.nativeaot.osx-arm64",
                version);
            string globalCacheNativeRoot = Path.Combine(packageRoot, "runtimes", "osx-arm64", "native");
            string packagePath = Path.Combine(
                packageRoot,
                $"microsoft.netcore.app.runtime.nativeaot.osx-arm64.{version}.nupkg");

            Directory.CreateDirectory(bundledNativeRoot);
            File.WriteAllText(Path.Combine(bundledNativeRoot, "nonportable.txt"), string.Empty);
            Directory.CreateDirectory(packageContentNativeRoot);
            Directory.CreateDirectory(globalCacheNativeRoot);
            File.WriteAllBytes(
                Path.Combine(packageContentNativeRoot, "libRuntime.WorkstationGC.a"),
                verifiedArchive);
            File.WriteAllBytes(Path.Combine(packageContentNativeRoot, "libbrotlicommon.a"), []);
            File.WriteAllBytes(Path.Combine(packageContentNativeRoot, "libz.a"), []);
            ZipFile.CreateFromDirectory(packageContentRoot, packagePath);

            string packageHash = Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(packagePath)));

            File.WriteAllText(packagePath + ".sha512", packageHash);
            File.WriteAllBytes(
                Path.Combine(globalCacheNativeRoot, "libRuntime.WorkstationGC.a"),
                tamperedArchive);
            File.WriteAllBytes(Path.Combine(globalCacheNativeRoot, "libbrotlicommon.a"), []);
            File.WriteAllBytes(Path.Combine(globalCacheNativeRoot, "libz.a"), []);

            string intermediateRoot = Path.Combine(temporaryRoot, "intermediate");
            string callerControlledRoot = Path.Combine(temporaryRoot, "caller-controlled");
            string testProject = Path.Combine(temporaryRoot, "VerifiedArchiveConsumption.proj");
            string verifiedArchiveHash = Convert.ToBase64String(SHA256.HashData(verifiedArchive));
            XDocument project = new(
                new XElement(
                    "Project",
                    new XElement(
                        "PropertyGroup",
                        new XElement("RuntimeIdentifier", "osx-arm64"),
                        new XElement("PublishAot", "true"),
                        new XElement("NetCoreRoot", netCoreRoot + Path.DirectorySeparatorChar),
                        new XElement("NuGetPackageRoot", Path.Combine(temporaryRoot, "packages") + Path.DirectorySeparatorChar),
                        new XElement("NativeIntermediateOutputPath", intermediateRoot + Path.DirectorySeparatorChar),
                        new XElement("BundledNETCoreAppPackageVersion", version)),
                    new XElement(
                        "Import",
                        new XAttribute("Project", Path.Combine(FindRepositoryRoot(), "Directory.Build.targets"))),
                    new XElement(
                        "PropertyGroup",
                        new XElement("ArcanumPortableMacOsNativeAotPackageSha512", packageHash)),
                    new XElement(
                        "Target",
                        new XAttribute("Name", "AssertVerifiedArchiveConsumption"),
                        new XAttribute("DependsOnTargets", "RequireArcanumPortableMacOsNativeAotPack"),
                        new XElement(
                            "VerifyFileHash",
                            new XAttribute(
                                "File",
                                "$(_ArcanumPortableNativeAotPath)libRuntime.WorkstationGC.a"),
                            new XAttribute("Hash", verifiedArchiveHash),
                            new XAttribute("Algorithm", "SHA256"),
                            new XAttribute("HashEncoding", "base64")))));

            project.Save(testProject);

            global::System.Diagnostics.ProcessStartInfo start = new("dotnet")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            start.ArgumentList.Add("msbuild");
            start.ArgumentList.Add(testProject);
            start.ArgumentList.Add("-t:AssertVerifiedArchiveConsumption");
            start.ArgumentList.Add(
                $"-p:_ArcanumPortableNativeAotPath={globalCacheNativeRoot}{Path.DirectorySeparatorChar}");
            start.ArgumentList.Add(
                $"-p:_ArcanumPortableMacOsNativeAotPackageRoot={callerControlledRoot}{Path.DirectorySeparatorChar}");
            start.ArgumentList.Add(
                $"-p:_ArcanumPortableMacOsNativeAotPackagePath={Path.Combine(callerControlledRoot, "missing.nupkg")}");
            start.ArgumentList.Add(
                $"-p:_ArcanumPortableMacOsNativeAotPackageSha512Path={Path.Combine(callerControlledRoot, "missing.nupkg.sha512")}");
            start.ArgumentList.Add("-nodeReuse:false");
            start.ArgumentList.Add("-v:minimal");
            start.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";

            using global::System.Diagnostics.Process process = new() { StartInfo = start };

            Assert.True(process.Start());

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

            await process.WaitForExitAsync(timeout.Token);

            string output = await standardOutput + await standardError;

            Assert.True(process.ExitCode == 0, output);
            Assert.Equal(
                tamperedArchive,
                File.ReadAllBytes(Path.Combine(globalCacheNativeRoot, "libRuntime.WorkstationGC.a")));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static async Task AssertTamperedPackageRejectedAsync(
        string recordedHash,
        string? overriddenHash,
        string expectedDiagnostic)
    {
        const string version = "10.0.11";
        string temporaryRoot = Directory
            .CreateTempSubdirectory("arcanum-nativeaot-package-")
            .FullName;

        try
        {
            string netCoreRoot = Path.Combine(temporaryRoot, "dotnet");
            string bundledNativeRoot = Path.Combine(
                netCoreRoot,
                "packs",
                "Microsoft.NETCore.App.Runtime.NativeAOT.osx-arm64",
                version,
                "runtimes",
                "osx-arm64",
                "native");
            string packageRoot = Path.Combine(
                temporaryRoot,
                "microsoft.netcore.app.runtime.nativeaot.osx-arm64",
                version);
            string nativeRoot = Path.Combine(packageRoot, "runtimes", "osx-arm64", "native");

            Directory.CreateDirectory(bundledNativeRoot);
            File.WriteAllText(Path.Combine(bundledNativeRoot, "nonportable.txt"), string.Empty);
            Directory.CreateDirectory(nativeRoot);
            File.WriteAllBytes(Path.Combine(nativeRoot, "libRuntime.WorkstationGC.a"), []);
            File.WriteAllBytes(Path.Combine(nativeRoot, "libbrotlicommon.a"), []);
            File.WriteAllBytes(Path.Combine(nativeRoot, "libz.a"), []);
            File.WriteAllText(
                Path.Combine(
                    packageRoot,
                    $"microsoft.netcore.app.runtime.nativeaot.osx-arm64.{version}.nupkg.sha512"),
                recordedHash);
            File.WriteAllText(
                Path.Combine(
                    packageRoot,
                    $"microsoft.netcore.app.runtime.nativeaot.osx-arm64.{version}.nupkg"),
                "tampered package bytes");

            string project = Path.Combine(
                FindRepositoryRoot(),
                "src",
                "RetroDownfall.Arcanum.Cli",
                "RetroDownfall.Arcanum.Cli.csproj");
            global::System.Diagnostics.ProcessStartInfo start = new("dotnet")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            start.ArgumentList.Add("msbuild");
            start.ArgumentList.Add(project);
            start.ArgumentList.Add("-t:RequireArcanumPortableMacOsNativeAotPack");
            start.ArgumentList.Add("-p:Configuration=Release");
            start.ArgumentList.Add("-p:RuntimeIdentifier=osx-arm64");
            start.ArgumentList.Add("-p:PublishAot=true");
            start.ArgumentList.Add(
                $"-p:NetCoreRoot={netCoreRoot}{Path.DirectorySeparatorChar}");
            start.ArgumentList.Add($"-p:BundledNETCoreAppPackageVersion={version}");
            start.ArgumentList.Add(
                $"-p:NuGetPackageRoot={temporaryRoot}{Path.DirectorySeparatorChar}");

            if (overriddenHash is not null)
            {
                start.ArgumentList.Add(
                    $"-p:ArcanumPortableMacOsNativeAotPackageSha512={overriddenHash}");
            }

            start.ArgumentList.Add("-nodeReuse:false");
            start.ArgumentList.Add("-v:minimal");
            start.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";

            using global::System.Diagnostics.Process process = new() { StartInfo = start };

            Assert.True(process.Start());

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

            await process.WaitForExitAsync(timeout.Token);

            string output = await standardOutput + await standardError;

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains(expectedDiagnostic, output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void Projects_without_entity_framework_queries_disable_experimental_query_precompilation()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] stateProjectDirectories =
        [
            Path.Combine(repositoryRoot, "src", "RetroDownfall.Arcanum.Infrastructure"),
            Path.Combine(repositoryRoot, "src", "RetroDownfall.Arcanum.Api"),
        ];

        foreach (string projectDirectory in stateProjectDirectories)
        {
            string projectPath = Assert.Single(Directory.EnumerateFiles(projectDirectory, "*.csproj"));
            XDocument project = XDocument.Load(projectPath);

            Assert.DoesNotContain(
                project.Descendants(),
                static element =>
                    element.Name.LocalName == "PackageReference"
                    && string.Equals(
                        (string?)element.Attribute("Include"),
                        "Microsoft.EntityFrameworkCore.Tasks",
                        StringComparison.Ordinal));
            Assert.Equal(
                "false",
                Assert.Single(
                    project.Descendants(),
                    static element => element.Name.LocalName == "EFOptimizeContext")
                    .Value
                    .Trim(),
                ignoreCase: true);
            Assert.DoesNotContain(
                project.Descendants(),
                static element =>
                    element.Name.LocalName == "EFPrecompileQueriesStage"
                    && !string.Equals(element.Value.Trim(), "none", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                project.Descendants(),
                static element =>
                    element.Name.LocalName == "InterceptorsNamespaces"
                    && element.Value.Contains(
                        "Microsoft.EntityFrameworkCore.GeneratedInterceptors",
                        StringComparison.Ordinal));
        }

        string optionsConfigurator = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "RetroDownfall.Arcanum.Infrastructure",
            "Data",
            "ArcanumDbContextOptionsConfigurator.cs"));

        Assert.Contains("UseModel(ArcanumDbContextModel.Instance)", optionsConfigurator, StringComparison.Ordinal);
    }

    /// <summary>
    /// MSBuild properties the SDK turns into runtimeconfig <c>configProperties</c> for every build,
    /// not only for a Native AOT publish: <c>Microsoft.NET.Sdk.targets</c> emits each
    /// <c>RuntimeHostConfigurationOption</c> on the sole condition that the property is set, and
    /// <c>PublishAot</c> pulls in the ILCompiler targets that force <c>DynamicCodeSupport=false</c>,
    /// <c>EventSourceSupport=false</c> and <c>CanEmitObjectArrayDelegate=false</c>. Leaving any of
    /// them ungated by <c>RuntimeIdentifier</c> stamps AOT feature switches into ordinary
    /// Debug/Release hosts, where <c>UseSystemResourceKeys=true</c> degrades every BCL exception
    /// message to a bare resource key and the other switches misrepresent the runtime's capabilities.
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

    private static bool IsMacOsNativeAotGated(XElement element)
    {
        string condition = string.Concat(
            element
                .AncestorsAndSelf()
                .Select(static ancestor => (string?)ancestor.Attribute("Condition") ?? string.Empty));

        return condition.Contains("osx-arm64", StringComparison.Ordinal)
            && condition.Contains("$(PublishAot)", StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        string sourceDirectory = Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("The test source path has no directory.");

        foreach (string startDirectory in new[] { sourceDirectory, global::System.Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(startDirectory);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
