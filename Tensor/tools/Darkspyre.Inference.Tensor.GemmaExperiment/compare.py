"""Measure isolated ONNX providers against the same ONNX CPU outputs."""

import json
import math
from pathlib import Path
import re
import statistics
import subprocess
import sys


def difference(vector, reference):
    """Return component error and cosine similarity for two embedding vectors."""
    if len(vector) != 768 or len(reference) != 768:
        raise ValueError("Expected 768-component vectors")
    if not all(math.isfinite(value) for value in vector + reference):
        raise ValueError("Non-finite embedding")
    norm = math.sqrt(sum(value * value for value in vector))
    reference_norm = math.sqrt(sum(value * value for value in reference))
    return {
        "maxAbs": max(abs(a - b) for a, b in zip(vector, reference)),
        "cosine": sum(a * b for a, b in zip(vector, reference)) / (norm * reference_norm),
        "norm": norm,
    }


def main():
    """Run each requested provider separately at 12, 128 and 512 tokens."""
    if len(sys.argv) < 4:
        raise SystemExit("Usage: compare.py <prepared-directory> <output-root> <provider> [...]")
    workspace = Path(__file__).resolve().parents[3]
    model = Path(sys.argv[1]).resolve()
    output = Path(sys.argv[2]).resolve()
    executable = Path(__file__).resolve().parent / "bin/Release/net11.0/Darkspyre.Inference.Tensor.GemmaExperiment.dll"
    failures = []
    for provider in sys.argv[3:]:
        for length in (12, 128, 512):
            case = output / f"{provider}-{length}"
            case.mkdir(parents=True, exist_ok=True)
            command = ["/usr/bin/time", "-l", "bash", "../agent-ds/scripts/workspace-env.sh",
                       "dotnet", str(executable), provider, str(model), str(case), str(length)]
            with (case / "run.log").open("w") as log:
                process = subprocess.run(command, cwd=workspace, stdout=log, stderr=subprocess.STDOUT)
            if process.returncode:
                raise SystemExit(f"{provider}/{length}: exit {process.returncode}; see {case / 'run.log'}")
            result = json.loads((case / "result.json").read_text())
            cpu = json.loads((output / f"cpu-{length}/result.json").read_text())
            if result["inputIds"] != cpu["inputIds"]:
                raise ValueError("Reference and candidate inputs differ")
            vectors = result["vectors"]
            comparisons = [difference(vector, cpu["vector"]) for vector in vectors]
            log = (case / "run.log").read_text()
            rss = re.search(r"(\d+)\s+maximum resident set size", log)
            summary = {
                "provider": provider, "tokens": length, "loadMs": result["loadMs"],
                "firstMs": result["timesMs"][0], "warmMs": result["timesMs"][1:],
                "warmMedianMs": statistics.median(result["timesMs"][1:]),
                "peakRssMiB": int(rss.group(1)) / 1024**2 if rss else None,
                "cpuMaxAbs": max(item["maxAbs"] for item in comparisons),
                "cpuMinCosine": min(item["cosine"] for item in comparisons),
                "maxNormError": max(abs(item["norm"] - 1) for item in comparisons),
                "repeatMaxAbs": max(difference(vector, vectors[0])["maxAbs"] for vector in vectors),
                "command": command,
            }
            # These bounds measure execution parity, not retrieval quality.
            summary["parityPassed"] = (summary["cpuMaxAbs"] <= 1e-4
                                       and summary["cpuMinCosine"] >= 0.999999
                                       and summary["maxNormError"] <= 1e-6)
            (case / "measurement.json").write_text(json.dumps(summary, indent=2) + "\n")
            print(json.dumps({key: value for key, value in summary.items() if key != "command"}), flush=True)
            if not summary["parityPassed"]:
                failures.append(f"{provider}/{length}")
    if failures:
        raise SystemExit("Parity failed: " + ", ".join(failures))


if __name__ == "__main__":
    main()
