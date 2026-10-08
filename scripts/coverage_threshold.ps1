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

# Assemblies that are instrumented so they appear in the report, but are held to their own
# per-assembly line floor and are removed from the aggregate that the line and branch targets
# above apply to. A floor of 0 reports an assembly without gating it.
$reportedAssemblyLineFloors = [ordered]@{
    "RetroDownfall.Arcanum.Cli" = Resolve-CoverageTarget -Name "COVERAGE_CLI_LINE_TARGET" -Default 75.0
    "RetroDownfall.Arcanum.Secrets" = Resolve-CoverageTarget -Name "COVERAGE_SECRETS_LINE_TARGET" -Default 60.0
}

$securityBranchTargets = @{
    "ApiKeyDigestCache" = 85.0
}

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

# Coverlet writes one <package> per instrumented assembly with exact line-rate and branch-rate
# attributes but no counts, so the weights that recombine assemblies are rebuilt from the class
# line entries: unique "file|line" pairs for lines, and the largest condition total any class
# reports for a line for branches. The rates stay exact; only the relative weights are counted.
function Get-PackageStats {
    param(
        [Parameter(Mandatory = $true)]
        $Package
    )

    $lineHits = @{}

    $lineBranches = @{}

    foreach ($packageClass in $Package.SelectNodes("./classes/class")) {
        $packageFile = [string] $packageClass.GetAttribute("filename")

        foreach ($packageLine in $packageClass.SelectNodes("./lines/line")) {
            $packageKey = "{0}|{1}" -f $packageFile, [string] $packageLine.GetAttribute("number")

            $hits = 0

            $null = [int]::TryParse([string] $packageLine.GetAttribute("hits"), [ref] $hits)

            $isHit = $hits -gt 0

            if (-not $lineHits.ContainsKey($packageKey)) {
                $lineHits[$packageKey] = $isHit
            }
            elseif ($isHit) {
                $lineHits[$packageKey] = $true
            }

            $packageCondition = [string] $packageLine.GetAttribute("condition-coverage")

            if ($packageCondition -notmatch "\((\d+)/(\d+)\)") {
                continue
            }

            $conditionCovered = [int] $Matches[1]

            $conditionTotal = [int] $Matches[2]

            if (-not $lineBranches.ContainsKey($packageKey) -or $conditionTotal -gt $lineBranches[$packageKey].Total) {
                $lineBranches[$packageKey] = @{
                    Covered = $conditionCovered
                    Total = $conditionTotal
                }
            }
        }
    }

    $validLines = $lineHits.Count

    $coveredLines = @($lineHits.Values | Where-Object { $_ }).Count

    $validBranches = 0

    $coveredBranches = 0

    foreach ($entry in $lineBranches.Values) {
        $validBranches += $entry.Total

        $coveredBranches += $entry.Covered
    }

    $packageLineRate = if ($Package.HasAttribute("line-rate")) {
        [double]::Parse(
            [string] $Package.GetAttribute("line-rate"),
            [System.Globalization.CultureInfo]::InvariantCulture
        ) * 100.0
    }
    elseif ($validLines -eq 0) {
        100.0
    }
    else {
        ($coveredLines / $validLines) * 100.0
    }

    $packageBranchRate = if ($Package.HasAttribute("branch-rate")) {
        [double]::Parse(
            [string] $Package.GetAttribute("branch-rate"),
            [System.Globalization.CultureInfo]::InvariantCulture
        ) * 100.0
    }
    elseif ($validBranches -eq 0) {
        100.0
    }
    else {
        ($coveredBranches / $validBranches) * 100.0
    }

    return [pscustomobject] @{
        Name = [string] $Package.GetAttribute("name")
        ValidLines = $validLines
        ValidBranches = $validBranches
        LineRate = $packageLineRate
        BranchRate = $packageBranchRate
    }
}

