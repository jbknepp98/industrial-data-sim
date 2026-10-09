#!/usr/bin/env python3
"""Explicit, bounded Test-only qualification of four windowed manufacturing sessions.

Creates fresh tags and private state; submits each mutation once. This is a short
capacity/freshness sample, not the release soak or a per-point delivery receipt.
"""
import argparse
from datetime import datetime, timedelta, timezone
import json
import math
import os
from pathlib import Path
import subprocess
import time
import uuid


def qualify(dotnet):
    if os.environ.get('TIMEBASE_DATASET') != 'Test':
        raise ValueError('qualification.dataset_guard: Set TIMEBASE_DATASET=Test before requesting writes.')
    if not os.environ.get('TIMEBASE_PROFILE'):
        raise ValueError('qualification.profile_missing: Set TIMEBASE_PROFILE to the deployment profile label used by these new sessions; no session was admitted.')
    root = Path(__file__).resolve().parents[1]
    identity = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S') + '-' + uuid.uuid4().hex[:6]
    folder = root / '.tools' / ('manufacturing-qualification-' + identity)
    folder.mkdir(mode=0o700)
    command = [dotnet, str(root / 'src/IndustrialDataSim.Cli/bin/Release/net10.0/IndustrialDataSim.Cli.dll')]
    database = str(folder / 'state.db')
    sequence = 0
    def invoke(arguments):
        nonlocal sequence
        sequence += 1
        began = time.monotonic()
        result = subprocess.run(command + arguments, capture_output=True, text=True, timeout=180)
        duration = (time.monotonic() - began) * 1000
        (folder / f'{sequence:04}-response.json').write_text(result.stdout)
        parsed = json.loads(result.stdout)
        if result.returncode or not parsed.get('valid'):
            raise ValueError('qualification.command_failed: Inspect retained diagnostics/state. No mutation or publish was retried.')
        return parsed['result'], duration

    utc = lambda stamp: stamp.isoformat().replace('+00:00', 'Z')
    now = datetime.now(timezone.utc).replace(microsecond=0)
    end = now + timedelta(seconds=90)
    session_ids = []
    for index in range(4):
        model = json.loads((root / 'examples/manufacturing-simulation.json').read_text().replace('Plant.', f'Sim.Qualification.{identity}.S{index}.'))
        session_id = f'qualification-{identity}-{index}'
        session_ids.append(session_id)
        model['session'].update(sessionId=session_id, dataset='Test', connectionProfile=os.environ['TIMEBASE_PROFILE'],
                                startUtc=utc(now - timedelta(minutes=5)), endUtc=utc(end))
        model['samplingIntervalMs'] = 1000
        model['manufacturing'].update(execution='windowed', tickMs=1000)
        for node in model['manufacturing']['nodes']:
            for key in ('target', 'finalPolicy', 'stopOnTarget'):
                node.get('accumulator', {}).pop(key, None)
        path = folder / f'model-{index}.json'
        path.write_text(json.dumps(model, indent=2))
        invoke(['production', 'start', database, str(path)])

    latencies, lags, timing_samples = [], [], []
    with (folder / 'host-output.json').open('w') as output, (folder / 'host-errors.txt').open('w') as errors:
        host = subprocess.Popen(command + ['production-host', 'run', database], stdout=output, stderr=errors)
        try:
            # Startup wait precedes the first control call. A lost response aborts
            # instead of retrying a command whose outcome might be ambiguous.
            time.sleep(2)
            deadline = time.monotonic() + 180
            while time.monotonic() < deadline:
                complete = True
                for session_id in session_ids:
                    status, duration = invoke(['production-live', 'status', database, session_id])
                    latencies.append(duration)
                    timing_samples.append({"sequence": sequence, "statusMs": duration})
                    state = status['session']['status']
                    if state in ('Failed', 'Uncertain'):
                        raise ValueError('qualification.session_failed: Preserve failed/uncertain work and inspect its status; no writes will be replayed.')
                    complete &= state == 'Complete'
                    # Backfill and finite completion are distinct from live lag.
                    sample_now = datetime.now(timezone.utc)
                    if now + timedelta(seconds=20) < sample_now < end and state != 'Complete':
                        timestamps = [datetime.fromisoformat(p['publishedUtc'].replace('Z', '+00:00')) for p in status['progress'] if p['publishedUtc']]
                        if len(timestamps) == 8:
                            lags.append(max(0, (sample_now - min(timestamps)).total_seconds()))
                if complete:
                    break
                time.sleep(2)
            else:
                raise ValueError('qualification.deadline: Sessions did not finish within the bounded run. Preserve state and inspect progress before continuing.')
            invoke(['production-host', 'stop', database])
            host.wait(timeout=60)
            if host.returncode:
                raise ValueError('qualification.host_exit: Host exit was unsuccessful. Inspect retained evidence before interpreting metrics.')
        finally:
            if host.poll() is None:
                # A cleanup stop is a distinct request, never a retry of a write.
                try:
                    invoke(['production-host', 'stop', database])
                    host.wait(timeout=60)
                except (ValueError, OSError, subprocess.TimeoutExpired):
                    host.terminate()
                    host.wait(timeout=30)

    arrival_by_session = []
    for session_id in session_ids:
        result, _ = invoke(['production', 'observations', database, session_id])
        arrival_by_session.append(any(row['nonNullCurrentTags'] == 8 and row['changedTags'] > 0 for row in result['observations']))
    def percentile(values):
        return sorted(values)[math.ceil(len(values) * .95) - 1] if values else None
    indicated = all(arrival_by_session)
    report = {'sessions': 4, 'tags': 32, 'historicalSeconds': 300, 'liveSeconds': 90,
              'statusSamples': len(latencies), 'timingSamples': timing_samples, 'statusP95Ms': percentile(latencies), 'statusMaximumMs': max(latencies),
              'lagSamples': len(lags), 'publishedLagP95Seconds': percentile(lags),
              'arrivalIndicatorsPresent': indicated, 'userReview': 'NotReviewed',
              'folder': str(folder.relative_to(root)),
              'notice': 'Short real-transport sample, not a soak. Published timestamps and arrival indicators are not per-point receipts.'}
    (folder / 'report.json').write_text(json.dumps(report, indent=2))
    print(json.dumps({key: value for key, value in report.items() if key != "timingSamples"}, indent=2))
    if not lags or not indicated or report['statusP95Ms'] > 5000 or report['publishedLagP95Seconds'] > 10:
        raise ValueError('qualification.target_not_met: A freshness/control/arrival target was not established. Preserve measurements; do not replay missing observations.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--write-test-dataset', action='store_true', required=True)
    args = parser.parse_args()
    try:
        qualify(args.dotnet)
    except ValueError as error:
        parser.exit(1, str(error) + '\n' if str(error).startswith('qualification.') else 'qualification.invalid_response: Inspect retained evidence; no publish was retried.\n')
    except (OSError, KeyError, TypeError, subprocess.TimeoutExpired):
        parser.exit(1, 'qualification.environment: Check local SDK, connection environment, host and retained state. No publish was retried.\n')
