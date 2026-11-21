"""Opt-in activation journey using the signed historical macOS Native AOT fixture."""

import argparse
import hashlib
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import platform
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import uuid

from fixtures import observe_repair_activation as fixture


SCRIPTS = Path(__file__).resolve().parents[1]
CONTROLLER = SCRIPTS / "activate-repair.py"
OBSERVER = Path(fixture.__file__).resolve()
SYSTEM_PATH = "/usr/bin:/bin:/usr/sbin:/sbin"
ATTACHMENT = b"preserved attachment fixture\n"
WORKSPACE = b"// preserved source fixture\n"
LAUNCHER = b'#!/bin/sh\nexec "$HOME/.local/share/arcanum/arcanum" "$@"\n'


def file_sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def install_hashes(directory):
    hashes = {}
    for path in directory.rglob("*"):
        if path.is_symlink() or not (path.is_file() or path.is_dir()):
            raise ValueError("A fixture closure contains a linked or unsupported path.")
        if path.is_file():
            hashes[path.relative_to(directory).as_posix()] = file_sha(path)
    return hashes


class FixtureProvider(ThreadingHTTPServer):
    def __init__(self):
        super().__init__(("127.0.0.1", 0), ProviderRequest)
        self.seed_calls = 0
        self.verification_rounds = []
        self.omit_probe_reasoning = False


