# Package Arcanum (Native AOT) + The Forge + Compendium for Windows private beta.
#
# Defaults to win-x64 on the current Windows host. Cross-OS packaging is handled
# by GitHub Actions.
#
# Usage:
#   .\package-windows.ps1 -Version 0.1.0-beta.1 -OutputDir .\dist
#   .\package-windows.ps1 -Version 0.1.0-beta.1 -OutputDir .\dist -Sign
#
# -Sign requires Windows Authenticode credentials via env:
#   WINDOWS_CERT_PATH  — path to .pfx
#   WINDOWS_CERT_PASSWORD — certificate password
# Missing credentials with -Sign causes a clear failure (unsigned builds do not).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [string]$Rid = "win-x64",

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

if ($Rid -ne "win-x64") {
    throw "Unsupported RID '$Rid' (expected win-x64 for this beta script)"
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..\..\..")).Path
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$Work = Join-Path ([System.IO.Path]::GetTempPath()) ("arcanum-win-pack-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $Work | Out-Null

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
    }

    function Publish-Cli {
        $publishDir = Join-Path $Work "cli-publish"
        $stageName = "arcanum-win-x64"
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

        New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
        Copy-Item -LiteralPath $published -Destination (Join-Path $stageDir "arcanum.exe")
        Copy-Item -LiteralPath (Join-Path $RepoRoot "docs\PRIVATE-BETA-NOTES.md") -Destination (Join-Path $stageDir "PRIVATE-BETA-NOTES.md")

        if ($Sign) {
            Write-Host "==> Authenticode signing arcanum.exe"
            $signtool = Get-Command signtool -ErrorAction SilentlyContinue
            if (-not $signtool) {
                throw "signtool not found on PATH; install Windows SDK signing tools or omit -Sign."
            }
            & signtool sign /f $env:WINDOWS_CERT_PATH /p $env:WINDOWS_CERT_PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 (Join-Path $stageDir "arcanum.exe")
            if ($LASTEXITCODE -ne 0) { throw "signtool failed for arcanum.exe" }
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
        Copy-Item -LiteralPath (Join-Path $RepoRoot "docs\PRIVATE-BETA-NOTES.md") -Destination (Join-Path $stageDir "PRIVATE-BETA-NOTES.md")

        if ($Sign) {
            $exes = Get-ChildItem -Path $stageDir -Filter *.exe -File
            foreach ($exe in $exes) {
                Write-Host "==> Authenticode signing $($exe.Name)"
                & signtool sign /f $env:WINDOWS_CERT_PATH /p $env:WINDOWS_CERT_PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $exe.FullName
                if ($LASTEXITCODE -ne 0) { throw "signtool failed for $($exe.Name)" }
            }
        }

        Write-Host "==> Creating $archive"
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        Compress-Archive -Path $stageDir -DestinationPath $archive
    }

    Publish-Cli
    Publish-Gui -Product "the-forge" `
        -ProjectRelative "src\RetroDownfall.TheForge.Ux\RetroDownfall.TheForge.Ux.csproj" `
        -FolderName "the-forge-win-x64"
    Publish-Gui -Product "compendium" `
        -ProjectRelative "src\RetroDownfall.Compendium.Ux\RetroDownfall.Compendium.Ux.csproj" `
        -FolderName "compendium-win-x64"

    Write-Host "==> Writing SHA256SUMS"
    $sumsPath = Join-Path $OutputDir "SHA256SUMS"
    $lines = @()
    foreach ($name in @(
            "arcanum-win-x64.zip",
            "the-forge-win-x64.zip",
            "compendium-win-x64.zip"
        )) {
        $path = Join-Path $OutputDir $name
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        $lines += "$hash  $name"
    }
    Set-Content -LiteralPath $sumsPath -Value $lines -Encoding ascii

    Write-Host "==> Windows private-beta artifacts in $OutputDir"
    Get-ChildItem -LiteralPath $OutputDir | Format-Table Name, Length
}
finally {
    if (Test-Path -LiteralPath $Work) {
        Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
