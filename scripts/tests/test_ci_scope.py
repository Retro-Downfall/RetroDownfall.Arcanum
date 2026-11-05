import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "ci_scope.py"
SPEC = importlib.util.spec_from_file_location("ci_scope", SCRIPT)
ci_scope = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ci_scope)


class CiScopeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.repo = Path(self.temporary.name)
        self.git("init", "-q")
        self.git("config", "user.name", "CI fixture")
        self.git("config", "user.email", "ci-fixture@example.invalid")
        self.git("config", "core.filemode", "true")
        for name in ("README.md", "AGENTS.md", "docs/Design.md", "src/Program.cs"):
            self.write(name, "baseline\n")
        self.base = self.commit()

    def git(self, *arguments):
        return subprocess.check_output(
            ["git", "-C", str(self.repo), *arguments], stderr=subprocess.PIPE
        ).decode().strip()

    def write(self, name, content):
        path = self.repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)

    def commit(self):
        self.git("add", "-A")
        self.git("commit", "-qm", "fixture")
        return self.git("rev-parse", "HEAD")

    def full(self, *, event="pull_request", base=None, head=None):
        return ci_scope.requires_full_qualification(
            self.repo, event, base or self.base, head or self.git("rev-parse", "HEAD")
        )

    def test_only_regular_authored_markdown_allows_documentation_qualification(self):
        self.write("README.md", "changed\n")
        self.write("AGENTS.md", "changed\n")
        self.write("docs/Design.md", "changed\n")
        self.write("docs/New guide.md", "new\n")
        self.commit()
        self.assertFalse(self.full())

    def test_document_deletion_still_requires_documentation_contracts_only(self):
        (self.repo / "docs/Design.md").unlink()
        self.commit()
        self.assertFalse(self.full())

    def test_every_non_allowlisted_path_requires_full_qualification(self):
        for name in (
            "src/Program.cs", "tests/Behavior.cs", "Directory.Build.props",
            "global.json", "NuGet.config", ".github/workflows/ci.yml",
            "scripts/ci_scope.py", "src/Native/runtimes/win-arm64/native/library.dll",
            "docs/fixture.json", "docs/nested/Guide.md", "scripts/README.md",
            "docs/Guide.MD", "Other.md", "docs/guide.md\nsource.cs",
        ):
            with self.subTest(name=name):
                self.git("reset", "--hard", self.base)
                self.write(name, "changed\n")
                self.commit()
                self.assertTrue(self.full())

    def test_rename_from_production_to_documentation_does_not_hide_deleted_code(self):
        self.git("mv", "src/Program.cs", "docs/Program.md")
        self.commit()
        self.assertTrue(self.full())

    def test_mixed_documentation_and_code_requires_full_qualification(self):
        self.write("docs/Design.md", "changed\n")
        self.write("src/Program.cs", "changed\n")
        self.commit()
        self.assertTrue(self.full())

    def test_document_symlink_requires_full_qualification(self):
        (self.repo / "docs/Design.md").unlink()
        (self.repo / "docs/Design.md").symlink_to("../src/Program.cs")
        self.commit()
        self.assertTrue(self.full())

    def test_executable_document_mode_requires_full_qualification(self):
        (self.repo / "docs/Design.md").chmod(0o755)
        self.commit()
        self.assertTrue(self.full())

    def test_manual_release_and_unknown_events_require_full_qualification(self):
        self.write("docs/Design.md", "changed\n")
        self.commit()
        for event in ("workflow_dispatch", "push", "release", "", "pull_request_target"):
            with self.subTest(event=event):
                self.assertTrue(self.full(event=event))

    def test_missing_noncommit_and_invalid_revisions_require_full_qualification(self):
        self.write("docs/Design.md", "changed\n")
        self.commit()
        blob = self.git("rev-parse", "HEAD:docs/Design.md")
        for revision in ("0" * 40, "HEAD", "--help", blob):
            with self.subTest(revision=revision):
                self.assertTrue(self.full(base=revision))
                self.assertTrue(self.full(head=revision))

    def test_empty_diff_requires_full_qualification(self):
        self.assertTrue(self.full())

    def test_unrelated_base_requires_full_qualification(self):
        self.write("src/Program.cs", "new base\n")
        unrelated = self.commit()
        self.git("checkout", "--detach", self.base)
        self.write("docs/Design.md", "docs branch\n")
        self.commit()
        self.assertTrue(self.full(base=unrelated))

    def test_cli_emits_only_the_github_boolean_output(self):
        self.write("docs/Design.md", "changed\n")
        pr_head = self.commit()
        self.git("checkout", "--detach", self.base)
        self.git("merge", "--no-ff", "-m", "tested merge", pr_head)
        head = self.git("rev-parse", "HEAD")
        completed = subprocess.run(
            [sys.executable, str(SCRIPT), "--repo", str(self.repo),
             "--event", "pull_request", "--base", self.base, "--head", head,
             "--pr-head", pr_head],
            capture_output=True, text=True, check=True,
        )
        self.assertEqual("full=false\n", completed.stdout)

    def test_cli_without_merge_identity_cannot_narrow_qualification(self):
        self.write("docs/Design.md", "changed\n")
        head = self.commit()
        completed = subprocess.run(
            [sys.executable, str(SCRIPT), "--repo", str(self.repo),
             "--event", "pull_request", "--base", self.base, "--head", head],
            capture_output=True, text=True, check=True,
        )
        self.assertEqual("full=true\n", completed.stdout)

    def test_cli_mismatched_checkout_or_merge_parents_requires_full_qualification(self):
        self.write("docs/Design.md", "changed\n")
        pr_head = self.commit()
        self.git("checkout", "--detach", self.base)
        self.git("merge", "--no-ff", "-m", "tested merge", pr_head)
        tested = self.git("rev-parse", "HEAD")
        for case in ("base", "head", "checkout"):
            with self.subTest(case=case):
                if case == "checkout":
                    self.git("checkout", "--detach", pr_head)
                completed = subprocess.run(
                    [sys.executable, str(SCRIPT), "--repo", str(self.repo),
                     "--event", "pull_request", "--head", tested,
                     "--base", pr_head if case == "base" else self.base,
                     "--pr-head", self.base if case == "head" else pr_head],
                    capture_output=True, text=True, check=True,
                )
                self.assertEqual("full=true\n", completed.stdout)


if __name__ == "__main__":
    unittest.main()
