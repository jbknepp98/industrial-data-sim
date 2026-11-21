#!/usr/bin/env python3
"""Drive and measure the Phase 5 release-candidate acceptance run. WRITES TO HISTORIAN.

Uses only the frozen packaged CLI. Admits the four prepared sessions into a new
private state database, runs the production host through backfill into live,
performs the extension, restart and pause/resume drills, and records measurements.

Safety rules followed here (see docs/phase-5-acceptance-plan.md):
- every mutation is submitted once; a lost or failed response is never retried;
- a Failed or Uncertain session stops the run with state preserved; nothing is
  replayed, force-acknowledged or re-admitted;
- the host is always stopped gracefully and its exit awaited before the
  database is reopened by another command;
- credentials come from the environment file and are never written anywhere.
"""
import argparse
from datetime import datetime, timedelta, timezone
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid

POLL_SECONDS = 15
CAUGHT_UP_LAG_SECONDS = 30
# Tags that are deliberately silent while their gate is closed are excluded from
# live lag, as the release specification requires.
GATED_PARTS = ('.Filler.', '.Labeler.', '.Reject.', '.Conveyor.', '.Capper.')


def utc_now():
    return datetime.now(timezone.utc)


def utc_text(moment):
    return moment.replace(microsecond=0).isoformat().replace('+00:00', 'Z')


def parse(stamp):
    return datetime.fromisoformat(stamp.replace('Z', '+00:00')) if stamp else None


def percentile(values, fraction=0.95):
    ordered = sorted(values)
    return ordered[math.ceil(len(ordered) * fraction) - 1] if ordered else None


def live_lag_seconds(progress, now):
    """Lag of the stalest ungated tag's newest published timestamp, or None."""
    stamps = [parse(p['publishedUtc']) for p in progress if not any(part in p['tag'] for part in GATED_PARTS)]
    if not stamps or any(stamp is None for stamp in stamps):
        return None
    return max(0.0, (now - min(stamps)).total_seconds())


def minute_ceiling(moment):
    rounded = moment.replace(second=0, microsecond=0)
    return rounded if rounded == moment else rounded + timedelta(minutes=1)


def load_environment(path):
    settings = {}
    for line in path.read_text().splitlines():
        if '=' in line and not line.lstrip().startswith('#'):
            key, value = line.split('=', 1)
            settings[key.strip()] = value.strip()
    if settings.get('TIMEBASE_DATASET') != 'Test':
        raise ValueError('rc.dataset_guard: The environment must name the Test Dataset; no session was admitted.')
    return settings


class Run:
    def __init__(self, cli, folder, environment):
        self.cli, self.folder, self.environment = cli, folder, {**os.environ, **environment}
        self.database = str(folder / 'state.db')
        self.sequence = 0
        self.log = (folder / 'events.jsonl').open('a')
        self.samples = (folder / 'samples.jsonl').open('a')
        self.host = None

    def event(self, kind, **details):
        record = {'utc': utc_text(utc_now()), 'event': kind, **details}
        self.log.write(json.dumps(record) + '\n'); self.log.flush()
        print(json.dumps(record), flush=True)

    def invoke(self, arguments, timeout=180):
        """Run one CLI command once. Responses are kept for evidence."""
        self.sequence += 1
        began = time.monotonic()
        result = subprocess.run([self.cli] + arguments, capture_output=True, text=True, timeout=timeout, env=self.environment)
        duration_ms = (time.monotonic() - began) * 1000
        (self.folder / 'responses' / f'{self.sequence:05}-{arguments[0]}-{arguments[1]}.json').write_text(result.stdout)
        try:
            parsed = json.loads(result.stdout)
        except json.JSONDecodeError:
            raise ValueError(f'rc.command_output: {arguments[0]} {arguments[1]} returned unreadable output; inspect responses/{self.sequence:05}. Not retried.')
        if result.returncode or not parsed.get('valid'):
            codes = [e.get('code') for e in parsed.get('errors', [])][:3]
            raise ValueError(f'rc.command_failed: {arguments[0]} {arguments[1]} failed with {codes}; inspect responses/{self.sequence:05}. Not retried.')
        return parsed['result'], duration_ms

    def start_host(self, label):
        output = (self.folder / f'host-{label}.out').open('w')
        errors = (self.folder / f'host-{label}.err').open('w')
        self.host = subprocess.Popen([self.cli, 'production-host', 'run', self.database], stdout=output, stderr=errors, env=self.environment)
        self.event('host_started', label=label, pid=self.host.pid)
        time.sleep(3)

    def stop_host(self, reason):
        if not self.host or self.host.poll() is not None:
            return
        began = time.monotonic()
        try:
            self.invoke(['production-host', 'stop', self.database])
            self.host.wait(timeout=300)
        except (ValueError, subprocess.TimeoutExpired):
            # A cleanup stop is a separate request, never a write retry.
            self.host.terminate()
            self.host.wait(timeout=60)
        self.event('host_stopped', reason=reason, exitCode=self.host.returncode, seconds=round(time.monotonic() - began, 1))

    def host_memory_mb(self):
        if not self.host or self.host.poll() is not None:
            return None
        result = subprocess.run(['ps', '-o', 'rss=', '-p', str(self.host.pid)], capture_output=True, text=True)
        return round(int(result.stdout.strip()) / 1024, 1) if result.stdout.strip() else None

    def storage_mb(self):
        total = sum(path.stat().st_size for path in self.folder.glob('state.db*') if path.is_file())
        return round(total / (1024 * 1024), 1)


