#!/usr/bin/env python3
"""Independent maintainer qualification and verification for an Arcanum repair bundle."""

import argparse
import base64
import hashlib
import json
import os
import platform
from pathlib import Path
import re
import shutil
import signal
import stat
import subprocess
import sys
import tarfile
import tempfile


CONTRACT = "ollama-tool-continuation-v1"
RIDS = {"osx-arm64", "win-x64", "win-arm64"}
FAMILIES = {"core", "guildCanonical", "covenantCanonical", "covenantAccelerator"}
CONDITIONS = ["schemaIdentical", "sqlCipherIdentical"]
BUNDLE_MEMBERS = {"receipt.json", "receipt.sig", "closure.json", "payload", "install-closure.json", "install"}
INSTALL_LICENSES = {
    "licenses/umap-learn-LICENSE.txt",
    "licenses/Microsoft.Data.Sqlite.Core/LICENSE.txt",
    "licenses/Microsoft.Data.Sqlite.Core/README.md",
    "licenses/Microsoft.Data.Sqlite.Core/source-manifest.json",
}
P256_SPKI_PREFIX = bytes.fromhex("3059301306072a8648ce3d020106082a8648ce3d03010703420004")
SCHEMA_ROOT = "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema"
ADAPTER_ROOT = "src/RetroDownfall.Arcanum.Api/Intelligence/"
ADAPTER_REPAIR_PATHS = {ADAPTER_ROOT + name + ".cs" for name in (
    "ChatClientFactory", "OllamaReasoningChatClient", "ToolExecutionPipeline", "WizardIntelligenceProvider",
)}
TERMINAL_OUTCOMES = {"passed", "failed", "cancelled", "unavailable", "incomplete"}


class QualificationError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise QualificationError(message)


def require_keys(document, keys, label):
    require(type(document) is dict and document.keys() == set(keys), f"Invalid {label} fields.")


def digest(value):
    return hashlib.sha256(value).hexdigest()


def encode(document):
    return (json.dumps(document, sort_keys=True, separators=(",", ":"), ensure_ascii=True) + "\n").encode()


def decode(value, label):
    def unique(pairs):
        result = {}
        for key, item in pairs:
            require(key not in result, f"Duplicate {label} field.")
            result[key] = item
        return result

    return json.loads(value.decode("utf-8"), object_pairs_hook=unique)


def is_plain(metadata, kind):
    return kind(metadata.st_mode) and not getattr(metadata, "st_file_attributes", 0) & 0x400


def require_directory(path):
    require(is_plain(path.lstat(), stat.S_ISDIR), "Bundle directory is missing or linked.")


def regular_identity(metadata):
    return (metadata.st_dev, metadata.st_ino, metadata.st_mode, metadata.st_nlink,
            metadata.st_size, metadata.st_mtime_ns, metadata.st_ctime_ns)


def read_regular(path, consume):
    initial = path.lstat()
    require(is_plain(initial, stat.S_ISREG) and initial.st_nlink == 1, "Bundle file is missing or linked.")
    options = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_BINARY", 0) | getattr(os, "O_NONBLOCK", 0)
    with os.fdopen(os.open(path, options), "rb") as stream:
        identity = regular_identity(initial)
        require(regular_identity(os.fstat(stream.fileno())) == identity, "Bundle file changed while opening.")
        result = consume(stream, initial)
        require(regular_identity(os.fstat(stream.fileno())) == identity
                and regular_identity(path.lstat()) == identity, "Bundle file changed while reading.")
        return result


def read_small(path, limit):
    def read(stream, metadata):
        require(metadata.st_size <= limit, "Bundle metadata exceeds its limit.")
        data = stream.read(limit + 1)
        require(len(data) == metadata.st_size, "Bundle metadata changed or exceeds its limit.")
        return data

    return read_regular(path, read)


def is_hash(value, length=64):
    return type(value) is str and re.fullmatch(f"[0-9a-f]{{{length}}}", value) is not None


def apphost_name(rid):
    return "RetroDownfall.Arcanum.Cli" + (".exe" if rid.startswith("win-") else "")


