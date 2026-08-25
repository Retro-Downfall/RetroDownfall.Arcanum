<#
.SYNOPSIS
    Builds one hermetic SQLCipher DLL for a Windows runtime identifier.

.DESCRIPTION
    The Windows counterpart of scripts/build-native-sqlcipher.sh. Every input is taken from
    src/RetroDownfall.Arcanum.NativeSqlCipher/native-source-manifest.json and verified by hash before
    use; OpenSSL's release signature is checked against the pinned signer fingerprint. OpenSSL is
    linked statically so the shipped DLL has no ambient crypto dependency.

    Run on a Windows runner with the MSVC toolchain, Perl, and NASM available (a Developer Command
    Prompt environment). win-arm64 is cross-compiled with the MSVC ARM64 toolset.

.PARAMETER Rid
    win-x64 or win-arm64.

.PARAMETER Output
    Directory to write the DLL and its build attestation into.

.EXAMPLE
    ./scripts/build-native-sqlcipher.ps1 -Rid win-x64 -Output artifacts/native
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Rid,

    [Parameter(Mandatory = $true)]
    [string] $Output
)

Set-StrictMode -Version Latest

$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path

$repositoryRoot = Split-Path -Parent $scriptDirectory

$assetProject = Join-Path $repositoryRoot 'src/RetroDownfall.Arcanum.NativeSqlCipher'

$manifestPath = Join-Path $assetProject 'native-source-manifest.json'

if (-not (Test-Path $manifestPath)) {
    throw "Missing native source manifest: $manifestPath"
}

$manifest = Get-Content -Raw -Path $manifestPath | ConvertFrom-Json

if ($manifest.schemaVersion -ne 1) {
    throw "Unsupported manifest schema version: $($manifest.schemaVersion)"
}

function Assert-Command {
    param([string[]] $Names)

    foreach ($name in $Names) {
        if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
            throw "Required command not found: $name"
        }
    }
}

