"""Fetch the pinned FP32 graphs and expose its initializer offsets for the probe."""

import argparse
import hashlib
import json
from pathlib import Path
import urllib.request

REVISION = "daa72c51243991dfcaf9f9137d2c573d8f7790c0"
BASE = f"https://huggingface.co/onnx-community/embeddinggemma-2-ONNX/resolve/{REVISION}/onnx/"
ARTIFACTS = {
    "model.onnx": (421421, "bc47de15f81208a5c99e5ab10f746d5e33b51ea228b7dc0bef9c133a94f1c1c3"),
    "model.onnx_data": (1084170240, "9fd452bfc6916e5a92f080f8dada35598ca4512b21c65897d4c5974d5f9630a2"),
}
VISION_ARTIFACTS = {
    "vision_encoder.onnx": (121253, "a3fb44f8b5b152cfee6afe16e2cd69e774b7e1d3b822d1db1190ddf7b08b575e"),
    "vision_encoder.onnx_data": (671026176, "ca8d28c99feb8129618f141978a27adae25090e0dadb68a30c235e191c01e37e"),
}


def fields(data):
    """Read protobuf wire fields without executing model code or installing tooling."""
    index = 0

    def varint():
        nonlocal index
        value, shift = 0, 0
        while True:
            byte = data[index]
            index += 1
            value |= (byte & 127) << shift
            if byte < 128:
                return value
            shift += 7

    while index < len(data):
        key = varint()
        wire = key & 7
        if wire == 0:
            value = varint()
        elif wire in (1, 2, 5):
            count = varint() if wire == 2 else (8 if wire == 1 else 4)
            value = data[index:index + count]
            index += count
        else:
            raise ValueError(f"Unsupported wire type {wire}")
        yield key >> 3, value


def main():
    """Verify downloads and write the exact graph's initializer metadata."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--vision", action="store_true", help="Also fetch the pinned FP32 image encoder")
    args = parser.parse_args()
    args.directory.mkdir(parents=True, exist_ok=True)
    artifacts = ARTIFACTS | (VISION_ARTIFACTS if args.vision else {})
    for name, (length, expected) in artifacts.items():
        path = args.directory / name
        if not path.exists():
            print(f"Downloading {name}: {length} bytes", flush=True)
            with urllib.request.urlopen(BASE + name) as response, path.open("wb") as output:
                while chunk := response.read(1024 * 1024):
                    output.write(chunk)
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if path.stat().st_size != length or digest != expected:
            raise ValueError(f"Artifact verification failed: {path}")
        print(f"Verified {name}", flush=True)
    graph = next(v for n, v in fields((args.directory / "model.onnx").read_bytes()) if n == 7)
    weights = {}
    for number, tensor in fields(graph):
        if number != 5:
            continue
        tensor_fields = list(fields(tensor))
        name = next(v.decode() for n, v in tensor_fields if n == 8)
        external = {}
        for n, entry in tensor_fields:
            if n == 13:
                pair = dict(fields(entry))
                external[pair[1].decode()] = pair[2].decode()
        weights[name] = {
            "shape": [v for n, v in tensor_fields if n == 1],
            "type": next(v for n, v in tensor_fields if n == 2),
            "external": external,
            "raw": list(next((v for n, v in tensor_fields if n == 9), b"")),
        }
    (args.directory / "weights.json").write_text(json.dumps(weights), encoding="utf-8")
    print(f"Recorded {len(weights)} initializers from {REVISION}")


if __name__ == "__main__":
    main()