def payload_manifest(payload):
    entries = []
    paths = set()

    def walk(directory):
        require_directory(directory)
        for child in sorted(directory.iterdir()):
            relative = child.relative_to(payload).as_posix()
            require(all(re.fullmatch(r"[A-Za-z0-9_.+-]+", part) and not part.endswith(".")
                        for part in relative.split("/")), "Invalid bundle member name.")
            folded = relative.casefold()
            require(folded not in paths, "Bundle names would collide across installation platforms.")
            paths.add(folded)
            metadata = child.lstat()
            if is_plain(metadata, stat.S_ISDIR):
                walk(child)
                require(any(entry["path"].startswith(relative + "/") for entry in entries),
                        "Empty bundle directories are unsupported.")
                continue

            def inspect(stream, opened):
                executable = child.suffix.lower() == ".exe" if os.name == "nt" else bool(opened.st_mode & 0o111)
                return {"path": relative, "length": opened.st_size, "executable": executable,
                        "sha256": hashlib.file_digest(stream, "sha256").hexdigest()}

            entries.append(read_regular(child, inspect))

    walk(payload)
    require(entries, "Bundle payload is empty.")
    return {"version": 1, "files": sorted(entries, key=lambda entry: entry["path"])}



def install_projection(manifest, rid):
    windows = rid.startswith("win-")
    natives = ("e_sqlcipher.dll", "libonigwrap.dll") if windows else ("libe_sqlcipher.dylib", "libonigwrap.dylib")
    mapping = {path: path for path in INSTALL_LICENSES | set(natives)}
    mapping[apphost_name(rid)] = "arcanum.exe" if windows else "arcanum"
    entries = {item["path"]: item for item in manifest["files"]}
    require(mapping.keys() <= entries.keys(), "The raw payload lacks the canonical install projection.")
    require({path for path in entries if path.startswith("licenses/")} == INSTALL_LICENSES,
            "The raw payload has an unreviewed install license projection.")
    projected = [dict(entries[source], path=destination) for source, destination in mapping.items()]
    return mapping, {"version": 1, "files": sorted(projected, key=lambda entry: entry["path"])}


def stage_install(payload, install, manifest, rid):
    mapping, projected = install_projection(manifest, rid)
    install.mkdir(mode=0o700)
    for source, destination in mapping.items():
        target = install / destination
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(payload / source, target, follow_symlinks=False)
    require(payload_manifest(install) == projected, "The canonical install projection changed while staging.")
    return projected

def verify_signature(receipt, signature, public_bytes):
    lines = public_bytes.strip().splitlines()
    require(len(lines) >= 3 and lines[0] == b"-----BEGIN PUBLIC KEY-----" and lines[-1] == b"-----END PUBLIC KEY-----",
            "The trusted key must be a P-256 public key PEM.")
    try:
        encoded = base64.b64decode(b"".join(lines[1:-1]), validate=True)
    except ValueError as error:
        raise QualificationError("The trusted key must be a P-256 public key PEM.") from error
    require(len(encoded) == 91 and encoded.startswith(P256_SPKI_PREFIX), "The trusted key must be P-256.")
    with tempfile.TemporaryDirectory(prefix="arcanum-repair-signature-") as directory:
        root = Path(directory)
        key = root / "key.pem"
        detached = root / "signature"
        key.write_bytes(public_bytes)
        detached.write_bytes(signature)
        process = subprocess.run(["openssl", "dgst", "-sha256", "-verify", str(key), "-signature", str(detached)],
                                 input=receipt, capture_output=True, check=False, timeout=15)
    require(process.returncode == 0, "The operator signature did not verify.")


def validate_schema(value):
    require_keys(value, ["families", "nativeSqlCipherSha256"], "schema identity")
    require(is_hash(value["nativeSqlCipherSha256"]), "Missing native SQLite identity.")
    require_keys(value["families"], FAMILIES, "schema families")
    for family in value["families"].values():
        require_keys(family, ["version", "fingerprint"], "schema family")
        require(type(family["version"]) is int and family["version"] > 0 and is_hash(family["fingerprint"]),
                "Incomplete schema family identity.")


