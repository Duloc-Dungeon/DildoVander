#!/usr/bin/env python3
"""Pack a local validation artifact with explicit local provenance metadata."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
PACKAGE_SCRIPT = ROOT / "scripts/ci/package.py"


def local_environment(base: dict[str, str], event_path: Path) -> dict[str, str]:
    """Provide unmistakable local metadata, ignoring inherited Actions values."""
    env = {key: value for key, value in base.items() if not key.startswith("GITHUB_")}
    env.update({
        "GITHUB_EVENT_PATH": str(event_path),
        "GITHUB_EVENT_NAME": "local_preflight",
        "GITHUB_REPOSITORY": "local/preflight",
        "GITHUB_RUN_ID": "0",
        "GITHUB_RUN_ATTEMPT": "0",
    })
    return env


def pack(source: Path, destination: Path) -> None:
    with tempfile.TemporaryDirectory(prefix="dildovander-local-package-") as temporary:
        event_path = Path(temporary) / "event.json"
        event_path.write_text(json.dumps({}), encoding="utf-8")
        subprocess.run(
            [sys.executable, str(PACKAGE_SCRIPT), "pack", str(source), str(destination)],
            cwd=ROOT,
            env=local_environment(dict(os.environ), event_path),
            check=True,
        )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    pack(args.source, args.destination)


if __name__ == "__main__":
    main()