$packages = @(
    foreach ($packageNode in $document.SelectNodes("/coverage/packages/package")) {
        Get-PackageStats -Package $packageNode
    }
)

$gatePackages = @($packages | Where-Object { -not $reportedAssemblyLineFloors.Contains($_.Name) })

# A report that carries none of the reported assemblies (or nothing else) keeps the root
# attributes, so the aggregate of a report without them is unchanged.
if ($gatePackages.Count -ne $packages.Count -and $gatePackages.Count -gt 0) {
    $lineWeight = 0.0

    $branchWeight = 0.0

    $weightedLine = 0.0

    $weightedBranch = 0.0

    foreach ($gatePackage in $gatePackages) {
        $lineWeight += $gatePackage.ValidLines

        $branchWeight += $gatePackage.ValidBranches

        $weightedLine += $gatePackage.LineRate * $gatePackage.ValidLines

        $weightedBranch += $gatePackage.BranchRate * $gatePackage.ValidBranches
    }

    $lineRate = if ($lineWeight -gt 0) { $weightedLine / $lineWeight } else { 100.0 }

    $branchRate = if ($branchWeight -gt 0) { $weightedBranch / $branchWeight } else { 100.0 }
}

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
# its worst observed condition coverage, so a covered shell can never mask an uncovered
# state machine on the same line.
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

    $worstByLine = $securityLines[$shortName]

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

        if (-not $worstByLine.ContainsKey($lineKey) -or $rate -lt $worstByLine[$lineKey].Rate) {
            $worstByLine[$lineKey] = @{
                Covered = $covered
                Total = $total
                Rate = $rate
            }
        }
    }
}

foreach ($shortName in ($seenSecurityTypes | Sort-Object)) {
    $worstByLine = $securityLines[$shortName]

    $branchCovered = 0

    $branchCount = 0

    foreach ($entry in $worstByLine.Values) {
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

    $typeBranchTarget = if ($securityBranchTargets.ContainsKey($shortName)) {
        $securityBranchTargets[$shortName]
    }
    else {
        $securityBranchTarget
    }

    if ($securityRate -lt $typeBranchTarget) {
        $failures.Add(
            ("security type {0}: branch coverage {1:F2}% < {2:F0}%" -f
                $shortName,
                $securityRate,
                $typeBranchTarget)
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

foreach ($reportedName in $reportedAssemblyLineFloors.Keys) {
    $reportedPackage = $packages | Where-Object { $_.Name -eq $reportedName } | Select-Object -First 1

    if ($null -eq $reportedPackage) {
        $failures.Add("required assembly $reportedName is absent from the coverage report")

        continue
    }

    $reportedFloor = $reportedAssemblyLineFloors[$reportedName]

    if ($reportedPackage.LineRate -lt $reportedFloor) {
        $failures.Add(
            ("assembly {0}: line coverage {1:F2}% < {2:G}%" -f
                $reportedName,
                $reportedPackage.LineRate,
                $reportedFloor)
        )
    }
}

Write-Output ("Overall line coverage:   {0:F2}% (target >= {1:G}%)" -f $lineRate, $lineTarget)

Write-Output ("Overall branch coverage: {0:F2}% (target >= {1:G}%)" -f $branchRate, $branchTarget)

foreach ($reportedName in ($reportedAssemblyLineFloors.Keys | Sort-Object)) {
    $reportedPackage = $packages | Where-Object { $_.Name -eq $reportedName } | Select-Object -First 1

    if ($null -eq $reportedPackage) {
        continue
    }

    Write-Output (
        "Reported assembly {0}: line {1:F2}%, branch {2:F2}% (line floor >= {3:G}%; outside the aggregate)" -f
            $reportedName,
            $reportedPackage.LineRate,
            $reportedPackage.BranchRate,
            $reportedAssemblyLineFloors[$reportedName]
    )
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Threshold failures:")

    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("  - $failure")
    }

    exit 1
}

Write-Output "All coverage thresholds met."

exit 0