def validate_receipt(receipt, expected_rid):
    require_keys(receipt, ["version", "contract", "disposition", "rid", "acceptanceCommit", "acceptanceTree",
                           "affected", "candidate", "checks", "stateCompatibility"], "receipt")
    require(type(receipt["version"]) is int and receipt["version"] == 1 and receipt["contract"] == CONTRACT,
            "Unsupported qualification receipt contract.")
    require(expected_rid in RIDS and receipt["rid"] == expected_rid, "Qualification target does not match the requested RID.")
    require(receipt["disposition"] in tuple(TERMINAL_OUTCOMES), "Unknown terminal qualification outcome.")
    require(is_hash(receipt["acceptanceCommit"], 40) and is_hash(receipt["acceptanceTree"], 40),
            "The acceptance source identity is incomplete.")

    affected, candidate = receipt["affected"], receipt["candidate"]
    require_keys(affected, ["commit", "tree", "apphostSha256", "disposition"], "affected artifact")
    require_keys(candidate, ["commit", "tree", "apphostSha256", "bundleSha256", "installSha256", "disposition"], "candidate artifact")
    for artifact in (affected, candidate):
        require(is_hash(artifact["commit"], 40) and is_hash(artifact["tree"], 40) and is_hash(artifact["apphostSha256"]),
                "Missing artifact or source identity.")
    require(all(affected[field] != candidate[field] for field in ("commit", "tree", "apphostSha256"))
            and is_hash(candidate["bundleSha256"]) and is_hash(candidate["installSha256"]),
            "Historical, repaired, and installed artifact identities are incomplete.")

    checks = receipt["checks"]
    require_keys(checks, ["affectedControl", "affectedRegression", "candidateShipping", "candidateRegression", "liveRegression"],
                 "qualification checks")
    for name, outcome in checks.items():
        special = {"affectedRegression": "contractMismatch", "liveRegression": "notRequired"}.get(name)
        require(outcome in tuple(TERMINAL_OUTCOMES) or special is not None and outcome == special,
                "Unknown terminal check outcome.")
    require(affected["disposition"] == checks["affectedRegression"] and candidate["disposition"] == checks["candidateRegression"],
            "Inconsistent qualified artifact outcomes.")

    compatibility = receipt["stateCompatibility"]
    require_keys(compatibility, ["disposition", "affected", "candidate", "reversibleConditions"], "state compatibility")
    state = compatibility["disposition"]
    require(state in ("sameSchema", "migrationRequired", "unavailable"), "Unknown state compatibility.")
    for value in (compatibility["affected"], compatibility["candidate"]):
        if value is not None or state != "unavailable":
            validate_schema(value)
    if state == "sameSchema":
        require(compatibility["affected"] == compatibility["candidate"] and compatibility["reversibleConditions"] == CONDITIONS,
                "The asserted reversible state transition was not proved.")
    else:
        require(compatibility["reversibleConditions"] == [], "An unproved state transition cannot assert reversibility.")

    if receipt["disposition"] == "passed":
        require(checks["affectedControl"] == "passed" and checks["affectedRegression"] == "contractMismatch"
                and checks["candidateShipping"] == "passed" and checks["candidateRegression"] == "passed"
                and checks["liveRegression"] in ("passed", "notRequired") and state == "sameSchema",
                "A mandatory qualification check lacks a passing terminal result.")


def inspect_bundle(bundle: Path, public_key: Path, expected_rid: str) -> dict:
    require_directory(bundle)
    bundle = bundle.resolve(strict=True)
    public_key = public_key.absolute()
    require(not public_key.resolve(strict=True).is_relative_to(bundle), "Provision the trusted key independently of the bundle.")
    require({item.name for item in bundle.iterdir()} == BUNDLE_MEMBERS, "Unexpected or missing qualification bundle members.")
    receipt_bytes = read_small(bundle / "receipt.json", 32_768)
    signature = read_small(bundle / "receipt.sig", 128)
    verify_signature(receipt_bytes, signature, read_small(public_key, 4096))
    receipt = decode(receipt_bytes, "receipt")
    validate_receipt(receipt, expected_rid)

    closure = read_small(bundle / "closure.json", 1_048_576)
    require(digest(closure) == receipt["candidate"]["bundleSha256"], "The payload manifest does not match the signed receipt.")
    observed = payload_manifest(bundle / "payload")
    require(encode(observed) == closure, "The exact payload differs from its signed manifest.")
    installed = read_small(bundle / "install-closure.json", 1_048_576)
    _, reviewed = install_projection(observed, expected_rid)
    require(digest(installed) == receipt["candidate"]["installSha256"] and encode(reviewed) == installed
            and payload_manifest(bundle / "install") == reviewed, "The exact install projection differs from the signed payload.")
    apphost = next((entry for entry in observed["files"] if entry["path"] == apphost_name(expected_rid)), None)
    require(apphost is not None and apphost["executable"] and apphost["sha256"] == receipt["candidate"]["apphostSha256"],
            "The exact apphost differs from its signed identity.")
    return {"eligible": receipt["disposition"] == "passed", "receipt": receipt}