def drive(args):
    environment = load_environment(args.environment_file)
    workload = args.workload
    manifest = json.loads((workload / 'manifest.json').read_text())
    anchor = parse(manifest['anchorUtc'])
    sessions = {s['sessionId']: s for s in manifest['sessions']}
    continuing = [sid for sid in sessions if not sid.endswith('-batchreactor')]
    folder = workload / 'run'
    (folder / 'responses').mkdir(parents=True, mode=0o700)
    run = Run(str(args.cli), folder, environment)
    full_live = timedelta(minutes=args.live_minutes)
    status_ms, lags, timeline = [], [], {'anchorUtc': manifest['anchorUtc']}
    caught_up = {}
    extensions = 0
    try:
        for sid, entry in sessions.items():
            result, duration = run.invoke(['production', 'start', run.database, str(workload / entry['file'])])
            run.event('admitted', session=sid, ms=round(duration), settings=result.get('settings'))
        run.start_host('initial')
        restarted = paused = False
        while True:
            now = utc_now()
            if run.host.poll() is not None:
                raise ValueError(f'rc.host_exit: The host exited unexpectedly with code {run.host.returncode}; state preserved. Inspect host-*.err.')
            snapshot = {'utc': utc_text(now), 'memoryMb': run.host_memory_mb(), 'storageMb': run.storage_mb(), 'sessions': {}}
            states = {}
            for sid in sessions:
                status, duration = run.invoke(['production-live', 'status', run.database, sid])
                status_ms.append(duration)
                state = status['session']['status']
                states[sid] = state
                if state in ('Failed', 'Uncertain'):
                    raise ValueError(f'rc.session_{state.lower()}: {sid} is {state}; stopping with state preserved. Never replay or resume Uncertain work.')
                lag = live_lag_seconds(status['progress'], utc_now())
                if sid not in caught_up and lag is not None and lag <= CAUGHT_UP_LAG_SECONDS and now >= anchor:
                    caught_up[sid] = now
                    run.event('caught_up', session=sid, secondsAfterAnchor=round((now - anchor).total_seconds()))
                if sid in caught_up and state != 'Complete' and lag is not None:
                    lags.append(lag)
                snapshot['sessions'][sid] = {'state': state, 'statusMs': round(duration), 'lagSeconds': None if lag is None else round(lag, 2),
                                             'queuedPoints': status['session']['queuedPoints'], 'queuedBytes': status['session']['queuedBytes'],
                                             'nextSlot': status['session']['nextSlot'], 'revision': (status.get('horizon') or {}).get('revision')}
            run.samples.write(json.dumps(snapshot) + '\n'); run.samples.flush()

            all_caught_up = all(sid in caught_up for sid in continuing)
            # Extension drill. First extension early, so no session can reach its
            # initial end while backfill is still catching up; a second one once
            # catch-up time is known guarantees the full live duration after it.
            wanted_end = minute_ceiling(max(anchor + full_live, max(caught_up.values()) + full_live if all_caught_up else anchor + full_live))
            due = (extensions == 0 and now >= anchor + timedelta(minutes=args.extend_after_minutes)) or (extensions == 1 and all_caught_up)
            if due:
                for sid in continuing:
                    if states[sid] == 'Complete':
                        continue
                    horizon, _ = run.invoke(['production-live', 'horizon', run.database, sid])
                    if parse(horizon['effectiveEndUtc']) >= wanted_end:
                        continue
                    receipt, duration = run.invoke(['production-live', 'extend', run.database, sid, utc_text(wanted_end),
                                                    str(horizon['revision']), str(uuid.uuid4())])
                    run.event('extended', session=sid, fromUtc=horizon['effectiveEndUtc'], toUtc=utc_text(wanted_end),
                              revision=receipt.get('revision'), ms=round(duration))
                extensions += 1

            # Restart drill: graceful stop, exit awaited, restart on the same
            # database; identity and forward progress are checked afterwards.
            if not restarted and all_caught_up and now >= max(caught_up.values()) + timedelta(minutes=args.restart_after_minutes):
                before = {}
                for sid in sessions:
                    status, _ = run.invoke(['production-live', 'status', run.database, sid])
                    before[sid] = status
                run.stop_host('restart_drill')
                stopped = utc_now()
                run.start_host('after_restart')
                time.sleep(max(POLL_SECONDS, 20))
                for sid in sessions:
                    after, _ = run.invoke(['production-live', 'status', run.database, sid])
                    old, new = before[sid], after
                    same_revision = (old.get('horizon') or {}).get('revision') == (new.get('horizon') or {}).get('revision')
                    forward = new['session']['nextSlot'] >= old['session']['nextSlot'] and all(
                        (parse(n['publishedUtc']) or datetime.min.replace(tzinfo=timezone.utc)) >= (parse(o['publishedUtc']) or datetime.min.replace(tzinfo=timezone.utc))
                        for o, n in zip(old['progress'], new['progress']))
                    run.event('restart_check', session=sid, sameRevision=same_revision, forwardOnly=forward, state=new['session']['status'])
                    if not (same_revision and forward):
                        raise ValueError(f'rc.restart_identity: {sid} changed identity or moved backwards across restart; state preserved.')
                timeline['restartUtc'] = utc_text(stopped)
                restarted = True

            # Pause/resume drill on the Utilities session: control latency and
            # clock continuity under live load.
            utilities = next(sid for sid in continuing if sid.endswith('-utilities'))
            if restarted and not paused and states[utilities] != 'Complete' and now >= caught_up[utilities] + timedelta(minutes=args.pause_after_minutes):
                _, pause_ms = run.invoke(['production-live', 'pause', run.database, utilities])
                run.event('paused', session=utilities, ms=round(pause_ms))
                time.sleep(args.pause_seconds)
                _, resume_ms = run.invoke(['production-live', 'resume', run.database, utilities])
                run.event('resumed', session=utilities, ms=round(resume_ms))
                status_ms.extend([pause_ms, resume_ms])
                paused = True

            if all(state == 'Complete' for state in states.values()):
                run.event('all_complete')
                break
            if now > anchor + timedelta(hours=args.deadline_hours):
                raise ValueError('rc.deadline: Sessions did not complete within the run deadline; state preserved for review.')
            time.sleep(POLL_SECONDS)
        run.stop_host('complete')
    except BaseException as error:
        run.event('run_stopped', reason=str(error) if str(error).startswith('rc.') else type(error).__name__)
        run.stop_host('failure')
        raise
    finally:
        arrivals = {}
        for sid in sessions:
            try:
                result, _ = run.invoke(['production', 'observations', run.database, sid])
                rows = result['observations']
                arrivals[sid] = {'observedBatches': len(rows), 'anyChanged': any(r.get('changedTags', 0) > 0 for r in rows)}
            except (ValueError, subprocess.TimeoutExpired):
                arrivals[sid] = None
        report = {'candidate': args.cli.parent.parent.name, 'namespace': manifest['namespace'], 'timeline': timeline,
                  'caughtUp': {sid: utc_text(t) for sid, t in caught_up.items()},
                  'statusSamples': len(status_ms), 'statusP95Ms': percentile(status_ms), 'statusMaxMs': max(status_ms, default=None),
                  'lagSamples': len(lags), 'liveLagP95Seconds': percentile(lags), 'liveLagMaxSeconds': max(lags, default=None),
                  'extensions': extensions, 'arrivalIndicators': arrivals, 'userReview': 'NotReviewed',
                  'notice': 'Published progress and arrival indicators are not per-point receipts. Absence never authorizes replay.'}
        (folder / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
        print(json.dumps(report, indent=2), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('workload', type=Path, help='Folder written by prepare_rc_acceptance.py')
    parser.add_argument('--cli', type=Path, required=True, help='Packaged IndustrialDataSim.Cli executable')
    parser.add_argument('--environment-file', type=Path, required=True)
    parser.add_argument('--write-test-dataset', action='store_true', required=True)
    parser.add_argument('--live-minutes', type=int, default=180)
    parser.add_argument('--extend-after-minutes', type=int, default=20)
    parser.add_argument('--restart-after-minutes', type=int, default=60)
    parser.add_argument('--pause-after-minutes', type=int, default=90)
    parser.add_argument('--pause-seconds', type=int, default=60)
    parser.add_argument('--deadline-hours', type=float, default=8)
    args = parser.parse_args()
    try:
        drive(args)
    except ValueError as error:
        parser.exit(1, (str(error) if str(error).startswith('rc.') else 'rc.invalid_response: Inspect retained evidence; nothing was retried.') + '\n')
    except (OSError, KeyError, TypeError, subprocess.TimeoutExpired) as error:
        parser.exit(1, f'rc.environment: {type(error).__name__}. Check the packaged CLI, environment file and run folder. Nothing was retried.\n')
