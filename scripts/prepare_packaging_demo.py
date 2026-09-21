#!/usr/bin/env python3
"""Prepare an offline, reproducible two-cell model; this script never publishes.

One SKU source and one readiness source control both routes. A fresh tag prefix
prevents overlap with earlier demonstrations. Reuse the generated model and
SQLite database after interruption; rerunning preparation creates a new demo.
"""
import argparse
import copy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import uuid

MINUTE = 60000
HOUR = 60 * MINUTE


def build_model(start, prefix, identifier, days=10):
    """The default schedule covers 72 hours backfill plus seven days live."""
    def name(suffix):
        return prefix + '.' + suffix

    tags, generators = [], []

    def add(suffix, value_type, pattern):
        tags.append({'name': name(suffix), 'valueType': value_type})
        generators.append({'tag': name(suffix), **pattern})

    cycles = days * 4
    add('Production.SKU.String', 'string', {
        'kind': 'stringTimeline', 'afterSteps': 'holdLast',
        'steps': [{'value': 'SKU-A' if i % 2 == 0 else 'SKU-B', 'durationMs': 6 * HOUR} for i in range(cycles)]})
    # Each shift has a readiness wait, production, an interruption and recovery.
    # All transitions are on the one-minute grid, so the Historian sees them.
    ready_cycle = [(False, 15), (True, 90), (False, 30), (True, 225)]
    add('Mixer.Ready.Boolean', 'boolean', {
        'kind': 'booleanTimeline', 'afterSteps': 'holdLast',
        'steps': [{'value': value, 'durationMs': minutes * MINUTE}
                  for _ in range(cycles) for value, minutes in ready_cycle]})
    add('Mixer.Temperature.C', 'number', {'kind': 'ramp', 'startValue': 20,
        'ratePerSecond': 0.01, 'minimum': 20, 'maximum': 80})
    for cell in ('A', 'B'):
        feed = 'Packaging' + cell + '.FeedEnabled.Boolean'
        add(feed, 'boolean', {'kind': 'skuRoute', 'skuTag': name('Production.SKU.String'),
            'readyTag': name('Mixer.Ready.Boolean'), 'sku': 'SKU-' + cell})
        # Both variants use the same immutable sequence and seeds. Differences
        # therefore come from gate-clock policy, not independent random draws.
        steps = []
        for cycle in range(cycles + 1):
            steps.append({'pattern': {'kind': 'staircase', 'seed': 100 + cycle,
                'maxTotalDurationMs': 2 * HOUR, 'afterSteps': 'holdLast', 'steps': [
                    {'value': 0, 'durationRangeMs': {'minimum': 15 * MINUTE, 'maximum': 45 * MINUTE}},
                    {'value': 10, 'durationRangeMs': {'minimum': 15 * MINUTE, 'maximum': 45 * MINUTE}},
                    {'value': 20, 'durationMs': 30 * MINUTE}]}})
            steps.append({'pattern': {'kind': 'randomIntegerHold', 'minimum': 21, 'maximum': 29,
                'seed': 1000 + cycle, 'durationMs': 5 * HOUR,
                'holdDurationRangeMs': {'minimum': 15 * MINUTE, 'maximum': 45 * MINUTE},
                'adjacentValues': 'requireChange', 'afterDuration': 'holdLast'}})
        for label, mode in [('PauseClock', 'pauseAndSuppress'), ('ContinueClock', 'continueAndSuppress')]:
            add('Packaging' + cell + '.Speed.' + label, 'number', {
                'kind': 'booleanGate', 'triggerTag': name(feed), 'whenFalse': mode,
                'pattern': {'kind': 'sequence', 'afterSequence': 'holdLast', 'steps': copy.deepcopy(steps)}})
    return {'schemaVersion': 1, 'generatorVersion': 1,
        'session': {'schemaVersion': 1, 'sessionId': identifier,
            'connectionProfile': 'local-development', 'dataset': 'Test',
            'startUtc': utc(start), 'endUtc': utc(start + timedelta(days=days)), 'outputTags': tags},
        'samplingIntervalMs': MINUTE, 'generators': generators}


def utc(value):
    return value.astimezone(timezone.utc).isoformat(timespec='seconds').replace('+00:00', 'Z')


def prepare(root):
    now = datetime.now(timezone.utc).replace(second=0, microsecond=0)
    token = now.strftime('%Y%m%dT%H%M') + '-' + uuid.uuid4().hex[:6]
    folder = root / '.tools' / ('packaging-demo-' + token)
    folder.mkdir(mode=0o700)
    prefix = 'Sim.Packaging.' + token
    start = now - timedelta(hours=72)
    model = build_model(start, prefix, 'packaging-' + token)
    (folder / 'model.json').write_text(json.dumps(model, indent=2) + '\n')
    # The existing bounded preview can show 12 hours (6480 candidate slots).
    # Only its end is shortened; start and complete schedules remain identical.
    preview = copy.deepcopy(model)
    preview['session']['endUtc'] = utc(start + timedelta(hours=12))
    (folder / 'preview-model.json').write_text(json.dumps(preview, indent=2) + '\n')
    manifest = {'folder': str(folder), 'sessionId': model['session']['sessionId'],
        'tagPrefix': prefix, 'dataset': 'Test', 'startUtc': utc(start),
        'plannedCatchupUtc': utc(now), 'endUtc': model['session']['endUtc'],
        'samplingIntervalSeconds': 60, 'tags': model['session']['outputTags'],
        'routing': {'SKU-A': 'PackagingA', 'SKU-B': 'PackagingB'},
        'review': 'NotReviewed'}
    (folder / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    return manifest


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()
    try:
        print(json.dumps(prepare(Path(__file__).resolve().parents[1]), indent=2))
    except OSError:
        parser.exit(1, 'demo.prepare_failed: Cannot save the model. Check local storage permissions and free space; preserve any existing demo folder. No Historian operation was attempted.\n')
