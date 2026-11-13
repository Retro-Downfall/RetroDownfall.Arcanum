"""Authenticated .NET 11 Darwin GC archive staging and refusal controls."""
import hashlib
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/prepare_macos_nativeaot_gc.py"
PACK_ROOT = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages")))
ARCHIVE = PACK_ROOT / "microsoft.netcore.app.runtime.nativeaot.osx-arm64/11.0.0/runtimes/osx-arm64/native/libRuntime.WorkstationGC.a"
SOURCE_SHA256 = "c570ce37acd42e93d9eca3ec724470abf9e27d96a4d7215f9f0afad3e22db14f"
OUTPUT_SHA256 = "6cecb3c68eb4f98c1cbee073cbf7346e0e7b8033b2100e2f61b5690af13813fa"


class MacOsNativeAotGcTests(unittest.TestCase):
    def run_helper(self, source, output, version="11.0.0"):
        return subprocess.run([sys.executable, str(SCRIPT), "--package-version", version,
                               "--input", str(source), "--output", str(output)],
                              text=True, capture_output=True, timeout=20)

    def assert_refused_preserving_output(self, source_bytes=None, version="11.0.0"):
        with tempfile.TemporaryDirectory() as directory:
            source, output = Path(directory) / "source.a", Path(directory) / "output.a"
            if source_bytes is not None:
                source.write_bytes(source_bytes)
            output.write_bytes(b"existing output must survive refusal")
            result = self.run_helper(source, output, version)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("refused", result.stderr.lower())
            self.assertEqual(output.read_bytes(), b"existing output must survive refusal")
            self.assertEqual(sorted(p.name for p in Path(directory).iterdir()),
                             ["output.a"] if source_bytes is None else ["output.a", "source.a"])

    def test_missing_input_is_refused_atomically(self):
        self.assert_refused_preserving_output()

    def test_wrong_input_is_refused_atomically(self):
        self.assert_refused_preserving_output(b"not the audited archive")

    def test_other_runtime_pack_version_is_refused(self):
        self.assert_refused_preserving_output(b"not the audited archive", "10.0.14")

    @unittest.skipUnless(ARCHIVE.is_file(), "Requires the official portable 11.0.0 macOS pack")
    def test_tampered_timestamp_or_payload_is_refused_atomically(self):
        for offset in (2951697, 100):
            with self.subTest(offset=offset):
                data = bytearray(ARCHIVE.read_bytes())
                data[offset] ^= 1
                self.assert_refused_preserving_output(data)

    @unittest.skipUnless(ARCHIVE.is_file(), "Requires the official portable 11.0.0 macOS pack")
    def test_exact_transform_is_deterministic_and_preserves_the_source_and_every_other_byte(self):
        original = ARCHIVE.read_bytes()
        self.assertEqual(hashlib.sha256(original).hexdigest(), SOURCE_SHA256)
        with tempfile.TemporaryDirectory() as directory:
            outputs = []
            for name in ("first.a", "second.a"):
                output = Path(directory) / name
                output.write_bytes(b"old destination")
                result = self.run_helper(ARCHIVE, output)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                outputs.append(output.read_bytes())
            self.assertEqual(outputs[0], outputs[1])
            self.assertEqual(hashlib.sha256(outputs[0]).hexdigest(), OUTPUT_SHA256)
            self.assertEqual([i for i, (a, b) in enumerate(zip(original, outputs[0])) if a != b], [2951697])
            self.assertEqual(len(original), len(outputs[0]))
            self.assertEqual(outputs[0][2951688:2951700], b"1793326189  ")
            self.assertEqual(ARCHIVE.read_bytes(), original)

    @unittest.skipUnless(ARCHIVE.is_file(), "Requires the official portable 11.0.0 macOS pack")
    def test_in_place_mutation_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source.a"
            original = ARCHIVE.read_bytes()
            source.write_bytes(original)
            result = self.run_helper(source, source)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(source.read_bytes(), original)

    @unittest.skipUnless(sys.platform == "darwin" and ARCHIVE.is_file(), "Requires macOS and the official portable 11.0.0 pack")
    def test_msbuild_staging_normalizes_the_verified_dotnet11_gc_archive(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            project = ET.Element("Project")
            properties = ET.SubElement(project, "PropertyGroup")
            for name, value in {"RuntimeIdentifier": "osx-arm64", "PublishAot": "true",
                                "NetCoreRoot": str(root / "empty-sdk") + "/",
                                "BundledNETCoreAppPackageVersion": "11.0.0",
                                "IlcFrameworkNativePath": str(ARCHIVE.parent) + "/",
                                "NativeIntermediateOutputPath": str(root / "native") + "/"}.items():
                ET.SubElement(properties, name).text = value
            ET.SubElement(project, "Import", Project=str(ROOT / "Directory.Build.targets"))
            ET.SubElement(project, "Target", Name="SetupProperties")
            path = root / "Stage.proj"
            ET.ElementTree(project).write(path)
            result = subprocess.run(["dotnet", "msbuild", str(path), "-nologo",
                                     "-t:PrepareArcanumMacOsCryptoArchive", "-nodeReuse:false"],
                                    text=True, capture_output=True, timeout=60, cwd=ROOT)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            staged = root / "native/arcanum-portable-native/libRuntime.WorkstationGC.a"
            self.assertEqual(hashlib.sha256(staged.read_bytes()).hexdigest(), OUTPUT_SHA256)
            self.assertEqual(hashlib.sha256(ARCHIVE.read_bytes()).hexdigest(), SOURCE_SHA256)


if __name__ == "__main__":
    unittest.main()