def verify_bundle(bundle: Path, public_key: Path, expected_rid: str) -> dict:
    result = inspect_bundle(bundle, public_key, expected_rid)
    require(result["eligible"], "Qualification does not permit activation.")
    return result["receipt"]


def outside_inputs(path, roots, label):
    physical = path.resolve(strict=True)
    require(not any(physical.is_relative_to(root) for root in roots), f"Provision {label} independently of source and payloads.")
    return path.absolute()


def operator_private_key(path):
    def read(stream, metadata):
        if hasattr(os, "getuid"):
            require(metadata.st_uid == os.getuid() and metadata.st_mode & 0o077 == 0,
                    "The operator signing key must be private to the current operator.")
        require(metadata.st_size <= 4096, "Invalid operator signing key size.")
        data = stream.read(4097)
        require(0 < len(data) <= 4096, "Invalid operator signing key size.")
        return data

    return read_regular(path, read)


def sign_receipt(receipt, private_key, public_key):
    with tempfile.TemporaryDirectory(prefix="arcanum-repair-private-") as directory:
        key = Path(directory) / "operator.pem"
        with os.fdopen(os.open(key, os.O_CREAT | os.O_WRONLY | os.O_EXCL, 0o600), "wb") as output:
            output.write(operator_private_key(private_key))
        signed = subprocess.run(["openssl", "dgst", "-sha256", "-sign", str(key)], input=receipt,
                                capture_output=True, check=False, timeout=15)
    require(signed.returncode == 0, "The operator could not sign qualification evidence.")
    verify_signature(receipt, signed.stdout, public_key)
    return signed.stdout


def git_run(repository, *args, **options):
    environment = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}
    completed = subprocess.run(["git", "--no-replace-objects", "-C", str(repository), *args],
                               env=environment, check=False, stderr=subprocess.PIPE, timeout=120, **options)
    require(completed.returncode == 0, "The repository cannot supply the independently pinned source identity.")
    return completed.stdout


def git_bytes(repository, *args):
    return git_run(repository, *args, stdout=subprocess.PIPE)


def check_source(repository, commit, tree):
    require(is_hash(commit, 40) and is_hash(tree, 40), "Source identities must be full Git commit and tree hashes.")
    require(git_bytes(repository, "rev-parse", commit + "^{commit}").decode().strip() == commit
            and git_bytes(repository, "rev-parse", commit + "^{tree}").decode().strip() == tree,
            "The signed accepted source does not match the selected Git objects.")


def export_acceptance(repository, commit, destination):
    destination.mkdir()
    with tempfile.TemporaryFile() as archive:
        git_run(repository, "archive", "--format=tar", commit, stdout=archive)
        archive.seek(0)
        with tarfile.open(fileobj=archive, mode="r:") as payload:
            payload.extractall(destination, filter="data")


