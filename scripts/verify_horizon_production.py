#!/usr/bin/env python3
"""Explicit Test-only live extension smoke, with fresh tags and no write retries."""
import argparse
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import subprocess
import uuid


def verify(dotnet):
    if os.environ.get('TIMEBASE_DATASET') != 'Test':
        raise ValueError('horizon.dataset_guard: Set TIMEBASE_DATASET=Test; no other Dataset is permitted.')
    root = Path(__file__).resolve().parents[1]
    token = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S') + '-' + uuid.uuid4().hex[:6]
    folder = root / '.tools' / ('horizon-smoke-' + token)
    folder.mkdir(mode=0o700)
    model = json.loads((root / 'examples/ramp-simulation.json').read_text())
    start = datetime.now(timezone.utc).replace(microsecond=0) - timedelta(minutes=5)
    utc = lambda value: value.isoformat().replace('+00:00', 'Z')
    session = model['session']
    session.update(sessionId='horizon-' + token, dataset='Test', connectionProfile=os.environ['TIMEBASE_PROFILE'],
                   startUtc=utc(start), endUtc=utc(start + timedelta(seconds=60)))
    for tag, pattern in zip(session['outputTags'], model['generators']):
        tag['name'] = 'Sim.Horizon.' + token + '.' + tag['name'].split('.')[-1]
        pattern['tag'] = tag['name']
    # Keep the ramp changing across the extension boundary for arrival evidence.
    model['generators'][0].update(startValue=0, ratePerSecond=1, minimum=0, maximum=1000)
    path = folder / 'model.json'
    path.write_text(json.dumps(model, indent=2))
    command = [dotnet, str(root / 'src/IndustrialDataSim.Cli/bin/Release/net10.0/IndustrialDataSim.Cli.dll'), 'production']
    sequence = 0
    def invoke(action, *arguments):
        nonlocal sequence
        sequence += 1
        result = subprocess.run(command + [action, str(folder / 'state.db')] + list(arguments), capture_output=True, text=True, timeout=180)
        (folder / f'{sequence:02d}-{action}.json').write_text(result.stdout)
        parsed = json.loads(result.stdout)
        if result.returncode or not parsed.get('valid'):
            raise ValueError('horizon.command_failed: Inspect the retained command diagnostics and state. No write or mutation was retried.')
        return parsed['result']
    invoke('start', str(path))
    first = invoke('run', '10')
    if first['stopReason'] != 'Completed':
        raise ValueError('horizon.incomplete: Initial run did not complete; retain state and inspect it before continuing.')
    invoke('extend', session['sessionId'], utc(start + timedelta(seconds=120)), '0', str(uuid.uuid4()))
    second = invoke('run', '10')
    if second['stopReason'] != 'Completed':
        raise ValueError('horizon.incomplete: Extension did not complete; never replay ambiguous writes.')
    observations = invoke('observations', session['sessionId'])['observations']
    if not observations or any(row['status'] not in ('Observed', 'ConsistentWithoutNewArrival') or row['nonNullCurrentTags'] != 3 or row['changedTags'] < 1 for row in observations):
        raise ValueError('horizon.arrival_unconfirmed: Review retained observations and tags; missing samples never authorize replay.')
    invoke('archive', session['sessionId'], str(folder / 'archive'))
    invoke('verify-archive', session['sessionId'], str(folder / 'archive'))
    print(json.dumps({'folder': str(folder.relative_to(root)), 'sessionId': session['sessionId'],
                      'publishedBefore': first['publishedBatches'], 'publishedAfter': second['publishedBatches'],
                      'arrival': 'IndicatorsPresent', 'archive': 'Verified', 'userReview': 'NotReviewed'}, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--write-test-dataset', action='store_true', required=True)
    args = parser.parse_args()
    try:
        verify(args.dotnet)
    except ValueError as error:
        parser.exit(1, str(error) + '\n' if str(error).startswith('horizon.') else 'horizon.response_invalid: Inspect retained results; no write was retried.\n')
    except (OSError, KeyError, TypeError, subprocess.TimeoutExpired):
        parser.exit(1, 'horizon.environment: Check credentials, TLS, build and local storage. Preserve this run; no writes were retried.\n')
