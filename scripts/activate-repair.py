#!/usr/bin/env python3
"""Operator-controlled activation and recovery for a qualified macOS Arcanum repair."""

import argparse
from contextlib import contextmanager
import ctypes
import fcntl
import hashlib
import hmac
import http.client
import importlib.util
import json
import os
from pathlib import Path
import platform
import plistlib
import re
import stat
import subprocess
import sys
import tempfile
import time
import uuid
from urllib.parse import urlsplit


SCRIPT_ROOT = Path(__file__).resolve().parent
VERIFIER_SPEC = importlib.util.spec_from_file_location("repair_qualification", SCRIPT_ROOT / "qualify-repair.py")
qualification = importlib.util.module_from_spec(VERIFIER_SPEC)
VERIFIER_SPEC.loader.exec_module(qualification)

RID = "osx-arm64"
BEHAVIOR_CONTRACT = "ollama-reasoning-continuation-v1"
APPHOST = "arcanum"
NATIVE_LIBRARY = "libe_sqlcipher.dylib"
OPERATION_DIRECTORY = ".arcanum-repair"
OPERATION_FILE = "activation.json"
PHASES = {
    "staged", "draining", "swapping", "starting", "checking",
    "rollbackPending", "rollbackStarting", "applied", "rolledBack",
}
SAFE_PATH = "/usr/bin:/bin:/usr/sbin:/sbin"
EXCHANGE = 0x00000002
LAUNCHD_LABEL = "com.retrodownfall.arcanum"


class ActivationError(ValueError):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def require(condition, code, message):
    if not condition:
        raise ActivationError(code, message)


def operator_key():
    key = os.environ.get("ARCANUM_API_KEY")
    require(key and "\r" not in key and "\n" not in key, "operatorCredentialRequired",
            "Provide the existing installation API key through ARCANUM_API_KEY.")
    return key


def optional_environment(profile):
    home = profile.parent.parent
    test_home = os.environ.get("ARCANUM_TEST_HOME")
    if test_home is None:
        require(Path.home().resolve() == home, "profileMismatch", "The given profile belongs to a different home.")
        return {"HOME": str(home), "USERPROFILE": str(home), "PATH": SAFE_PATH}

    temporary = Path(os.environ.get("TMPDIR") or tempfile.gettempdir()).resolve(strict=True)
    test_root = temporary / "arcanum-tests"
    require(Path(test_home).resolve() == home and home != test_root and home.is_relative_to(test_root)
            and os.environ.get("DOTNET_ENVIRONMENT") == "Testing"
            and os.environ.get("ASPNETCORE_ENVIRONMENT") == "Testing"
            and os.environ.get("ARCANUM_TEST_IN_MEMORY_CREDENTIALS") == "1", "profileMismatch",
            "The disposable profile must use the apphost's isolated in-memory credential scope.")
    environment = {"HOME": str(home), "USERPROFILE": str(home), "PATH": SAFE_PATH,
                   "ARCANUM_TEST_HOME": str(home), "DOTNET_ENVIRONMENT": "Testing",
                   "ASPNETCORE_ENVIRONMENT": "Testing", "TMPDIR": str(temporary),
                   "ARCANUM_TEST_IN_MEMORY_CREDENTIALS": "1"}
    for name, value in os.environ.items():
        if re.fullmatch(r"ARCANUM_PROVIDER_[A-Z0-9_]+_API_KEY", name):
            environment[name] = value
    return environment


def environment_binding(environment, key):
    message = b"arcanum-repair-launch-v1\0" + qualification.encode(environment)
    return hmac.digest(key.encode(), message, "sha256").hex()


def identity(metadata):
    return [metadata.st_dev, metadata.st_ino]


def plain_directory(path, private=False):
    metadata = path.lstat()
    require(stat.S_ISDIR(metadata.st_mode) and metadata.st_uid == os.getuid()
            and not metadata.st_mode & (0o077 if private else 0o022), "unsafeDirectory",
            "The installation control directories must be owned by the current operator.")
    return metadata


def directory(path, private=False):
    plain_directory(path, private)
    canonical = path.resolve(strict=True)
    plain_directory(canonical, private)
    return canonical


def profile_identity(profile):
    metadata = plain_directory(profile)
    database = profile / "arcanum.db"
    db = database.lstat()
    config = profile / "arcanum.json"
    configuration = config.lstat()
    require(stat.S_ISREG(db.st_mode) and db.st_nlink == 1 and db.st_uid == os.getuid()
            and stat.S_ISREG(configuration.st_mode) and configuration.st_nlink == 1
            and configuration.st_uid == os.getuid(), "profileMismatch",
            "The existing profile database and configuration must be the operator's plain files.")
    return {"directory": identity(metadata), "database": identity(db)}


def normalized_origin(value):
    try:
        origin = urlsplit(value)
        require(origin.scheme == "http" and origin.hostname in ("127.0.0.1", "::1")
                and origin.port and origin.port > 0 and not origin.username and not origin.password
                and origin.path in ("", "/") and not origin.query and not origin.fragment,
                "unsafeHost", "Supply the exact loopback HTTP listener for this installation.")
        return f"http://{'[::1]' if origin.hostname == '::1' else '127.0.0.1'}:{origin.port}"
    except ValueError as error:
        if isinstance(error, ActivationError):
            raise
        raise ActivationError("unsafeHost", "Supply the exact loopback HTTP listener for this installation.") from error


