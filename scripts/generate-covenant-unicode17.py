#!/usr/bin/env python3
"""Generate Covenant policy-v1 Unicode 17 tables and its NFC test corpus."""

from __future__ import annotations

import argparse
import hashlib
import struct
from dataclasses import dataclass
from pathlib import Path


GENERATOR_VERSION = 1
UNICODE_VERSION = "17.0.0"

SOURCES = {
    "UnicodeData.txt": (
        "https://www.unicode.org/Public/17.0.0/ucd/UnicodeData.txt",
        "2e1efc1dcb59c575eedf5ccae60f95229f706ee6d031835247d843c11d96470c",
    ),
    "DerivedNormalizationProps.txt": (
        "https://www.unicode.org/Public/17.0.0/ucd/DerivedNormalizationProps.txt",
        "71fd6a206a2c0cdd41feb6b7f656aa31091db45e9cedc926985d718397f9e488",
    ),
    "NormalizationTest.txt": (
        "https://www.unicode.org/Public/17.0.0/ucd/NormalizationTest.txt",
        "5019ffd530751a741900c849c0e010332f142a3612234639bd200b82138a87db",
    ),
}

EXPECTED_DECOMPOSITIONS = 2_081
EXPECTED_NONZERO_CCC = 968
EXPECTED_FULL_COMPOSITION_EXCLUSIONS = 1_120
EXPECTED_COMPOSITIONS = 961
EXPECTED_FORMAT_SCALARS = 170
EXPECTED_FORMAT_RANGES = 21
EXPECTED_NORMALIZATION_CASES = 20_034

TABLE_PATH = Path("src/RetroDownfall.Arcanum.Core/Covenant/CovenantUnicode17Tables.g.cs")
CORPUS_PATH = Path("tests/RetroDownfall.Arcanum.Tests/TestData/Covenant/Unicode17/NormalizationTest.nfc.bin")
PROVENANCE_PATH = Path("tests/RetroDownfall.Arcanum.Tests/TestData/Covenant/Unicode17/PROVENANCE.md")


@dataclass(frozen=True)
class UnicodeRecord:
    scalar: int
    category: str
    combining_class: int
    canonical_decomposition: tuple[int, ...]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input-dir", required=True, type=Path)
    parser.add_argument("--repo-root", default=Path.cwd(), type=Path)
    args = parser.parse_args()

    input_dir = args.input_dir.resolve()
    repo_root = args.repo_root.resolve()
    verified = verify_sources(input_dir)

    records = parse_unicode_data(verified["UnicodeData.txt"])
    exclusions = parse_full_composition_exclusions(verified["DerivedNormalizationProps.txt"])
    decompositions, decomposition_scalars = build_decompositions(records)
    combining_classes = build_combining_classes(records)
    compositions = build_compositions(records, exclusions)
    format_ranges, format_scalar_count = build_format_ranges(records)
    normalization_corpus, normalization_case_count = build_normalization_corpus(
        verified["NormalizationTest.txt"]
    )

    assert_count("canonical decomposition records", len(decompositions), EXPECTED_DECOMPOSITIONS)
    assert_count("nonzero CCC records", len(combining_classes), EXPECTED_NONZERO_CCC)
    assert_count("full composition exclusions", len(exclusions), EXPECTED_FULL_COMPOSITION_EXCLUSIONS)
    assert_count("eligible composition pairs", len(compositions), EXPECTED_COMPOSITIONS)
    assert_count("Format scalars", format_scalar_count, EXPECTED_FORMAT_SCALARS)
    assert_count("Format ranges", len(format_ranges), EXPECTED_FORMAT_RANGES)
    assert_count("normalization cases", normalization_case_count, EXPECTED_NORMALIZATION_CASES)

    table = render_table_source(
        decompositions,
        decomposition_scalars,
        combining_classes,
        compositions,
        format_ranges,
    ).encode("utf-8")

    write_generated(repo_root / TABLE_PATH, table)
    write_generated(repo_root / CORPUS_PATH, normalization_corpus)

    provenance = render_provenance(
        verified,
        len(table),
        len(normalization_corpus),
        normalization_case_count,
    ).encode("utf-8")

    write_generated(repo_root / PROVENANCE_PATH, provenance)
    return 0


def verify_sources(input_dir: Path) -> dict[str, bytes]:
    verified: dict[str, bytes] = {}

    for filename, (_, expected_hash) in SOURCES.items():
        path = input_dir / filename
        data = path.read_bytes()
        actual_hash = hashlib.sha256(data).hexdigest()

        if actual_hash != expected_hash:
            raise ValueError(
                f"{filename} SHA-256 mismatch: expected {expected_hash}, got {actual_hash}."
            )

        verified[filename] = data

    return verified


