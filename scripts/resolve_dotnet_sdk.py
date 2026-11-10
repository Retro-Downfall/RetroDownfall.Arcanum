#!/usr/bin/env python3
"""Resolve the highest stable supported SDK from Microsoft's bounded release index."""
import argparse
import json
from pathlib import Path
import re
import sys
import subprocess
import urllib.request
import xml.etree.ElementTree as ET

INDEX_URL = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json"
MAX_BYTES = 1024 * 1024
MAX_CHANNELS = 128
TIMEOUT_SECONDS = 20


def stable_version(value):
    if not isinstance(value, str) or not re.fullmatch(r"[1-9][0-9]*\.[0-9]+\.[0-9]{3}", value):
        raise ValueError("SDK version must be a full stable version")
    return tuple(int(part) for part in value.split("."))


def read_metadata(stream):
    data = stream.read(MAX_BYTES + 1)
    if len(data) > MAX_BYTES:
        raise ValueError("release metadata exceeds 1 MiB")
    return json.loads(data)


class OfficialRedirectsOnly(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if newurl != INDEX_URL:
            raise ValueError("Invalid release metadata redirect: only the official index is allowed")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def fetch_metadata():
    opener = urllib.request.build_opener(OfficialRedirectsOnly())
    with opener.open(INDEX_URL, timeout=TIMEOUT_SECONDS) as response:
        return read_metadata(response)


def resolve(metadata, minimum):
    if not isinstance(metadata, dict) or not isinstance(metadata.get("releases-index"), list):
        raise ValueError("Invalid release metadata: missing releases-index array")
    rows = metadata["releases-index"]
    if len(rows) > MAX_CHANNELS:
        raise ValueError("Invalid release metadata: too many channels")
    candidates = []
    for row in rows:
        if not isinstance(row, dict):
            raise ValueError("Invalid release metadata: channel must be an object")
        if row.get("support-phase") not in ("active", "maintenance"):
            continue
        channel = row.get("channel-version")
        if not isinstance(channel, str) or not re.fullmatch(r"[1-9][0-9]*\.[0-9]+", channel):
            raise ValueError("Invalid release metadata: invalid channel")
        official_feed = f"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{channel}/releases.json"
        if row.get("releases.json") != official_feed:
            raise ValueError("Invalid release metadata: unknown channel feed")
        version = row.get("latest-sdk")
        # Preview versions are never candidates even when a channel claims supported status.
        if isinstance(version, str) and re.fullmatch(r"[1-9][0-9]*\.[0-9]+\.[0-9]{3}-[0-9A-Za-z.-]+", version):
            continue
        try:
            parsed = stable_version(version)
        except ValueError as error:
            raise ValueError("Invalid release metadata: invalid SDK version") from error
        if parsed[:2] != tuple(int(part) for part in channel.split(".")):
            raise ValueError("Invalid release metadata: SDK/channel mismatch")
        if parsed >= minimum:
            candidates.append((parsed, version))
    if not candidates:
        raise ValueError("No stable supported SDK satisfies the repository minimum")
    return max(candidates)[1]


def target_runtime(build_props):
    """Read the shared literal target framework instead of inferring it from the build SDK."""
    try:
        root = ET.parse(build_props).getroot()
    except ET.ParseError as error:
        raise ValueError("Invalid target framework policy XML") from error
    frameworks = root.findall("./PropertyGroup/TargetFramework")
    if len(frameworks) != 1 or root.findall("./PropertyGroup/TargetFrameworks"):
        raise ValueError("A single literal target framework is required")
    match = re.fullmatch(r"net([1-9][0-9]*)\.([0-9]+)", (frameworks[0].text or "").strip())
    if match is None:
        raise ValueError("A literal net<major>.<minor> target framework is required")
    return tuple(int(part) for part in match.groups())


def verify_runtimes(required):
    result = subprocess.run(["dotnet", "--list-runtimes"], text=True, capture_output=True, check=True)
    installed = set()
    for line in result.stdout.splitlines():
        match = re.match(r"^(Microsoft\.(?:NETCore|AspNetCore)\.App) ([0-9]+)\.([0-9]+)\.[0-9]+ \[", line)
        if match and tuple(int(part) for part in match.groups()[1:]) == required:
            installed.add(match.group(1))
    frameworks = ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App")
    missing = [framework for framework in frameworks if framework not in installed]
    version = ".".join(str(part) for part in required)
    if missing:
        raise ValueError("Missing target framework runtime(s): " + ", ".join(f"{name} {version}" for name in missing)
                         + f". Install the {version}.x SDK in the isolated dotnet root.")
    print("Verified target framework runtimes: " + ", ".join(f"{name} {version}" for name in frameworks))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--global-json", type=Path, default=Path(__file__).resolve().parents[1] / "global.json")
    source = parser.add_mutually_exclusive_group()
    source.add_argument("--metadata-file", type=Path, help="Resolve an archived official-format release index")
    source.add_argument("--validate-version", help="Validate a version inherited from this workflow run")
    source.add_argument("--runtime-sdk-version", help="Select the target-runtime SDK alongside this exact build SDK")
    source.add_argument("--verify-runtimes", action="store_true", help="Verify both shared target-framework runtimes")
    parser.add_argument("--build-props", type=Path, default=Path(__file__).resolve().parents[1] / "Directory.Build.props")
    args = parser.parse_args()
    try:
        policy = json.loads(args.global_json.read_text(encoding="utf-8"))["sdk"]
        minimum = stable_version(policy["version"])
        if policy.get("rollForward") != "latestMajor" or policy.get("allowPrerelease") is not False:
            raise ValueError("SDK policy must use latestMajor and allowPrerelease:false")
        if args.runtime_sdk_version is not None:
            parsed = stable_version(args.runtime_sdk_version)
            if parsed < minimum:
                raise ValueError("SDK version is below the repository minimum")
            required = target_runtime(args.build_props)
            selected = args.runtime_sdk_version if parsed[:2] == required else ".".join(str(part) for part in required) + ".x"
        elif args.verify_runtimes:
            verify_runtimes(target_runtime(args.build_props))
            return 0
        elif args.validate_version is not None:
            if stable_version(args.validate_version) < minimum:
                raise ValueError("SDK version is below the repository minimum")
            selected = args.validate_version
        elif args.metadata_file is not None:
            with args.metadata_file.open("rb") as stream:
                selected = resolve(read_metadata(stream), minimum)
        else:
            selected = resolve(fetch_metadata(), minimum)
        print(selected)
        return 0
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        print(f"SDK resolution failed (release metadata/policy): {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
