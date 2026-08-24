#!/usr/bin/env python3
"""
Align common C# vertical whitespace across the repo (structure-aware enough for
day-to-day use; not a full Roslyn formatter).

Rules:
  - One blank line after the last contiguous `using ...;` before `namespace` / file types.
  - One blank line between file-level `}` and a following `///` at column 0.
  - Collapse runs of 3+ newlines to 2 (at most one blank line between non-empty lines).
  - Trim trailing whitespace on each line.
  - Between consecutive single-line type members (fields / auto-properties / events) at the same
    indentation, ensure one blank line.
  - After a closing `}` that ends a block, if the next non-empty line is a sibling at the same
    indentation (another statement, `finally`/`catch`, `await`, `return`, doc `///`, attributes
    `[`, or a following `public`/`private` member), insert one blank line when missing — so
    method bodies read like `McpBridgeTool` / `ApiBootstrapper` without hand-editing every file.
  - After a single-line type member (`public`/`private`/…), if the next line is `///` at the same
    indent, insert one blank line before the doc comment.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

SKIP_DIR_NAMES = frozenset({"Generated", "obj", "bin", ".git"})

MEMBER_LINE = re.compile(
    r"^(\s{4,})"  # type-body indent
    r"(?:\[[^\]]*\]\s*)*"  # optional attributes on same line (rare)
    r"(?:public|private|internal|protected)\s+"
    r"(?:static\s+|readonly\s+|const\s+|new\s+|override\s+|virtual\s+|async\s+|partial\s+|required\s+|extern\s+)*"
    r".+\s+[\w\.]+\s*"  # return type-ish + member name
    r"(?:=>[^;]+)?;\s*$"
)

ACCESSOR_LINE = re.compile(r"^\s+(get|set|add|remove);\s*$")

CLOSING_BRACE = re.compile(r"^(\s+)\}\s*$")

# Next line at same indent as closing `}` should get a leading blank when it starts like this:
NEXT_SIBLING_AFTER_BRACE = re.compile(
    r"^(\s+)(///|\[|"
    r"if\b|else\b|for\b|foreach\b|while\b|switch\b|do\b|"
    r"return\b|throw\b|await\b|try\b|catch\b|finally\b|lock\b|using\b|"
    r"var\b|string\b|bool\b|int\b|long\b|uint\b|ulong\b|void\b|float\b|double\b|decimal\b|byte\b|"
    r"Dictionary\b|List\b|HashSet\b|Array\b|Span\b|ReadOnlySpan\b|Memory\b|"
    r"ConcurrentDictionary\b|SemaphoreSlim\b|IReadOnlyList\b|IReadOnlyDictionary\b|IEnumerable\b|"
    r"Task\b|ValueTask\b|CancellationToken\b|ObjectDisposedException\b|"
    r"public\b|private\b|protected\b|internal\b)"
)


def should_skip_dir(path: Path, repo: Path) -> bool:
    try:
        rel = path.relative_to(repo)
    except ValueError:
        return True
    return any(p in SKIP_DIR_NAMES for p in rel.parts)


def fix_using_namespace_block(text: str) -> str:
    def repl(m: re.Match[str]) -> str:
        usings = m.group(1)
        rest = m.group(2)
        usings = usings.rstrip("\n") + "\n"
        return usings + "\n" + rest

    return re.sub(
        r"(?ms)((?:^using\s+[^;]+;\s*\n)+)(^\s*namespace\s+)",
        repl,
        text,
    )


def fix_file_level_type_gap(text: str) -> str:
    return re.sub(r"(?m)(^\}\s*\n)(///)", r"\1\n\2", text)


def collapse_multi_blank_lines(text: str) -> str:
    prev = None
    while prev != text:
        prev = text
        text = re.sub(r"\n{3,}", "\n\n", text)
    return text


def trim_trailing_ws(text: str) -> str:
    lines = text.split("\n")
    return "\n".join(line.rstrip(" \t") for line in lines)


def is_member_decl_line(line: str) -> bool:
    s = line.rstrip("\n")
    if not s.strip():
        return False
    if ACCESSOR_LINE.match(s):
        return False
    if re.search(r"\b(if|for|while|switch|return|throw|else|catch|try|lock|using)\b", s):
        return False
    return MEMBER_LINE.match(s) is not None


def insert_blank_between_member_lines(lines: list[str]) -> list[str]:
    out: list[str] = []
    for i, line in enumerate(lines):
        out.append(line)
        if i >= len(lines) - 1:
            continue
        j = i + 1
        while j < len(lines) and lines[j].strip() == "":
            j += 1
        if j >= len(lines):
            continue
        nxt = lines[j]
        if not is_member_decl_line(line) or not is_member_decl_line(nxt):
            continue
        if len(line) - len(line.lstrip(" ")) != len(nxt) - len(nxt.lstrip(" ")):
            continue
        if j > i + 1:
            continue
        out.append("")
    return out


def insert_blank_before_doc_after_member(lines: list[str]) -> list[str]:
    out: list[str] = []
    for i, line in enumerate(lines):
        out.append(line)
        if not is_member_decl_line(line):
            continue
        j = i + 1
        while j < len(lines) and lines[j].strip() == "":
            j += 1
        if j >= len(lines):
            continue
        nxt = lines[j]
        if not nxt.lstrip().startswith("///"):
            continue
        if len(line) - len(line.lstrip(" ")) != len(nxt) - len(nxt.lstrip(" ")):
            continue
        if j > i + 1:
            continue
        out.append("")
    return out


def remove_blank_before_attached_clauses(lines: list[str]) -> list[str]:
    """Remove a blank line between `}` and `else` / `catch` / `finally`."""
    i = 0
    while i < len(lines) - 2:
        if (
            re.match(r"^\s+\}\s*$", lines[i])
            and lines[i + 1].strip() == ""
            and re.match(r"^\s+(else|catch|finally)\b", lines[i + 2])
        ):
            del lines[i + 1]
            continue
        i += 1
    return lines


def insert_blank_after_closing_braces(lines: list[str]) -> list[str]:
    out: list[str] = []
    for i, line in enumerate(lines):
        out.append(line)
        m = CLOSING_BRACE.match(line)
        if not m:
            continue
        ind = len(m.group(1))
        j = i + 1
        while j < len(lines) and lines[j].strip() == "":
            j += 1
        if j >= len(lines):
            continue
        nxt = lines[j]
        if CLOSING_BRACE.match(nxt):
            continue
        n_m = re.match(r"^(\s+)", nxt)
        if not n_m or len(n_m.group(1)) != ind:
            continue
        # Keep `else` / `catch` / `finally` attached to the preceding `}` (no blank line).
        if re.match(r"^\s+(else|catch|finally)\b", nxt):
            continue
        if not NEXT_SIBLING_AFTER_BRACE.match(nxt):
            continue
        if j > i + 1:
            continue
        out.append("")
    return out


def process_file(path: Path, dry_run: bool) -> bool:
    # `raw` loses its BOM below so the rules never have to special-case column 0, but the
    # change comparison has to run against the untouched original -- comparing the re-prefixed
    # result against the stripped copy reported every BOM file as dirty forever, which is why
    # --check could never go green on the EF-generated migrations.
    original = path.read_text(encoding="utf-8")
    raw = original
    had_bom = raw.startswith("\ufeff")
    if had_bom:
        raw = raw[1:]

    text = raw.replace("\r\n", "\n")
    text = fix_using_namespace_block(text)
    text = fix_file_level_type_gap(text)

    lines = text.split("\n")
    lines = insert_blank_between_member_lines(lines)
    lines = insert_blank_before_doc_after_member(lines)
    lines = insert_blank_after_closing_braces(lines)
    lines = remove_blank_before_attached_clauses(lines)
    text = "\n".join(lines)

    text = collapse_multi_blank_lines(text)
    text = trim_trailing_ws(text)
    text = collapse_multi_blank_lines(text)
    if not text.endswith("\n"):
        text += "\n"

    new_raw = ("\ufeff" if had_bom else "") + text
    if new_raw == original:
        return False
    if not dry_run:
        path.write_text(new_raw, encoding="utf-8", newline="\n")
    return True


def iter_cs_files(repo: Path, extra: list[Path]) -> list[Path]:
    files: list[Path] = []
    if extra:
        for p in extra:
            p = p.resolve()
            if p.is_file() and p.suffix == ".cs":
                files.append(p)
            elif p.is_dir():
                for f in p.rglob("*.cs"):
                    if not should_skip_dir(f.parent, repo):
                        files.append(f)
        return sorted(set(files))

    for f in (repo / "src").rglob("*.cs"):
        if should_skip_dir(f.parent, repo):
            continue
        files.append(f)
    return sorted(set(files))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--repo", type=Path, default=Path.cwd(), help="Repository root (contains src/)")
    ap.add_argument("--check", action="store_true", help="Do not write; exit 2 if any file would change")
    ap.add_argument("paths", nargs="*", type=Path, help="Optional files/dirs (default: src/)")
    args = ap.parse_args()

    repo = args.repo.resolve()
    if not (repo / "src").is_dir():
        print(f"Expected {repo / 'src'} to exist.", file=sys.stderr)
        return 2

    changed = 0
    for path in iter_cs_files(repo, [p.resolve() for p in args.paths]):
        if process_file(path, dry_run=args.check):
            changed += 1
            print(path.relative_to(repo))

    print(f"{'Would change' if args.check else 'Updated'} {changed} file(s).")
    return 2 if args.check and changed else 0


if __name__ == "__main__":
    raise SystemExit(main())
