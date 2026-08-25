# Package Arcanum (Native AOT) + Compendium (+ optional The Forge) for Windows.
#
# Defaults to win-x64 on the current Windows host. Cross-OS packaging is handled
# by GitHub Actions.
#
# Usage:
#   .\package-windows.ps1 -Version 0.1.0-beta.1 -OutputDir .\dist
#   .\package-windows.ps1 -Version 0.1.0-beta.1 -OutputDir .\dist -SkipForge
#   .\package-windows.ps1 -Version 0.1.0-beta.1 -OutputDir .\dist -Sign
#
# -SkipForge omits The Forge zip (used by the Arcanum + Compendium Windows build action).
# -Sign requires Windows Authenticode credentials via env:
#   WINDOWS_CERT_PATH  — path to .pfx
#   WINDOWS_CERT_PASSWORD — certificate password; consumed only to import the PFX into a
#     transient Cert:\CurrentUser\My entry, never passed to signtool on a command line.
# Missing credentials with -Sign causes a clear failure (unsigned builds do not).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [string]$Rid = "win-x64",

    [switch]$SkipForge,

    [switch]$Sign
)

$ErrorActionPreference = "Stop"

if ($Version -match '\+') {
    throw "SemVer build metadata (+...) is not allowed: '$Version'"
}

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$') {
    throw "Invalid SemVer '$Version'"
}

if ($IsLinux -or $IsMacOS) {
    throw "package-windows.ps1 must run on Windows. Use GitHub Actions for cross-OS artifacts."
}

