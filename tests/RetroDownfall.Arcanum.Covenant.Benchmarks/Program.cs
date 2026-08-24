using System.Runtime.InteropServices;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Performance;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Covenant.Benchmarks;

// The Covenant release benchmark.
//
//   --out <path>        write the measured run as JSON
//   --gate              fail the process when an absolute ceiling or the control is breached
//   --compare <path>    compare against a recorded baseline with the paired bootstrap
//
// Exit codes are the contract: 0 measured and within every stated bound, 1 a breach, 2 the run could
// not be made. A breach and an unmeasurable run are different answers and a caller has to be able to
// tell them apart without reading the output.

string? outputPath = null;

string? baselinePath = null;

bool gate = false;

for (int index = 0; index < args.Length; index++)
{

    switch (args[index])
    {

        case "--out" when index + 1 < args.Length:

            outputPath = args[++index];

            break;

        case "--compare" when index + 1 < args.Length:

            baselinePath = args[++index];

            break;

        case "--gate":

            gate = true;

            break;

        default:

            Console.Error.WriteLine($"Unrecognized argument: {args[index]}");

            return 2;

    }

}

WorkloadManifest manifest = WorkloadManifestLoader.Load();

Console.Error.WriteLine($"workload {manifest.WorkloadId} schema {manifest.SchemaVersion}");

await using CovenantWorkloadBed bed = await CovenantWorkloadBed.CreateAsync(manifest, CancellationToken.None);

Console.Error.WriteLine($"corpus {bed.CorpusDigest}");

if (manifest.Corpus.CorpusDigest is { Length: > 0 } pinnedCorpus
    && !string.Equals(pinnedCorpus, bed.CorpusDigest, StringComparison.Ordinal))
{

    // Every recorded baseline was produced against the pinned corpus. Measuring a different one and
    // comparing anyway would report a corpus change as a code regression.
    Console.Error.WriteLine($"The seeded corpus does not match the manifest: expected {pinnedCorpus}.");

    return 2;

}

Guid campaign = bed.Campaigns[0];

// Every commit advances the lane, so the next one has to expect the revision the last one produced.
long commitRevision = 1;

ArcanumInvocationContext invocation = CovenantWorkloadBed.Unwrap(ArcanumInvocationContext.Create(
    ArcanumExecutionSurface.SessionBackedOperatorTurn,
    CanonicalCampaignContext.Create(
        SessionCampaignBinding.ForCampaign(campaign),
        campaignAvailabilityGeneration: 1,
        pathIdentityPolicyVersion: 1,
        pathIdentityRevision: null,
        rootIdentityDigest: null),
    InvocationAttendance.Attended,
    CovenantContextPolicy.Default,
    ToolPolicy.AllTools,
    CovenantWorkloadBed.Unwrap(CovenantReadAuthorityEpoch.Create(bed.Authority.Current))));

CovenantTurnPlan admissionPlan;

await using (CovenantTurnContext startup = CovenantWorkloadBed.Unwrap(
    await bed.Context.BeginTurnAsync(invocation, Guid.CreateVersion7(), CancellationToken.None)))
{

    admissionPlan = startup.Plan
        ?? throw new InvalidOperationException(
            $"The benchmark bed produced no turn plan ({startup.Absence}), so there is nothing to measure.");

}

// The corpus digest pins what was written; it says nothing about what the store and the linker hand
// back. This invocation can stage, so the provider returns a plan even when the link found nothing,
// and a store or linker regression that returned zero entries would make turn.plan dramatically
// faster while every ceiling passed. Fail closed on the counts, exactly as the digest mismatch does.
int expectedGlobal = manifest.Corpus.GlobalConfirmedEntries;

int expectedCampaign = manifest.Corpus.CampaignConfirmedEntriesPerCampaign;

if (admissionPlan.GlobalConfirmedSection.Candidates.Length != expectedGlobal
    || admissionPlan.CampaignConfirmedSection.Candidates.Length != expectedCampaign)
{

    Console.Error.WriteLine(
        $"The linked plan holds {admissionPlan.GlobalConfirmedSection.Candidates.Length} Global and "
        + $"{admissionPlan.CampaignConfirmedSection.Candidates.Length} Campaign Confirmed entries; the "
        + $"manifest seeds {expectedGlobal} and {expectedCampaign}.");

    return 2;

}

if (admissionPlan.GlobalConfirmedSection.RenderedBytes.Length == 0
    || admissionPlan.CampaignConfirmedSection.RenderedBytes.Length == 0)
{

    Console.Error.WriteLine("The linked plan rendered an empty Confirmed section, so the measured turn carries no Covenant.");

    return 2;

}

// The control is measured first, because every allocation number below is a difference against it.
double[] controlSamples = await BenchmarkHarness.MeasureControlAsync(manifest.Measurement);

double controlPerIteration = NearestRankPercentile.Of(controlSamples, 50);

Console.Error.WriteLine($"control {controlPerIteration:F1}B per iteration");

List<BenchmarkOperationResult> measured = [];

foreach (WorkloadOperation operation in manifest.Operations)
{

    measured.Add(await BenchmarkHarness.MeasureAsync(
        operation.Id,
        manifest.Measurement,
        controlPerIteration,
        Operation(operation.Id)));

    Console.Error.WriteLine(
        $"  {operation.Id,-18} p50 {measured[^1].P50Microseconds,9:F1}us  "
        + $"p95 {measured[^1].P95Microseconds,9:F1}us  "
        + $"p99 {measured[^1].P99Microseconds,9:F1}us  "
        + $"alloc {measured[^1].AllocationBytes,9:F0}B");

}