def parse_unicode_data(data: bytes) -> list[UnicodeRecord]:
    records: list[UnicodeRecord] = []
    previous_scalar = -1

    for line_number, line in enumerate(data.decode("utf-8", errors="strict").splitlines(), 1):
        fields = line.split(";")

        if len(fields) != 15:
            raise ValueError(f"UnicodeData.txt line {line_number} does not have 15 fields.")

        scalar = int(fields[0], 16)

        if scalar <= previous_scalar:
            raise ValueError(f"UnicodeData.txt is not strictly ordered at line {line_number}.")

        previous_scalar = scalar
        decomposition_field = fields[5]
        canonical_decomposition: tuple[int, ...] = ()

        if decomposition_field and not decomposition_field.startswith("<"):
            canonical_decomposition = tuple(int(value, 16) for value in decomposition_field.split())

            if len(canonical_decomposition) not in (1, 2):
                raise ValueError(
                    f"UnicodeData.txt line {line_number} has an unsupported canonical decomposition length."
                )

        records.append(
            UnicodeRecord(
                scalar=scalar,
                category=fields[2],
                combining_class=int(fields[3], 10),
                canonical_decomposition=canonical_decomposition,
            )
        )

    return records


def parse_full_composition_exclusions(data: bytes) -> set[int]:
    exclusions: set[int] = set()

    for line in data.decode("utf-8", errors="strict").splitlines():
        body = line.split("#", 1)[0].strip()

        if not body:
            continue

        fields = [field.strip() for field in body.split(";")]

        if len(fields) < 2 or fields[1] != "Full_Composition_Exclusion":
            continue

        bounds = fields[0].split("..", 1)
        start = int(bounds[0], 16)
        end = int(bounds[1], 16) if len(bounds) == 2 else start
        exclusions.update(range(start, end + 1))

    return exclusions


def build_decompositions(records: list[UnicodeRecord]) -> tuple[list[int], list[int]]:
    packed_records: list[int] = []
    scalars: list[int] = []

    for record in records:
        if not record.canonical_decomposition:
            continue

        offset = len(scalars)
        length = len(record.canonical_decomposition)

        if offset >= 1 << 12:
            raise ValueError("Canonical decomposition scalar offsets exceed the packed table width.")

        packed_records.append((record.scalar << 13) | (offset << 1) | (length - 1))
        scalars.extend(record.canonical_decomposition)

    return packed_records, scalars


def build_combining_classes(records: list[UnicodeRecord]) -> list[int]:
    return [
        (record.scalar << 8) | record.combining_class
        for record in records
        if record.combining_class != 0
    ]


def build_compositions(records: list[UnicodeRecord], exclusions: set[int]) -> list[int]:
    combining_by_scalar = {record.scalar: record.combining_class for record in records}
    compositions: list[tuple[int, int, int]] = []

    for record in records:
        decomposition = record.canonical_decomposition

        if len(decomposition) != 2 or record.scalar in exclusions:
            continue

        first, second = decomposition

        if combining_by_scalar.get(first, 0) != 0:
            raise ValueError(f"Composition starter U+{first:04X} has a nonzero combining class.")

        compositions.append((first, second, record.scalar))

    compositions.sort()

    if len({(first, second) for first, second, _ in compositions}) != len(compositions):
        raise ValueError("Eligible composition pairs are not unique.")

    return [
        (first << 42) | (second << 21) | composed
        for first, second, composed in compositions
    ]


def build_format_ranges(records: list[UnicodeRecord]) -> tuple[list[int], int]:
    scalars = [record.scalar for record in records if record.category == "Cf"]
    ranges: list[tuple[int, int]] = []

    for scalar in scalars:
        if not ranges or scalar != ranges[-1][1] + 1:
            ranges.append((scalar, scalar))
        else:
            ranges[-1] = (ranges[-1][0], scalar)

    return [(start << 21) | end for start, end in ranges], len(scalars)


def build_normalization_corpus(data: bytes) -> tuple[bytes, int]:
    cases: list[tuple[int, tuple[bytes, bytes, bytes, bytes, bytes]]] = []

    for line_number, line in enumerate(data.decode("utf-8", errors="strict").splitlines(), 1):
        body = line.split("#", 1)[0].strip()

        if not body or body.startswith("@"):
            continue

        fields = body.split(";")

        if len(fields) < 5:
            raise ValueError(f"NormalizationTest.txt line {line_number} has fewer than five fields.")

        encoded_fields: list[bytes] = []

        for field in fields[:5]:
            text = "".join(chr(int(value, 16)) for value in field.split())
            encoded = text.encode("utf-8", errors="strict")

            if len(encoded) > 0xFFFF:
                raise ValueError(f"NormalizationTest.txt line {line_number} exceeds the corpus field width.")

            encoded_fields.append(encoded)

        cases.append((line_number, tuple(encoded_fields)))

    output = bytearray(b"ARCUNFC1")
    output.extend(bytes.fromhex(SOURCES["NormalizationTest.txt"][1]))
    output.extend(struct.pack(">I", len(cases)))

    for line_number, fields in cases:
        output.extend(struct.pack(">I", line_number))

        for field in fields:
            output.extend(struct.pack(">H", len(field)))
            output.extend(field)

    return bytes(output), len(cases)


