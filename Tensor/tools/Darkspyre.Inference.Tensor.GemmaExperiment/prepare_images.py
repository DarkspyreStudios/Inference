"""Create deterministic RGB fixtures using the pinned Gemma4 PIL preprocessing recipe."""

import hashlib
import json
import math
from pathlib import Path
import sys

import numpy as np
from PIL import Image, __version__ as pillow_version

REFERENCE = "https://github.com/huggingface/transformers/blob/e880ddc57baa4e642c357e7c0a8e77b8d7f1b3f0/src/transformers/models/gemma4/image_processing_pil_gemma4.py"


def main():
    """Write reference patches, position IDs and input images without a model runtime."""
    root = Path(sys.argv[1])
    root.mkdir(parents=True, exist_ok=True)
    cases = []
    # Aligned square isolates patch order; wide/tall images exercise down/up-sampling.
    for name, width, height in [("square", 768, 768), ("landscape", 1200, 600), ("portrait", 201, 401)]:
        y, x = np.indices((height, width))
        rgb = np.stack([(x * 255 // max(1, width - 1)),
                        (y * 255 // max(1, height - 1)),
                        (((x // 17 + y // 23) % 2) * 255)], axis=-1).astype(np.uint8)
        image = Image.fromarray(rgb)
        image.save(root / f"{name}.png")
        factor = math.sqrt(2520 * 16**2 / (height * width))
        target_h = math.floor(factor * height / 48) * 48
        target_w = math.floor(factor * width / 48) * 48
        if (target_w, target_h) != image.size:
            image = image.resize((target_w, target_h), Image.Resampling.BICUBIC)
        # The PIL backend rescales in float64, then returns float32.
        chw = (np.asarray(image).astype(np.float64) / 255).astype(np.float32).transpose(2, 0, 1)
        ph, pw = target_h // 16, target_w // 16
        patches = chw.reshape(3, ph, 16, pw, 16).transpose(1, 3, 2, 4, 0).reshape(ph * pw, 768)
        gx, gy = np.meshgrid(np.arange(pw), np.arange(ph), indexing="xy")
        positions = np.stack([gx, gy], axis=-1).reshape(-1, 2)
        patches = np.pad(patches, [(0, 2520 - ph * pw), (0, 0)])
        positions = np.pad(positions, [(0, 2520 - ph * pw), (0, 0)], constant_values=-1)
        patches.astype("<f4").tofile(root / f"{name}.f32")
        positions.astype("<i8").tofile(root / f"{name}.i64")
        cases.append({"name": name, "width": target_w, "height": target_h, "softTokens": ph * pw // 9,
                      "sha256": {suffix: hashlib.sha256((root / f"{name}.{suffix}").read_bytes()).hexdigest()
                                 for suffix in ("png", "f32", "i64")}})
    (root / "images.json").write_text(json.dumps({"reference": REFERENCE, "pillow": pillow_version,
                                                  "numpy": np.__version__, "cases": cases}, indent=2) + "\n")
    print(json.dumps(cases), flush=True)


if __name__ == "__main__":
    main()