BenchmarkControlResult control = BenchmarkHarness.Judge(controlSamples, measured);

BenchmarkRun run = new(
    manifest.WorkloadId,
    manifest.SchemaVersion,
    RuntimeInformation.RuntimeIdentifier,
    bed.CorpusDigest,
    BenchmarkManifestDigest.Of(manifest),
    [.. measured],
    control);

// Every turn the measured loop opened has been disposed, so the gate should hold nothing. A leaked
// registration is not a hang here — ordinary acquisitions never drain — but it is a measurement of a
// gate that grows all run, and it would have to be found by reading numbers rather than by a check.
if (bed.Gate.LiveRegistrationCount != 0)
{

    Console.Error.WriteLine(
        $"The operation gate still holds {bed.Gate.LiveRegistrationCount} registrations after the "
        + "measured loop, so the run measured a gate that grew under it.");

    return 2;

}

if (outputPath is { Length: > 0 })
{

    await File.WriteAllTextAsync(
        outputPath,
        JsonSerializer.Serialize(run, BenchmarkJsonContext.Default.BenchmarkRun));

    Console.Error.WriteLine($"wrote {outputPath}");

}

int exit = 0;

if (baselinePath is { Length: > 0 })
{

    exit = Math.Max(
        exit,
        BenchmarkBaselineComparison.Compare(
            run,
            JsonSerializer.Deserialize(
                File.ReadAllText(baselinePath),
                BenchmarkJsonContext.Default.BenchmarkRun),
            Console.Error));

}

if (gate)
{

    exit = Math.Max(exit, BenchmarkGate.Evaluate(run, manifest.Operations, Console.Error));

}

return exit;

// Production disposes the turn context at the end of the turn, which is what releases the turn lease
// and removes the gate registration the provider took. An iteration that kept it would measure a gate
// whose registration list grew on every one of the roughly nine hundred turns this run opens.
async Task MeasuredPlanAsync()
{

    await using CovenantTurnContext context = CovenantWorkloadBed.Unwrap(
        await bed.Context.BeginTurnAsync(invocation, Guid.CreateVersion7(), CancellationToken.None));

    _ = context.Plan
        ?? throw new InvalidOperationException(
            $"The benchmark bed produced no turn plan ({context.Absence}), so there is nothing to measure.");

}

Func<Task> Operation(string id) => id switch
{

    "turn.plan" => MeasuredPlanAsync,

    "turn.admission" => () =>
    {

        _ = CovenantAdmissionPlanner.Plan(
            admissionPlan,
            availableTokenBudget: 4096,
            // A fixed four-bytes-per-token estimate, not a tokenizer. The planner takes its
            // measurement as a delegate precisely so it stays provider-independent, and calling a
            // real tokenizer here would fold that tokenizer's cost into the admission number.
            static sections => (ulong)Math.Max(
                1,
                (sections.GlobalConfirmed.Length
                    + sections.CampaignConfirmed.Length
                    + sections.CampaignProposed.Length) / 4),
            static fragment => (ulong)Math.Max(1, fragment.Length / 4));

        return Task.CompletedTask;

    }
    ,

    "mutation.prepare" => async () =>
    {

        await using CovenantInstallationReadLease read = CovenantWorkloadBed.Unwrap(
            await bed.Gate.AcquireInstallationReadAsync(CancellationToken.None));

        _ = CovenantWorkloadBed.Unwrap(await bed.Mutations.PrepareSetAsync(
            new CovenantSetPrepareRequest(
                CovenantScope.Global,
                null,
                "preference.global.00",
                "A measured rewrite of an existing standing preference.",
                ExpectedRevision: 1,
                Guid.CreateVersion7(),
                Reactivate: false),
            read,
            CancellationToken.None));

    }
    ,

    "mutation.commit" => MeasuredCommitAsync,

    "status.census" => async () =>
        _ = CovenantWorkloadBed.Unwrap(await bed.Management.StatusAsync(CancellationToken.None)),

    _ => throw new InvalidOperationException($"The manifest names an operation this host cannot run: {id}."),

};

// A fixed expected revision would measure one real append and then the idempotency-conflict path,
// which is a different and much cheaper answer, so the expected revision advances with the lane.
async Task MeasuredCommitAsync()
{

    long expected = commitRevision;

    Guid mutationId = Guid.CreateVersion7();

    string preflight;

    await using (CovenantInstallationReadLease read = CovenantWorkloadBed.Unwrap(
        await bed.Gate.AcquireInstallationReadAsync(CancellationToken.None)))
    {

        preflight = CovenantWorkloadBed.Unwrap(await bed.Mutations.PrepareSetAsync(
            new CovenantSetPrepareRequest(
                CovenantScope.Global,
                null,
                "preference.global.01",
                $"A measured append number {expected}.",
                expected,
                mutationId,
                Reactivate: false),
            read,
            CancellationToken.None)).PreflightToken;

    }

    await using CovenantWriteLease write = CovenantWorkloadBed.Unwrap(
        await bed.Gate.AcquireWriteAsync(CovenantOperationScope.Global, CancellationToken.None));

    _ = CovenantWorkloadBed.Unwrap(await bed.Mutations.SetAsync(
        new CovenantSetRequest(
            CovenantScope.Global,
            null,
            "preference.global.01",
            $"A measured append number {expected}.",
            expected,
            mutationId,
            Reactivate: false,
            preflight),
        write,
        CancellationToken.None));

    commitRevision = expected + 1;

}
