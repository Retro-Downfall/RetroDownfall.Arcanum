import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { dirname, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const Seed = 0x415243414e554d31n;
const CaseCount = 128;
const RecordedNodeVersion = "24.19.0";
const RecordedV8Version = "13.6.233.17-node.51";
const RecordedPayloadBytes = 5_160;
const RecordedPayloadSha256 = "73913dabdc8cf14b603746698ebb4d32178f78b8009d44d55b35ad924573489e";

const Mask64 = (1n << 64n) - 1n;
const SplitMixIncrement = 0x9e3779b97f4a7c15n;
const SplitMixMultiplier1 = 0xbf58476d1ce4e5b9n;
const SplitMixMultiplier2 = 0x94d049bb133111ebn;

const scriptPath = fileURLToPath(import.meta.url);
const repositoryRoot = resolve(dirname(scriptPath), "..");
const testPath = resolve(
    repositoryRoot,
    "tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantCanonicalJsonTests.cs");
const provenancePath = resolve(dirname(scriptPath), "generate-covenant-v8-oracle.md");

function generateVectors()
{
    const bitsBuffer = new ArrayBuffer(8);
    const bitsView = new DataView(bitsBuffer);
    const vectors = [];
    let state = Seed;

    while (vectors.length < CaseCount)
    {
        state = (state + SplitMixIncrement) & Mask64;

        let bits = state;

        bits = ((bits ^ (bits >> 30n)) * SplitMixMultiplier1) & Mask64;
        bits = ((bits ^ (bits >> 27n)) * SplitMixMultiplier2) & Mask64;
        bits = (bits ^ (bits >> 31n)) & Mask64;

        const exponent = (bits >> 52n) & 0x7ffn;

        if (exponent === 0x7ffn)
        {
            continue;
        }

        bitsView.setBigUint64(0, bits, false);

        const value = bitsView.getFloat64(0, false);
        const canonicalNumber = JSON.stringify(value);

        if (canonicalNumber === undefined || canonicalNumber === "null")
        {
            throw new Error(`V8 did not serialize finite bits ${formatBits(bits)} as a JSON number.`);
        }

        vectors.push({ bits: formatBits(bits), canonicalNumber });
    }

    return vectors;
}

function formatBits(bits)
{
    return bits.toString(16).padStart(16, "0");
}

function createCanonicalPayload(vectors)
{
    return vectors
        .map(({ bits, canonicalNumber }) => `${bits}\t${canonicalNumber}\n`)
        .join("");
}

function createPayloadSha256(payload)
{
    return createHash("sha256").update(payload, "utf8").digest("hex");
}

function readCheckedInVectors()
{
    const source = readFileSync(testPath, "utf8");
    const startMarker = "public static TheoryData<ulong, string> V8Binary64OracleCorpus =>";
    const endMarker = "public static TheoryData<byte[]> MalformedRawUtf8Vectors =>";
    const start = source.indexOf(startMarker);
    const end = source.indexOf(endMarker, start + startMarker.length);

    if (start < 0 || end < 0)
    {
        throw new Error("The checked-in Covenant V8 oracle table markers were not found.");
    }

    const table = source.slice(start, end);
    const entryPattern = /\{ 0x([0-9a-f]{16})UL, "(-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:e[+-]?[0-9]+)?)" \}/g;
    const vectors = [];

    for (const match of table.matchAll(entryPattern))
    {
        vectors.push({ bits: match[1], canonicalNumber: match[2] });
    }

    return vectors;
}

function verifyVectorEquality(generatedVectors, checkedInVectors)
{
    if (checkedInVectors.length !== CaseCount)
    {
        throw new Error(`Expected ${CaseCount} checked-in vectors, found ${checkedInVectors.length}.`);
    }

    for (let index = 0; index < CaseCount; index++)
    {
        const generated = generatedVectors[index];
        const checkedIn = checkedInVectors[index];

        if (generated.bits !== checkedIn.bits
            || generated.canonicalNumber !== checkedIn.canonicalNumber)
        {
            throw new Error(
                `Vector ${index} differs. Generated ${generated.bits}\t${generated.canonicalNumber}, `
                + `checked in ${checkedIn.bits}\t${checkedIn.canonicalNumber}.`);
        }
    }
}

function verifyPayload(payload)
{
    const payloadBytes = Buffer.byteLength(payload, "utf8");
    const payloadSha256 = createPayloadSha256(payload);

    if (payloadBytes !== RecordedPayloadBytes)
    {
        throw new Error(`Expected ${RecordedPayloadBytes} payload bytes, generated ${payloadBytes}.`);
    }

    if (payloadSha256 !== RecordedPayloadSha256)
    {
        throw new Error(`Expected payload SHA-256 ${RecordedPayloadSha256}, generated ${payloadSha256}.`);
    }

    return { payloadBytes, payloadSha256 };
}

function verifyProvenance()
{
    const provenance = readFileSync(provenancePath, "utf8");
    const requiredLines =
    [
        `- Seed: \`0x${Seed.toString(16).toUpperCase()}\``,
        `- Case count: \`${CaseCount}\``,
        `- Generation runtime: Node.js \`${RecordedNodeVersion}\``,
        `- Generation engine: V8 \`${RecordedV8Version}\``,
        `- Canonical payload bytes: \`${RecordedPayloadBytes}\``,
        `- Canonical payload SHA-256: \`${RecordedPayloadSha256}\``
    ];

    for (const requiredLine of requiredLines)
    {
        if (!provenance.includes(requiredLine))
        {
            throw new Error(`Provenance is missing the exact line: ${requiredLine}`);
        }
    }
}

function emitCSharp(vectors)
{
    for (let index = 0; index < vectors.length; index++)
    {
        const { bits, canonicalNumber } = vectors[index];
        const comma = index < vectors.length - 1 ? "," : "";

        process.stdout.write(`            { 0x${bits}UL, "${canonicalNumber}" }${comma}\n`);
    }
}

function writeSummary(payloadBytes, payloadSha256)
{
    const testRelativePath = relative(repositoryRoot, testPath);
    const provenanceRelativePath = relative(repositoryRoot, provenancePath);

    process.stderr.write(
        `cases=${CaseCount}\n`
        + `seed=0x${Seed.toString(16).toUpperCase()}\n`
        + `recorded_node=${RecordedNodeVersion}\n`
        + `recorded_v8=${RecordedV8Version}\n`
        + `current_node=${process.versions.node}\n`
        + `current_v8=${process.versions.v8}\n`
        + `payload_bytes=${payloadBytes}\n`
        + `payload_sha256=${payloadSha256}\n`
        + `test_table=${testRelativePath}\n`
        + `provenance=${provenanceRelativePath}\n`);
}

function main()
{
    const mode = process.argv[2] ?? "--verify";

    if (process.argv.length > 3
        || !["--verify", "--emit-csharp", "--emit-payload"].includes(mode))
    {
        throw new Error(
            "Usage: node scripts/generate-covenant-v8-oracle.mjs "
            + "[--verify|--emit-csharp|--emit-payload]");
    }

    const generatedVectors = generateVectors();
    const payload = createCanonicalPayload(generatedVectors);
    const { payloadBytes, payloadSha256 } = verifyPayload(payload);

    if (mode === "--verify")
    {
        verifyVectorEquality(generatedVectors, readCheckedInVectors());
        verifyProvenance();
        process.stdout.write(`Verified ${CaseCount} Covenant V8/JCS binary64 vectors.\n`);
    }
    else if (mode === "--emit-csharp")
    {
        emitCSharp(generatedVectors);
    }
    else
    {
        process.stdout.write(payload);
    }

    writeSummary(payloadBytes, payloadSha256);
}

try
{
    main();
}
catch (error)
{
    const message = error instanceof Error ? error.message : String(error);

    process.stderr.write(`Covenant V8 oracle generation failed: ${message}\n`);
    process.exitCode = 1;
}
