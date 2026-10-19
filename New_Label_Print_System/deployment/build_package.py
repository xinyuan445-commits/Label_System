"""Allowlisted standalone package; run with backend's locked virtual environment."""
from __future__ import annotations

import argparse
import sysconfig
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--application-only", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    paths = ["backend/app", "backend/local_server.py", "backend/standalone_labelsystem.py",
             "frontend/dist", "connector/dist/blg-connector-windows.exe", "deployment"]
    for name in paths:
        if not (root / name).exists():
            raise SystemExit("Missing build input: " + name)
    output = Path(args.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    with ZipFile(output, "w", ZIP_DEFLATED, compresslevel=2) as archive:
        for name in paths:
            source = root / name
            for file in ([source] if source.is_file() else source.rglob("*")):
                if file.is_file() and "__pycache__" not in file.parts:
                    archive.write(file, file.relative_to(root).as_posix())
        if not args.application_only:
            libraries = Path(sysconfig.get_path("purelib"))
            for file in libraries.rglob("*"):
                if file.is_file() and "__pycache__" not in file.parts:
                    archive.write(file, "site-packages/" + file.relative_to(libraries).as_posix())
    print("Standalone package bytes:", output.stat().st_size)


if __name__ == "__main__":
    main()
