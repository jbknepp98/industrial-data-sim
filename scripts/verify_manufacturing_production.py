#!/usr/bin/env python3
"""Explicit Test-only manufacturing acceptance with unique tags and no replay.

All output is bounded CLI diagnostics. Credentials are inherited from the local
connection environment and are never written into the model or report.
"""
import argparse
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import subprocess
import uuid


def verify(dotnet):
    if os.environ.get('TIMEBASE_DATASET') != 'Test':
        raise ValueError('manufacturing.dataset_guard: Set TIMEBASE_DATASET=Test before explicitly requesting this write.')
    root = Path(__file__).resolve().parents[1]
    identity = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S') + '-' + uuid.uuid4().hex[:6]
    folder = root / '.tools' / ('manufacturing-smoke-' + identity)
    folder.mkdir(mode=0o700)
    model = json.loads((root / 'examples/manufacturing-simulation.json').read_text())
    names = {tag['name']: 'Sim.Manufacturing.' + identity + '.' + tag['name'].split('.', 1)[1]
             for tag in model['session']['outputTags']}
    def rename(value):
        if isinstance(value, dict):
            return {key: names.get(child, child) if key in ('tag', 'previous', 'name') and isinstance(child, str) else rename(child)
                    for key, child in value.items()}
        if isinstance(value, list):
            return [rename(child) for child in value]
        return value
    model = rename(model)
    start = datetime.now(timezone.utc).replace(microsecond=0) - timedelta(minutes=5)
    utc = lambda value: value.isoformat().replace('+00:00', 'Z')
    model['session'].update(sessionId='manufacturing-' + identity, dataset='Test',
                            connectionProfile=os.environ['TIMEBASE_PROFILE'], startUtc=utc(start), endUtc=utc(start + timedelta(minutes=2)))
    path = folder / 'model.json'
    path.write_text(json.dumps(model, indent=2))
    command = [dotnet, str(root / 'src/IndustrialDataSim.Cli/bin/Release/net10.0/IndustrialDataSim.Cli.dll')]
    sequence = 0
    def invoke(arguments):
        nonlocal sequence
        sequence += 1
        result = subprocess.run(command + arguments, capture_output=True, text=True, timeout=180)
        (folder / f'{sequence:02d}-response.json').write_text(result.stdout)
        parsed = json.loads(result.stdout)
        if result.returncode or not parsed.get('valid'):
            raise ValueError('manufacturing.command_failed: Inspect retained diagnostics and state. No mutation or publish was retried.')
        return parsed
    preview = invoke(['dry-run', str(path)])
    invoke(['explain-process', str(path)])
    db = str(folder / 'state.db')
    invoke(['production', 'start', db, str(path)])
    result = invoke(['production', 'run', db, '20'])['result']
    if result['stopReason'] != 'Completed':
        raise ValueError('manufacturing.incomplete: Preserve state and inspect session errors; never replay uncertain delivery.')
    rows = invoke(['production', 'observations', db, model['session']['sessionId']])['result']['observations']
    indicated = any(row['nonNullCurrentTags'] == len(names) and row['changedTags'] > 0 for row in rows)
    report = {'sessionId': model['session']['sessionId'], 'folder': str(folder.relative_to(root)),
              'tags': list(names.values()), 'previewPoints': preview['pointCount'],
              'publishedBatches': result['publishedBatches'], 'arrivalIndicatorsPresent': indicated,
              'userReview': 'NotReviewed', 'notice': 'Arrival indicators are not per-point acceptance receipts.'}
    (folder / 'report.json').write_text(json.dumps(report, indent=2))
    print(json.dumps(report, indent=2))
    if not indicated:
        raise ValueError('manufacturing.arrival_unconfirmed: Inspect retained observations and current values. Missing evidence does not authorize replay.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--write-test-dataset', action='store_true', required=True)
    args = parser.parse_args()
    try:
        verify(args.dotnet)
    except ValueError as error:
        parser.exit(1, str(error) + '\n' if str(error).startswith('manufacturing.') else 'manufacturing.response_invalid: Inspect retained responses; no write was retried.\n')
    except (OSError, KeyError, TypeError, subprocess.TimeoutExpired):
        parser.exit(1, 'manufacturing.environment: Check credentials, TLS, Release build and storage. Preserve state; no writes were retried.\n')
