#!/usr/bin/env python3
"""Explicit live smoke: two disjoint synthetic sessions in Test, never retries writes.

Requires deployment environment settings and --write-test-dataset. Keeps models,
state and reports in ignored .tools for inspection. Never included in offline CI.
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
        raise RuntimeError('production.dataset_guard: Set TIMEBASE_DATASET=Test for this explicit smoke; no other Dataset is permitted.')
    root = Path(__file__).resolve().parents[1]
    identifier = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S') + '-' + uuid.uuid4().hex[:6]
    folder = root / '.tools' / ('production-smoke-' + identifier)
    folder.mkdir(mode=0o700)
    database = folder / 'state.db'
    command = [dotnet, str(root / 'src/IndustrialDataSim.Cli/bin/Release/net10.0/IndustrialDataSim.Cli.dll'), 'production']

    def invoke(action, *arguments):
        result = subprocess.run(command + [action, str(database)] + list(arguments), capture_output=True, text=True, timeout=180)
        response = json.loads(result.stdout)
        (folder / (action + '-' + str(len(list(folder.glob('*.json')))) + '.json')).write_text(json.dumps(response, indent=2))
        if result.returncode != 0 or not response.get('valid'):
            raise RuntimeError('production.command_failed: Inspect saved command diagnostics and durable status. No mutation was retried; retain this run before another test.')
        return response['result']

    start = datetime.now(timezone.utc).replace(microsecond=0) - timedelta(minutes=10)
    session_ids = []
    for label in ('A', 'B'):
        model = json.loads((root / 'examples/ramp-simulation.json').read_text())
        session = model['session']
        session['sessionId'] = 'production-' + label + '-' + identifier
        session_ids.append(session['sessionId'])
        session['connectionProfile'] = os.environ['TIMEBASE_PROFILE']
        session['dataset'] = 'Test'
        session['startUtc'] = start.isoformat().replace('+00:00', 'Z')
        session['endUtc'] = (start + timedelta(seconds=60)).isoformat().replace('+00:00', 'Z')
        for tag, generator in zip(session['outputTags'], model['generators']):
            tag['name'] = 'Sim.Production.' + identifier + '.' + label + '.' + tag['name'].split('.')[-1]
            generator['tag'] = tag['name']
        path = folder / ('model-' + label + '.json')
        path.write_text(json.dumps(model, indent=2))
        invoke('start', str(path))
    run = invoke('run', '10')
    if run['stopReason'] != 'Completed':
        raise RuntimeError('production.incomplete: Inspect retained sessions; do not repeat a publish blindly.')
    for session_id in session_ids:
        observation = invoke('observations', session_id)['observations']
        if not observation or any(item['status'] != 'Observed' for item in observation):
            raise RuntimeError('production.arrival_unconfirmed: Publishing completed but pattern indicators were not all observed. Review Test tags and retained observations; do not resend missing samples.')
    print(json.dumps({'folder': str(folder.relative_to(root)), 'sessions': session_ids,
                      'publishedBatches': run['publishedBatches'], 'arrival': 'Observed', 'userReview': 'NotReviewed'}, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--write-test-dataset', action='store_true', required=True)
    args = parser.parse_args()
    try:
        verify(args.dotnet)
    except RuntimeError as error:
        parser.exit(1, str(error) + '\n')
    except (OSError, ValueError, KeyError, TypeError, subprocess.TimeoutExpired):
        parser.exit(1, 'production.environment: Live smoke could not finish. Check deployment environment, Release build and local storage. Preserve its state; no writes were retried.\n')