# Both shipping Windows RIDs. The restriction to win-x64 was a guard rather than a limitation:
# everything below that named the architecture was a folder or archive name, and the publish itself
# only ever passed $Rid through to dotnet. A release that ships one Windows architecture and calls
# itself a Windows release is the thing worth preventing, so the list is explicit rather than open.
if ($Rid -notin @("win-x64", "win-arm64")) {
    throw "Unsupported RID '$Rid' (expected win-x64 or win-arm64)"
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..\..\..")).Path
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$Work = Join-Path ([System.IO.Path]::GetTempPath()) ("arcanum-win-pack-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $Work | Out-Null

$script:SignThumbprint = $null
$script:SignCertImported = $false

# The Native AOT image does NOT absorb the P/Invoke shared libraries. SQLitePCLRaw only
# static-links e_sqlcipher for browser-wasm, so every win-x64 publish emits e_sqlcipher.dll
# (and libonigwrap.dll) beside the host. Shipping a zip without them produces a CLI that
# dies with DllNotFoundException the moment it opens the Grimoire, and nothing downstream
# launches the binary — so assert here and fail the build loudly.
function Assert-StagedNatives {
    param(
        [Parameter(Mandatory = $true)]
        [string]$StageDir,

        [Parameter(Mandatory = $true)]
        [string[]]$Names
    )

    $missing = @($Names | Where-Object { -not (Test-Path -LiteralPath (Join-Path $StageDir $_)) })
    if ($missing.Count -gt 0) {
        Get-ChildItem -LiteralPath $StageDir | Format-Table Name, Length | Out-String | Write-Host
        throw "Staged package is missing required native libraries: $($missing -join ', '). Expected them beside the host in $StageDir; the archive would be unusable."
    }

    Write-Host "==> Verified native sidecars in ${StageDir}: $($Names -join ', ')"
}

try {
    if ($Sign) {
        $certPath = $env:WINDOWS_CERT_PATH
        $certPassword = $env:WINDOWS_CERT_PASSWORD
        if ([string]::IsNullOrWhiteSpace($certPath) -or -not (Test-Path -LiteralPath $certPath)) {
            throw "-Sign requested but WINDOWS_CERT_PATH is missing or not a file."
        }
        if ($null -eq $certPassword) {
            throw "-Sign requested but WINDOWS_CERT_PASSWORD is not set."
        }
        if (-not (Get-Command signtool -ErrorAction SilentlyContinue)) {
            throw "signtool not found on PATH; install Windows SDK signing tools or omit -Sign."
        }

        # Never hand the PFX password to signtool on the command line (CWE-214): a child
        # process command line is visible to local process auditing (4688/Sysmon/EDR) and
        # to any concurrent local user. Import once, then sign by thumbprint.
        Write-Host "==> Importing signing certificate into Cert:\CurrentUser\My"
        $thumbprintsBefore = [string[]] @(Get-ChildItem -Path Cert:\CurrentUser\My | Select-Object -ExpandProperty Thumbprint)
        $securePassword = ConvertTo-SecureString -String $certPassword -AsPlainText -Force
        $imported = @(Import-PfxCertificate -FilePath $certPath -CertStoreLocation Cert:\CurrentUser\My -Password $securePassword)
        $securePassword = $null
        $certPassword = $null
        $leaf = $imported | Where-Object { $_.HasPrivateKey } | Select-Object -First 1
        if ($null -eq $leaf) {
            throw "WINDOWS_CERT_PATH did not yield a certificate with a private key."
        }
        $script:SignThumbprint = $leaf.Thumbprint
        # Only reclaim what this run actually added, so a developer's pre-installed
        # certificate survives the cleanup in the finally block.
        $script:SignCertImported = -not ($thumbprintsBefore -contains $script:SignThumbprint)
    }

    function Invoke-AuthenticodeSign {
        param(
            [Parameter(Mandatory = $true)]
            [string[]]$Path
        )

        & signtool sign /sha1 $script:SignThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 @Path
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $($Path -join ', ')" }
    }

    # An Authenticode signature covers one file, so every PE in the staged tree needs its own.
    # The CLI zip ships e_sqlcipher.dll and libonigwrap.dll beside the host, and the GUI zips are
    # not AOT — their .exe is an apphost stub with all first-party code in neighbouring
    # RetroDownfall.*.dll. Signing only the .exe leaves the code that actually executes unsigned
    # and outside any WDAC/AppLocker publisher rule, while the archive looks signed. This is the
    # Windows counterpart of sign_publish_dir in scripts/packaging/macos/common.sh.
    function Invoke-StageAuthenticodeSign {
        param(
            [Parameter(Mandatory = $true)]
            [string]$StageDir
        )

        $all = @(
            Get-ChildItem -LiteralPath $StageDir -File -Recurse |
                Where-Object { $_.Extension -eq ".exe" -or $_.Extension -eq ".dll" } |
                ForEach-Object { $_.FullName }
        )

        if ($all.Count -eq 0) {
            throw "No .exe or .dll found under $StageDir; refusing to archive an unverified tree."
        }

        # The Microsoft runtime assemblies arrive already signed by Microsoft; re-signing would
        # replace that provenance with ours for no gain. Sign whatever is not already valid, so
        # that every PE in the archive carries a signature once this returns.
        $unsigned = @($all | Where-Object {
                (Get-AuthenticodeSignature -LiteralPath $_).Status -ne "Valid"
            })

        Write-Host "==> Authenticode signing $($unsigned.Count) of $($all.Count) file(s) under $StageDir"

        # Batch the calls: a self-contained Avalonia publish is a few hundred assemblies, and one
        # timestamp round trip per file invites rate limiting from the timestamp authority.
        $batchSize = 40

        # Verify what this pass signed, the way sign_publish_dir verifies each item it signs.
        # The rest of the tree was already Valid before we started.
        for ($i = 0; $i -lt $unsigned.Count; $i += $batchSize) {
            $batch = @($unsigned[$i..([Math]::Min($i + $batchSize - 1, $unsigned.Count - 1))])
            Invoke-AuthenticodeSign -Path $batch
            & signtool verify /pa /q @batch
            if ($LASTEXITCODE -ne 0) { throw "signtool verify failed under $StageDir" }
        }
    }

    function Publish-Cli {
        $publishDir = Join-Path $Work "cli-publish"
        $stageName = "arcanum-$Rid"
        $stageDir = Join-Path $Work "stage\$stageName"
        $archive = Join-Path $OutputDir "$stageName.zip"
        $project = Join-Path $RepoRoot "src\RetroDownfall.Arcanum.Cli\RetroDownfall.Arcanum.Cli.csproj"

        Write-Host "==> Publishing Arcanum Native AOT ($Rid, Version=$Version)"
        & dotnet publish $project -c Release -r $Rid --self-contained true "-p:Version=$Version" -o $publishDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish Cli failed" }

        $published = Join-Path $publishDir "RetroDownfall.Arcanum.Cli.exe"
        if (-not (Test-Path -LiteralPath $published)) {
            $published = Join-Path $publishDir "arcanum.exe"
        }
        if (-not (Test-Path -LiteralPath $published)) {
            throw "Expected published Cli executable not found under $publishDir"
        }

        # Stage the whole publish directory (as Publish-Gui and the macOS packager do) and
        # rename only the apphost; the flattened native sidecars must travel with it.
        New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
        Copy-Item -Path (Join-Path $publishDir "*") -Destination $stageDir -Recurse -Force

        $stagedHost = Join-Path $stageDir (Split-Path -Leaf $published)
        $stagedArcanum = Join-Path $stageDir "arcanum.exe"
        if ($stagedHost -ne $stagedArcanum) {
            Move-Item -LiteralPath $stagedHost -Destination $stagedArcanum -Force
        }
        Copy-Item -LiteralPath (Join-Path $RepoRoot "README.md") -Destination (Join-Path $stageDir "README.md")

        Assert-StagedNatives -StageDir $stageDir -Names @("e_sqlcipher.dll", "libonigwrap.dll")

        if ($Sign) {
            Invoke-StageAuthenticodeSign -StageDir $stageDir
        }

        Write-Host "==> Creating $archive"
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        Compress-Archive -Path $stageDir -DestinationPath $archive
    }

    function Publish-Gui {
        param(
            [string]$Product,
            [string]$ProjectRelative,
            [string]$FolderName
        )

        $publishDir = Join-Path $Work "$Product-publish"
        $stageDir = Join-Path $Work "stage\$FolderName"
        $archive = Join-Path $OutputDir "$FolderName.zip"
        $project = Join-Path $RepoRoot $ProjectRelative

        Write-Host "==> Publishing $Product self-contained Avalonia folder ($Rid, Version=$Version)"
        & dotnet publish $project -c Release -r $Rid --self-contained true `
            "-p:Version=$Version" "-p:UseAppHost=true" "-p:PublishSingleFile=false" -o $publishDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish $Product failed" }

        New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
        Copy-Item -Path (Join-Path $publishDir "*") -Destination $stageDir -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $RepoRoot "README.md") -Destination (Join-Path $stageDir "README.md")

        if ($Sign) {
            Invoke-StageAuthenticodeSign -StageDir $stageDir
        }

        Write-Host "==> Creating $archive"
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        Compress-Archive -Path $stageDir -DestinationPath $archive
    }

    Publish-Cli
    if (-not $SkipForge) {
        Publish-Gui -Product "the-forge" `
            -ProjectRelative "src\RetroDownfall.TheForge.Ux\RetroDownfall.TheForge.Ux.csproj" `
            -FolderName "the-forge-$Rid"
    }
    else {
        Write-Host "==> Skipping The Forge (-SkipForge)"
    }
    Publish-Gui -Product "compendium" `
        -ProjectRelative "src\RetroDownfall.Compendium.Ux\RetroDownfall.Compendium.Ux.csproj" `
        -FolderName "compendium-$Rid"

    Write-Host "==> Writing SHA256SUMS"
    $sumsPath = Join-Path $OutputDir "SHA256SUMS"
    $artifactNames = [System.Collections.Generic.List[string]]::new()
    $artifactNames.Add("arcanum-$Rid.zip") | Out-Null
    if (-not $SkipForge) {
        $artifactNames.Add("the-forge-$Rid.zip") | Out-Null
    }
    $artifactNames.Add("compendium-$Rid.zip") | Out-Null
    $lines = @()
    foreach ($name in $artifactNames) {
        $path = Join-Path $OutputDir $name
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        $lines += "$hash  $name"
    }
    Set-Content -LiteralPath $sumsPath -Value $lines -Encoding ascii

    Write-Host "==> Windows artifacts in $OutputDir"
    Get-ChildItem -LiteralPath $OutputDir | Format-Table Name, Length
}
finally {
    if ($script:SignCertImported -and -not [string]::IsNullOrWhiteSpace($script:SignThumbprint)) {
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($script:SignThumbprint)" -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $Work) {
        Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
