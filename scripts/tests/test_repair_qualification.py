import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "qualify-repair.py"
APPHOST = "RetroDownfall.Arcanum.Cli"
LICENSES = ("licenses/umap-learn-LICENSE.txt", "licenses/Microsoft.Data.Sqlite.Core/source-manifest.json",
            "licenses/Microsoft.Data.Sqlite.Core/README.md", "licenses/Microsoft.Data.Sqlite.Core/LICENSE.txt")


def encode(document):
    return (json.dumps(document, sort_keys=True, separators=(",", ":")) + "\n").encode()


def digest(content):
    return hashlib.sha256(content).hexdigest()


@unittest.skipUnless(shutil.which("openssl"), "fixture requires OpenSSL")
class RepairQualificationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.key = self.root / "operator-key.pem"
        self.public = self.root / "operator-public.pem"
        self.openssl("ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", self.key)
        self.key.chmod(0o600)
        self.openssl("pkey", "-in", self.key, "-pubout", "-out", self.public)
        self.bundle = self.root / "bundle"
        self.payload = self.bundle / "payload"
        self.payload.mkdir(parents=True)
        self.apphost = self.payload / APPHOST
        self.apphost.write_bytes(b"exact repaired apphost bytes\n")
        self.apphost.chmod(0o700)
        self.asset = self.payload / "libe_sqlcipher.dylib"
        self.asset.write_bytes(b"exact companion bytes\n")
        self.asset.chmod(0o600)
        (self.payload / "libonigwrap.dylib").write_bytes(b"exact second companion bytes\n")
        (self.payload / "arcanum.pdb").write_bytes(b"symbols retained for qualification\n")
        for name in LICENSES:
            license_file = self.payload / name
            license_file.parent.mkdir(parents=True, exist_ok=True)
            license_file.write_text(name)
        self.install = self.bundle / "install"
        shutil.copytree(self.payload, self.install, ignore=shutil.ignore_patterns("*.pdb"))
        (self.install / APPHOST).rename(self.install / "arcanum")
        self.closure = self.manifest(self.payload)
        self.installed_closure = self.manifest(self.install)
        (self.bundle / "closure.json").write_bytes(encode(self.closure))
        (self.bundle / "install-closure.json").write_bytes(encode(self.installed_closure))
        schema = {
            "families": {family: {"version": 1, "fingerprint": "1" * 64} for family in
                         ("core", "guildCanonical", "covenantCanonical", "covenantAccelerator")},
            "nativeSqlCipherSha256": "2" * 64,
        }
        self.receipt = {
            "version": 1,
            "contract": "ollama-tool-continuation-v1",
            "disposition": "passed",
            "rid": "osx-arm64",
            "acceptanceCommit": "a" * 40,
            "acceptanceTree": "b" * 40,
            "affected": {"commit": "c" * 40, "tree": "d" * 40, "apphostSha256": "3" * 64,
                         "disposition": "contractMismatch"},
            "candidate": {"commit": "e" * 40, "tree": "f" * 40, "apphostSha256": digest(self.apphost.read_bytes()),
                          "bundleSha256": digest(encode(self.closure)), "installSha256": digest(encode(self.installed_closure)),
                          "disposition": "passed"},
            "checks": {"affectedControl": "passed", "affectedRegression": "contractMismatch", "candidateShipping": "passed",
                       "candidateRegression": "passed", "liveRegression": "notRequired"},
            "stateCompatibility": {"disposition": "sameSchema", "affected": schema, "candidate": schema,
                                   "reversibleConditions": ["schemaIdentical", "sqlCipherIdentical"]},
        }
        self.sign()

    @staticmethod
    def manifest(root):
        files = [{"path": path.relative_to(root).as_posix(), "length": path.stat().st_size, "sha256": digest(path.read_bytes()),
                  "executable": bool(path.stat().st_mode & 0o111)} for path in sorted(root.rglob("*")) if path.is_file()]
        return {"version": 1, "files": files}

    @staticmethod
    def openssl(*args):
        subprocess.run(["openssl", *map(str, args)], check=True, capture_output=True)

    def sign(self):
        receipt = self.bundle / "receipt.json"
        receipt.write_bytes(encode(self.receipt))
        self.openssl("dgst", "-sha256", "-sign", self.key, "-out", self.bundle / "receipt.sig", receipt)

    def verify(self, rid="osx-arm64", public=None):
        return subprocess.run([sys.executable, SCRIPT, "verify", "--bundle", self.bundle,
                               "--public-key", public or self.public, "--rid", rid],
                              capture_output=True, text=True)

    def test_operator_independently_signed_exact_bundle_is_accepted(self):
        verified = self.verify()
        self.assertEqual(verified.returncode, 0, verified.stderr)
        self.assertEqual(json.loads(verified.stdout)["candidate"], self.receipt["candidate"])

    def test_measurement_exports_canonical_closed_publish_manifest(self):
        measured = subprocess.run([sys.executable, SCRIPT, "manifest", "--publish", self.payload], capture_output=True)
        self.assertEqual(measured.returncode, 0, measured.stderr)
        self.assertEqual(measured.stdout, encode(self.closure))

    def test_even_operator_sealed_installed_projection_must_have_only_reviewed_paths(self):
        present = self.install / "licenses" / "extra-project-file"
        present.write_text("arbitrary projected source")
        (self.bundle / "install-closure.json").write_bytes(encode(self.manifest(self.install)))
        self.receipt["candidate"]["installSha256"] = digest((self.bundle / "install-closure.json").read_bytes())
        self.sign()
        refused = self.verify()
        self.assertNotEqual(refused.returncode, 0)
        self.assertIn("projection", refused.stderr)

    def test_changed_companion_apphost_mode_or_extra_entry_refuses_activation(self):
        self.asset.write_bytes(b"substituted data")
        self.assertNotEqual(self.verify().returncode, 0)
        self.asset.write_bytes(b"exact companion bytes\n")
        self.apphost.chmod(0o600)
        self.assertNotEqual(self.verify().returncode, 0)
        self.apphost.chmod(0o700)
        (self.payload / "extra").touch()
        self.assertNotEqual(self.verify().returncode, 0)

    @unittest.skipIf(os.name == "nt", "fixture requires Unix links")
    def test_even_identical_payload_bytes_cannot_be_reached_through_a_link(self):
        external = self.root / "untrusted"
        external.write_bytes(self.asset.read_bytes())
        self.asset.unlink()
        self.asset.symlink_to(external)
        self.assertNotEqual(self.verify().returncode, 0)
        self.asset.unlink()
        self.asset.hardlink_to(external)
        self.assertNotEqual(self.verify().returncode, 0)

    def test_changed_signed_bytes_and_bundle_supplied_key_are_refused(self):
        receipt = self.bundle / "receipt.json"
        original = receipt.read_bytes()
        receipt.write_bytes(original + b"\n")
        self.assertNotEqual(self.verify().returncode, 0)
        receipt.write_bytes(original)
        inside = self.bundle / "key.pem"
        inside.write_bytes(self.public.read_bytes())
        self.assertNotEqual(self.verify(public=inside).returncode, 0)

    def test_signed_nonterminal_or_unproved_live_or_schema_states_are_refused(self):
        for status in ("failed", "cancelled", "unavailable", "incomplete"):
            with self.subTest(status=status):
                self.receipt["disposition"] = status
                self.sign()
                self.assertNotEqual(self.verify().returncode, 0)
        self.receipt["disposition"] = "passed"
        self.receipt["checks"]["liveRegression"] = "unavailable"
        self.sign()
        self.assertNotEqual(self.verify().returncode, 0)
        self.receipt["checks"]["liveRegression"] = "notRequired"
        self.receipt["stateCompatibility"]["disposition"] = "migrationRequired"
        self.sign()
        self.assertNotEqual(self.verify().returncode, 0)

    def test_inspection_authenticates_staged_incompatible_closure_without_granting_activation(self):
        compatibility = self.receipt["stateCompatibility"]
        compatibility["candidate"] = json.loads(encode(compatibility["candidate"]))
        compatibility["candidate"]["families"]["core"]["version"] = 2
        compatibility["disposition"] = "migrationRequired"
        compatibility["reversibleConditions"] = []
        self.receipt["disposition"] = "unavailable"
        self.sign()
        args = [sys.executable, SCRIPT, "inspect", "--bundle", self.bundle,
                "--public-key", self.public, "--rid", "osx-arm64"]
        result = subprocess.run(args, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout), {"eligible": False, "receipt": self.receipt})
        self.assertNotEqual(self.verify().returncode, 0)
        self.asset.write_bytes(b"changed staged closure")
        self.assertNotEqual(subprocess.run(args, capture_output=True).returncode, 0)

    def test_signed_pass_without_historical_regression_or_wrong_target_is_refused(self):
        self.assertNotEqual(self.verify(rid="win-arm64").returncode, 0)
        self.receipt["checks"]["affectedRegression"] = "failed"
        self.sign()
        self.assertNotEqual(self.verify().returncode, 0)


    def trusted_repository(self):
        source = self.root / "independent-source"
        source.mkdir()

        def git(*arguments):
            return subprocess.check_output(["git", "-C", source, *arguments], text=True).strip()

        git("init", "-q")
        git("config", "user.name", "Qualification Fixture")
        git("config", "user.email", "qualification-fixture@example.invalid")
        base = source / "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema"
        base.mkdir(parents=True)
        (base / "GrimoireSchemaVersionChains.cs").write_text("\n".join(
            f"internal const int {family}SchemaVersion = 1;" for family in
            ("Core", "GuildCanonical", "CovenantCanonical", "CovenantAccelerator")))
        adapter = source / "src/RetroDownfall.Arcanum.Api/Intelligence/OllamaReasoningChatClient.cs"
        adapter.parent.mkdir(parents=True)

        def commit(message):
            git("add", ".")
            git("commit", "-qm", message)
            return {"commit": git("rev-parse", "HEAD"), "tree": git("rev-parse", "HEAD^{tree}")}

        adapter.write_text("old")
        affected = commit("affected fixture")
        adapter.write_text("fixed")
        candidate = commit("candidate fixture")
        scripts = source / "scripts"
        scripts.mkdir()
        verifier = scripts / "verify-shipping-publish.sh"
        verifier.write_text("""#!/bin/sh
test "$ARCANUM_PUBLISHED_REPAIR_CONFINEMENT" = '1' || exit 2
case "$5" in
  --expect-ollama-mismatch) test "$(cat "$4/RetroDownfall.Arcanum.Cli")" = affected ;;
  --install-layout) test "$(cat "$4/arcanum")" = 'exact repaired apphost bytes' ;;
  *) exit 2 ;;
esac
""")
        acceptance = commit("trusted acceptance fixture")
        verifier.write_text("exit 99\n")
        return source, affected, candidate, acceptance

    @unittest.skipUnless(sys.platform == "darwin", "the local verifier requires the macOS apphost sandbox")
    def test_qualification_runs_frozen_independent_verifier_against_the_signed_exact_inputs(self):
        source, affected, candidate, acceptance = self.trusted_repository()
        affected_payload = self.root / "affected"
        shutil.copytree(self.payload, affected_payload)
        (affected_payload / APPHOST).write_bytes(b"affected\n")
        affected_manifest = self.manifest(affected_payload)
        inputs = {"version": 1, "contract": "ollama-tool-continuation-v1", "rid": "osx-arm64",
                  "acceptanceCommit": acceptance["commit"], "acceptanceTree": acceptance["tree"],
                  "affected": dict(affected, buildOutcome="passed", bundleSha256=digest(encode(affected_manifest))),
                  "candidate": dict(candidate, buildOutcome="passed", bundleSha256=digest(encode(self.closure))),
                  "liveRegression": "notRequired"}
        witness = self.root / "input.json"
        signature = self.root / "input.sig"
        witness.write_bytes(encode(inputs))
        self.openssl("dgst", "-sha256", "-sign", self.key, "-out", signature, witness)
        output = self.root / "qualified"
        arguments = [sys.executable, SCRIPT, "qualify", "--repository", source, "--inputs", witness,
                     "--inputs-signature", signature, "--public-key", self.public, "--sign-key", self.key,
                     "--affected-publish", affected_payload, "--candidate-publish", self.payload, "--output", output]
        result = subprocess.run(arguments, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        verified = subprocess.run([sys.executable, SCRIPT, "verify", "--bundle", output, "--public-key", self.public,
                                   "--rid", "osx-arm64"], capture_output=True, text=True)
        self.assertEqual(verified.returncode, 0, verified.stderr)
        receipt = json.loads(verified.stdout)
        self.assertEqual(acceptance["commit"], receipt["acceptanceCommit"])
        self.assertEqual(affected["commit"], receipt["affected"]["commit"])
        self.assertEqual("contractMismatch", receipt["checks"]["affectedRegression"])
        self.assertEqual(self.apphost.read_bytes(), (output / "payload" / APPHOST).read_bytes())
        self.assertEqual(self.apphost.read_bytes(), (output / "install" / "arcanum").read_bytes())
        self.assertFalse((output / "install" / "arcanum.pdb").exists())
        self.assertEqual(digest((output / "install-closure.json").read_bytes()), receipt["candidate"]["installSha256"])

        (output / "payload" / self.asset.name).write_bytes(b"post-verification replacement")
        self.assertNotEqual(subprocess.run([sys.executable, SCRIPT, "verify", "--bundle", output,
                                           "--public-key", self.public, "--rid", "osx-arm64"],
                                          capture_output=True).returncode, 0)

        arguments[-1] = self.root / "unproved"
        witness.write_bytes(encode(dict(inputs, acceptanceTree="b" * 40)))
        self.assertNotEqual(subprocess.run(arguments, capture_output=True).returncode, 0)
        self.assertFalse(arguments[-1].exists())


@unittest.skipUnless(os.name == "posix", "uses the trusted shell gate")
class ExistingPublishGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.payload = self.root / "candidate"
        self.payload.mkdir()
        apphost = self.payload / APPHOST
        apphost.write_bytes(b"exact artifact\n")
        apphost.chmod(0o700)
        bin_path = self.root / "trusted-tools"
        bin_path.mkdir()
        for name, script in {
            "dotnet": """#!/bin/sh
case "$1" in
  build) exit 0 ;;
  test)
    if [ -n "$ARCANUM_PUBLISHED_SMOKE_RECEIPT" ]; then
      printf '%s\\n' 'published-session-smoke:v1' > "$ARCANUM_PUBLISHED_SMOKE_RECEIPT"
    elif [ "$FIXTURE_MISMATCH" = yes ]; then
      printf '%s\\n' 'published-reasoning-continuation:wire-mismatch:v1' > "$ARCANUM_PUBLISHED_REASONING_RECEIPT"
      exit 1
    elif [ "$FIXTURE_MISMATCH" = bootstrap-failure ]; then
      exit 1
    elif [ "$FIXTURE_MISMATCH" = skipped ]; then
      exit 0
    else
      printf '%s\\n' 'published-reasoning-continuation:v1' > "$ARCANUM_PUBLISHED_REASONING_RECEIPT"
    fi
    exit 0 ;;
  *) printf '%s\\n' "untrusted build selected: $1" >&2; exit 90 ;;
esac
""",
            "file": "#!/bin/sh\n echo 'Mach-O 64-bit executable arm64'\n",
            "otool": "#!/bin/sh\n echo 'Apple platform Mach-O'\n",
        }.items():
            fake = bin_path / name
            fake.write_text(script)
            fake.chmod(0o700)
        self.environment = dict(os.environ, PATH=str(bin_path) + os.pathsep + os.environ["PATH"])

    def inspect(self, *options, outcome="passing"):
        return subprocess.run(["bash", ROOT / "scripts/verify-shipping-publish.sh", "--rid", "osx-arm64",
                               "--inspect-publish-dir", self.payload, *options],
                              env=dict(self.environment, FIXTURE_MISMATCH=outcome), capture_output=True, text=True)

    def test_detached_ship_gate_inspects_exact_prebuilt_closure_without_building_candidate(self):
        result = self.inspect()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn("untrusted build selected", result.stderr)

    def test_missing_trusted_test_receipt_is_incomplete(self):
        result = self.inspect(outcome="skipped")
        self.assertEqual(result.returncode, 3, result.stderr)

    def test_historical_regression_requires_exact_mismatch_receipt_and_passing_control(self):
        affected = self.inspect("--expect-ollama-mismatch", outcome="yes")
        self.assertEqual(affected.returncode, 0, affected.stderr)
        self.assertNotEqual(self.inspect(outcome="yes").returncode, 0)
        self.assertNotEqual(self.inspect("--expect-ollama-mismatch", outcome="bootstrap-failure").returncode, 0)
        self.assertNotEqual(self.inspect("--expect-ollama-mismatch").returncode, 0)


if __name__ == "__main__":
    unittest.main()