class ProviderRequest(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def send_payload(self, document, content_type="application/json"):
        body = document if isinstance(document, bytes) else json.dumps(document).encode()
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/v1/models":
            self.send_payload({"object": "list", "data": [{"id": fixture.MODEL, "object": "model"}]})
        else:
            self.send_payload({"models": []})

    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        messages = request.get("messages")
        if not messages:
            self.send_payload({})
            return

        prompt = next(str(message.get("content", "")) for message in reversed(messages) if message.get("role") == "user")
        original = fixture.SEED_PROMPT in prompt
        call_id = "seed-once" if original else "verify-once"
        continuation = messages[-1]["role"] == "tool"
        streamed = request.get("stream", False)
        message = {"role": "assistant"}

        if continuation:
            previous = next(message for message in reversed(messages) if message.get("tool_calls"))
            saw_original = any(message.get("role") == "assistant" and fixture.SEED_ANSWER in str(message.get("content"))
                               for message in messages)
            observed = (saw_original and previous.get("reasoning") == fixture.REASONING_MARKER
                        and messages[-1].get("tool_call_id") == call_id)
            if original:
                message["content"] = fixture.SEED_ANSWER
            else:
                self.server.verification_rounds.append(observed)
                message["content"] = fixture.WIRE_PROOF if observed else "continuation-wire-mismatch"
        else:
            call = {"id": call_id, "type": "function",
                    "function": {"name": "get_local_system_time", "arguments": "{}"}}
            if streamed:
                call["index"] = 0
            message.update(content="Observed fixture.", tool_calls=[call])
            if original or not self.server.omit_probe_reasoning:
                message["reasoning"] = fixture.REASONING_MARKER
            if original:
                self.server.seed_calls += 1

        response = {
            "id": "chatcmpl-fixture", "model": fixture.MODEL, "created": 0,
            "object": "chat.completion.chunk" if streamed else "chat.completion",
            "choices": [{"index": 0, "delta" if streamed else "message": message,
                         "finish_reason": "stop" if continuation else "tool_calls"}],
            "usage": {"prompt_tokens": 20, "completion_tokens": 5, "total_tokens": 25},
        }
        if streamed:
            self.send_payload(b"data: " + json.dumps(response).encode() + b"\n\ndata: [DONE]\n\n", "text/event-stream")
        else:
            self.send_payload(response)


class NativeRepairActivationTests(unittest.TestCase):
    inputs = None

    @classmethod
    def setUpClass(cls):
        if cls.inputs is None:
            raise unittest.SkipTest("Run directly with the affected and repaired packages, signed bundle, and public key.")

    def setUp(self):
        temporary = Path(tempfile.gettempdir()).resolve()
        test_root = temporary / "arcanum-tests"
        test_root.mkdir(mode=0o700, exist_ok=True)
        self.assertFalse(test_root.is_symlink())
        ownership = test_root.stat()
        self.assertEqual(os.getuid(), ownership.st_uid)
        self.assertEqual(0, ownership.st_mode & 0o022)

        lab = self.enterContext(tempfile.TemporaryDirectory(prefix="repair-native-", dir=test_root))
        self.lab = Path(lab).resolve()
        self.home = self.lab / "home"
        self.profile = self.home / ".config/arcanum"
        self.profile.mkdir(parents=True, mode=0o700)
        self.install = self.home / ".local/share/arcanum"
        self.install.parent.mkdir(parents=True, mode=0o700)
        self.environment = {
            "PATH": SYSTEM_PATH, "HOME": str(self.home), "USERPROFILE": str(self.home), "TMPDIR": str(temporary),
            "ARCANUM_TEST_HOME": str(self.home), "ASPNETCORE_ENVIRONMENT": "Testing", "DOTNET_ENVIRONMENT": "Testing",
            "ARCANUM_TEST_IN_MEMORY_CREDENTIALS": "1", "ARCANUM_PROVIDER_FIXTURE_API_KEY": "fixture-only",
        }

        qualification = subprocess.run(
            [sys.executable, str(SCRIPTS / "qualify-repair.py"), "verify", "--bundle", str(self.inputs.bundle),
             "--public-key", str(self.inputs.public_key), "--rid", "osx-arm64"],
            cwd=self.lab, env=self.environment, capture_output=True, timeout=120, check=False)
        self.assertEqual(0, qualification.returncode, "The detached qualification must validate.")
        receipt = json.loads(qualification.stdout)
        self.original_closure = install_hashes(self.inputs.affected_install)
        self.candidate_closure = install_hashes(self.inputs.repaired_install)
        self.assertEqual(receipt["affected"]["apphostSha256"], self.original_closure["arcanum"])
        self.assertEqual(receipt["candidate"]["apphostSha256"], self.candidate_closure["arcanum"])
        self.assertEqual(install_hashes(self.inputs.bundle / "install"), self.candidate_closure)
        shutil.copytree(self.inputs.affected_install, self.install)

        self.provider = FixtureProvider()
        self.addCleanup(self.provider.server_close)
        worker = threading.Thread(target=self.provider.serve_forever, daemon=True)
        worker.start()
        self.addCleanup(worker.join, 5)
        self.addCleanup(self.provider.shutdown)
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        self.origin = f"http://127.0.0.1:{port}"
        self.write_configuration(port)
        self.source = self.home / "repair.cs"
        self.source.write_bytes(WORKSPACE)
        self.launcher = self.home / ".local/bin/arcanum"
        self.launcher.parent.mkdir()
        self.launcher.write_bytes(LAUNCHER)
        self.launcher.chmod(0o700)
        self.key = None
        self.initial_process = None
        self.addCleanup(self.stop_remaining_host)

    def write_configuration(self, port):
        provider = f"http://127.0.0.1:{self.provider.server_port}"
        disabled = ("apprentices", "lexicon", "archiveSearch", "metrics", "scrying", "workspaceChecks", "annals")
        features = dict.fromkeys(disabled, False)
        features.update(reasoning=True, attachments=True, covenant=True, agentMemoryRecall=True, clientTools=True)
        settings = {"Arcanum": {
            "host": {"port": port}, "defaultModel": fixture.MODEL,
            "localRuntimes": [{"id": "fixture-runtime", "adapter": "ollama", "managementEndpoint": provider}],
            "providers": [{"name": "fixture-provider", "type": "OpenAICompatible", "endpoint": provider + "/v1",
                           "localRuntimeId": "fixture-runtime", "contextWindowLimit": 16384,
                           "credentialEnvironmentVariable": "ARCANUM_PROVIDER_FIXTURE_API_KEY",
                           "models": [{"name": fixture.MODEL, "supportsTools": True}]}],
            "features": features, "workspaces": {"defaultRoot": str(self.home)},
            "security": {"campaignRoots": [str(self.home)]},
        }}
        self.config = self.profile / "arcanum.json"
        self.config.write_text(json.dumps(settings) + "\n")
        self.config_hash = file_sha(self.config)

    def api(self, route, payload=None, content_type="application/json"):
        return fixture.envelope(fixture.request(self.origin, self.key, route, payload, content_type))

    def boot_affected(self):
        process = subprocess.Popen([str(self.install / "arcanum"), "serve", "--plain"],
                                   cwd=self.home, env=self.environment, stdin=subprocess.DEVNULL,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        self.initial_process = process
        self.addCleanup(self.reap_initial_host, process)

        def capture():
            for line in process.stdout:
                value = line.strip()
                if re.fullmatch(rb"[A-Za-z0-9+/]{43}=", value):
                    self.key = value.decode("ascii")

        reader = threading.Thread(target=capture, daemon=True)
        reader.start()
        for _ in range(300):
            self.assertIsNone(process.poll(), "The affected apphost exited during startup.")
            if self.key:
                try:
                    metadata = self.boot_metadata()
                except (OSError, ValueError, http.client.HTTPException):
                    pass
                else:
                    self.assertEqual(process.pid, metadata["processId"])
                    return process
            time.sleep(0.15)
        self.fail("The affected apphost did not answer its authenticated readiness probe.")

    def reap_initial_host(self, process):
        if process.poll() is None:
            process.terminate()
        process.wait(timeout=10)
        process.stdout.close()

    def boot_metadata(self):
        metadata = self.api("/api/meta")
        self.assertEqual(str(self.profile), str(Path(metadata["grimoireDirectory"]).resolve()))
        self.assertIs(metadata["nativeAot"], True)
        self.assertEqual("osx-arm64", metadata["runtimeIdentifier"])
        return {name: metadata[name] for name in ("processId", "startTime")}

    def stop_host(self, expected):
        self.assertEqual(expected, self.boot_metadata())
        self.api("/api/server/quit", {})
        process_id = expected["processId"]
        if self.initial_process is not None and self.initial_process.pid == process_id:
            self.initial_process.wait(timeout=45)
            return
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            try:
                os.kill(process_id, 0)
            except ProcessLookupError:
                return
            time.sleep(0.1)
        self.fail("The authenticated disposable host did not exit.")

    def stop_remaining_host(self):
        if self.key is None:
            return
        try:
            metadata = self.boot_metadata()
        except (OSError, http.client.HTTPException):
            return
        self.stop_host(metadata)

    def control(self, operation, *arguments, exit_code=0):
        completed = subprocess.run(
            [sys.executable, str(CONTROLLER), operation, *map(str, arguments)],
            cwd=self.lab, env={**self.environment, "ARCANUM_API_KEY": self.key},
            capture_output=True, timeout=180, check=False)
        status = json.loads(completed.stdout)
        self.assertEqual(exit_code, completed.returncode, f"{operation} returned {status.get('code', status.get('phase'))}.")
        return status

    def stage(self, session, public_key=None, exit_code=0):
        return self.control(
            "stage", "--bundle", self.inputs.bundle, "--public-key", public_key or self.inputs.public_key,
            "--install-dir", self.install, "--profile", self.profile, "--api-url", self.origin,
            "--session", session, "--behavior-check", OBSERVER, exit_code=exit_code)

    def stage_operation(self, session):
        result = self.stage(session)
        self.assertEqual("staged", result["phase"])
        self.assertIs(result["eligible"], True)
        self.assertEqual(session, result["sessionId"])
        return result["operation"]

    def retained_entries(self, session):
        return {entry["id"]: entry for entry in self.api(f"/api/sessions/{session}/entries?limit=64")}

    def assert_retained(self, session, entries, attachment, closure):
        self.assertEqual(session, self.api(f"/api/sessions/{session}")["id"])
        actual = self.retained_entries(session)
        self.assertEqual(entries, {identifier: actual[identifier] for identifier in entries})
        original_tools = [entry["toolName"] for entry in entries.values() if entry.get("toolName")]
        self.assertEqual(["get_local_system_time"], original_tools)
        self.assertEqual([attachment], self.api(f"/api/sessions/{session}/attachments"))
        route = f"/api/sessions/{session}/attachments/{attachment['id']}/content"
        self.assertEqual(ATTACHMENT, fixture.request(self.origin, self.key, route))
        self.assertEqual(self.config_hash, file_sha(self.config))
        self.assertEqual(WORKSPACE, self.source.read_bytes())
        self.assertEqual(LAUNCHER, self.launcher.read_bytes())
        self.assertEqual(0o700, self.launcher.stat().st_mode & 0o777)
        self.assertEqual(closure, install_hashes(self.install))
        self.assertEqual(1, self.provider.seed_calls)

    def test_rolls_back_failed_native_behavior_and_applies_signed_repair_in_the_same_session(self):
        self.boot_affected()
        original_boot = self.boot_metadata()
        session = self.api("/api/sessions", {"title": "Retained repair Session"})["id"]
        seeded = self.api("/api/intelligence/ping", fixture.turn(session, fixture.SEED_PROMPT))
        self.assertIn(fixture.SEED_ANSWER, seeded["text"])
        entries = self.retained_entries(session)
        self.assertEqual(4, len(entries))
        boundary = "ArcanumAttachmentBoundary"
        multipart = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="note.txt"\r\n'
                     "Content-Type: text/plain\r\n\r\n").encode()
        multipart += ATTACHMENT + f"\r\n--{boundary}--\r\n".encode()
        attachment = self.api(f"/api/sessions/{session}/attachments", multipart, f"multipart/form-data; boundary={boundary}")

        with self.assertRaisesRegex(ValueError, "reasoning-continuation wire check"):
            fixture.verify(self.origin, self.key, session, original_boot["processId"], str(uuid.uuid4()))
        self.assertEqual([False], self.provider.verification_rounds)
        self.assert_retained(session, entries, attachment, self.original_closure)

        invalid_key = self.lab / "invalid-maintainer.pem"
        invalid_key.write_bytes(b"not a public key\n")
        bad_stage = self.stage(session, public_key=invalid_key, exit_code=2)
        self.assertEqual("unsafeHandoff", bad_stage["code"])
        self.assertNotIn("operation", bad_stage)
        self.assertEqual(original_boot, self.boot_metadata())
        self.assert_retained(session, entries, attachment, self.original_closure)

        failed_operation = self.stage_operation(session)
        staged = self.control("apply", "--operation", failed_operation, exit_code=2)
        self.assertEqual(("staged", "historicalDrainUnproved"), (staged["phase"], staged["code"]))
        self.assertEqual(original_boot, self.boot_metadata())
        self.stop_host(original_boot)

        self.provider.omit_probe_reasoning = True
        failed = self.control("apply", "--operation", failed_operation, exit_code=2)
        self.assertEqual(("rollbackPending", "rollbackDrainUnproved"), (failed["phase"], failed["code"]))
        self.assertEqual([False, False], self.provider.verification_rounds)
        candidate_boot = self.boot_metadata()
        self.assertNotEqual(original_boot["startTime"], candidate_boot["startTime"])
        self.assert_retained(session, entries, attachment, self.candidate_closure)
        self.stop_host(candidate_boot)

        rolled_back = self.control("recover", "--operation", failed_operation, exit_code=2)
        self.assertEqual(("rolledBack", "behaviorNotVerified"), (rolled_back["phase"], rolled_back["code"]))
        self.assertIs(rolled_back["continuationReady"], False)
        self.assertEqual(rolled_back, self.control("status", "--operation", failed_operation))
        rollback_boot = self.boot_metadata()
        self.assertNotEqual(candidate_boot["startTime"], rollback_boot["startTime"])
        self.assert_retained(session, entries, attachment, self.original_closure)

        self.provider.omit_probe_reasoning = False
        qualified_operation = self.stage_operation(session)
        self.stop_host(rollback_boot)
        applied = self.control("apply", "--operation", qualified_operation)
        self.assertEqual("applied", applied["phase"])
        self.assertIs(applied["continuationReady"], True)
        self.assertEqual(session, applied["sessionId"])
        applied_boot = self.boot_metadata()
        self.assertNotEqual(rollback_boot["startTime"], applied_boot["startTime"])
        self.assertEqual([False, False, True, True], self.provider.verification_rounds)
        self.assert_retained(session, entries, attachment, self.candidate_closure)
        self.assertEqual(applied, self.control("status", "--operation", qualified_operation))
        self.assertEqual(applied, self.control("recover", "--operation", qualified_operation))
        self.assertEqual(applied_boot, self.boot_metadata())
        self.assertEqual([False, False, True, True], self.provider.verification_rounds)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    if sys.version_info < (3, 11):
        parser.error("Run this fixture with Python 3.11 or later.")
    for argument in ("affected-install", "repaired-install", "bundle", "public-key"):
        parser.add_argument(f"--{argument}", required=True, type=lambda path: Path(path).resolve(strict=True))
    NativeRepairActivationTests.inputs = parser.parse_args()
    if sys.platform != "darwin" or platform.machine() != "arm64":
        parser.error("The native activation fixture requires Apple silicon macOS.")
    unittest.main(argv=[sys.argv[0]], verbosity=2)