function Assert-Sha256 {
    param(
        [string] $Path,
        [string] $Expected,
        [string] $Label
    )

    $actual = (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant()

    if ($actual -ne $Expected) {
        throw "Hash mismatch for ${Label}: expected $Expected, got $actual"
    }

    Write-Host "  verified $Label ($actual)"
}

Assert-Command -Names @('cl.exe', 'link.exe', 'dumpbin.exe', 'nmake.exe', 'perl', 'git', 'gpg', 'tar')

$asset = $manifest.assets | Where-Object { $_.rid -eq $Rid }

if (-not $asset) {
    throw "The manifest declares no asset for RID $Rid."
}

$toolchain = $manifest.toolchains | Where-Object { $_.rid -eq $Rid }

# OpenSSL stamps a build date into buildinf.h, and it is linked statically into the library that
# ships, so without a fixed epoch two clean builds of identical sources differ and the reproducibility
# proof fails on a difference that says nothing about the sources. /BREPRO already pins the PE
# header's own timestamp; it does nothing for a date the compiler was handed as a string. The macOS
# builder exports the same manifest value for the same reason.
$env:SOURCE_DATE_EPOCH = $manifest.sqlcipher.sourceDateEpoch

$outputName = $asset.outputFileName

# Fixed rather than random, and that is the difference between reproducing and not. OpenSSL compiles
# its own build command line into buildinf.h, and that command line carries the include paths of the
# work area; the amalgamation then includes OpenSSL's headers, so the path reaches the object as well.
# Two runs under two random directories therefore produced a different libcrypto and a different object
# from a byte-identical amalgamation, which is exactly what the input fingerprints showed. The name is
# per-RID so two RIDs built side by side do not share one area, and the directory is cleared on entry
# because a fixed path is a path that can survive a crash.
$workDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "arcanum-sqlcipher-$Rid"

Remove-Item -Recurse -Force -Path $workDirectory -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Path $workDirectory -Force | Out-Null

try {
    Write-Host '==> Hermetic SQLCipher build'
    Write-Host "    RID:       $Rid"
    Write-Host "    SQLCipher: $($manifest.sqlcipher.tag) ($($manifest.sqlcipher.commit))"
    Write-Host "    OpenSSL:   $($manifest.openssl.version)"

    Write-Host '==> Proving the pinned tag object resolves to the pinned commit'

    $refs = & git ls-remote $manifest.sqlcipher.repository "refs/tags/$($manifest.sqlcipher.tag)" "refs/tags/$($manifest.sqlcipher.tag)^{}"

    $remoteTagObject = ($refs | Where-Object { $_ -match "refs/tags/$([regex]::Escape($manifest.sqlcipher.tag))$" }) -split "\s+" | Select-Object -First 1

    $remoteCommit = ($refs | Where-Object { $_ -match '\^\{\}$' }) -split "\s+" | Select-Object -First 1

    if ($remoteTagObject -ne $manifest.sqlcipher.tagObject) {
        throw "Upstream tag object $remoteTagObject does not match pinned $($manifest.sqlcipher.tagObject)."
    }

    if ($remoteCommit -ne $manifest.sqlcipher.commit) {
        throw "Upstream tag peels to $remoteCommit, not pinned $($manifest.sqlcipher.commit)."
    }

    Write-Host '==> Fetching and verifying pinned sources'

    $sqlcipherArchive = Join-Path $workDirectory 'sqlcipher.tar.gz'

    $opensslArchive = Join-Path $workDirectory 'openssl.tar.gz'

    $opensslSignature = Join-Path $workDirectory 'openssl.tar.gz.asc'

    $opensslKeys = Join-Path $workDirectory 'pubkeys.asc'

    Invoke-WebRequest -Uri $manifest.sqlcipher.archiveUrl -OutFile $sqlcipherArchive -UseBasicParsing

    Assert-Sha256 -Path $sqlcipherArchive -Expected $manifest.sqlcipher.archiveSha256 -Label 'SQLCipher archive'

    Invoke-WebRequest -Uri $manifest.openssl.archiveUrl -OutFile $opensslArchive -UseBasicParsing

    Assert-Sha256 -Path $opensslArchive -Expected $manifest.openssl.archiveSha256 -Label 'OpenSSL archive'

    Invoke-WebRequest -Uri $manifest.openssl.signatureUrl -OutFile $opensslSignature -UseBasicParsing

    Invoke-WebRequest -Uri $manifest.openssl.publicKeysUrl -OutFile $opensslKeys -UseBasicParsing

    Assert-Sha256 -Path $opensslKeys -Expected $manifest.openssl.publicKeysSha256 -Label 'OpenSSL public keys'

    $gnupgHome = Join-Path $workDirectory 'gnupg'

    New-Item -ItemType Directory -Path $gnupgHome -Force | Out-Null

    # Git for Windows ships an MSYS gpg that resolves paths POSIX-style: it reads the leading drive
    # letter as an ordinary relative segment and resolves the whole value against its own working
    # directory. A Windows GNUPGHOME therefore became '/d/a/<repo>/C:\Users\...\gnupg', which does
    # not exist, so gpg reported no writable keyring, imported nothing, and the OpenSSL signature
    # check failed with 'No public key' on every Windows runner. The directory is created with the
    # Windows path because PowerShell makes it; only the value handed to gpg is translated.
    $env:GNUPGHOME = '/' + $gnupgHome.Substring(0, 1).ToLowerInvariant() + ($gnupgHome.Substring(2) -replace '\\', '/')

    & gpg --batch --quiet --import $opensslKeys

    $statusFile = Join-Path $workDirectory 'gpg-status.txt'

    & gpg --batch --status-file $statusFile --verify $opensslSignature $opensslArchive | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw 'OpenSSL release signature did not verify.'
    }

    if (-not (Select-String -Path $statusFile -Pattern "VALIDSIG $($manifest.openssl.signerFingerprint)" -Quiet)) {
        throw "OpenSSL signature is not from the pinned signer $($manifest.openssl.signerFingerprint)."
    }

    Write-Host "  verified OpenSSL signature from $($manifest.openssl.signerFingerprint)"

    Write-Host '==> Extracting sources'

    $sourceRoot = Join-Path $workDirectory 'src'

    New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null

    & tar xzf $sqlcipherArchive -C $sourceRoot

    & tar xzf $opensslArchive -C $sourceRoot

    $sqlcipherSource = Join-Path $sourceRoot "sqlcipher-$($manifest.sqlcipher.commit)"

    $opensslSource = Join-Path $sourceRoot "openssl-$($manifest.openssl.version)"

    # OpenSSL compiles its configured directories and CFLAGS into libcrypto, so configure against a
    # fixed virtual prefix and redirect only installation. Otherwise the random work-area path ends
    # up in the binary and the build stops being reproducible.
    $opensslStage = Join-Path $workDirectory 'openssl-install'

    Write-Host "==> Building OpenSSL $($manifest.openssl.version) (static)"

    Push-Location $opensslSource

    try {
        & perl Configure $toolchain.opensslTarget `
            --prefix=/openssl `
            --openssldir=/openssl/ssl `
            --libdir=lib `
            no-shared no-module no-tests no-docs no-legacy no-engine no-apps | Out-Null

        & nmake build_libs | Out-Null

        & nmake install_dev DESTDIR=$opensslStage | Out-Null
    }
    finally {
        Pop-Location
    }

    $opensslPrefix = Join-Path $opensslStage 'openssl'

    $libcrypto = Join-Path $opensslPrefix 'lib/libcrypto.lib'

    if (-not (Test-Path $libcrypto)) {
        throw 'OpenSSL did not install libcrypto.lib into the staged prefix.'
    }

    Write-Host '==> Generating the SQLCipher amalgamation'

    Push-Location $sqlcipherSource

    try {
        if ($Rid -eq 'win-arm64') {

            # The amalgamation is produced by jimsh0.exe, which this makefile builds from source with
            # whatever cl.exe is on PATH. Under the arm64 cross toolset that yields an arm64 executable,
            # and the x64 runner cannot spawn it: NMAKE fails with U1045 and 0x800700d8, 'not compatible
            # with the version of Windows you're running'. The tool has to be built for the host even
            # when the library is built for the target. The amalgamation is architecture-independent C,
            # so generating it under the host toolset changes nothing about what is compiled afterwards.
            $vcvarsall = Join-Path $env:VCINSTALLDIR 'Auxiliary\Build\vcvarsall.bat'

            if (-not (Test-Path $vcvarsall)) {
                throw "Cannot locate vcvarsall.bat under VCINSTALLDIR ('$env:VCINSTALLDIR') to build the host amalgamation tool."
            }

            & cmd.exe /c "`"$vcvarsall`" x64 >nul && nmake /f Makefile.msc sqlite3.c" 2>&1 |
                Tee-Object -Variable amalgamationLog | Out-Null

            if ($LASTEXITCODE -ne 0) {
                $amalgamationLog | ForEach-Object { Write-Host $_ }
            }

        }
        else {

            & nmake /f Makefile.msc sqlite3.c 2>&1 | Tee-Object -Variable amalgamationLog | Out-Null

            if ($LASTEXITCODE -ne 0) {
                $amalgamationLog | ForEach-Object { Write-Host $_ }
            }

        }
    }
    finally {
        Pop-Location
    }

    $amalgamation = Join-Path $sqlcipherSource 'sqlite3.c'

    if (-not (Test-Path $amalgamation)) {
        throw 'SQLCipher amalgamation was not generated.'
    }

    Write-Host "==> Linking $outputName"

    $buildDirectory = Join-Path $workDirectory 'build'

    New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null

    $defines = $manifest.compileOptions | ForEach-Object { "/D$_" }

    $exportDefinition = Join-Path $buildDirectory 'e_sqlcipher.def'

    $objectFile = Join-Path $buildDirectory 'sqlite3.obj'

    & cl.exe /nologo /c /O2 /MD /GS /guard:cf /Zc:inline `
        @defines `
        "/I$sqlcipherSource" `
        "/I$(Join-Path $opensslPrefix 'include')" `
        "/Fo$objectFile" `
        $amalgamation 2>&1 | Tee-Object -Variable compileLog | Out-Null

    if ($LASTEXITCODE -ne 0) {
        # Kept quiet on success and shown in full on failure. Discarding it outright left the
        # operator with 'Compilation failed.' and no diagnostic at all, on the one path where the
        # compiler has already said exactly what is wrong.
        $compileLog | ForEach-Object { Write-Host $_ }

        throw 'Compilation failed.'
    }

    # Only the documented SQLite C API is exported; SQLCipher internals and every statically linked
    # OpenSSL symbol stay private to the DLL.
    #
    # Read from the compiled object rather than from the amalgamation's text. The source declares
    # every SQLITE_API entry point unconditionally and then guards the definitions, so a scan of the
    # source exported names this build never emits: preupdate and normalize are not enabled, ICU,
    # carray and CEROD are not present, mutex_held needs SQLITE_DEBUG, and load_extension is
    # deliberately omitted. Windows validates a .def against the object and refused all of them,
    # which is why no Windows library has ever linked; macOS never noticed because Mach-O exports
    # what exists and says nothing about what does not. Taking the list from the object makes the two
    # platforms agree by construction and keeps agreeing when a compile option changes.
    'EXPORTS' | Set-Content -Path $exportDefinition -Encoding ascii

    & dumpbin.exe /nologo /symbols $objectFile |
        Select-String -Pattern 'SECT.*External\s+\|\s+(sqlite3_\w+)\s*$' -AllMatches |
        ForEach-Object { $_.Matches } |
        ForEach-Object { $_.Groups[1].Value } |
        Sort-Object -Unique |
        Add-Content -Path $exportDefinition -Encoding ascii

    $exportCount = (Get-Content -Path $exportDefinition).Count - 1

    Write-Host "  exporting $exportCount sqlite3 entry points the object actually defines"

    if ($exportCount -lt 100) {
        throw "Only $exportCount exports were found in $objectFile; the symbol scan did not match."
    }

    $outputPath = Join-Path $buildDirectory $outputName

    & link.exe /nologo /DLL /NXCOMPAT /DYNAMICBASE /guard:cf /BREPRO /INCREMENTAL:NO `
        "/DEF:$exportDefinition" `
        "/OUT:$outputPath" `
        $objectFile `
        $libcrypto `
        ws2_32.lib crypt32.lib advapi32.lib user32.lib bcrypt.lib 2>&1 |
        Tee-Object -Variable linkLog | Out-Null

    if ($LASTEXITCODE -ne 0) {
        # Same reasoning as the compile step above: the linker names the missing symbol or library,
        # and throwing without it turns a specific failure into an unactionable one.
        $linkLog | ForEach-Object { Write-Host $_ }

        throw 'Linking failed.'
    }

    New-Item -ItemType Directory -Path $Output -Force | Out-Null

    Copy-Item -Path $outputPath -Destination (Join-Path $Output $outputName) -Force

    $outputSha = (Get-FileHash -Algorithm SHA256 -Path (Join-Path $Output $outputName)).Hash.ToLowerInvariant()

    Write-Host "  linked $outputName ($outputSha)"

    # Printed so a reproducibility failure identifies which input moved. Comparing two DLL hashes says
    # only that something differed; comparing these says whether it was OpenSSL, the amalgamation, or
    # the compile and link of an identical amalgamation.
    Write-Host "  inputs: amalgamation=$((Get-FileHash -Algorithm SHA256 -Path $amalgamation).Hash.ToLowerInvariant()) libcrypto=$((Get-FileHash -Algorithm SHA256 -Path $libcrypto).Hash.ToLowerInvariant()) obj=$((Get-FileHash -Algorithm SHA256 -Path $objectFile).Hash.ToLowerInvariant())"

    Write-Host '==> Writing build attestation'

    $clBanner = (& cl.exe 2>&1 | Select-Object -First 1)

    $attestation = [ordered]@{
        rid              = $Rid
        outputFileName   = $outputName
        sha256           = $outputSha
        sqlcipher        = [ordered]@{
            commit             = $manifest.sqlcipher.commit
            archiveSha256      = $manifest.sqlcipher.archiveSha256
            amalgamationSha256 = (Get-FileHash -Algorithm SHA256 -Path $amalgamation).Hash.ToLowerInvariant()
        }
        openssl          = [ordered]@{
            version         = $manifest.openssl.version
            archiveSha256   = $manifest.openssl.archiveSha256
            libcryptoSha256 = (Get-FileHash -Algorithm SHA256 -Path $libcrypto).Hash.ToLowerInvariant()
        }
        toolchain        = [ordered]@{
            compiler = "$clBanner"
            linker   = 'MSVC link.exe'
            image    = "$env:ImageOS $env:ImageVersion"
        }
        sourceDateEpoch  = $manifest.sqlcipher.sourceDateEpoch
        compileOptions   = $manifest.compileOptions
    }

    $attestation |
        ConvertTo-Json -Depth 6 |
        Set-Content -Path (Join-Path $Output "$Rid-attestation.json") -Encoding utf8

    Write-Host '==> Done'
    Write-Host "    $(Join-Path $Output $outputName)"
    Write-Host "    $(Join-Path $Output "$Rid-attestation.json")"
}
finally {
    Remove-Item -Recurse -Force -Path $workDirectory -ErrorAction SilentlyContinue
}
