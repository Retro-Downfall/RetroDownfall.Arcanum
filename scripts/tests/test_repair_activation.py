from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
import hashlib
import http.client
import importlib.util
import json
import os
from pathlib import Path
import plistlib
import shutil
import signal
import socket
import subprocess
import sys
from tempfile import TemporaryDirectory
from threading import Event
import time
import unittest
import uuid
from unittest import mock

import test_repair_qualification as qualification


CONTROLLER = qualification.ROOT / "scripts" / "activate-repair.py"


@unittest.skipUnless(os.name == "posix", "requires the controller's POSIX installation locks")
class ActivationLockOrderTests(unittest.TestCase):
    def test_maintenance_owner_can_enter_client_gate_during_contending_activation(self):
        spec = importlib.util.spec_from_file_location("repair_activation_lock_test", CONTROLLER)
        controller = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(controller)
        real_lock = controller.lock

        with TemporaryDirectory(prefix="arcanum-activation-lock-order-") as folder:
            root = Path(folder).resolve()
            profile = root / "arcanum"
            profile.mkdir(mode=0o700)
            database = profile / "arcanum.db"
            database.write_bytes(b"disposable retained database")
            (profile / "arcanum.json").write_text("{}\n")
            installed = root / "installed"
            installed.mkdir(mode=0o700)
            installed_apphost = installed / controller.APPHOST
            installed_apphost.write_bytes(b"prior executable fixture")
            operation = root / "activation" / "operation.json"
            candidate = operation.parent / "qualification" / "install"
            candidate.mkdir(parents=True, mode=0o700)
            candidate_apphost = candidate / controller.APPHOST
            candidate_apphost.write_bytes(b"candidate executable fixture")
            record = {"profile": str(profile), "profileIdentity": controller.profile_identity(profile),
                      "installDirectory": str(installed)}
            maintenance = root / ".arcanum-maintenance-arcanum.lock"
            client = root / ".arcanum-client-mutation-arcanum.lock"
            activation_reached_maintenance = Event()
            resume_activation = Event()

            @contextmanager
            def observe_lock(path, operation=False):
                if path == maintenance:
                    activation_reached_maintenance.set()
                    if not resume_activation.wait(timeout=5):
                        raise TimeoutError("maintenance owner did not finish its client-gate attempt")
                with real_lock(path, operation=operation):
                    yield

            with real_lock(maintenance), ThreadPoolExecutor(max_workers=1) as workers:
                with mock.patch.object(controller, "lock", observe_lock):
                    activation = workers.submit(controller.swap, operation, record, "unused-fixture-key", "prior")
                    owner_entered_client = False
                    try:
                        self.assertTrue(activation_reached_maintenance.wait(timeout=5))
                        try:
                            with real_lock(client):
                                owner_entered_client = True
                        except controller.ActivationError as error:
                            self.assertEqual(error.code, "installationBusy")
                    finally:
                        resume_activation.set()

                    with self.assertRaises(controller.ActivationError) as refused:
                        activation.result(timeout=5)

            self.assertEqual(refused.exception.code, "installationBusy")
            self.assertTrue(owner_entered_client, "Activation withheld the client gate from the maintenance owner.")
            self.assertEqual(installed_apphost.read_bytes(), b"prior executable fixture")
            self.assertEqual(candidate_apphost.read_bytes(), b"candidate executable fixture")
            self.assertEqual(database.read_bytes(), b"disposable retained database")


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("openssl"), "requires the macOS packaging and signature fixture")
class RepairActivationTests(unittest.TestCase):
    def setUp(self):
        self.fixture = qualification.RepairQualificationTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.root = self.fixture.root.resolve()
        self.root.chmod(0o700)

        self.install = self.root / "installed"
        shutil.copytree(self.fixture.install, self.install)
        self.install.chmod(0o700)
        self.installed_host = self.install / "arcanum"
        self.old_bytes = b"prior independently qualified native apphost fixture\n"
        self.installed_host.write_bytes(self.old_bytes)
        self.installed_host.chmod(0o700)
        shutil.copy2(self.fixture.asset, self.install / self.fixture.asset.name)
        self.fixture.receipt["affected"]["apphostSha256"] = self.sha(self.old_bytes)
        compatibility = self.fixture.receipt["stateCompatibility"]
        for role in ("affected", "candidate"):
            compatibility[role]["nativeSqlCipherSha256"] = self.sha(self.fixture.asset.read_bytes())
        self.fixture.sign()

        self.runtime_tmp = self.root / "fixture-runtime"
        self.profile = self.runtime_tmp / "arcanum-tests" / "home" / ".config" / "arcanum"
        self.profile.mkdir(parents=True, mode=0o700)
        (self.profile / "arcanum.json").write_text('{"Arcanum":{"DefaultModel":"saved-local-role"}}\n')
        (self.profile / "arcanum.db").write_bytes(b"retained profile fixture")
        self.launcher = self.root / "arcanum"
        self.launcher.symlink_to(self.installed_host)
        self.behavior_check = self.root / "operator-verifier"
        self.behavior_check.write_text("#!/bin/sh\nexit 0\n")
        self.behavior_check.chmod(0o700)
        self.session = str(uuid.uuid4())
        self.port = 65534

    @staticmethod
    def sha(data):
        return hashlib.sha256(data).hexdigest()

    def environment(self):
        return {**os.environ, "ARCANUM_API_KEY": "activation-fixture-only-secret",
                "ARCANUM_TEST_HOME": str(self.profile.parent.parent), "TMPDIR": str(self.runtime_tmp),
                "ARCANUM_TEST_IN_MEMORY_CREDENTIALS": "1",
                "ARCANUM_PROVIDER_FIXTURE_API_KEY": "fixture-provider-value",
                "DOTNET_ENVIRONMENT": "Testing", "ASPNETCORE_ENVIRONMENT": "Testing"}

    def run_controller(self, *args):
        return subprocess.run([sys.executable, CONTROLLER, *map(str, args)], check=False,
                              capture_output=True, text=True, timeout=30, env=self.environment())

    def stage(self):
        return self.run_controller("stage", "--bundle", self.fixture.bundle, "--public-key", self.fixture.public,
                                   "--install-dir", self.install, "--profile", self.profile,
                                   "--api-url", f"http://127.0.0.1:{self.port}", "--session", self.session,
                                   "--behavior-check", self.behavior_check,
                                   "--behavior-check-id", "ollama-reasoning-continuation-v1")

    def staged(self):
        result = self.stage()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        record = json.loads(result.stdout)
        self.assertEqual(record["phase"], "staged")
        return Path(record["operation"])

    def server(self, method="GET", route="/api/meta"):
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=2)
        try:
            connection.request(method, route, headers={"Authorization": "Bearer activation-fixture-only-secret"})
            response = connection.getresponse()
            return json.loads(response.read())["data"]
        finally:
            connection.close()

    def cleanup_server(self):
        try:
            self.server("POST", "/api/server/quit")
        except OSError:
            pass

    def compiled_installation(self, startup_failure=False, startup_delay=0, behavior_failure=False,
                              historical=False, outstanding_effect=False, historical_candidate=False):
        if not shutil.which("cc"):
            self.skipTest("requires the macOS compiler for a native process fixture")
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            self.port = probe.getsockname()[1]
        source = Path(__file__).parent / "fixtures" / "repair_activation_host.c"
        for repaired, target in enumerate((self.installed_host, self.fixture.apphost)):
            process = subprocess.run(["cc", str(source), "-o", str(target), f"-DFIXTURE_PORT={self.port}",
                                      f"-DREPAIRED={repaired}", f"-DSTART_FAILURE={int(startup_failure and repaired)}",
                                      f"-DSTART_DELAY={startup_delay if repaired else 0}",
                                      f"-DLEGACY_DRAIN={int(historical if not repaired else historical_candidate)}",
                                      f"-DRETAINED_EFFECT={int(outstanding_effect)}"], capture_output=True, text=True)
            self.assertEqual(process.returncode, 0, process.stderr)

        self.old_bytes = self.installed_host.read_bytes()
        self.fixture.receipt["affected"]["apphostSha256"] = self.sha(self.old_bytes)
        shutil.copy2(self.fixture.apphost, self.fixture.install / "arcanum")
        closed = qualification.encode(self.fixture.manifest(self.fixture.payload))
        projected = qualification.encode(self.fixture.manifest(self.fixture.install))
        (self.fixture.bundle / "closure.json").write_bytes(closed)
        (self.fixture.bundle / "install-closure.json").write_bytes(projected)
        self.fixture.receipt["candidate"]["apphostSha256"] = self.sha(self.fixture.apphost.read_bytes())
        self.fixture.receipt["candidate"]["bundleSha256"] = self.sha(closed)
        self.fixture.receipt["candidate"]["installSha256"] = self.sha(projected)
        self.fixture.sign()
        self.behavior_check.write_text("""#!/usr/bin/python3
import argparse
import http.client
import json
import os
from urllib.parse import urlsplit
parser = argparse.ArgumentParser()
for name in ("api-url", "session", "process-id", "apphost-sha256", "activation-id"):
    parser.add_argument("--" + name, required=True)
args = parser.parse_args()
origin = urlsplit(args.api_url)
connection = http.client.HTTPConnection(origin.hostname, origin.port, timeout=3)
connection.request("GET", "/api/meta", headers={"Authorization": "Bearer " + os.environ["ARCANUM_API_KEY"]})
metadata = json.loads(connection.getresponse().read())["data"]
assert metadata["processId"] == int(args.process_id) and metadata["fixtureRepaired"]
assert "ARCANUM_PROVIDER_FIXTURE_API_KEY" not in os.environ
print(json.dumps({"contract": "ollama-reasoning-continuation-v1", "activationId": args.activation_id,
                  "sessionId": args.session, "processId": metadata["processId"],
                  "apphostSha256": args.apphost_sha256, "disposition": "passed"}))
""" if not behavior_failure else "#!/bin/sh\nexit 1\n")
        self.addCleanup(self.cleanup_server)

    def initial_server(self):
        host = subprocess.Popen([str(self.installed_host), "serve"], env=self.environment(),
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        def cleanup():
            if host.poll() is None:
                host.terminate()
                host.wait(timeout=5)
        self.addCleanup(cleanup)
        for _ in range(100):
            try:
                return self.server()
            except ConnectionRefusedError:
                time.sleep(0.02)
        self.fail("prior local process never listened")

    def assert_retained(self, repaired):
        current = self.server()
        self.assertEqual(current["fixtureRepaired"], repaired)
        session = self.server(route=f"/api/sessions/{self.session}")
        self.assertEqual(session["id"], self.session)
        self.assertGreaterEqual(session["entryCount"], 3)
        self.assertEqual(self.launcher.readlink(), self.installed_host)
        self.assertEqual((self.profile / "arcanum.db").read_bytes(), b"retained profile fixture")
        self.assertIn("saved-local-role", (self.profile / "arcanum.json").read_text())
        return current

    def test_activate_verifies_the_already_restarted_process_and_reuses_terminal_receipt(self):
        self.compiled_installation()
        prior = self.initial_server()
        operation = self.staged()
        self.assertEqual(self.server()["processId"], prior["processId"])
        upgraded = self.run_controller("apply", "--operation", operation)
        self.assertEqual(upgraded.returncode, 0, upgraded.stdout + upgraded.stderr)
        applied = json.loads(upgraded.stdout)
        self.assertEqual(applied["phase"], "applied")
        self.assertTrue(applied["continuationReady"])
        active = self.assert_retained(True)
        self.assertNotEqual(active["processId"], prior["processId"])
        self.assertEqual(self.sha(self.installed_host.read_bytes()), self.fixture.receipt["candidate"]["apphostSha256"])
        self.assertEqual(self.fixture.manifest(self.install), self.fixture.manifest(self.fixture.install))
        self.assertFalse((self.install / "arcanum.pdb").exists())
        repeated = self.run_controller("apply", "--operation", operation)
        self.assertEqual(repeated.returncode, 0, repeated.stdout + repeated.stderr)
        self.assertEqual(repeated.stdout, upgraded.stdout)
        self.assertEqual(self.server()["processId"], active["processId"])

    def test_valid_upgrade_preserves_an_unknown_effect_and_keeps_continuation_blocked(self):
        self.compiled_installation(outstanding_effect=True)
        self.initial_server()
        operation = self.staged()
        upgraded = self.run_controller("apply", "--operation", operation)
        self.assertEqual(upgraded.returncode, 0, upgraded.stdout + upgraded.stderr)
        applied = json.loads(upgraded.stdout)
        self.assertEqual(applied["phase"], "applied")
        self.assertEqual(applied["effectState"], "dispatched")
        self.assertFalse(applied["continuationReady"])
        self.assert_retained(True)
        record = json.loads(operation.read_text())["record"]
        self.assertEqual(record["priorSession"]["effect"]["state"], "dispatched")

    def test_behavior_failure_after_swap_restores_the_exact_prior_process_and_session(self):
        self.compiled_installation(behavior_failure=True)
        prior = self.initial_server()
        operation = self.staged()
        result = self.run_controller("apply", "--operation", operation)
        self.assertNotEqual(result.returncode, 0, result.stdout)
        rolled_back = json.loads(result.stdout)
        self.assertEqual((rolled_back["phase"], rolled_back["code"]), ("rolledBack", "behaviorNotVerified"))
        self.assertFalse(rolled_back["continuationReady"])
        self.assertNotEqual(self.assert_retained(False)["processId"], prior["processId"])
        self.assertEqual(self.installed_host.read_bytes(), self.old_bytes)

    def test_a_live_historical_candidate_waits_for_proved_stop_before_rollback(self):
        self.compiled_installation(behavior_failure=True, historical_candidate=True)
        self.initial_server()
        operation = self.staged()
        refused = self.run_controller("apply", "--operation", operation)
        self.assertEqual(refused.returncode, 2, refused.stdout + refused.stderr)
        state = json.loads(refused.stdout)
        self.assertEqual((state["phase"], state["code"]), ("rollbackPending", "rollbackDrainUnproved"))
        self.assertTrue(self.server()["fixtureRepaired"])
        self.server("POST", "/api/server/quit")
        self.wait_until_stopped()
        recovered = self.run_controller("recover", "--operation", operation)
        self.assertEqual(recovered.returncode, 2, recovered.stdout + recovered.stderr)
        self.assertEqual(json.loads(recovered.stdout)["phase"], "rolledBack")
        self.assert_retained(False)
        self.assertEqual(self.installed_host.read_bytes(), self.old_bytes)

    def test_failed_candidate_start_recovers_the_prior_closure_from_a_stopped_installation(self):
        self.compiled_installation(startup_failure=True)
        self.initial_server()
        operation = self.staged()
        self.server("POST", "/api/server/quit")
        self.wait_until_stopped()
        result = self.run_controller("apply", "--operation", operation)
        self.assertNotEqual(result.returncode, 0, result.stdout)
        rolled_back = json.loads(result.stdout)
        self.assertEqual((rolled_back["phase"], rolled_back["code"]), ("rolledBack", "restartFailed"))
        self.assertFalse(rolled_back["continuationReady"])
        self.assert_retained(False)
        self.assertEqual(self.installed_host.read_bytes(), self.old_bytes)

    def wait_until_stopped(self):
        for _ in range(150):
            try:
                self.server()
            except ConnectionRefusedError:
                return
            time.sleep(0.02)
        self.fail("prior process did not leave service")

    def test_historical_active_host_is_untouched_until_its_real_stop_is_proved(self):
        self.compiled_installation(historical=True)
        prior = self.initial_server()
        operation = self.staged()
        refused = self.run_controller("apply", "--operation", operation)
        self.assertEqual(refused.returncode, 2, refused.stdout + refused.stderr)
        self.assertEqual(json.loads(refused.stdout)["code"], "historicalDrainUnproved")
        self.assertEqual(json.loads(refused.stdout)["phase"], "staged")
        self.assertEqual(self.server()["processId"], prior["processId"])
        self.assertEqual(self.installed_host.read_bytes(), self.old_bytes)
        self.server("POST", "/api/server/quit")
        self.wait_until_stopped()
        applied = self.run_controller("apply", "--operation", operation)
        self.assertEqual(applied.returncode, 0, applied.stdout + applied.stderr)
        self.assert_retained(True)

    def test_launchd_restarts_the_pinned_job_without_modifying_its_existing_plist(self):
        spec = importlib.util.spec_from_file_location("repair_activation", CONTROLLER)
        controller = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(controller)
        home = self.profile.parent.parent
        directory = home / "Library" / "LaunchAgents"
        directory.mkdir(parents=True)
        plist = directory / (controller.LAUNCHD_LABEL + ".plist")
        original = plistlib.dumps({"Label": controller.LAUNCHD_LABEL,
                                   "ProgramArguments": [str(self.installed_host), "serve"],
                                   "RunAtLoad": True, "KeepAlive": True})
        plist.write_bytes(original)
        environment = {"HOME": str(home), "PATH": controller.SAFE_PATH}
        launched = True
        recorded = []
        def fake_launch(_, command, *arguments):
            nonlocal launched
            if command == "list":
                return subprocess.CompletedProcess([], 0 if launched else 1, b'"PID" = 123;', b"")
            recorded.append((command, *arguments))
            launched = command == "bootstrap"
            return subprocess.CompletedProcess([], 0, b"", b"")

        with mock.patch.object(controller, "launchctl", fake_launch):
            plan = controller.launcher_plan(self.install, environment)
            record = {"launcher": plan, "profile": str(self.profile),
                      "configurationSha256": self.sha((self.profile / "arcanum.json").read_bytes())}
            controller.unload_launcher(record, environment)
            self.assertIsNone(controller.start_host(record, environment))
            self.assertTrue(launched)

        expected = f"gui/{os.getuid()}"
        self.assertEqual(recorded, [("bootout", expected, str(plist)), ("bootstrap", expected, str(plist))])
        self.assertEqual(plist.read_bytes(), original)

    def test_recover_process_interrupted_after_exchange_does_not_apply_a_second_swap(self):
        self.compiled_installation(startup_delay=2)
        self.initial_server()
        operation = self.staged()
        process = subprocess.Popen([sys.executable, CONTROLLER, "apply", "--operation", operation],
                                   env=self.environment(), stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            for _ in range(300):
                record = json.loads(operation.read_text())["record"]
                if record["phase"] == "starting" and record.get("childPid") is not None:
                    break
                if process.poll() is not None:
                    self.fail(process.communicate())
                time.sleep(0.01)
            else:
                self.fail("the apphost exchange did not publish the starting checkpoint")
            process.send_signal(signal.SIGKILL)
        finally:
            if process.poll() is None:
                process.kill()
            process.communicate(timeout=5)
        recovered = self.run_controller("recover", "--operation", operation)
        self.assertEqual(recovered.returncode, 0, recovered.stdout + recovered.stderr)
        self.assertEqual(json.loads(recovered.stdout)["phase"], "applied")
        self.assertEqual(self.assert_retained(True)["processId"], record["childPid"])

    def test_actual_installed_apphost_name_with_external_launcher_and_licenses_can_be_staged(self):
        operation = self.staged()
        self.assertEqual(self.installed_host.read_bytes(), self.old_bytes)
        self.assertEqual(self.launcher.readlink(), self.install / "arcanum")
        self.assertTrue((self.install / "licenses" / "umap-learn-LICENSE.txt").is_file())
        projected = operation.parent / "qualification" / "install"
        self.assertEqual(self.fixture.manifest(projected), self.fixture.manifest(self.fixture.install))
        self.assertEqual((projected / "arcanum").read_bytes(), self.fixture.apphost.read_bytes())
        self.assertFalse((projected / qualification.APPHOST).exists())
        self.assertFalse((projected / "arcanum.pdb").exists())

    def test_unrelated_installed_file_blocks_exchange_without_moving_it(self):
        unrelated = self.install / "licenses" / "personal-notes.txt"
        unrelated.write_text("Retain this file.")
        result = self.stage()
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertEqual(json.loads(result.stdout)["code"], "installationOwnershipUnproved")
        self.assertEqual(unrelated.read_text(), "Retain this file.")
        self.assertEqual(self.installed_host.read_bytes(), self.old_bytes)

    def test_disposable_profile_cannot_fall_back_to_the_real_credential_store(self):
        without_isolation = {**self.environment(), "ARCANUM_TEST_IN_MEMORY_CREDENTIALS": "0"}
        with mock.patch.object(self, "environment", return_value=without_isolation):
            result = self.stage()
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertEqual(json.loads(result.stdout)["code"], "profileMismatch")
        self.assertFalse((self.install.parent / ".arcanum-repair").exists())
        self.assertEqual(self.installed_host.read_bytes(), self.old_bytes)

    def test_signed_bundle_is_staged_without_stopping_or_changing_installation(self):
        operation = self.staged()
        self.assertEqual(self.installed_host.read_bytes(), self.old_bytes)
        self.assertEqual(self.launcher.readlink(), self.installed_host)
        self.assertIn("saved-local-role", (self.profile / "arcanum.json").read_text())
        status = self.run_controller("status", "--operation", operation)
        self.assertEqual(status.returncode, 0, status.stderr)
        view = json.loads(status.stdout)
        self.assertEqual(view["phase"], "staged")
        self.assertEqual(view["sessionId"], self.session)
        self.assertNotIn("activation-fixture-only-secret", status.stdout)

    def test_modified_staged_verifier_is_refused_before_example_host_or_closure_is_touched(self):
        operation = self.staged()
        verifier = operation.parent / "behavior-check"
        verifier.write_text("#!/bin/sh\nexit 1\n")
        rejected = self.run_controller("apply", "--operation", operation)
        self.assertNotEqual(rejected.returncode, 0)
        self.assertEqual(json.loads(rejected.stdout)["code"], "observerChanged")
        self.assertEqual(self.installed_host.read_bytes(), self.old_bytes)

    def test_unproved_state_is_retained_staged_and_never_activated(self):
        self.fixture.receipt["disposition"] = "unavailable"
        compatibility = self.fixture.receipt["stateCompatibility"]
        compatibility["disposition"] = "migrationRequired"
        compatibility["candidate"] = json.loads(json.dumps(compatibility["affected"]))
        compatibility["candidate"]["families"]["core"]["version"] += 1
        compatibility["reversibleConditions"] = []
        self.fixture.sign()
        result = self.stage()
        self.assertNotEqual(result.returncode, 0)
        staged = json.loads(result.stdout)
        self.assertEqual(staged["phase"], "staged")
        self.assertEqual(staged["code"], "stateRecoveryUnproved")
        self.assertTrue(Path(staged["operation"]).is_file())
        self.assertEqual(self.installed_host.read_bytes(), self.old_bytes)

    def test_signed_artifact_for_another_installation_is_refused(self):
        self.installed_host.write_bytes(b"some other installed apphost")
        result = self.stage()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(json.loads(result.stdout)["code"], "priorBuildMismatch")
        self.assertEqual(self.installed_host.read_bytes(), b"some other installed apphost")


if __name__ == "__main__":
    unittest.main()
