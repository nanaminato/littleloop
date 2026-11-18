"""Repository toolchain smoke checks; all writes use synthetic temporary models."""
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LAUNCHER = ROOT / "tools" / "model.py"


def run(*args, cwd=None):
    return subprocess.run([sys.executable, str(LAUNCHER), *args],
                          cwd=cwd, text=True, encoding="utf-8", capture_output=True)


class ModelToolsTests(unittest.TestCase):
    def test_validate_from_another_directory_does_not_rewrite_model(self):
        model = ROOT / ".singularity" / "model.json"
        before = model.read_bytes()
        with tempfile.TemporaryDirectory() as cwd:
            result = run("validate", cwd=cwd)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue(json.loads(result.stdout)["valid"])
        self.assertEqual(before, model.read_bytes())

    def test_missing_submodule_is_actionable_and_does_not_download(self):
        with tempfile.TemporaryDirectory() as directory:
            clone = Path(directory)
            (clone / "tools").mkdir()
            shutil.copyfile(LAUNCHER, clone / "tools" / "model.py")
            result = subprocess.run([sys.executable, str(clone / "tools" / "model.py"), "validate"],
                                    text=True, encoding="utf-8", capture_output=True)
            self.assertEqual(2, result.returncode)
            self.assertIn("git submodule update --init --recursive", result.stderr)
            self.assertFalse((clone / "tools" / "singularity").exists())

    def test_transaction_dry_run_apply_readback_and_revision_conflict(self):
        with tempfile.TemporaryDirectory() as directory:
            project = Path(directory)
            init = run("--project", str(project), "init", "--name", "Synthetic")
            self.assertEqual(0, init.returncode, init.stderr)
            revision = json.loads(init.stdout)["revision"]
            operations = project / "operations.json"
            operations.write_text(json.dumps([{"action": "put", "collection": "references", "value": {
                "id": "REF-README", "kind": "project_file", "target": "README.md",
                "purpose": "Synthetic transaction smoke check", "tracking": "live"
            }}]), encoding="utf-8")
            (project / "README.md").write_text("Synthetic", encoding="utf-8")
            model = project / ".singularity" / "model.json"
            before = model.read_bytes()
            args = ("--project", str(project), "apply", str(operations), "--expected", revision)
            preview = run(*args, "--dry-run")
            self.assertEqual(0, preview.returncode, preview.stderr)
            self.assertEqual(before, model.read_bytes())
            applied = run(*args)
            self.assertEqual(0, applied.returncode, applied.stderr)
            self.assertNotEqual(revision, json.loads(applied.stdout)["revision"])
            get = run("--project", str(project), "get", "REF-README")
            self.assertEqual(0, get.returncode, get.stderr)
            self.assertEqual("REF-README", json.loads(get.stdout)["object"]["id"])
            stale = run(*args)
            self.assertNotEqual(0, stale.returncode)
            self.assertIn("Model changed", stale.stderr)

    def test_bounded_query_workflow(self):
        found = run("find", "SQLite", "--limit", "10")
        self.assertEqual(0, found.returncode, found.stderr)
        json.loads(found.stdout)
        self.assertIn("MOD-CHAT-COMMITMENTS", found.stdout)
        context = run("context", "MOD-CHAT-COMMITMENTS", "--summary",
                      "--max-nodes", "12", "--max-bytes", "8192")
        self.assertEqual(0, context.returncode, context.stderr)
        json.loads(context.stdout)
        self.assertLessEqual(len(context.stdout.encode("utf-8")), 8192)
        complete = run("get", "MOD-CHAT-COMMITMENTS")
        self.assertEqual(0, complete.returncode, complete.stderr)
        self.assertIn("body", json.loads(complete.stdout)["object"])

    def test_reference_guard(self):
        result = subprocess.run([sys.executable, str(ROOT / "tools" / "check_model.py")],
                                text=True, encoding="utf-8", capture_output=True)
        self.assertEqual(0, result.returncode, result.stderr)


if __name__ == "__main__":
    unittest.main()
