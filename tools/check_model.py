"""Validate the project model and its declared local file references."""
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def command(*args):
    result = subprocess.run(
        [sys.executable, str(ROOT / "tools" / "model.py"), *args],
        capture_output=True, text=True, encoding="utf-8",
    )
    if result.returncode:
        print(result.stderr, file=sys.stderr, end="")
        raise SystemExit(result.returncode)
    return json.loads(result.stdout)


def main():
    validation = command("validate")
    references = command("check-refs")
    bad = [r for r in references if r["status"] not in ("exists", "matched", "unchecked")]
    print(json.dumps({"validation": validation, "references": references}, ensure_ascii=False, indent=2))
    if bad:
        print("Project model contains missing, changed, or unsafe local references.", file=sys.stderr)
        return 1
    # 'exists' checks the path only, not its meaning or the test claims.
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
