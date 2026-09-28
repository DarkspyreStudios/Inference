#!/usr/bin/env python3
"""Fetch only the pinned Apple C package into the prototype's ignored artifacts."""

import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import tarfile
import urllib.request


REVISION = "e868e60811aa0706feb2ccb33cfe7e27626287b7"
ARCHIVE_SHA256 = "354c0178acc43c5c62eb550aad3ddb552de10e2a7487680096fa0cf6a7a5ac0f"
ROOT = Path(__file__).resolve().parent / ".artifacts"
DESTINATION = ROOT / "upstream"
URL = f"https://codeload.github.com/apple/python-apple-fm-sdk/tar.gz/{REVISION}"


def main():
    marker = DESTINATION / "source.json"
    if DESTINATION.exists():
        if marker.exists() and json.loads(marker.read_text())["revision"] == REVISION:
            print(marker.read_text())
            return
        raise SystemExit(f"Incomplete or different dependency at {DESTINATION}; inspect it before retrying.")

    request = urllib.request.Request(URL, headers={"User-Agent": "DSA-59-prototype"})
    with urllib.request.urlopen(request, timeout=60) as response:
        archive = response.read()
    if hashlib.sha256(archive).hexdigest() != ARCHIVE_SHA256:
        raise SystemExit("The pinned archive digest changed; inspect the upstream artifact before proceeding.")

    prefix = f"python-apple-fm-sdk-{REVISION}"
    with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as package:
        selected = []
        for member in package.getmembers():
            parts = PurePosixPath(member.name).parts
            if not parts or parts[0] != prefix or ".." in parts:
                raise SystemExit("Unexpected archive path.")
            relative = PurePosixPath(*parts[1:])
            if str(relative) != "LICENSE" and "foundation-models-c" not in relative.parts[:1]:
                continue
            if member.isdir():
                continue
            if not member.isfile():
                raise SystemExit(f"Unsupported archive member: {member.name}")
            selected.append((member, relative))
        if not any(str(relative) == "foundation-models-c/Package.swift" for _, relative in selected):
            raise SystemExit("The pinned C package is missing from the archive.")
        for member, relative in selected:
            target = DESTINATION / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            with package.extractfile(member) as source:
                target.write_bytes(source.read())

    metadata = {"revision": REVISION, "url": URL, "archiveSha256": hashlib.sha256(archive).hexdigest()}
    marker.write_text(json.dumps(metadata, indent=2) + "\n")
    print(marker.read_text())


if __name__ == "__main__":
    main()
