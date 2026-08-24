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

$lineTarget = Resolve-CoverageTarget -Name "COVERAGE_LINE_TARGET" -Default 80.0
$branchTarget = Resolve-CoverageTarget -Name "COVERAGE_BRANCH_TARGET" -Default 70.0
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
        "WardGate",
        "WorkspacePathPolicy",
        # The authenticated-envelope codec. It seals and opens every Covenant fragment, so a branch it
        # never exercises is an authentication path nothing has proved refuses.
        "CovenantEnvelopeCodec"
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

# Fold a Cobertura class name onto the short name of its declaring type. Coverlet keeps
# async/iterator state machines as nested classes, e.g.
# Namespace.OutboundUrlGuard/<EgressConnectCallbackAsync>d__17. Matching on the substring
# after the last "." would yield "OutboundUrlGuard/<...>d__17" and skip every async body,
# so strip the nested suffix before stripping the namespace.
function Resolve-DeclaringTypeName {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $Name
    )

    $outer = $Name.Split("/")[0]

    return $outer.Substring($outer.LastIndexOf(".") + 1)
}

# One branch tally per security type, aggregated over the declaring class *and* every
# compiler-generated state machine nested inside it. Keyed by "file|line" so a line
# reported by both the synchronous shell and its async state machine is counted once, at
# its best observed condition coverage.
$securityLines = @{}

$securityClassRates = @{}

foreach ($class in $document.SelectNodes("//class")) {
    $name = [string] $class.GetAttribute("name")

    $shortName = Resolve-DeclaringTypeName -Name $name

    if (-not $securityTypes.Contains($shortName)) {
        continue
    }

    $null = $seenSecurityTypes.Add($shortName)

    $fileName = [string] $class.GetAttribute("filename")

    if (-not $securityLines.ContainsKey($shortName)) {
        $securityLines[$shortName] = @{}

        $securityClassRates[$shortName] = [System.Collections.Generic.List[double]]::new()
    }

    $bestByLine = $securityLines[$shortName]

    $securityClassRates[$shortName].Add(
        [double]::Parse(
            [string] $class.GetAttribute("branch-rate"),
            [System.Globalization.CultureInfo]::InvariantCulture
        ) * 100.0
    )

    foreach ($line in $class.SelectNodes(".//line")) {
        $conditionCoverage = [string] $line.GetAttribute("condition-coverage")

        if ($conditionCoverage -notmatch "\((\d+)/(\d+)\)") {
            continue
        }

        $lineKey = "{0}|{1}" -f $fileName, [string] $line.GetAttribute("number")

        $covered = [int] $Matches[1]

        $total = [int] $Matches[2]

        $rate = if ($total -eq 0) { 1.0 } else { $covered / $total }

        if (-not $bestByLine.ContainsKey($lineKey) -or $rate -gt $bestByLine[$lineKey].Rate) {
            $bestByLine[$lineKey] = @{
                Covered = $covered
                Total = $total
                Rate = $rate
            }
        }
    }
}

foreach ($shortName in ($seenSecurityTypes | Sort-Object)) {
    $bestByLine = $securityLines[$shortName]

    $branchCovered = 0

    $branchCount = 0

    foreach ($entry in $bestByLine.Values) {
        $branchCovered += $entry.Covered

        $branchCount += $entry.Total
    }

    if ($branchCount -eq 0) {
        # Fall back to the class branch-rate attributes; take the worst so a fully covered
        # shell can never mask an uncovered state machine.
        $securityRate = ($securityClassRates[$shortName] | Measure-Object -Minimum).Minimum
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
