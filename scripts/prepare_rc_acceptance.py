#!/usr/bin/env python3
"""Write the release-candidate acceptance workload: four models and a manifest.

Writes files only. It does not authenticate, admit sessions or publish values.
The workload follows the release specification: four sessions with eight output
tags each, all in the Test Dataset with disjoint tags under one fresh namespace,
covering simple signals, gated sequences, two-cell SKU routing and a
target-bounded production model. Each model spans a 72-hour backfill before the
anchor plus an initial live horizon; the run later extends the horizon so the
extension drill is part of acceptance.

Timing is plant-scale (minutes to hours) so a human reviewer can recognize the
intended behavior across a multi-day trend. Seeds are fixed per run and recorded
in the manifest so every model can be regenerated exactly.
"""
import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import re
import sys

BACKFILL = timedelta(hours=72)
# The target-bounded model uses precompiled execution, capped at 250,000
# node-clock evaluations (eight nodes at a one-second clock is about 8.7 hours).
# The specification requires the 72-hour backfill only for continuing models, so
# this session gets a short backfill and keeps the one-second process clock.
TARGET_MODEL_BACKFILL = timedelta(hours=6)
MINUTE_MS = 60 * 1000
HOUR_MS = 60 * MINUTE_MS
# Patterns need a finite duration. Cover backfill, the full three-hour live run
# and generous room for a late start or extension without reaching the end.
PATTERN_SPAN_MS = 96 * HOUR_MS


def utc_text(moment):
    return moment.isoformat().replace('+00:00', 'Z')


def single_step_sequence(pattern):
    # Switch branches must be sequences; a one-step sequence wraps one pattern.
    return {'kind': 'sequence', 'afterSequence': 'holdLast', 'steps': [{'pattern': pattern}]}


def header(namespace, part, anchor, initial_live, profile, backfill):
    return {
        'schemaVersion': 1,
        'sessionId': f'{namespace}-{part}'.lower(),
        'connectionProfile': profile,
        'dataset': 'Test',
        'startUtc': utc_text(anchor - backfill),
        'endUtc': utc_text(anchor + initial_live),
        'outputTags': [],
    }


def output(model, name, value_type):
    model['session']['outputTags'].append({'name': name, 'valueType': value_type})
    return name


