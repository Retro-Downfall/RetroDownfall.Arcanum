#!/usr/bin/env python3
"""Choose full CI unless an exact commit diff contains only regular authored docs."""

import argparse
from pathlib import Path, PurePosixPath
import re
import subprocess


def _git(repo, *arguments):
    return subprocess.check_output(
        ["git", "-C", str(repo), *arguments], stderr=subprocess.PIPE
    )


def _authored_document(path):
    name = path.decode("utf-8", errors="strict")
    if any(ord(character) < 32 for character in name):
        return False
    candidate = PurePosixPath(name)
    return name in ("README.md", "AGENTS.md") or (
        candidate.parent == PurePosixPath("docs") and candidate.suffix == ".md"
    )


def requires_full_qualification(repo, event_name, base, head):
    if event_name != "pull_request":
        return True
    try:
        for revision in (base, head):
            if not re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", revision):
                return True
            if _git(repo, "cat-file", "-t", revision).strip() != b"commit":
                return True
        _git(repo, "merge-base", "--is-ancestor", base, head)
        raw = _git(
            repo, "diff", "--raw", "-z", "--no-renames", "--no-ext-diff",
            "--no-textconv", "--no-abbrev", base, head, "--",
        )
        records = raw.split(b"\0")
        if not raw or records.pop() != b"" or len(records) % 2:
            return True
        for index in range(0, len(records), 2):
            fields = records[index].split(b" ")
            if len(fields) != 5 or not fields[0].startswith(b":"):
                return True
            old_mode, new_mode = fields[0][1:], fields[1]
            expected_modes = {
                b"A": (b"000000", b"100644"),
                b"D": (b"100644", b"000000"),
                b"M": (b"100644", b"100644"),
            }
            if expected_modes.get(fields[4]) != (old_mode, new_mode):
                return True
            if not _authored_document(records[index + 1]):
                return True
        return False
    except (OSError, subprocess.CalledProcessError, UnicodeError, ValueError):
        return True


def matches_tested_pull_request(repo, base, head, pr_head):
    if not all(re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", value)
               for value in (base, head, pr_head)):
        return False
    try:
        checkout = _git(repo, "rev-parse", "HEAD").decode().strip()
        parents = _git(repo, "rev-list", "--parents", "-n", "1", head).decode().split()
        return checkout == head and parents == [head, base, pr_head]
    except (OSError, subprocess.CalledProcessError, UnicodeError):
        return False


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--event", required=True)
    parser.add_argument("--base", default="")
    parser.add_argument("--head", required=True)
    parser.add_argument("--pr-head", default="")
    arguments = parser.parse_args()
    full = not matches_tested_pull_request(
        arguments.repo, arguments.base, arguments.head, arguments.pr_head
    ) or requires_full_qualification(
        arguments.repo, arguments.event, arguments.base, arguments.head
    )
    print("full=" + str(full).lower())


if __name__ == "__main__":
    main()
