#!/usr/bin/env python3
"""Build a separately identified prototype dependency; leave the baseline untouched."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess


ROOT = Path(__file__).resolve().parent
SOURCE = ROOT / ".artifacts/upstream/foundation-models-c"
INPUTS = [ROOT / "native/bridge-lifecycle.patch",
          ROOT.parent / "AppleFoundationModelsHost/Sources/PendingToolCall.swift",
          ROOT / "native/PendingToolCallTests.swift", ROOT / "native/Package.swift"]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--build", action="store_true")
    parser.add_argument("--test", action="store_true")
    args = parser.parse_args()
    if not SOURCE.is_dir():
        raise SystemExit("Run prepare-native.py first.")
    if shutil.which("patch") is None:
        raise SystemExit("Required tool missing: install the patch command before continuing.")

    digest = hashlib.sha256()
    for item in INPUTS:
        digest.update(item.name.encode() + b"\0" + item.read_bytes())
    revision = digest.hexdigest()
    destination = ROOT / ".artifacts/patched" / revision[:16] / "foundation-models-c"
    marker = destination.parent / "source.json"
    if not destination.exists():
        shutil.copytree(SOURCE, destination, ignore=shutil.ignore_patterns(".build", ".swiftpm"))
        subprocess.run(["patch", "--batch", "--forward", "-p1", "-d", str(destination),
                        "-i", str(INPUTS[0])], check=True)
        shutil.copy2(INPUTS[1], destination / "Sources/FoundationModelsCBindings/PendingToolCall.swift")
        shutil.copy2(INPUTS[2], destination / "Tests/FoundationModelsCBindingsTests/PendingToolCallTests.swift")
        metadata = json.loads((SOURCE.parent / "source.json").read_text())
        metadata.update(prototypePatchSha256=revision, packagePath=str(destination),
                        libraryPath=str(destination / ".build/release/libFoundationModels.dylib"))
        marker.write_text(json.dumps(metadata, indent=2) + "\n")
    if not marker.exists() or json.loads(marker.read_text())["prototypePatchSha256"] != revision:
        raise SystemExit(f"Incomplete patched tree at {destination}; inspect it before retrying.")

    if args.build:
        subprocess.run(["xcrun", "swift", "build", "--package-path", str(destination),
                        "--configuration", "release", "--product", "FoundationModels"], check=True)
    if args.test:
        # Test exactly the same helper source without building Apple's unrelated products.
        tests = destination.parent / "lifecycle-tests"
        if not tests.exists():
            (tests / "Sources").mkdir(parents=True)
            (tests / "Tests").mkdir()
            shutil.copy2(INPUTS[3], tests / "Package.swift")
            shutil.copy2(INPUTS[1], tests / "Sources/PendingToolCall.swift")
            shutil.copy2(INPUTS[2], tests / "Tests/PendingToolCallTests.swift")
        subprocess.run(["xcrun", "swift", "test", "--package-path", str(tests)], check=True)
    (ROOT / ".artifacts/patched.json").write_text(marker.read_text())
    print(marker.read_text())


if __name__ == "__main__":
    main()
