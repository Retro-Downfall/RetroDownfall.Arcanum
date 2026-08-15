# Covenant V8 Binary64 Oracle Provenance

This artifact records the reproducible source of the 128 literal `V8Binary64OracleCorpus` pairs in `CovenantCanonicalJsonTests.cs`. The generator uses only Node.js built-ins and is never a product or test runtime dependency.

## Generation identity

- Generator: `scripts/generate-covenant-v8-oracle.mjs`
- Algorithm: SplitMix64 with unsigned 64-bit wraparound, skipping exponent field `0x7ff`
- Seed: `0x415243414E554D31`
- Case count: `128`
- Generation runtime: Node.js `24.19.0`
- Generation engine: V8 `13.6.233.17-node.51`
- Number serializer: V8 `JSON.stringify` on the finite binary64 value represented by each generated bit pattern

## Canonical vector payload

The hashed payload contains no header or BOM. It is UTF-8 with exactly 128 lines in generation order. Each line is the lowercase 16-digit hexadecimal binary64 bits without `0x`, one U+0009 tab, the V8 JSON number, and one U+000A LF. The final line includes its LF.

- Canonical payload bytes: `5160`
- Canonical payload SHA-256: `73913dabdc8cf14b603746698ebb4d32178f78b8009d44d55b35ad924573489e`

## Offline reproduction

From the repository root:

```bash
node scripts/generate-covenant-v8-oracle.mjs --verify
node scripts/generate-covenant-v8-oracle.mjs --emit-csharp
node scripts/generate-covenant-v8-oracle.mjs --emit-payload
```

`--verify` regenerates all 128 pairs, verifies the payload byte length and SHA-256, compares every generated pair with the checked-in C# table in order, and verifies the pinned metadata in this file. The two emit modes write the C# initializer rows or the canonical payload to standard output and write generation evidence to standard error.
