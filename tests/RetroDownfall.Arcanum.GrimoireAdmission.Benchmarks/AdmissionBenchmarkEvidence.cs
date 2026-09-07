using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

using System.Text.Json.Serialization.Metadata;

namespace RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks;

internal sealed record AdmissionBenchmarkEvidenceValidation(
    bool Valid,
    string[] Errors);

internal static class AdmissionBenchmarkEvidence
{

    internal static AdmissionBenchmarkEvidenceBundle ParseBundle(
        string json,
        JsonTypeInfo<AdmissionBenchmarkEvidenceBundle> typeInfo)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        try
        {

            return JsonSerializer.Deserialize(json, typeInfo)
                ?? throw new InvalidDataException("The evidence bundle is empty.");

        }
        catch (JsonException exception)
        {

            throw new InvalidDataException("The evidence bundle is truncated or invalid.", exception);

        }

    }

    internal static AdmissionBenchmarkEvidenceValidation Validate(
        AdmissionBenchmarkManifest manifest,
        AdmissionBenchmarkEvidenceBundle evidence,
        bool baseIsAncestor)
    {

        ArgumentNullException.ThrowIfNull(manifest);

        ArgumentNullException.ThrowIfNull(evidence);

        List<string> errors = [];

        if (!baseIsAncestor)
        {

            errors.Add("reversed-ancestry: baseline is not an ancestor of candidate.");

        }

        AdmissionBenchmarkComparisonReport shape = AdmissionBenchmarkComparison.Compare(manifest, evidence);

        if (!shape.Valid)
        {

            errors.AddRange(shape.Reasons.Select(static reason => "pair: " + reason));

        }

        if (evidence.Pairs is null || evidence.Pairs.Length == 0)
        {

            return new(false, errors.ToArray());

        }

        AdmissionBenchmarkRevisionRun reference = evidence.Pairs[0].Baseline;

        AdmissionBenchmarkRevisionRun candidateReference = evidence.Pairs[0].Candidate;

        foreach (AdmissionBenchmarkPair pair in evidence.Pairs)
        {

            ValidateRun(reference, pair.Baseline, manifest, errors);

            ValidateRun(reference, pair.Candidate, manifest, errors);

            CompareSources(pair.Baseline.Inputs.Inputs, pair.Candidate.Inputs.Inputs, manifest, errors);

            CompareSameRoleSources(
                reference.Inputs.Inputs,
                pair.Baseline.Inputs.Inputs,
                "baseline-source",
                errors);

            CompareSameRoleSources(
                candidateReference.Inputs.Inputs,
                pair.Candidate.Inputs.Inputs,
                "candidate-source",
                errors);

        }

        string[] baselineRevisions = evidence.Pairs.Select(static pair => pair.Baseline.Revision).Distinct(StringComparer.Ordinal).ToArray();

        string[] candidateRevisions = evidence.Pairs.Select(static pair => pair.Candidate.Revision).Distinct(StringComparer.Ordinal).ToArray();

        if (baselineRevisions.Length != 1 || candidateRevisions.Length != 1 || baselineRevisions[0] == candidateRevisions[0])
        {

            errors.Add("non-commit: revision roles are missing, duplicated, or equal.");

        }

        return new(errors.Count == 0, errors.Distinct(StringComparer.Ordinal).ToArray());

    }

    private static void ValidateRun(
        AdmissionBenchmarkRevisionRun reference,
        AdmissionBenchmarkRevisionRun run,
        AdmissionBenchmarkManifest manifest,
        ICollection<string> errors)
    {

        if (run.Inputs is null
            || run.Environment is null
            || run.FinalState is null
            || run.Cells is null)
        {

            errors.Add("missing: revision evidence omitted a required object or cell set.");

            return;

        }

        if (!IsCommit(run.Revision))
        {

            errors.Add("non-commit: revision is not an exact lowercase 40-hex commit.");

        }

        if (!run.CleanTree)
        {

            errors.Add("dirty: revision tree was not clean.");

        }

        if (run.SessionId != reference.SessionId)
        {

            errors.Add("session: run session does not match the measurement session.");

        }

        if (run.ExitCode == 130)
        {

            errors.Add("cancel: a measurement process was cancelled.");

        }
        else if (run.ExitCode != 0)
        {

            errors.Add("crash: a measurement process did not complete successfully.");

        }

        if (run.FinalState.LiveRequests != 0
            || run.FinalState.LiveWork != 0
            || run.FinalState.LiveOpens != 0
            || run.FinalState.LiveEffects != 0
            || run.FinalState.LiveWaiters != 0
            || !run.FinalState.DrainSucceeded
            || !run.FinalState.ReopenSucceeded)
        {

            errors.Add("leak: final admission state was not drained and reopened cleanly.");

        }

        int[] requiredChurn = [0, 64, 640];

        if (run.HistoricalChurn is null
            || !run.HistoricalChurn.Select(static result => result.DisposedAdmissions).SequenceEqual(requiredChurn)
            || run.HistoricalChurn.Any(static result => result.RetainedBytesBeforeClose < 0
                || result.RetainedBytesAfterClose < 0
                || result.CloseNanoseconds < 0
                || !double.IsFinite(result.CloseNanoseconds)
                || !result.DrainSucceeded))
        {

            errors.Add("historical-churn: predeclared maintenance observations are missing or invalid.");

        }

        CompareInputs(reference.Inputs, run.Inputs, errors);

        if (run.Environment != reference.Environment)
        {

            errors.Add("environment: runtime or machine identity changed.");

        }

        if (string.IsNullOrWhiteSpace(run.Environment.RuntimeIdentifier)
            || string.IsNullOrWhiteSpace(run.Environment.ProcessArchitecture)
            || string.IsNullOrWhiteSpace(run.Environment.OsArchitecture)
            || string.IsNullOrWhiteSpace(run.Environment.OsVersion)
            || string.IsNullOrWhiteSpace(run.Environment.RuntimeVersion)
            || string.IsNullOrWhiteSpace(run.Environment.SdkVersion)
            || string.IsNullOrWhiteSpace(run.Environment.CpuIdentity)
            || run.Environment.LogicalProcessorCount <= 0
            || run.Environment.StopwatchFrequency <= 0
            || run.Environment.DynamicCodeSupported)
        {

            errors.Add("environment-shape: runtime or machine identity is incomplete or unsupported.");

        }

        ValidateInputMap(run.Inputs, manifest, errors);

        foreach (AdmissionBenchmarkCellResult cell in run.Cells)
        {

            int expectedWorkers = cell.Concurrency switch
            {
                "one" => 1,
                "two" => 2,
                "eight" => 8,
                "logical" => run.Environment.LogicalProcessorCount,
                _ => -1,
            };

            if (cell.Workers != expectedWorkers || cell.FailureCount != 0)
            {

                errors.Add("execution: worker identity or ordinary success accounting is invalid.");

            }

        }

    }

    private static void CompareInputs(
        AdmissionBenchmarkInputIdentity expected,
        AdmissionBenchmarkInputIdentity actual,
        ICollection<string> errors)
    {

        if (actual.CatalogShapeDigest != expected.CatalogShapeDigest)
        {

            errors.Add("catalog-shape: input catalog shape changed.");

        }

        if (actual.ImmutableContentDigest != expected.ImmutableContentDigest)
        {

            errors.Add("immutable-content: immutable benchmark or product input changed.");

        }

        if (actual.HarnessDigest != expected.HarnessDigest)
        {

            errors.Add("harness: harness digest changed.");

        }

        if (actual.ManifestDigest != expected.ManifestDigest)
        {

            errors.Add("manifest: manifest digest changed.");

        }

        if (actual.ProjectDigest != expected.ProjectDigest)
        {

            errors.Add("project: project/build digest changed.");

        }

        if (actual.LockfileDigest != expected.LockfileDigest)
        {

            errors.Add("lock: dependency lock digest changed.");

        }

        if (actual.ToolchainDigest != expected.ToolchainDigest)
        {

            errors.Add("toolchain: toolchain digest changed.");

        }

        if (actual.NativeManifestDigest != expected.NativeManifestDigest
            || actual.NativeBinaryDigest != expected.NativeBinaryDigest)
        {

            errors.Add("native: native SQLCipher inputs changed.");

        }

    }

    private static void ValidateInputMap(
        AdmissionBenchmarkInputIdentity identity,
        AdmissionBenchmarkManifest manifest,
        ICollection<string> errors)
    {

        AdmissionBenchmarkDigestEntry[] sources = identity.Inputs;

        if (sources is null
            || sources.Length == 0
            || sources.Select(static source => source.Path).Distinct(StringComparer.Ordinal).Count() != sources.Length
            || !sources.SequenceEqual(sources.OrderBy(static source => source.Path, StringComparer.Ordinal)))
        {

            errors.Add("duplicate-source: compiled-source map contains duplicate paths.");

            return;

        }

        foreach (AdmissionBenchmarkDigestEntry source in sources)
        {

            if (string.IsNullOrWhiteSpace(source.Path)
                || Path.IsPathFullyQualified(source.Path)
                || source.Path.Contains('\\')
                || source.Path.Split('/').Any(static segment => segment is "" or "." or "..")
                || source.Present && !IsDigest(source.Digest)
                || !source.Present && source.Digest.Length != 0
                || !source.Present && source.Path != manifest.SourceDifferenceAllowlist[1])
            {

                errors.Add("source-map: compiled-source presence and digest disagree.");

            }

        }

        string shapeDigest = ShapeDigest(sources, manifest.SourceDifferenceAllowlist[1]);

        if (!IsDigest(identity.CatalogShapeDigest)
            || identity.CatalogShapeDigest != manifest.InputCatalogShapeDigest
            || shapeDigest != identity.CatalogShapeDigest
            || !IsDigest(identity.ImmutableContentDigest)
            || !IsDigest(identity.ManifestDigest)
            || !IsDigest(identity.HarnessDigest)
            || !IsDigest(identity.ProjectDigest)
            || !IsDigest(identity.LockfileDigest)
            || !IsDigest(identity.ToolchainDigest)
            || !IsDigest(identity.NativeManifestDigest)
            || !IsDigest(identity.NativeBinaryDigest))
        {

            errors.Add("input-digest: input identity contains a malformed or mismatched digest.");

        }

        string gate = manifest.SourceDifferenceAllowlist[0];

        if (!sources.Any(source => source.Path == gate && source.Present))
        {

            errors.Add("gate-absent: the production gate source is absent.");

        }

    }

    private static void CompareSources(
        AdmissionBenchmarkDigestEntry[] baseline,
        AdmissionBenchmarkDigestEntry[] candidate,
        AdmissionBenchmarkManifest manifest,
        ICollection<string> errors)
    {

        if (baseline is null || candidate is null)
        {

            errors.Add("source-map: a compiled-source map is missing.");

            return;

        }

        if (baseline.Select(static source => source.Path).Distinct(StringComparer.Ordinal).Count() != baseline.Length
            || candidate.Select(static source => source.Path).Distinct(StringComparer.Ordinal).Count() != candidate.Length)
        {

            return;

        }

        Dictionary<string, AdmissionBenchmarkDigestEntry> left = baseline.ToDictionary(static source => source.Path, StringComparer.Ordinal);

        Dictionary<string, AdmissionBenchmarkDigestEntry> right = candidate.ToDictionary(static source => source.Path, StringComparer.Ordinal);

        HashSet<string> paths = [.. left.Keys, .. right.Keys];

        foreach (string path in paths)
        {

            bool hasLeft = left.TryGetValue(path, out AdmissionBenchmarkDigestEntry? baselineSource);

            bool hasRight = right.TryGetValue(path, out AdmissionBenchmarkDigestEntry? candidateSource);

            bool differs = !hasLeft
                || !hasRight
                || baselineSource!.Present != candidateSource!.Present
                || baselineSource.Digest != candidateSource.Digest;

            if (!differs)
            {

                continue;

            }

            if (!manifest.SourceDifferenceAllowlist.Contains(path, StringComparer.Ordinal))
            {

                errors.Add("outside-source: compiled source changed outside the exact allowlist.");

                continue;

            }

            if (path == manifest.SourceDifferenceAllowlist[0]
                && (!baselineSource!.Present || !candidateSource!.Present))
            {

                errors.Add("gate-absent: the production gate must exist in both revisions.");

            }

            if (path == manifest.SourceDifferenceAllowlist[1]
                && baselineSource is { Present: true }
                && candidateSource is { Present: false })
            {

                errors.Add("outside-source: the epoch source may be added at C, not removed from B.");

            }

        }

    }

    private static void CompareSameRoleSources(
        AdmissionBenchmarkDigestEntry[] expected,
        AdmissionBenchmarkDigestEntry[] actual,
        string errorCode,
        ICollection<string> errors)
    {

        if (expected is null || actual is null)
        {

            errors.Add(errorCode + ": a same-revision compiled-source map is missing.");

            return;

        }

        AdmissionBenchmarkDigestEntry[] left = expected.OrderBy(static source => source.Path, StringComparer.Ordinal).ToArray();

        AdmissionBenchmarkDigestEntry[] right = actual.OrderBy(static source => source.Path, StringComparer.Ordinal).ToArray();

        if (!left.SequenceEqual(right))
        {

            errors.Add(errorCode + ": compiled inputs changed between same-revision processes.");

        }

    }

    private static bool IsCommit(string value) =>
        value is { Length: 40 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsDigest(string value) =>
        value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ShapeDigest(
        IEnumerable<AdmissionBenchmarkDigestEntry> entries,
        string optionalPath)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Span<byte> length = stackalloc byte[sizeof(int)];

        foreach (AdmissionBenchmarkDigestEntry entry in entries)
        {

            byte[] path = Encoding.UTF8.GetBytes(entry.Path);

            BinaryPrimitives.WriteInt32LittleEndian(length, path.Length);

            hash.AppendData(length);

            hash.AppendData(path);

            hash.AppendData([entry.Path == optionalPath ? (byte)1 : (byte)0]);

        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

    }

}
