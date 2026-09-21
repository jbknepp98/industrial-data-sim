#!/usr/bin/env python3
"""Verify the demo's complete offline output against independent routing rules."""
import argparse
from datetime import datetime, timedelta
import json
from pathlib import Path


def check(manifest, preview):
    if not preview.get('valid'):
        raise ValueError('demo.preview_invalid: Correct the preview diagnostics before publishing.')
    prefix = manifest['tagPrefix'] + '.'
    data = preview['data']
    start = datetime.fromisoformat(manifest['startUtc'].replace('Z', '+00:00'))
    def stamp(minute):
        return (start + timedelta(minutes=minute)).isoformat(timespec='seconds').replace('+00:00', 'Z')
    # Preview dictionaries contain actual samples, not interpolated/current values.
    points = {name[len(prefix):]: {p['t']: p['v'] for p in values} for name, values in data.items()}
    for name, values in data.items():
        times = [p['t'] for p in values]
        if times != sorted(set(times)) or any(p['q'] != 192 for p in values):
            raise ValueError('demo.order_or_quality: Preview timestamps must be unique and increasing with quality 192. Correct the model or generator before publishing.')
    differences = {'A': 0, 'B': 0}
    for minute in range(72 * 60):
        timestamp = stamp(minute)
        sku = 'SKU-A' if (minute // 360) % 2 == 0 else 'SKU-B'
        ready = 15 <= minute % 360 < 105 or minute % 360 >= 135
        if points['Production.SKU.String'].get(timestamp) != sku or points['Mixer.Ready.Boolean'].get(timestamp) != ready:
            raise ValueError('demo.source_mismatch: SKU/readiness samples differ from the intended six-hour schedule. Do not publish until corrected.')
        active = 0
        for cell in ('A', 'B'):
            enabled = ready and sku == 'SKU-' + cell
            if points['Packaging' + cell + '.FeedEnabled.Boolean'].get(timestamp) != enabled:
                raise ValueError('demo.route_mismatch: A cell feed differs from its SKU/readiness rule. Correct routing before publishing.')
            active += int(enabled)
            variants = [points['Packaging' + cell + '.Speed.' + mode] for mode in ('PauseClock', 'ContinueClock')]
            if any((timestamp in series) != enabled for series in variants):
                raise ValueError('demo.gate_mismatch: A sequence emitted during a closed gate or omitted an open slot. Correct gating before publishing.')
            if enabled and variants[0][timestamp] != variants[1][timestamp]:
                differences[cell] += 1
            if any(timestamp in series and not 0 <= series[timestamp] <= 29 for series in variants):
                raise ValueError('demo.range_mismatch: A speed sample is outside 0–29. Inspect the sequence before publishing.')
        if active != int(ready):
            raise ValueError('demo.exclusivity: Exactly one cell must receive product while ready. Correct SKU routes before publishing.')
    if not all(differences.values()):
        raise ValueError('demo.clock_comparison_missing: Both cells must exhibit a pause/continue difference. Adjust interruption timing before publishing.')
    return {'valid': True, 'checkedMinutes': 4320, 'exclusiveRouting': True,
            'closedGateSamples': 0, 'clockDifferenceMinutes': differences,
            'pointCount': preview['pointCount'], 'evidence': 'offline expectations; not Historian receipt or user acceptance'}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('folder', type=Path)
    args = parser.parse_args()
    try:
        result = check(json.loads((args.folder / 'manifest.json').read_text()),
                       json.loads((args.folder / 'preview-72h.json').read_text()))
        (args.folder / 'offline-verification.json').write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps(result, indent=2))
    except ValueError as error:
        if str(error).startswith('demo.'):
            parser.exit(1, str(error) + '\n')
        parser.exit(1, 'demo.preview_read_failed: Preview JSON could not be read. Regenerate the offline preview and inspect its exit status.\n')
    except (OSError, KeyError, TypeError):
        parser.exit(1, 'demo.preview_read_failed: Check the manifest and preview files, permissions and expected schema. No Historian operation was performed.\n')