def read_build_inputs(options, public_bytes):
    witness = read_small(options.inputs, 32_768)
    verify_signature(witness, read_small(options.inputs_signature, 128), public_bytes)
    inputs = decode(witness, "build inputs")
    require_keys(inputs, ["version", "contract", "rid", "acceptanceCommit", "acceptanceTree", "affected", "candidate",
                          "liveRegression"], "build inputs")
    require(type(inputs["version"]) is int and inputs["version"] == 1 and inputs["contract"] == CONTRACT
            and type(inputs["rid"]) is str and inputs["rid"] in RIDS, "The signed build witness has an unsupported contract or target.")
    require(inputs["liveRegression"] in (*TERMINAL_OUTCOMES, "notRequired"), "The signed live regression policy has no accepted evidence state.")
    check_source(options.repository, inputs["acceptanceCommit"], inputs["acceptanceTree"])
    for role in ("affected", "candidate"):
        entry = inputs[role]
        require_keys(entry, ["commit", "tree", "bundleSha256", "buildOutcome"], "build source")
        check_source(options.repository, entry["commit"], entry["tree"])
        require(is_hash(entry["bundleSha256"]) and entry["buildOutcome"] in tuple(TERMINAL_OUTCOMES),
                "The signed build witness lacks an artifact digest or terminal outcome.")
    require(inputs["affected"]["tree"] != inputs["candidate"]["tree"], "The affected and repaired source trees must differ.")
    return inputs


def source_schema(repository, commit, manifest, rid):
    versions_text = git_bytes(repository, "show", f"{commit}:{SCHEMA_ROOT}/GrimoireSchemaVersionChains.cs").decode("utf-8")
    schema_inventory = git_bytes(repository, "ls-tree", "-r", "-z", commit, "--", SCHEMA_ROOT)
    require(schema_inventory, "Missing authoritative schema source inventory.")
    families = {}
    for family in FAMILIES:
        identifier = family[0].upper() + family[1:] + "SchemaVersion"
        matches = re.findall(rf"\b{identifier}\s*=\s*([1-9][0-9]*)\s*;", versions_text)
        require(len(matches) == 1, "Missing authoritative schema family version.")
        version = int(matches[0])
        fingerprint = digest(f"{family}|{version}|".encode() + schema_inventory)
        families[family] = {"version": version, "fingerprint": fingerprint}
    native_name = "e_sqlcipher.dll" if rid.startswith("win-") else "libe_sqlcipher.dylib"
    native = next((entry for entry in manifest["files"] if entry["path"] == native_name), None)
    require(native is not None, "Missing native SQLite artifact identity.")
    return {"families": families, "nativeSqlCipherSha256": native["sha256"]}


def state_compatibility(repository, inputs, manifests):
    affected, candidate = (inputs[role]["commit"] for role in ("affected", "candidate"))
    paths = git_bytes(repository, "diff", "--no-renames", "--name-only", "-z", affected, candidate).decode().split("\0")[:-1]
    adapter_only = set(paths).issubset(ADAPTER_REPAIR_PATHS | {"scripts/verify-published-apphost.sh"}
                                             | {path for path in paths if path.startswith(("docs/", "tests/"))})
    result = {"affected": None, "candidate": None, "disposition": "unavailable", "reversibleConditions": []}
    try:
        for role in ("affected", "candidate"):
            result[role] = source_schema(repository, inputs[role]["commit"], manifests[role], inputs["rid"])
    except (QualificationError, UnicodeError):
        return result
    if result["affected"] != result["candidate"]:
        result["disposition"] = "migrationRequired"
    elif adapter_only:
        result["disposition"] = "sameSchema"
        result["reversibleConditions"] = CONDITIONS
    return result


def checkout_test_outcome(work, acceptance, closure, rid, logs, affected=False):
    if rid != "osx-arm64" or sys.platform != "darwin" or platform.machine() != "arm64" \
            or not Path("/usr/bin/sandbox-exec").is_file():
        return "unavailable"
    arguments = ["bash", str(acceptance / "scripts" / "verify-shipping-publish.sh"), "--rid", rid,
                 "--inspect-publish-dir", str(closure)]
    arguments.append("--expect-ollama-mismatch" if affected else "--install-layout")
    environment = {name: value for name, value in os.environ.items()
                   if not name.upper().startswith(("ARCANUM_", "GITHUB_", "GH_"))}
    environment["ARCANUM_PUBLISHED_REPAIR_CONFINEMENT"] = "1"
    environment["ARCANUM_PUBLISHED_ACCEPTANCE_ARTIFACTS"] = str(work / "test-artifacts")
    environment["TMPDIR"] = str(work / "scratch")
    environment["TMP"] = environment["TEMP"] = environment["TMPDIR"]
    process = None
    try:
        kind = "affected" if affected else "candidate"
        with (logs / f"{kind}.log").open("xb") as log:
            process = subprocess.Popen(arguments, cwd=acceptance, env=environment,
                                       stdout=log, stderr=subprocess.STDOUT, start_new_session=os.name != "nt")
            exit_code = process.wait(timeout=30 * 60)
    except (KeyboardInterrupt, subprocess.TimeoutExpired) as error:
        if process is not None and process.poll() is None:
            if os.name == "nt":
                process.kill()
            else:
                os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=15)
        return "cancelled" if isinstance(error, KeyboardInterrupt) else "incomplete"
    except OSError:
        return "unavailable"
    if exit_code == 0:
        return "passed"
    if exit_code == 3:
        return "incomplete"
    if exit_code in (2, 126, 127):
        return "unavailable"
    if exit_code in (130, 143, -signal.SIGINT, -signal.SIGTERM):
        return "cancelled"
    return "failed"


