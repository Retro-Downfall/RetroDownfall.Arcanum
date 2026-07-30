param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $CoveragePath
)

$ErrorActionPreference = "Stop"

function Resolve-CoverageTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [double] $Default
    )

    $raw = [System.Environment]::GetEnvironmentVariable($Name)

    if ([string]::IsNullOrWhiteSpace($raw)) {
        return $Default
    }

    $value = 0.0
    $parsed = [double]::TryParse(
        $raw,
        [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [ref] $value
    )

    if (-not $parsed -or [double]::IsNaN($value) -or [double]::IsInfinity($value) -or $value -lt 0.0 -or $value -gt 100.0) {
        throw "$Name must be a number from 0 through 100"
    }

    return $value
}

$lineTarget = Resolve-CoverageTarget -Name "COVERAGE_LINE_TARGET" -Default 85.0
$branchTarget = Resolve-CoverageTarget -Name "COVERAGE_BRANCH_TARGET" -Default 75.0
$securityBranchTarget = 100.0

$securityTypes = [System.Collections.Generic.HashSet[string]]::new(
    [string[]] @(
        "ApiKeyEndpointFilter",
        "ApiKeyDigestCache",
        "DataProtectionSecretStore",
        "GrimoireKeyDerivation",
        "McpSecurityLimits",
        "TrustedMcpWorkspaceStore",
        "SandboxedFileIo",
        "SecureFileReader",
        "IdentityOwnedFileSystemCleanup",
        "SanctumGuard",
        "OutboundUrlGuard",
        "HostProcessToolPolicy",
        "IdempotencyClaimStore",
        "BudgetReservationService",
        "WardGate"
    ),
    [System.StringComparer]::Ordinal
)

[xml] $document = Get-Content -LiteralPath $CoveragePath -Raw

$coverage = $document.coverage

$lineRate = [double]::Parse(
    [string] $coverage.GetAttribute("line-rate"),
    [System.Globalization.CultureInfo]::InvariantCulture
) * 100.0

$branchRate = [double]::Parse(
    [string] $coverage.GetAttribute("branch-rate"),
    [System.Globalization.CultureInfo]::InvariantCulture
) * 100.0

$failures = [System.Collections.Generic.List[string]]::new()
$seenSecurityTypes = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal
)

if ($lineRate -lt $lineTarget) {
    $failures.Add(("line coverage {0:F2}% < {1:G}%" -f $lineRate, $lineTarget))
}

if ($branchRate -lt $branchTarget) {
    $failures.Add(("branch coverage {0:F2}% < {1:G}%" -f $branchRate, $branchTarget))
}

foreach ($class in $document.SelectNodes("//class")) {
    $name = [string] $class.GetAttribute("name")

    $shortName = $name.Substring($name.LastIndexOf(".") + 1)

    if (-not $securityTypes.Contains($shortName)) {
        continue
    }

    $null = $seenSecurityTypes.Add($shortName)

    $bestByLine = @{}

    foreach ($line in $class.SelectNodes(".//line")) {
        $conditionCoverage = [string] $line.GetAttribute("condition-coverage")

        if ($conditionCoverage -notmatch "\((\d+)/(\d+)\)") {
            continue
        }

        $lineNumber = [string] $line.GetAttribute("number")

        $covered = [int] $Matches[1]

        $total = [int] $Matches[2]

        $rate = if ($total -eq 0) { 1.0 } else { $covered / $total }

        if (-not $bestByLine.ContainsKey($lineNumber) -or $rate -gt $bestByLine[$lineNumber].Rate) {
            $bestByLine[$lineNumber] = @{
                Covered = $covered
                Total = $total
                Rate = $rate
            }
        }
    }

    $branchCovered = 0

    $branchCount = 0

    foreach ($entry in $bestByLine.Values) {
        $branchCovered += $entry.Covered

        $branchCount += $entry.Total
    }

    if ($branchCount -eq 0) {
        $securityRate = [double]::Parse(
            [string] $class.GetAttribute("branch-rate"),
            [System.Globalization.CultureInfo]::InvariantCulture
        ) * 100.0
    }
    else {
        $securityRate = ($branchCovered / $branchCount) * 100.0
    }

    if ($securityRate -lt $securityBranchTarget) {
        $failures.Add(
            ("security type {0}: branch coverage {1:F2}% < {2:F0}%" -f
                $shortName,
                $securityRate,
                $securityBranchTarget)
        )
    }
}

foreach ($requiredType in $securityTypes) {
    if (-not $seenSecurityTypes.Contains($requiredType)) {
        $failures.Add(
            "required security type $requiredType is absent from the coverage report"
        )
    }
}

Write-Output ("Overall line coverage:   {0:F2}% (target >= {1:G}%)" -f $lineRate, $lineTarget)

Write-Output ("Overall branch coverage: {0:F2}% (target >= {1:G}%)" -f $branchRate, $branchTarget)

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Threshold failures:")

    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("  - $failure")
    }

    exit 1
}

Write-Output "All coverage thresholds met."

exit 0