def render_table_source(
    decompositions: list[int],
    decomposition_scalars: list[int],
    combining_classes: list[int],
    compositions: list[int],
    format_ranges: list[int],
) -> str:
    return "\n".join(
        [
            "// <auto-generated />",
            "// Generated by scripts/generate-covenant-unicode17.py version 1.",
            "// Unicode data is licensed under Unicode-3.0. See the checked-in Unicode17 provenance and license notice.",
            "",
            "namespace RetroDownfall.Arcanum.Core.Covenant;",
            "",
            "internal static class CovenantUnicode17Tables",
            "{",
            f"    internal const int CanonicalDecompositionCount = {len(decompositions):_};",
            "",
            f"    internal const int NonzeroCombiningClassCount = {len(combining_classes)};",
            "",
            f"    internal const int FullCompositionExclusionCount = {EXPECTED_FULL_COMPOSITION_EXCLUSIONS:_};",
            "",
            f"    internal const int CompositionPairCount = {len(compositions)};",
            "",
            f"    internal const int FormatScalarCount = {EXPECTED_FORMAT_SCALARS};",
            "",
            f"    internal const int FormatRangeCount = {len(format_ranges)};",
            "",
            "    internal static ReadOnlySpan<ulong> CanonicalDecompositions =>",
            "    [",
            *format_values(decompositions, "ulong"),
            "    ];",
            "",
            "    internal static ReadOnlySpan<uint> DecompositionScalars =>",
            "    [",
            *format_values(decomposition_scalars, "uint"),
            "    ];",
            "",
            "    internal static ReadOnlySpan<uint> CombiningClasses =>",
            "    [",
            *format_values(combining_classes, "uint"),
            "    ];",
            "",
            "    internal static ReadOnlySpan<ulong> Compositions =>",
            "    [",
            *format_values(compositions, "ulong"),
            "    ];",
            "",
            "    internal static ReadOnlySpan<ulong> FormatRanges =>",
            "    [",
            *format_values(format_ranges, "ulong"),
            "    ];",
            "}",
            "",
        ]
    )


def format_values(values: list[int], kind: str) -> list[str]:
    width = 16 if kind == "ulong" else 8
    suffix = "UL" if kind == "ulong" else "U"
    rendered = [f"0x{value:0{width}X}{suffix}" for value in values]
    return [
        "        " + ", ".join(rendered[index : index + 4]) + ","
        for index in range(0, len(rendered), 4)
    ]


def render_provenance(
    verified: dict[str, bytes],
    table_bytes: int,
    corpus_bytes: int,
    normalization_cases: int,
) -> str:
    source_rows = []

    for filename, (url, digest) in SOURCES.items():
        source_rows.append(f"| `{filename}` | {url} | `{digest}` | {len(verified[filename])} |")

    return f"""# Covenant Unicode 17 provenance

The Covenant compiler policy v1 tables were generated solely from the checksum-verified Unicode {UNICODE_VERSION} inputs below. Product and test execution never download or inspect host Unicode data.

| Input | Official URL | SHA-256 | Bytes |
|---|---|---|---:|
{chr(10).join(source_rows)}

Generator version: `{GENERATOR_VERSION}`

Deterministic command:

```bash
python3 scripts/generate-covenant-unicode17.py --input-dir <verified-ucd-directory> --repo-root .
```

Generated artifacts:

- `CovenantUnicode17Tables.g.cs`: {table_bytes} bytes
- `NormalizationTest.nfc.bin`: {corpus_bytes} bytes, {normalization_cases} complete official conformance rows
- canonical decomposition records: {EXPECTED_DECOMPOSITIONS}
- nonzero canonical combining-class records: {EXPECTED_NONZERO_CCC}
- full composition exclusions applied: {EXPECTED_FULL_COMPOSITION_EXCLUSIONS}
- eligible composition pairs: {EXPECTED_COMPOSITIONS}
- Unicode `Cf` scalars: {EXPECTED_FORMAT_SCALARS}, compressed to {EXPECTED_FORMAT_RANGES} ranges

The Unicode inputs and generated tables are licensed under `Unicode-3.0`. The required copyright and permission notice is preserved in `LICENSE-Unicode-3.0.txt`, downloaded from https://www.unicode.org/license.txt with SHA-256 `e7a93b009565cfce55919a381437ac4db883e9da2126fa28b91d12732bc53d96`.
"""


def write_generated(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def assert_count(name: str, actual: int, expected: int) -> None:
    if actual != expected:
        raise ValueError(f"Unexpected {name}: expected {expected}, got {actual}.")


if __name__ == "__main__":
    raise SystemExit(main())