def stage_payload(source, destination, trusted_digest):
    observed = payload_manifest(source)
    require(digest(encode(observed)) == trusted_digest, "The supplied closure differs from the independently signed build witness.")
    shutil.copytree(source, destination, symlinks=True)
    require(payload_manifest(destination) == observed, "The exact closure changed while staging.")
    return observed


def save_receipt(output, receipt, private_key, public_bytes):
    encoded = encode(receipt)
    signature = sign_receipt(encoded, private_key, public_bytes)
    for name, value in (("receipt.json", encoded), ("receipt.sig", signature)):
        with (output / name).open("xb") as file:
            file.write(value)
            file.flush()
            os.fsync(file.fileno())


def qualify(options):
    sources = tuple(path.resolve(strict=True) for path in (options.repository, options.affected_publish, options.candidate_publish))
    for path in (options.repository, options.affected_publish, options.candidate_publish):
        require_directory(path)
    public_key = outside_inputs(options.public_key, sources, "the trusted public key")
    private_key = outside_inputs(options.sign_key, sources, "the operator signing key")
    public_bytes = read_small(public_key, 4096)
    inputs = read_build_inputs(options, public_bytes)
    rid = inputs["rid"]
    output = options.output.absolute()
    require_directory(output.parent)
    require(not any(output.parent.resolve().is_relative_to(root) for root in sources),
            "Stage the qualification outside both input artifacts and the repository.")
    logs = options.logs.absolute() if options.logs else None
    if logs is not None:
        require_directory(logs.parent)
        require(not any(logs.parent.resolve().is_relative_to(root) for root in sources)
                and not logs.is_relative_to(output) and not output.is_relative_to(logs),
                "Retain gate logs separately outside the source, inputs, and signed bundle.")
        logs.mkdir(mode=0o700)
    output.mkdir(mode=0o700)

    with tempfile.TemporaryDirectory(prefix="arcanum-repair-verifier-") as directory:
        work = Path(directory).resolve(strict=True)
        (work / "scratch").mkdir()
        logs = logs or work
        closure = {"affected": work / "affected", "candidate": output / "payload"}
        manifests = {role: stage_payload(source, closure[role], inputs[role]["bundleSha256"]) for role, source in
                     zip(("affected", "candidate"), sources[1:])}
        schema = state_compatibility(options.repository, inputs, manifests)
        artifacts = {}
        for role, manifest in manifests.items():
            apphost = next((part for part in manifest["files"] if part["path"] == apphost_name(rid) and part["executable"]), None)
            require(apphost is not None, "The exact published apphost is missing.")
            artifacts[role] = {name: inputs[role][name] for name in ("commit", "tree")}
            artifacts[role]["apphostSha256"] = apphost["sha256"]
        candidate_closure = encode(manifests["candidate"])
        (output / "closure.json").write_bytes(candidate_closure)
        artifacts["candidate"]["bundleSha256"] = digest(candidate_closure)
        install = output / "install"
        installed = stage_install(closure["candidate"], install, manifests["candidate"], rid)
        installed_encoding = encode(installed)
        (output / "install-closure.json").write_bytes(installed_encoding)
        artifacts["candidate"]["installSha256"] = digest(installed_encoding)
        require(artifacts["affected"]["apphostSha256"] != artifacts["candidate"]["apphostSha256"],
                "The historical and repaired apphosts must differ.")

        checks = {"affectedControl": "incomplete", "affectedRegression": "incomplete", "candidateShipping": "incomplete",
                  "candidateRegression": "incomplete", "liveRegression": inputs["liveRegression"]}
        receipt = {"version": 1, "contract": CONTRACT, "rid": rid, "acceptanceCommit": inputs["acceptanceCommit"],
                   "acceptanceTree": inputs["acceptanceTree"], "affected": artifacts["affected"],
                   "candidate": artifacts["candidate"], "checks": checks, "stateCompatibility": schema,
                   "disposition": "incomplete"}
        acceptance = work / "acceptance"
        export_acceptance(options.repository, inputs["acceptanceCommit"], acceptance)
        affected = inputs["affected"]["buildOutcome"]
        if affected == "passed":
            affected = checkout_test_outcome(work, acceptance, closure["affected"], rid, logs, affected=True)
        checks["affectedControl"] = affected
        checks["affectedRegression"] = "contractMismatch" if affected == "passed" else affected

        candidate = inputs["candidate"]["buildOutcome"]
        if affected == "cancelled" or candidate != "passed":
            checks["candidateShipping"] = checks["candidateRegression"] = candidate if affected != "cancelled" else "cancelled"
        else:
            candidate = checkout_test_outcome(work, acceptance, install, rid, logs)
            checks["candidateShipping"] = checks["candidateRegression"] = candidate
        artifacts["affected"]["disposition"] = checks["affectedRegression"]
        artifacts["candidate"]["disposition"] = checks["candidateRegression"]
        if payload_manifest(install) != installed or any(payload_manifest(closure[role]) != original
                                                        for role, original in manifests.items()):
            receipt["disposition"] = "incomplete"
        else:
            for status in ("cancelled", "failed", "unavailable", "incomplete"):
                if status in checks.values():
                    receipt["disposition"] = status
                    break
            else:
                receipt["disposition"] = "passed" if schema["disposition"] == "sameSchema" else "unavailable"
        validate_receipt(receipt, rid)
        save_receipt(output, receipt, private_key, public_bytes)
        return receipt


