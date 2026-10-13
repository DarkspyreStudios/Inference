"""Compare production-library outputs with CPU and frozen-image references."""

import argparse
import json
from pathlib import Path
from compare import difference


def parity(actual, expected):
    """Apply the experiment's existing normalized embedding parity bounds."""
    result = difference(actual, expected)
    result['passed'] = result['maxAbs'] <= 1e-4 and result['cosine'] >= .999999 and abs(result['norm'] - 1) <= 1e-6
    return result


def main():
    """Require every platform result and retain per-case numerical evidence."""
    parser = argparse.ArgumentParser()
    parser.add_argument('root', type=Path)
    parser.add_argument('image_reference', type=Path)
    args = parser.parse_args()
    baseline = json.loads((args.root / 'mac-cpu/result.json').read_text())
    expected = {item['name']: item for item in baseline['results']}
    images = {name: json.loads((args.image_reference / name / 'result.json').read_text())['vectors'][0]
              for name in ('square', 'portrait', 'landscape')}
    reports, missing = [], []
    for platform in ('mac', 'linux', 'windows'):
        for backend in ('cpu', 'webgpu'):
            name = platform + '-' + backend
            path = args.root / name / 'result.json'
            if not path.exists():
                missing.append(name)
                continue
            run = json.loads(path.read_text())
            results = {item['name']: item for item in run['results']}
            if results.keys() != expected.keys() or len(results) != len(run['results']):
                raise ValueError('Missing or duplicate input cases: ' + name)
            cases = []
            for key, item in results.items():
                checks = parity(item['vector'], expected[key]['vector'])
                checks['name'] = key
                checks['connectMaxAbs'] = item['maxError']
                checks['passed'] &= item['maxError'] <= 1e-5 and item['ModelFingerprint'] == expected[key]['ModelFingerprint']
                if key in images:
                    checks['frozenImageReference'] = parity(item['vector'], images[key])
                    checks['passed'] &= checks['frozenImageReference']['passed']
                cases.append(checks)
            # This fixed related/unrelated fixture qualifies the agreed threshold, not general relevance.
            query = results['christmas-query']['vector']
            scores = {key: sum(a * b for a, b in zip(query, results[key]['vector']))
                      for key in ('lifetime', 'christmas', 'database', 'code')}
            threshold_passed = scores['christmas'] >= .60 and all(
                value < .60 for key, value in scores.items() if key != 'christmas')
            flags = all(run[key] is True for key in
                        ('tokenizerParity', 'retrievalPassed', 'cancellationPassed', 'releasedPack'))
            reports.append(dict(name=name, loadMs=run['loadMs'], cases=cases,
                                recall=dict(threshold=.60, scores=scores, passed=threshold_passed),
                                timings=[{key: item[key] for key in ('name', 'directMs', 'connectMs')}
                                         for item in run['results']],
                                passed=flags and threshold_passed and all(case['passed'] for case in cases)))
    outcome = dict(passed=not missing and all(run['passed'] for run in reports), missing=missing)
    report = dict(outcome=outcome, runs=reports)
    (args.root / 'engine-summary.json').write_text(json.dumps(report, indent=2) + '\n')
    for run in reports:
        print(run['name'], 'passed=' + str(run['passed']),
              'maxAbs=' + str(max(case['maxAbs'] for case in run['cases'])), 'recall=' + str(run['recall']))
    print(json.dumps(outcome))
    if not outcome['passed']:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
