"""LittleLoop's repository-local entry point to the pinned Singularity CLI.

No network or automatic dependency update occurs when this command runs.
"""
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CLI = ROOT / "tools" / "singularity" / "tools" / "singularity.py"


def main():
    if sys.version_info < (3, 11):
        print("Project-model tools require Python 3.11 or newer.", file=sys.stderr)
        return 2
    if not CLI.is_file():
        print(
            "The pinned Singularity submodule is missing. From the LittleLoop root run:\n"
            "  git submodule update --init --recursive\n"
            "This setup step downloads the recorded revision; ordinary model commands are offline.",
            file=sys.stderr,
        )
        return 2
    return subprocess.call([sys.executable, str(CLI), "--project", str(ROOT), *sys.argv[1:]])


if __name__ == "__main__":
    raise SystemExit(main())