def cancel_qualification(_signal, _frame):
    raise KeyboardInterrupt

def main():
    if sys.version_info < (3, 11):
        print("Repair qualification requires Python 3.11 or later.", file=sys.stderr)
        return 2

    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    verify = commands.add_parser("verify", help="Validate an independently signed exact repair bundle for activation.")
    inspect = commands.add_parser("inspect", help="Authenticate a staged repair bundle without granting activation.")
    for check in (verify, inspect):
        check.add_argument("--bundle", type=Path, required=True)
        check.add_argument("--public-key", type=Path, required=True)
        check.add_argument("--rid", required=True)
    manifest_command = commands.add_parser("manifest", help="Measure the closed set of exact shipping files.")
    manifest_command.add_argument("--publish", type=Path, required=True)
    qualify_command = commands.add_parser("qualify", help="Run pinned independent gates on signed prebuilt Arcanum artifacts.")
    for name in ("repository", "inputs", "inputs-signature", "public-key", "sign-key", "affected-publish", "candidate-publish", "output"):
        qualify_command.add_argument("--" + name, type=Path, required=True)
    qualify_command.add_argument("--logs", type=Path, help="Retain local gate logs in a fresh separate directory.")
    options = parser.parse_args()
    try:
        if options.command == "manifest":
            print(encode(payload_manifest(options.publish)).decode(), end="")
            return 0
        if options.command == "inspect":
            print(encode(inspect_bundle(options.bundle, options.public_key, options.rid)).decode(), end="")
            return 0
        if options.command == "qualify":
            signal.signal(signal.SIGTERM, cancel_qualification)
            receipt = qualify(options)
        else:
            receipt = verify_bundle(options.bundle, options.public_key, options.rid)
        print(encode(receipt).decode(), end="")
        return 0 if receipt["disposition"] == "passed" else 1
    except KeyboardInterrupt:
        print("Qualification was cancelled before a complete staged record could be committed.", file=sys.stderr)
        return 130
    except (ValueError, OSError, subprocess.SubprocessError, tarfile.TarError) as error:
        print(f"Qualification refused. {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