def file_hash(path):
    return qualification.read_regular(path, lambda stream, _: hashlib.file_digest(stream, "sha256").hexdigest())


def write_private(path, bytes_, executable=False):
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o700 if executable else 0o600), "wb") as stream:
        stream.write(bytes_)
        stream.flush()
        os.fsync(stream.fileno())


def copy_private(source, target, executable=False):
    def copy(stream, _):
        with os.fdopen(os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o700 if executable else 0o600), "wb") as output:
            while block := stream.read(1024 * 1024):
                output.write(block)
            output.flush()
            os.fsync(output.fileno())
    qualification.read_regular(source, copy)


def copy_bundle(source, target):
    require({entry.name for entry in source.iterdir()} == qualification.BUNDLE_MEMBERS, "unexpectedBundleMember",
            "The qualification has unexpected or missing members.")
    target.mkdir(mode=0o700)
    for member in ("receipt.json", "receipt.sig", "closure.json", "install-closure.json"):
        copy_private(source / member, target / member)
    for tree in ("payload", "install"):
        payload = target / tree
        payload.mkdir(mode=0o700)
        for entry in qualification.payload_manifest(source / tree)["files"]:
            destination = payload / entry["path"]
            destination.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
            copy_private(source / tree / entry["path"], destination, executable=entry["executable"])
        sync_directory(payload)


