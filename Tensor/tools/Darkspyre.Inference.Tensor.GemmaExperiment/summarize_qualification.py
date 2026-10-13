"""Compare captured native-platform text and image qualification outputs."""

import json
from pathlib import Path
import statistics
import sys

import numpy as np
from compare import difference


def vector_summary(vectors, reference):
    """Check every normalized embedding against the same FP32 CPU reference."""
    checks = [difference(v, reference) for v in vectors]
    result = {"calls": len(vectors), "maxAbs": max(x["maxAbs"] for x in checks),
              "minCosine": min(x["cosine"] for x in checks),
              "maxNormError": max(abs(x["norm"] - 1) for x in checks),
              "repeatMaxAbs": max(difference(v, vectors[0])["maxAbs"] for v in vectors)}
    result["parityPassed"] = result["maxAbs"] <= 1e-4 and result["minCosine"] >= .999999 and result["maxNormError"] <= 1e-6
    return result


def feature_summary(case, reference):
    """Report unnormalized vision-feature error separately from pooled-vector bounds."""
    baseline = np.fromfile(reference / "features-0.f32", dtype="<f4").astype(float)
    errors, relatives, cosines = [], [], []
    for path in sorted(case.glob("features-*.f32")):
        actual = np.fromfile(path, dtype="<f4").astype(float)
        if actual.shape != baseline.shape or not np.isfinite(actual).all():
            raise ValueError(f"Invalid vision output: {path}")
        errors.append(float(np.max(np.abs(actual - baseline))))
        relatives.append(float(np.linalg.norm(actual - baseline) / np.linalg.norm(baseline)))
        a, b = actual.reshape(-1, 512), baseline.reshape(-1, 512)
        cosines.append(float(np.min(np.sum(a * b, axis=1) / np.linalg.norm(a, axis=1) / np.linalg.norm(b, axis=1))))
    return {"maxAbs": max(errors), "relativeL2": max(relatives), "minTokenCosine": min(cosines)}


def profile_counts(path):
    """Count distinct node events per provider, preserving incomplete-profile errors."""
    try:
        events = json.loads(path.read_text())
    except (ValueError, OSError) as error:
        return {"path": str(path), "error": str(error)}
    providers = {}
    for event in events:
        provider = event.get("args", {}).get("provider")
        if provider:
            providers.setdefault(provider, set()).add(event["name"])
    return {"path": str(path), "distinctNodeEvents": {key: len(value) for key, value in providers.items()}}


def main():
    """Summarize measured outputs without treating missing or crashed runs as passes."""
    images_only = sys.argv[1] == "images"
    root = Path(sys.argv[2] if images_only else sys.argv[1])
    text_reference = None if images_only else Path(sys.argv[2])
    image_reference = (Path(sys.argv[3]) if images_only else root) / "mac/images/cpu"
    results = []
    for platform in ("mac", "linux", "windows"):
        for path in sorted((root / platform).rglob("result.json")):
            run = json.loads(path.read_text())
            case = path.parent
            area = case.relative_to(root / platform).parts[0]
            if area == "images":
                reference_case = image_reference / run["name"]
                baseline = json.loads((reference_case / "result.json").read_text())
                if baseline["inputIds"] != run["inputIds"]:
                    raise ValueError("Image token inputs do not match")
                reference = baseline["vectors"][0]
            else:
                length = len(run["inputIds"])
                baseline = json.loads((text_reference / f"cpu-{length}/result.json").read_text())
                if baseline["inputIds"] != run["inputIds"]:
                    raise ValueError("Text inputs do not match")
                reference = baseline["vector"]
            result = {"platform": platform, "path": str(case.relative_to(root)), "provider": run["provider"],
                      **vector_summary(run["vectors"], reference),
                      "firstMs": run["timesMs"][0], "warmMedianMs": statistics.median(run["timesMs"][1:])}
            if area == "images":
                result["sharedPreprocessing"] = run.get("sharedPreprocessing", False)
                result["vision"] = feature_summary(case, reference_case)
                result["visionWarmMedianMs"] = statistics.median(run["visionTimesMs"][1:])
            execution_path = (case.parent if area == "images" else case) / "execution.json"
            result["execution"] = json.loads(execution_path.read_text()) if execution_path.exists() else None
            results.append(result)
    profiles = [profile_counts(p) for p in root.rglob("*.json")
                if p.name.startswith(("ort_", "vision_", "text_", "onnxruntime_profile__"))]
    expected = []
    for platform in ("mac", "linux", "windows"):
        if not images_only:
            expected.extend(f"{platform}/text/default-memory-plugin-{length}" for length in (12, 128, 512))
        for provider in ("cpu", "plugin-default-memory"):
            expected.extend(f"{platform}/images/{provider}/{name}" for name in ("square", "landscape", "portrait"))
        if not images_only and platform != "mac":  # Mac CPU text is the previously captured reference.
            expected.extend(f"{platform}/text/cpu-{length}" for length in (12, 128, 512))
    by_path = {result["path"]: result for result in results}
    missing = [path for path in expected if path not in by_path]
    qualified = [by_path[path] for path in expected if path in by_path]
    failed = [result["path"] for result in qualified
              if not result["parityPassed"] or result["calls"] != 6
              or (images_only and not result.get("sharedPreprocessing"))
              or (result["execution"] or {}).get("exitCode") != 0
              or (result["execution"] or {}).get("invalidForPluginQualification")]
    preprocessing = {platform: json.loads((root / platform / "preprocessing/preprocessing.json").read_text())
                     for platform in (("mac", "linux", "windows") if images_only else ("mac",))}
    preprocessing_passed = all({case["name"] for case in cases} == {"square", "landscape", "portrait"}
                              and all(case["parityPassed"] for case in cases) for cases in preprocessing.values())
    outcome = {"runtimePassed": not missing and not failed, "missingCases": missing, "failedCases": failed,
               "runtimeCalls": sum(result["calls"] for result in qualified),
               "preprocessingPassed": preprocessing_passed}
    summary = {"environment": json.loads((root / "environment.json").read_text()), "results": results,
               "profiles": profiles, "outcome": outcome,
               "textReference": str(text_reference) if text_reference else None, "imageReference": str(image_reference),
               "executions": [{"path": str(p.parent.relative_to(root)), **json.loads(p.read_text())}
                              for p in root.rglob("execution.json")],
               "preprocessing": preprocessing,
               "crashes": [] if images_only else json.loads((root / "mac/crashes.json").read_text())}
    (root / "qualification-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    for result in results:
        print(json.dumps(result))
    print("Checked", sum(x["calls"] for x in results), "embeddings; numerical failures:", sum(not x["parityPassed"] for x in results))
    print(json.dumps(outcome))
    # Failed diagnostic controls remain in the report but do not replace the explicit run matrix.
    if not outcome["runtimePassed"] or not preprocessing_passed:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
