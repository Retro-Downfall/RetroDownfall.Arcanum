#!/usr/bin/env python3
"""Repair one authenticated .NET 11 Darwin archive-member identity in private build staging."""
import argparse
import hashlib
import os
from pathlib import Path
import sys
import tempfile

PACKAGE_VERSION = "11.0.0"
ARCHIVE_SIZE = 7_379_304
INPUT_SHA256 = "c570ce37acd42e93d9eca3ec724470abf9e27d96a4d7215f9f0afad3e22db14f"
OUTPUT_SHA256 = "6cecb3c68eb4f98c1cbee073cbf7346e0e7b8033b2100e2f61b5690af13813fa"
TIMESTAMP_OFFSET = 2_951_688
BYTE_OFFSET = 2_951_697


def prepare(source, output, package_version):
    if package_version != PACKAGE_VERSION:
        raise ValueError("only the audited portable 11.0.0 GC archive is supported")
    if source.resolve() == output.resolve():
        raise ValueError("input and private staging output must be different files")
    with source.open("rb") as stream:
        data = stream.read(ARCHIVE_SIZE + 1)
    if len(data) != ARCHIVE_SIZE or hashlib.sha256(data).hexdigest() != INPUT_SHA256:
        raise ValueError("GC archive bytes do not match the audited input SHA-256")
    if data[TIMESTAMP_OFFSET:TIMESTAMP_OFFSET + 12] != b"1793326188  " or data[BYTE_OFFSET] != ord("8"):
        raise ValueError("the audited duplicate member timestamp is absent")

    # The second contractpointerdata.cpp.o needs a distinct nonzero Darwin member timestamp.
    # No archive index, member offset, object payload, code, symbol or DWARF byte changes.
    prepared = bytearray(data)
    prepared[BYTE_OFFSET] = ord("9")
    if hashlib.sha256(prepared).hexdigest() != OUTPUT_SHA256:
        raise ValueError("prepared archive does not match the audited output SHA-256")

    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=output.parent, prefix="." + output.name + ".", delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(prepared)
        os.replace(temporary, output)
        temporary = None
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-version", required=True)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        prepare(args.input, args.output, args.package_version)
        return 0
    except (OSError, ValueError) as error:
        print("Native AOT GC staging refused: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