def sync_directory(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def atomic_json(path, document):
    temp = path.parent / (".activation-" + uuid.uuid4().hex)
    try:
        write_private(temp, qualification.encode(document))
        os.replace(temp, path)
        sync_directory(path.parent)
    finally:
        if temp.exists():
            temp.unlink()


def journal_mac(document, key):
    message = b"arcanum-repair-journal-v1\0" + qualification.encode(document)
    return hmac.digest(key.encode(), message, "sha256").hex()


def save(operation, record, key, **updates):
    previous = record["phase"]
    record.update(updates)
    atomic_json(operation, {"record": record, "mac": journal_mac(record, key)})
    if record["phase"] != previous:
        print(f"Activation {record['activationId']} entered {record['phase']}.", file=sys.stderr, flush=True)


def load(operation, key):
    raw = qualification.read_small(operation, 1_048_576)
    envelope = qualification.decode(raw, "repair handoff")
    require(type(envelope) is dict and envelope.keys() == {"record", "mac"} and type(envelope["record"]) is dict
            and type(envelope["mac"]) is str and hmac.compare_digest(journal_mac(envelope["record"], key), envelope["mac"]),
            "handoffUntrusted", "The staged handoff could not be authenticated for this installation.")
    record = envelope["record"]
    require(record["version"] == 1 and record["phase"] in PHASES
            and record["activationId"] == operation.parent.name and operation == Path(record["operation"]),
            "handoffUntrusted", "The staged handoff identity could not be verified.")
    return record


def operation_path(value):
    path = value.absolute()
    require(path.name == OPERATION_FILE and path.parent.parent.name == OPERATION_DIRECTORY, "invalidOperation",
            "Supply the activation journal returned by stage.")
    directory(path.parent, private=True)
    directory(path.parent.parent, private=True)
    return path.parent.resolve(strict=True) / OPERATION_FILE


def manifest_entry(manifest, path):
    return next((entry for entry in manifest["files"] if entry["path"] == path), None)


def manifest_hash(manifest):
    return qualification.digest(qualification.encode(manifest))


def verify_native_identity(manifest, compatibility):
    library = manifest_entry(manifest, NATIVE_LIBRARY)
    require(library is not None and library["sha256"] == compatibility["nativeSqlCipherSha256"],
            "nativeLibraryMismatch", "The native SQLCipher sidecar disagrees with the signed state proof.")


def inspect_staged(bundle, trusted):
    return qualification.inspect_bundle(bundle, trusted, RID)


def check_profile_binding(record):
    profile = Path(record["profile"])
    require(profile_identity(profile) == record["profileIdentity"], "profileMismatch",
            "The physical profile or its database has changed since staging.")
    return profile


def public_status(operation, record, code=None):
    result = {"operation": str(operation), "activationId": record["activationId"], "phase": record["phase"],
              "sessionId": record["sessionId"], "eligible": record["eligible"],
              "continuationReady": record["phase"] == "applied" and record.get("restoredEffectState") != "dispatched"}
    if record.get("restoredEffectState"):
        result["effectState"] = record["restoredEffectState"]
    outcome = code or record.get("code")
    if outcome:
        result["code"] = outcome
    return result


def stage(arguments, key):
    install = directory(arguments.install_dir)
    parent = directory(install.parent)
    require(plain_directory(install).st_dev == plain_directory(parent).st_dev, "crossVolume",
            "The staged and installed application closures require the same installation volume.")
    profile = directory(arguments.profile)
    require(profile.parent.name == ".config" and profile.name == "arcanum"
            and not profile.is_relative_to(install) and not install.is_relative_to(profile),
            "profileMismatch", "Keep the profile and launcher state outside the packaged application directory.")
    bound_profile = profile_identity(profile)
    launch_environment = optional_environment(profile)
    origin = normalized_origin(arguments.api_url)
    session = str(uuid.UUID(arguments.session))
    bundle = directory(arguments.bundle)
    public_key = arguments.public_key.absolute()
    observer = arguments.behavior_check.absolute()
    require(not any(path.resolve(strict=True).is_relative_to(root) for path in (public_key, observer)
                    for root in (bundle, install, profile)), "untrustedControl",
            "Provision the signing key and behavior verifier independently of the release and profile contents.")
    observer_metadata = observer.lstat()
    require(stat.S_ISREG(observer_metadata.st_mode) and observer_metadata.st_nlink == 1
            and observer_metadata.st_uid == os.getuid() and observer_metadata.st_mode & 0o100
            and not observer_metadata.st_mode & 0o022,
            "observerUntrusted", "The independent behavior verifier must be an operator-owned executable.")
    inspected_source = inspect_staged(bundle, public_key)
    maintenance = parent / OPERATION_DIRECTORY
    maintenance.mkdir(mode=0o700, exist_ok=True)
    directory(maintenance, private=True)
    activation_id = str(uuid.uuid4())
    work = maintenance / activation_id
    work.mkdir(mode=0o700)
    operation = work / OPERATION_FILE
    staged = work / "qualification"
    trusted = work / "operator.pem"
    witness = work / "behavior-check"
    copy_private(public_key, trusted)
    copy_private(observer, witness, executable=True)
    copy_bundle(bundle, staged)

    inspected = inspect_staged(staged, trusted)
    require(inspected == inspected_source, "bundleChanged",
            "The qualification changed while it was being staged.")
    receipt = inspected["receipt"]
    eligible = inspected["eligible"]
    prior = qualification.payload_manifest(install)
    managed = {APPHOST, NATIVE_LIBRARY, "libonigwrap.dylib"} | qualification.INSTALL_LICENSES
    require({entry["path"] for entry in prior["files"]} <= managed, "installationOwnershipUnproved",
            "The installed directory contains files outside the reviewed application closure.")
    app = manifest_entry(prior, APPHOST)
    require(app and app["executable"] and app["sha256"] == receipt["affected"]["apphostSha256"],
            "priorBuildMismatch", "The installed apphost is not the affected build bound by this qualification.")
    compatibility = receipt["stateCompatibility"]
    if compatibility["affected"] is not None:
        verify_native_identity(prior, compatibility["affected"])
    if compatibility["candidate"] is not None:
        verify_native_identity(qualification.payload_manifest(staged / "install"), compatibility["candidate"])
    write_private(work / "prior-closure.json", qualification.encode(prior))
    sync_directory(staged)
    sync_directory(maintenance)

    record = {
        "version": 1, "operation": str(operation), "activationId": activation_id, "phase": "staged",
        "installDirectory": str(install), "installParentIdentity": identity(plain_directory(parent)),
        "priorDirectoryIdentity": identity(plain_directory(install)),
        "candidateDirectoryIdentity": identity(plain_directory(staged / "install", private=True)),
        "profile": str(profile), "profileIdentity": bound_profile, "apiOrigin": origin, "sessionId": session,
        "configurationSha256": file_hash(profile / "arcanum.json"),
        "operatorKeySha256": file_hash(trusted), "observerSha256": file_hash(witness),
        "observerContract": arguments.behavior_check_id,
        "launchEnvironment": environment_binding(launch_environment, key),
        "launcher": launcher_plan(install, launch_environment),
        "receiptSha256": file_hash(staged / "receipt.json"),
        "signatureSha256": file_hash(staged / "receipt.sig"),
        "priorManifestSha256": manifest_hash(prior), "candidateManifestSha256": receipt["candidate"]["installSha256"],
        "qualificationManifestSha256": receipt["candidate"]["bundleSha256"],
        "priorApphostSha256": receipt["affected"]["apphostSha256"], "candidateApphostSha256": receipt["candidate"]["apphostSha256"],
        "eligible": eligible, "code": None if eligible else (
            "stateRecoveryUnproved" if compatibility["disposition"] != "sameSchema" else "qualificationNotPassed"),
    }
    if eligible:
        metadata = api(record, key, "/api/meta", missing=True)
        if metadata is not None:
            process_id = require_expected_host(record, metadata, record["priorApphostSha256"])
            require_launcher_owner(record, launch_environment, process_id)
            prior = ready_session(record, key)
            record.update(priorProcessId=process_id, priorStartTime=metadata["startTime"], priorSession=prior,
                          priorEntryCount=prior["entryCount"])
    save(operation, record, key)
    return public_status(operation, record), 0 if eligible else 2


def revalidate_observer(operation, record):
    witness = operation.parent / "behavior-check"
    require(file_hash(witness) == record["observerSha256"] and record["observerContract"] == BEHAVIOR_CONTRACT,
            "observerChanged", "The previously staged behavior verifier changed.")
    return witness


def verify_handoff(operation, record):
    check_profile_binding(record)
    revalidate_observer(operation, record)
    require(record["eligible"], record.get("code") or "qualificationNotPassed",
            "The package remains staged until independent qualification and recovery compatibility are proved.")
    staged = operation.parent / "qualification"
    trusted = operation.parent / "operator.pem"
    require(file_hash(trusted) == record["operatorKeySha256"] and file_hash(staged / "receipt.json") == record["receiptSha256"]
            and file_hash(staged / "receipt.sig") == record["signatureSha256"]
            and file_hash(staged / "closure.json") == record["qualificationManifestSha256"]
            and file_hash(staged / "install-closure.json") == record["candidateManifestSha256"]
            and file_hash(operation.parent / "prior-closure.json") == record["priorManifestSha256"],
            "handoffChanged", "The qualified artifact or independent signing authority changed after staging.")


@contextmanager
def lock(path, operation=False):
    plain_directory(path.parent)
    descriptor = os.open(path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        observed = os.fstat(descriptor)
        require(stat.S_ISREG(observed.st_mode) and observed.st_uid == os.getuid()
                and observed.st_nlink == 1 and not observed.st_mode & 0o077
                and identity(observed) == identity(path.lstat()), "unsafeLock", "An installation lock changed identity.")
        try:
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            code = "activationBusy" if operation else "installationBusy"
            raise ActivationError(code, "The same installation or activation is currently owned.") from error
        require(identity(os.fstat(descriptor)) == identity(path.lstat()), "unsafeLock", "An installation lock changed after admission.")
        yield
    finally:
        os.close(descriptor)


def api(record, key, route, method="GET", missing=False, unsupported=False, payload=None, timeout=3):
    origin = urlsplit(record["apiOrigin"])
    connection = http.client.HTTPConnection(origin.hostname, origin.port, timeout=timeout)
    try:
        headers = {"Authorization": "Bearer " + key, "Accept": "application/json"}
        body = None
        if payload is not None:
            headers["Content-Type"] = "application/json"
            body = qualification.encode(payload)
        connection.request(method, route, body=body, headers=headers)
        response = connection.getresponse()
        if unsupported and response.status in (404, 405):
            return None
        body = response.read(2_097_153)
        require(len(body) <= 2_097_152 and response.status in (200, 202) and response.headers.get("Content-Type", "").startswith("application/json"),
                "hostRefused", "The exact authenticated local host did not accept the operation.")
        envelope = qualification.decode(body, "host response")
        require(type(envelope) is dict and envelope.get("isSuccess") is True and "data" in envelope,
                "hostRefused", "The exact authenticated local host did not accept the operation.")
        return envelope["data"]
    except ConnectionRefusedError:
        if missing:
            return None
        raise ActivationError("hostUnavailable", "The expected local host is not available.")
    except (http.client.HTTPException, TimeoutError, OSError) as error:
        raise ActivationError("hostUnavailable", "The expected local host is not available.") from error
    finally:
        connection.close()


def process_path(process_id):
    if type(process_id) is not int or process_id <= 0:
        return None
    finder = ctypes.CDLL("/usr/lib/libproc.dylib", use_errno=True).proc_pidpath
    finder.argtypes = (ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32)
    finder.restype = ctypes.c_int
    path = ctypes.create_string_buffer(4096)
    return Path(os.fsdecode(path.value)) if finder(process_id, path, len(path)) > 0 else None


def require_expected_host(record, metadata, expected_sha, expected_pid=None):
    executable = Path(record["installDirectory"]) / APPHOST
    process_id = metadata.get("processId")
    require(type(process_id) is int and process_id > 0
            and (expected_pid is None or process_id == expected_pid)
            and process_path(process_id) == executable
            and metadata.get("nativeAot") is True
            and metadata.get("runtimeIdentifier") == RID
            and isinstance(metadata.get("startTime"), str)
            and Path(metadata.get("grimoireDirectory", "")).resolve() == Path(record["profile"])
            and Path(metadata.get("configPath", "")).resolve() == Path(record["profile"]) / "arcanum.json"
            and file_hash(executable) == expected_sha,
            "hostIdentityMismatch", "The responding process is not the exact apphost and profile bound by the operation.")
    check_profile_binding(record)
    return process_id


def ready_session(record, key):
    health = api(record, key, "/api/health")
    checks = health.get("components")
    require(type(checks) is list and health.get("status") not in (2, "unhealthy", "Unhealthy")
            and any(item.get("name") == "Grimoire" and item.get("status") in (0, "healthy", "Healthy") for item in checks),
            "grimoireNotReady", "The hosted Grimoire did not verify its existing installation state.")
    session = api(record, key, f"/api/sessions/{record['sessionId']}")
    require(type(session) is dict and session.get("id", "").lower() == record["sessionId"]
            and type(session.get("entryCount")) is int and session["entryCount"] >= record.get("priorEntryCount", 0),
            "sessionNotRetained", "The expected Session did not reopen with its retained history.")
    evidence = {"entryCount": session["entryCount"]}
    checkpoint = session.get("recoveryCheckpoint")
    if checkpoint is not None:
        require(type(checkpoint) is dict and str(uuid.UUID(checkpoint["sessionId"])) == record["sessionId"],
                "sessionNotRetained", "The recovered checkpoint belongs to a different Session.")
        evidence["checkpointId"] = str(uuid.UUID(checkpoint["id"]))
        if checkpoint.get("invocationId") is not None:
            invocation = str(uuid.UUID(checkpoint["invocationId"]))
            detail = api(record, key, f"/api/sessions/{record['sessionId']}/tool-invocations/{invocation}")
            require(type(detail) is dict and str(uuid.UUID(detail["receiptId"])) == invocation
                    and detail["state"] in ("prepared", "dispatched", "completed"),
                    "effectEvidenceMissing", "The retained Session does not have its exact invocation disposition.")
            evidence["effect"] = {"id": invocation, "assistantEntryId": str(uuid.UUID(detail["assistantEntryId"])),
                                  "state": detail["state"], "resultEntryId": detail.get("resultEntryId")}
    prior = record.get("priorSession")
    if prior and prior.get("checkpointId") is not None:
        require(evidence.get("checkpointId") == prior["checkpointId"], "sessionNotRetained",
                "The original pending checkpoint did not reopen.")
    if prior and prior.get("effect") is not None:
        previous = prior["effect"]
        restored = evidence.get("effect")
        require(restored is not None and restored["id"] == previous["id"]
                and restored["assistantEntryId"] == previous["assistantEntryId"]
                and (previous["state"] != "completed" or (restored["state"] == "completed"
                     and restored["resultEntryId"] == previous["resultEntryId"]))
                and (previous["state"] != "dispatched" or restored["state"] in ("dispatched", "completed")),
                "effectEvidenceMissing", "The prior tool invocation disposition did not survive the handoff.")
    return evidence


def layout(operation, record):
    installed = Path(record["installDirectory"])
    staged = operation.parent / "qualification" / "install"
    require(identity(plain_directory(installed.parent)) == record["installParentIdentity"],
            "installationChanged", "The installation parent changed since staging.")
    live = (identity(plain_directory(installed)), manifest_hash(qualification.payload_manifest(installed)))
    retained = (identity(plain_directory(staged)), manifest_hash(qualification.payload_manifest(staged)))
    previous = (record["priorDirectoryIdentity"], record["priorManifestSha256"])
    candidate = (record["candidateDirectoryIdentity"], record["candidateManifestSha256"])
    if (live, retained) == (previous, candidate):
        return "prior"
    if (live, retained) == (candidate, previous):
        return "candidate"
    raise ActivationError("installationChanged", "The two closures no longer match the staged installation identities.")


def verify_unchanged_config(profile, record):
    require(file_hash(profile / "arcanum.json") == record["configurationSha256"],
            "profileChanged", "The profile configuration changed since the handoff was staged.")


def assert_no_non_activation_control(profile):
    blocker = profile.parent / f".arcanum-client-mutation-{profile.name}.blocked.json"
    require(not blocker.exists() and not blocker.is_symlink(), "otherMaintenanceActive",
            "An existing reset or restore owns this installation.")


def launchctl(environment, *arguments):
    return subprocess.run(["/bin/launchctl", *arguments], cwd=environment["HOME"], env=environment,
                          capture_output=True, timeout=8, check=False)


def launchd_job(environment):
    observed = launchctl(environment, "list", LAUNCHD_LABEL)
    if observed.returncode != 0:
        return False, None
    output = observed.stdout.decode("utf-8", errors="replace")
    dictionary = re.search(r'(?m)^\s*"?PID"?\s*=\s*(\d+)\s*;?\s*$', output)
    row = re.search(rf'(?m)^\s*(\d+)\s+[-\d]+\s+{re.escape(LAUNCHD_LABEL)}\s*$', output)
    match = dictionary or row
    return True, int(match.group(1)) if match else None


def require_launcher_owner(record, environment, process_id):
    if record["launcher"]["kind"] == "launchd":
        loaded, observed = launchd_job(environment)
        require(loaded and observed == process_id, "launcherOwnerChanged",
                "The existing launchd job does not own the authenticated process.")


def launcher_plan(install, environment):
    if "ARCANUM_TEST_HOME" in environment:
        return {"kind": "foreground"}
    plist = Path(environment["HOME"]) / "Library" / "LaunchAgents" / (LAUNCHD_LABEL + ".plist")
    loaded, _ = launchd_job(environment)
    if not loaded and not plist.exists():
        return {"kind": "foreground"}
    require(plist.exists(), "launcherControlUnavailable", "The loaded launchd job has no matching owner plist.")
    raw = qualification.read_small(plist, 65_536)
    metadata = plist.lstat()
    decoded = plistlib.loads(raw)
    arguments = decoded.get("ProgramArguments", [])
    require(metadata.st_uid == os.getuid() and not metadata.st_mode & 0o022
            and decoded.get("Label") == LAUNCHD_LABEL and type(arguments) is list and len(arguments) == 2
            and arguments[1] == "serve" and Path(arguments[0]).is_absolute()
            and Path(arguments[0]).resolve() == install / APPHOST,
            "launcherControlUnavailable", "The existing launchd owner does not point to this exact application closure.")
    return {"kind": "launchd", "plist": str(plist), "plistSha256": qualification.digest(raw)}


def validate_launcher(record, environment):
    expected = record["launcher"]
    if expected["kind"] == "launchd":
        require(file_hash(Path(expected["plist"])) == expected["plistSha256"], "launcherChanged",
                "The existing launchd plist changed after staging.")
    elif "ARCANUM_TEST_HOME" not in environment:
        require(not launchd_job(environment)[0], "launcherChanged", "A different launchd owner appeared after staging.")


def unload_launcher(record, environment):
    validate_launcher(record, environment)
    if record["launcher"]["kind"] == "launchd" and launchd_job(environment)[0]:
        domain = f"gui/{os.getuid()}"
        response = launchctl(environment, "bootout", domain, record["launcher"]["plist"])
        require(response.returncode == 0 and not launchd_job(environment)[0], "launcherStopFailed",
                "The existing launchd job did not unload after its verified drain.")


def start_host(record, environment):
    verify_unchanged_config(Path(record["profile"]), record)
    validate_launcher(record, environment)
    if record["launcher"]["kind"] == "launchd":
        if not launchd_job(environment)[0]:
            response = launchctl(environment, "bootstrap", f"gui/{os.getuid()}", record["launcher"]["plist"])
            require(response.returncode == 0, "restartFailed", "The existing launchd job could not reload its unchanged plist.")
        return None

    return subprocess.Popen([str(Path(record["installDirectory"]) / APPHOST), "serve", "--plain"],
                            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                            cwd=environment["HOME"], env=environment, start_new_session=True)


def await_ready(record, key, expected_sha, child=None, expected_pid=None):
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        if child is not None and child.poll() is not None:
            raise ActivationError("restartFailed", "The exact staged apphost exited before it became ready.")
        try:
            metadata = api(record, key, "/api/meta", missing=True)
            if metadata is not None:
                process_id = require_expected_host(record, metadata, expected_sha, expected_pid)
                return metadata, process_id, ready_session(record, key)
        except ActivationError as error:
            if error.code not in ("hostUnavailable", "grimoireNotReady"):
                raise
        time.sleep(0.1)
    raise ActivationError("restartFailed", "The exact staged apphost did not become ready.")


def drain_host(record, key, observed):
    intent = {"activationId": record["activationId"], "processId": observed["processId"], "startTime": observed["startTime"],
              "qualificationSha256": record["receiptSha256"]}
    receipt = api(record, key, "/api/server/repair-drain", method="POST", unsupported=True, payload=intent, timeout=180)
    if receipt is None:
        return False
    require(receipt == {**intent, "drained": True}, "drainUnproved",
            "The expected boot did not attest a completed drain for this activation.")
    return True


def wait_process_exit(process_id):
    deadline = time.monotonic() + 45
    while process_path(process_id) is not None:
        try:
            os.waitpid(process_id, os.WNOHANG)
        except ChildProcessError:
            pass
        require(time.monotonic() < deadline, "drainPending",
                "The same activation is waiting for the acknowledged host to leave service.")
        time.sleep(0.1)


def stop_exact_host(record, key, observed, expected_sha, environment):
    process_id = observed["processId"]
    profile = check_profile_binding(record)
    expected_path = Path(record["installDirectory"]) / APPHOST
    running = process_path(process_id)
    if running is None:
        require(api(record, key, "/api/meta", missing=True) is None, "hostOwnerChanged",
                "Another process answered on the exact profile's listener.")
        if record["launcher"]["kind"] == "launchd":
            require(not launchd_job(environment)[0], "hostUnavailable",
                    "The same installation launchd job still owns a running or restarting host.")
        return True

    require(running == expected_path and file_hash(expected_path) == expected_sha, "hostOwnerChanged",
            "The physical process no longer matches the planned host.")
    if not drain_host(record, key, observed):
        return False

    if record["launcher"]["kind"] == "launchd":
        require_launcher_owner(record, environment, process_id)
        unload_launcher(record, environment)
    else:
        require(api(record, key, "/api/server/quit", method="POST") is True, "drainRefused",
                "The expected host did not acknowledge graceful shutdown.")
    wait_process_exit(process_id)
    verify_unchanged_config(profile, record)
    check_profile_binding(record)
    return True


def swap(operation, record, key, expected_layout):
    profile = check_profile_binding(record)
    installed = Path(record["installDirectory"])
    staged = operation.parent / "qualification" / "install"
    client_lock = profile.parent / f".arcanum-client-mutation-{profile.name}.lock"
    maintenance_lock = profile.parent / f".arcanum-maintenance-{profile.name}.lock"
    with lock(client_lock), lock(maintenance_lock):
        assert_no_non_activation_control(profile)
        verify_unchanged_config(profile, record)
        require(layout(operation, record) == expected_layout, "installationChanged",
                "The installation no longer matches the expected stopped state.")
        if expected_layout == "prior":
            qualification.verify_bundle(staged.parent, operation.parent / "operator.pem", RID)
        next_phase = "starting" if expected_layout == "prior" else "rollbackStarting"
        install_parent = os.open(installed.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            bundle_parent = os.open(staged.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                require(identity(os.fstat(install_parent)) == record["installParentIdentity"]
                        and identity(os.fstat(bundle_parent)) == identity(staged.parent.lstat()),
                        "installationChanged", "A closure parent changed before exchange.")
                exchange = ctypes.CDLL(None, use_errno=True).renameatx_np
                exchange.argtypes = (ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint)
                exchange.restype = ctypes.c_int
                require(exchange(install_parent, os.fsencode(installed.name), bundle_parent, os.fsencode(staged.name), EXCHANGE) == 0,
                        "swapFailed", "The system did not exchange the staged and installed closures.")
                os.fsync(install_parent)
                os.fsync(bundle_parent)
            finally:
                os.close(bundle_parent)
        finally:
            os.close(install_parent)
        require(layout(operation, record) == ("candidate" if expected_layout == "prior" else "prior"),
                "installationChanged", "The physical closure identities did not exchange as expected.")
        save(operation, record, key, phase=next_phase, childPid=None)


def observe_behavior(operation, record, key, process_id):
    observer = revalidate_observer(operation, record)
    expected = {"contract": BEHAVIOR_CONTRACT, "activationId": record["activationId"],
                "sessionId": record["sessionId"], "processId": process_id,
                "apphostSha256": record["candidateApphostSha256"], "disposition": "passed"}
    observer_environment = {"HOME": str(operation.parent), "PATH": SAFE_PATH, "ARCANUM_API_KEY": key}
    arguments = [str(observer), "--api-url", record["apiOrigin"], "--session", record["sessionId"],
                 "--process-id", str(process_id), "--apphost-sha256", record["candidateApphostSha256"],
                 "--activation-id", record["activationId"]]
    try:
        result = subprocess.run(arguments, cwd=operation.parent, env=observer_environment, stdin=subprocess.DEVNULL,
                                capture_output=True, timeout=120, check=False)
        output = qualification.decode(result.stdout, "behavior evidence") if len(result.stdout) <= 16384 else None
        require(result.returncode == 0 and output == expected, "behaviorNotVerified",
                "The pinned verifier did not prove that this running apphost passed the behavior check.")
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        if isinstance(error, ActivationError):
            raise
        raise ActivationError("behaviorNotVerified",
                              "The pinned verifier did not prove that this running apphost passed the behavior check.") from error


def start_or_adopt(operation, record, key, environment, expected_sha):
    metadata = api(record, key, "/api/meta", missing=True)
    if metadata is not None:
        require_expected_host(record, metadata, expected_sha, record.get("childPid"))
        require_launcher_owner(record, environment, metadata["processId"])
        return await_ready(record, key, expected_sha, expected_pid=metadata["processId"])
    owned = record.get("childPid")
    if owned is not None and process_path(owned) is not None:
        return await_ready(record, key, expected_sha, expected_pid=owned)
    try:
        child = start_host(record, environment)
    except OSError as error:
        raise ActivationError("restartFailed", "The exact staged apphost could not be launched.") from error
    save(operation, record, key, childPid=child.pid if child is not None else None)
    try:
        ready = await_ready(record, key, expected_sha, child, child.pid if child is not None else None)
        require_launcher_owner(record, environment, ready[1])
        return ready
    except ActivationError:
        if child is not None and child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=8)
            except subprocess.TimeoutExpired:
                pass
        raise


def rollback(operation, record, key, environment):
    installed = layout(operation, record)
    if installed == "candidate":
        if record.get("candidateStartTime") and record.get("childPid"):
            metadata = {"processId": record["childPid"], "startTime": record["candidateStartTime"]}
        else:
            metadata = api(record, key, "/api/meta", missing=True)
            if metadata is not None:
                require_expected_host(record, metadata, record["candidateApphostSha256"], record.get("childPid"))
                save(operation, record, key, childPid=metadata["processId"], candidateStartTime=metadata["startTime"])
        if metadata is not None:
            require(stop_exact_host(record, key, metadata, record["candidateApphostSha256"], environment),
                    "rollbackDrainUnproved", "The active historical candidate cannot attest its safe point. Stop it before recovering.")
        else:
            require(process_path(record.get("childPid")) is None, "rollbackWaitsForHost",
                    "The issued candidate process still owns installation startup.")
            if record["launcher"]["kind"] == "launchd":
                unload_launcher(record, environment)
        swap(operation, record, key, "candidate")
    elif record["phase"] != "rollbackStarting":
        save(operation, record, key, phase="rollbackStarting", childPid=None)

    _, process_id, restored = start_or_adopt(operation, record, key, environment, record["priorApphostSha256"])
    save(operation, record, key, phase="rolledBack", childPid=process_id,
         restoredEffectState=restored.get("effect", {}).get("state"))
    return public_status(operation, record), 2


def advance(operation, record, key, recovery):
    if record["phase"] in ("applied", "rolledBack"):
        return public_status(operation, record), 0 if record["phase"] == "applied" else 2
    require(recovery or record["phase"] == "staged", "activationNeedsRecovery",
            "Resume the same interrupted activation with recover.")
    profile = check_profile_binding(record)
    verify_unchanged_config(profile, record)
    environment = optional_environment(profile)
    require(environment_binding(environment, key) == record["launchEnvironment"], "launchEnvironmentChanged",
            "The independent launch environment changed since staging.")
    launch_lock = profile.parent / f".arcanum-serve-{profile.name}.lock"
    installed = Path(record["installDirectory"])
    install_lock = installed.parent / (".arcanum-repair-" + qualification.digest(os.fsencode(installed.name))[:24] + ".lock")
    with lock(install_lock), lock(launch_lock):
        assert_no_non_activation_control(profile)
        validate_launcher(record, environment)
        if record["phase"] in ("rollbackPending", "rollbackStarting"):
            return rollback(operation, record, key, environment)

        current_layout = layout(operation, record)
        try:
            verify_handoff(operation, record)
        except ActivationError as error:
            if recovery and current_layout == "candidate" and record["phase"] in ("swapping", "starting", "checking"):
                save(operation, record, key, phase="rollbackPending", code=error.code)
                return rollback(operation, record, key, environment)
            raise
        if current_layout == "prior":
            if record["phase"] == "staged":
                qualification.verify_bundle(operation.parent / "qualification", operation.parent / "operator.pem", RID)
                current = api(record, key, "/api/meta", missing=True)
                if current is not None:
                    process_id = require_expected_host(record, current, record["priorApphostSha256"])
                    require_launcher_owner(record, environment, process_id)
                    prior = ready_session(record, key)
                    save(operation, record, key, phase="draining", priorProcessId=process_id,
                         priorStartTime=current["startTime"], priorEntryCount=prior["entryCount"], priorSession=prior,
                         childPid=None, code=None)
                else:
                    require(record.get("priorProcessId") is not None, "priorSessionProofRequired",
                            "Stage while the exact prior host can verify the retained Session, then stop it before applying.")
                    save(operation, record, key, phase="draining", code=None)
            require(record["phase"] in ("draining", "swapping"), "installationChanged",
                    "The prior bytes do not match this activation phase.")
            stopped = stop_exact_host(record, key,
                                      {"processId": record["priorProcessId"], "startTime": record["priorStartTime"]},
                                      record["priorApphostSha256"], environment)
            if not stopped:
                save(operation, record, key, phase="staged", code="historicalDrainUnproved")
                return public_status(operation, record), 2
            save(operation, record, key, phase="swapping")
            swap(operation, record, key, "prior")
        else:
            require(record["phase"] in ("swapping", "starting", "checking"), "installationChanged",
                    "The candidate bytes do not match this activation phase.")
            if record["phase"] == "swapping":
                save(operation, record, key, phase="starting", childPid=None)

        try:
            metadata, process_id, _ = start_or_adopt(operation, record, key, environment, record["candidateApphostSha256"])
            require(metadata["startTime"] != record["priorStartTime"], "hostOwnerChanged", "The verified host did not create a fresh boot.")
            save(operation, record, key, phase="checking", childPid=process_id, candidateStartTime=metadata["startTime"])
            observe_behavior(operation, record, key, process_id)
            verified = api(record, key, "/api/meta")
            require_expected_host(record, verified, record["candidateApphostSha256"], process_id)
            require(verified["startTime"] == metadata["startTime"], "hostOwnerChanged",
                    "The candidate boot changed while the independent behavior check was running.")
            restored = ready_session(record, key)
            verify_unchanged_config(profile, record)
            save(operation, record, key, phase="applied", code=None,
                 restoredEffectState=restored.get("effect", {}).get("state"))
            return public_status(operation, record), 0
        except ActivationError as error:
            save(operation, record, key, phase="rollbackPending", code=error.code)
            return rollback(operation, record, key, environment)


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    begin = commands.add_parser("stage", help="Copy and verify a repair before live work is affected.")
    for name in ("bundle", "public-key", "install-dir", "profile", "behavior-check"):
        begin.add_argument("--" + name, required=True, type=Path)
    for name in ("api-url", "session"):
        begin.add_argument("--" + name, required=True)
    begin.add_argument("--behavior-check-id", default=BEHAVIOR_CONTRACT, choices=[BEHAVIOR_CONTRACT])
    for name in ("apply", "status", "recover"):
        commands.add_parser(name).add_argument("--operation", required=True, type=Path)
    return parser.parse_args()


def main():
    arguments = parse_args()
    operation = None
    record = None
    try:
        require(sys.version_info >= (3, 11) and sys.platform == "darwin" and platform.machine() == "arm64",
                "unsupportedRuntime", "Use Python 3.11 or later on Apple silicon macOS for this package.")
        key = operator_key()
        os.environ["PATH"] = SAFE_PATH
        if arguments.command == "stage":
            result, status = stage(arguments, key)
        else:
            operation = operation_path(arguments.operation)
            if arguments.command == "status":
                record = load(operation, key)
                result, status = public_status(operation, record), 0
            else:
                with lock(operation.parent / "controller.lock", operation=True):
                    record = load(operation, key)
                    result, status = advance(operation, record, key, recovery=arguments.command == "recover")
        print(json.dumps(result, sort_keys=True))
        return status
    except ActivationError as error:
        body = public_status(operation, record, error.code) if record is not None else {"code": error.code}
        body["message"] = str(error)
        print(json.dumps(body, sort_keys=True))
        return 2
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
        body = public_status(operation, record, "unsafeHandoff") if record is not None else {"code": "unsafeHandoff"}
        body["message"] = "The installation or signed qualification could not be verified safely."
        print(json.dumps(body, sort_keys=True))
        return 2


if __name__ == "__main__":
    sys.exit(main())