def utilities_model(namespace, anchor, initial_live, profile, seed, backfill):
    """Simple signals: holds, a bounded ramp, staircases and constants."""
    prefix = f'{namespace}.Utilities.'
    model = {'schemaVersion': 1, 'generatorVersion': 1,
             'session': header(namespace, 'utilities', anchor, initial_live, profile, backfill),
             'samplingIntervalMs': 1000, 'generators': []}
    add = model['generators'].append

    def random_hold(name, minimum, maximum, hold_minutes, offset):
        add({'tag': output(model, prefix + name, 'number'), 'kind': 'randomIntegerHold',
             'minimum': minimum, 'maximum': maximum, 'seed': seed + offset, 'durationMs': PATTERN_SPAN_MS,
             'holdDurationRangeMs': {'minimum': hold_minutes[0] * MINUTE_MS, 'maximum': hold_minutes[1] * MINUTE_MS},
             'adjacentValues': 'requireChange', 'afterDuration': 'holdLast'})

    random_hold('Compressor.PressurePsi', 95, 110, (10, 30), 1)
    random_hold('Chiller.SupplyTempC', 6, 9, (20, 60), 2)
    random_hold('Air.DewpointC', -40, -30, (30, 90), 3)
    # A slow fill that reaches its bound late in the backfill and then holds.
    add({'tag': output(model, prefix + 'Tank.LevelPct', 'number'), 'kind': 'ramp',
         'startValue': 20, 'ratePerSecond': 0.0003, 'minimum': 0, 'maximum': 95})
    # Day/night setpoint keyed to UTC hour so trends line up with wall time.
    start = anchor - backfill
    steps = []
    for hour in range(PATTERN_SPAN_MS // HOUR_MS):
        local_hour = (start + timedelta(hours=hour)).hour
        steps.append({'value': 120 if 6 <= local_hour < 18 else 80, 'durationMs': HOUR_MS})
    add({'tag': output(model, prefix + 'Water.FlowSetpointGpm', 'number'), 'kind': 'staircase',
         'afterSteps': 'holdLast', 'steps': steps})
    # Randomized dwell between a repeating set of demand levels.
    levels = [410, 455, 520, 480, 430, 395]
    demand_steps = [{'value': levels[index % len(levels)], 'durationRangeMs': {'minimum': HOUR_MS, 'maximum': 2 * HOUR_MS}}
                    for index in range(PATTERN_SPAN_MS // HOUR_MS)]
    add({'tag': output(model, prefix + 'Power.DemandKw', 'number'), 'kind': 'staircase', 'afterSteps': 'holdLast',
         'steps': demand_steps, 'seed': seed + 4, 'maxTotalDurationMs': 2 * PATTERN_SPAN_MS})
    add({'tag': output(model, prefix + 'Boiler.Running', 'boolean'), 'kind': 'constant', 'value': True})
    add({'tag': output(model, prefix + 'Site.Mode', 'string'), 'kind': 'constant', 'value': 'Normal'})
    return model


def packaging_model(namespace, anchor, initial_live, profile, seed, backfill):
    """Gated sequences driven by an eight-hour shift with a changeover window."""
    prefix = f'{namespace}.PackagingLine.'
    model = {'schemaVersion': 1, 'generatorVersion': 1,
             'session': header(namespace, 'packaging', anchor, initial_live, profile, backfill),
             'samplingIntervalMs': 1000, 'generators': []}
    add = model['generators'].append
    ready = output(model, prefix + 'LineReady', 'boolean')
    shifts = []
    for _ in range(PATTERN_SPAN_MS // (8 * HOUR_MS)):
        shifts += [{'value': False, 'durationMs': 30 * MINUTE_MS}, {'value': True, 'durationMs': 450 * MINUTE_MS}]
    add({'tag': ready, 'kind': 'booleanTimeline', 'afterSteps': 'holdLast', 'steps': shifts})

    def hold(minimum, maximum, minutes, offset):
        return {'kind': 'randomIntegerHold', 'minimum': minimum, 'maximum': maximum, 'seed': seed + offset,
                'durationMs': PATTERN_SPAN_MS, 'holdDurationRangeMs': {'minimum': minutes[0] * MINUTE_MS, 'maximum': minutes[1] * MINUTE_MS},
                'adjacentValues': 'requireChange', 'afterDuration': 'holdLast'}

    def gate(name, when_false, pattern):
        add({'tag': output(model, prefix + name, 'number'), 'kind': 'booleanGate', 'triggerTag': ready,
             'whenFalse': when_false, 'pattern': pattern})

    # pauseAndSuppress resumes where it stopped; continueAndSuppress keeps its
    # clock running through the changeover. Both are reviewed in the trend.
    gate('Filler.RateBpm', 'pauseAndSuppress', hold(380, 420, (5, 15), 1))
    gate('Filler.CountTotal', 'pauseAndSuppress', {'kind': 'ramp', 'startValue': 0, 'ratePerSecond': 6.5, 'maximum': 3000000})
    gate('Labeler.SpeedBpm', 'continueAndSuppress', hold(370, 430, (5, 20), 2))
    gate('Reject.RatePct', 'continueAndSuppress', hold(0, 4, (15, 45), 3))
    gate('Conveyor.LoadPct', 'pauseAndSuppress', hold(40, 80, (3, 10), 4))
    warmup = {'kind': 'staircase', 'afterSteps': 'holdLast',
              'steps': [{'value': 5, 'durationMs': 10 * MINUTE_MS}, {'value': 12, 'durationMs': 10 * MINUTE_MS},
                        {'value': 18, 'durationMs': 10 * MINUTE_MS}]}
    gate('Capper.TorqueInLb', 'pauseAndSuppress',
         {'kind': 'sequence', 'afterSequence': 'holdLast', 'steps': [{'pattern': warmup}, {'pattern': hold(18, 22, (10, 30), 5)}]})
    # Phase restarts on every shift edge: startup ramp then steady production,
    # or a cleanup staircase during changeover.
    add({'tag': output(model, prefix + 'Line.Phase', 'number'), 'kind': 'booleanSwitch', 'triggerTag': ready,
         'onChange': 'restartBranch',
         'whenTrue': single_step_sequence({'kind': 'staircase', 'afterSteps': 'holdLast', 'steps': [
             {'value': 1, 'durationMs': 15 * MINUTE_MS}, {'value': 2, 'durationMs': 15 * MINUTE_MS},
             {'value': 3, 'durationMs': 60 * MINUTE_MS}]}),
         'whenFalse': single_step_sequence({'kind': 'staircase', 'afterSteps': 'holdLast', 'steps': [
             {'value': 9, 'durationMs': 10 * MINUTE_MS}, {'value': 0, 'durationMs': 10 * MINUTE_MS}]})})
    return model


def process_cell_model(namespace, anchor, initial_live, profile, seed, backfill):
    """Windowed manufacturing: two downstream cells chosen exclusively by SKU."""
    prefix = f'{namespace}.ProcessCell.'
    model = {'schemaVersion': 1, 'generatorVersion': 1,
             'session': header(namespace, 'processcell', anchor, initial_live, profile, backfill),
             'samplingIntervalMs': 1000, 'manufacturing': {'tickMs': 1000, 'execution': 'windowed', 'nodes': []}}
    node = model['manufacturing']['nodes'].append
    tag = lambda name: {'tag': prefix + name}
    ready = output(model, prefix + 'Ready', 'boolean')
    sku = output(model, prefix + 'SKU', 'string')
    state = output(model, prefix + 'State', 'string')
    # A five-hour cycle with a twenty-minute stop; SKU changes on a different
    # five-hour rhythm, so stops and changeovers do not always coincide.
    node({'tag': ready, 'expression': {'op': 'schedule', 'repeat': True, 'steps': [
        {'durationMs': 20 * MINUTE_MS, 'value': False}, {'durationMs': 280 * MINUTE_MS, 'value': True}]}})
    node({'tag': sku, 'expression': {'op': 'schedule', 'repeat': True, 'steps': [
        {'durationMs': 3 * HOUR_MS, 'value': 'SKU-A'}, {'durationMs': 2 * HOUR_MS, 'value': 'SKU-B'}]}})
    node({'tag': state, 'state': {'initial': 'Idle', 'transitions': [
        {'from': '*', 'to': 'Idle', 'when': {'op': 'not', 'arg': tag('Ready')}},
        {'from': 'Idle', 'to': 'Startup', 'when': tag('Ready')},
        {'from': 'Startup', 'to': 'Running', 'when': tag('Ready'), 'afterMs': 10 * MINUTE_MS}]}})
    running = {'op': 'eq', 'left': tag('State'), 'right': 'Running'}
    for cell, chosen, low, high, offset in (('A', 'SKU-A', 110, 125, 1), ('B', 'SKU-B', 85, 100, 2)):
        node({'tag': output(model, prefix + cell + '.Speed', 'number'), 'expression': {
            'op': 'if', 'when': {'op': 'and', 'args': [running, {'op': 'eq', 'left': tag('SKU'), 'right': chosen}]},
            'then': {'op': 'uniform', 'minimum': low, 'maximum': high, 'seed': seed + offset, 'holdMs': 2 * MINUTE_MS},
            'else': 0}})
    # Faults: one freeze and one bad-quality window in backfill, one freeze live.
    node({'tag': output(model, prefix + 'Temperature', 'number'),
          'expression': {'op': 'if', 'when': running, 'then': 75, 'else': 40},
          'noise': {'amplitude': 0.5, 'seed': seed + 3},
          'faults': [{'kind': 'freeze', 'startMs': 30 * HOUR_MS, 'durationMs': 10 * MINUTE_MS},
                     {'kind': 'quality', 'startMs': 60 * HOUR_MS, 'durationMs': 5 * MINUTE_MS, 'quality': 0},
                     {'kind': 'freeze', 'startMs': 72 * HOUR_MS + 45 * MINUTE_MS, 'durationMs': 5 * MINUTE_MS}]})
    node({'tag': output(model, prefix + 'TimeTotal', 'number'), 'accumulator': {
        'mode': 'time', 'initial': 0,
        'ratePerSecond': {'op': 'if', 'when': running, 'then': 25,
                          'else': {'op': 'if', 'when': {'op': 'eq', 'left': tag('State'), 'right': 'Startup'}, 'then': 10, 'else': 0}}}})
    node({'tag': output(model, prefix + 'BatchTotal', 'number'), 'accumulator': {
        'mode': 'batch', 'initial': 0, 'enabled': running,
        'quantityRange': {'minimum': 500, 'maximum': 800},
        'durationRangeMs': {'minimum': 20 * MINUTE_MS, 'maximum': 40 * MINUTE_MS}, 'seed': seed + 4}})
    return model


def batch_reactor_model(namespace, anchor, initial_live, profile, seed, backfill, target):
    """Production-target model: stops the session when the batch total is reached.

    stopOnTarget requires the precompiled execution mode, because windowed
    execution needs a fixed session end.
    """
    prefix = f'{namespace}.BatchReactor.'
    model = {'schemaVersion': 1, 'generatorVersion': 1,
             'session': header(namespace, 'batchreactor', anchor, initial_live, profile, TARGET_MODEL_BACKFILL),
             'samplingIntervalMs': 1000, 'manufacturing': {'tickMs': 1000, 'nodes': []}}
    node = model['manufacturing']['nodes'].append
    tag = lambda name: {'tag': prefix + name}
    ready = output(model, prefix + 'Ready', 'boolean')
    node({'tag': ready, 'expression': {'op': 'schedule', 'repeat': True, 'steps': [
        {'durationMs': 30 * MINUTE_MS, 'value': False}, {'durationMs': 330 * MINUTE_MS, 'value': True}]}})
    node({'tag': output(model, prefix + 'Recipe', 'string'), 'expression': {'op': 'schedule', 'repeat': True, 'steps': [
        {'durationMs': 6 * HOUR_MS, 'value': 'R-100'}, {'durationMs': 6 * HOUR_MS, 'value': 'R-200'}]}})
    node({'tag': output(model, prefix + 'Phase', 'string'), 'state': {'initial': 'Idle', 'transitions': [
        {'from': '*', 'to': 'Idle', 'when': {'op': 'not', 'arg': tag('Ready')}},
        {'from': 'Idle', 'to': 'Charge', 'when': tag('Ready')},
        {'from': 'Charge', 'to': 'React', 'when': tag('Ready'), 'afterMs': 10 * MINUTE_MS},
        {'from': 'React', 'to': 'Discharge', 'when': tag('Ready'), 'afterMs': 40 * MINUTE_MS},
        {'from': 'Discharge', 'to': 'Charge', 'when': tag('Ready'), 'afterMs': 10 * MINUTE_MS}]}})
    reacting = {'op': 'eq', 'left': tag('Phase'), 'right': 'React'}
    node({'tag': output(model, prefix + 'TempC', 'number'),
          'expression': {'op': 'if', 'when': reacting, 'then': 180, 'else': 60},
          'noise': {'amplitude': 1.5, 'seed': seed + 1}})
    node({'tag': output(model, prefix + 'PressureBar', 'number'),
          'expression': {'op': 'if', 'when': reacting,
                         'then': {'op': 'uniform', 'minimum': 4, 'maximum': 6, 'seed': seed + 2, 'holdMs': 5 * MINUTE_MS},
                         'else': 1}})
    node({'tag': output(model, prefix + 'AgitatorRpm', 'number'),
          'expression': {'op': 'if', 'when': tag('Ready'), 'then': 120, 'else': 0}})
    node({'tag': output(model, prefix + 'RunHours', 'number'), 'accumulator': {
        'mode': 'time', 'initial': 0, 'ratePerSecond': {'op': 'if', 'when': tag('Ready'), 'then': 1 / 3600, 'else': 0}}})
    # Randomized production increments; the session finishes at the target.
    node({'tag': output(model, prefix + 'ProducedKg', 'number'), 'accumulator': {
        'mode': 'batch', 'initial': 0, 'enabled': {'op': 'eq', 'left': tag('Phase'), 'right': 'Discharge'},
        'quantityRange': {'minimum': 300, 'maximum': 400},
        'durationRangeMs': {'minimum': 2 * MINUTE_MS, 'maximum': 4 * MINUTE_MS}, 'seed': seed + 3,
        'target': target, 'finalPolicy': 'cap', 'stopOnTarget': True}})
    return model


def prepare(anchor, initial_live, profile, seed, target, destination, namespace, backfill=BACKFILL):
    if not re.fullmatch(r'[A-Za-z0-9_-]{1,64}', profile):
        raise ValueError('rc.profile: Use the deployment profile label (letters, digits, hyphen, underscore; 1–64).')
    destination.mkdir(parents=True, exist_ok=False)
    builders = (('utilities', utilities_model, 100), ('packaging', packaging_model, 200),
                ('processcell', process_cell_model, 300), ('batchreactor', batch_reactor_model, 400))
    manifest = {'format': 1, 'namespace': namespace, 'dataset': 'Test', 'anchorUtc': utc_text(anchor),
                'backfillHours': backfill.total_seconds() / 3600, 'initialLiveMinutes': int(initial_live.total_seconds() // 60),
                'seed': seed, 'batchReactorTarget': target, 'historianWrites': 0, 'sessions': []}
    for part, builder, offset in builders:
        arguments = (namespace, anchor, initial_live, profile, seed + offset, backfill)
        model = builder(*arguments, target) if part == 'batchreactor' else builder(*arguments)
        text = json.dumps(model, indent=2) + '\n'
        path = destination / f'{part}.json'
        path.write_text(text)
        manifest['sessions'].append({'file': path.name, 'sessionId': model['session']['sessionId'],
                                     'sha256': hashlib.sha256(text.encode()).hexdigest(),
                                     'tags': [tag['name'] for tag in model['session']['outputTags']]})
    tags = [name for session in manifest['sessions'] for name in session['tags']]
    if len(tags) != 32 or len(set(tags)) != 32:
        raise ValueError('rc.tags: Expected 32 distinct output tags across four sessions.')
    (destination / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    return manifest


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--anchor-utc', required=True, help='Planned live start (backfill/live boundary), e.g. 2026-11-21T15:00:00Z')
    parser.add_argument('--initial-live-minutes', type=int, default=120, help='Live horizon before the extension drill')
    parser.add_argument('--profile', default='local-development')
    parser.add_argument('--seed', type=int, default=20261120)
    parser.add_argument('--target-kg', type=int, required=True, help='BatchReactor production target; tune from the preview')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--backfill-hours', type=int, default=72, help='Continuing-model backfill; 72 for acceptance, shorter only for a smoke run')
    parser.add_argument('--namespace-prefix', default='RC1', help='Use RC1 for acceptance; a smoke run uses its own prefix')
    args = parser.parse_args()
    try:
        anchor = datetime.fromisoformat(args.anchor_utc.replace('Z', '+00:00'))
        if anchor.tzinfo is None or anchor.utcoffset() != timedelta(0) or anchor.second or anchor.microsecond:
            raise ValueError('rc.anchor: Use a UTC time on a whole minute ending in Z.')
        if not 1 <= args.backfill_hours <= 72 or not re.fullmatch(r'[A-Za-z0-9]{1,16}', args.namespace_prefix):
            raise ValueError('rc.arguments: Backfill must be 1-72 hours and the prefix 1-16 letters or digits.')
        if not 1 <= args.initial_live_minutes <= 180:
            raise ValueError('rc.live: Initial live horizon must be 1–180 minutes; the full run is three hours live.')
        namespace = args.namespace_prefix + '-' + anchor.strftime('%Y%m%dT%H%M')
        manifest = prepare(anchor, timedelta(minutes=args.initial_live_minutes), args.profile, args.seed,
                           args.target_kg, args.output, namespace, timedelta(hours=args.backfill_hours))
        print(json.dumps({'outcome': 'Prepared', 'namespace': manifest['namespace'], 'anchorUtc': manifest['anchorUtc'],
                          'sessions': [s['sessionId'] for s in manifest['sessions']], 'historianWrites': 0}))
    except (ValueError, OSError) as error:
        message = str(error) if str(error).startswith('rc.') else 'rc.write: Could not write the workload. Use a new output directory and check permissions.'
        print(json.dumps({'outcome': 'Failed', 'message': message}))
        sys.exit(1)
