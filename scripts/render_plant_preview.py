#!/usr/bin/env python3
"""Turn a `plant preview` JSON file into a review page folder (index.html + page-data.json).

Usage: render_plant_preview.py <preview.json> <output-folder> [--title TEXT] [--note TEXT ...]
Standard library only. The page shows each line's states, every breakdown with its
signals and records side by side, and every table's final rows. Publish the folder
as an artifact for owner review.
"""
import argparse
import json
import shutil
from datetime import datetime
from pathlib import Path


def parse(text):
    # Python 3.9 accepts at most 6 fractional digits; the simulator writes whole seconds.
    return datetime.fromisoformat(text.replace('Z', '+00:00'))


def build(preview, title=None, notes=()):
    tables = preview['tables']
    prefix = preview['simulation'].lower() + '_'
    start = parse(preview['startUtc'])
    hours = lambda s: round((parse(s) - start).total_seconds() / 3600, 4) if s else None
    tags = {t['name']: t for t in preview['tags']}
    root = preview['tags'][0]['name'].split('.')[0] if preview['tags'] else preview['simulation']

    def series(name, a, b):
        return [[hours(t), v] for t, v in tags[name]['points'] if a <= hours(t) <= b] if name in tags else []

    tickets = tables.get(prefix + 'maintenancetickets', [])
    status = tables.get(prefix + 'maintenanceticketstatus', [])
    downtime = {r['downtime_id']: r for r in tables.get(prefix + 'downtimeevents', [])}
    parts = tables.get(prefix + 'partsusage', [])
    incidents = []
    for f in preview['failures']:
        ticket = next((t for t in tickets if t['ticket_type'] == 'Corrective' and t['downtime_id']
                       and downtime[t['downtime_id']]['start_utc'] == f['failedUtc'] and downtime[t['downtime_id']]['line_id'] == f['line']), None)
        failed = hours(f['failedUtc'])
        repaired = hours(f['repairedUtc']) if f['repairedUtc'] else None
        window = [max(0, failed - max(f['warningHours'], 2) - 1), (repaired if repaired else failed + 2) + 1.5]
        line, unit = f['line'], f['equipment']
        trends = {'speed': series(f'{root}.{line}.Speed', *window), 'vibration': series(f'{root}.{line}.{unit}.Vibration', *window)}
        if f'{root}.{line}.{unit}.FillVolume' in tags:
            trends['fill'] = series(f'{root}.{line}.{unit}.FillVolume', *window)
        records = {}
        if ticket:
            records = {'ticket': ticket, 'downtime': downtime[ticket['downtime_id']],
                       'status': [s for s in status if s['ticket_id'] == ticket['ticket_id']],
                       'parts': [p for p in parts if p['ticket_id'] == ticket['ticket_id']]}
        incidents.append({'line': line, 'unit': unit, 'mode': f['mode'], 'failed': failed, 'repaired': repaired,
                          'warning': f['warningHours'], 'planted': f['planted'], 'window': window, 'trends': trends, 'records': records})
    return {
        'simulation': preview['simulation'], 'title': title, 'notes': list(notes), 'prefix': prefix,
        'start': preview['startUtc'], 'end': preview['endUtc'], 'anchor': preview['anchorUtc'],
        'hours': hours(preview['endUtc']), 'anchorHour': hours(preview['anchorUtc']),
        'lines': sorted({s['line'] for s in preview['segments']}),
        'summary': preview['summary'],
        'segments': [[s['line'], hours(s['start']), hours(s['end']), s['state'], s['sku'], s['order'], s['reason'], s['unit']] for s in preview['segments']],
        'incidents': incidents,
        'modes': {m['failure_code']: m['description'] for m in tables.get(prefix + 'failurecodes', [])},
        'pm': [{'line': t['equipment_id'].split('.')[0], 'start': hours(t['started_utc']), 'id': t['ticket_id']} for t in tickets if t['ticket_type'] == 'Preventive'],
        'tables': tables, 'changeCount': preview['changeCount'], 'tagCount': len(preview['tags'])
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('preview', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--title')
    parser.add_argument('--note', action='append', default=[])
    args = parser.parse_args()
    data = build(json.loads(args.preview.read_text()), args.title, args.note)
    args.output.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(Path(__file__).resolve().parents[1] / 'tools/plant-preview/index.html', args.output / 'index.html')
    (args.output / 'page-data.json').write_text(json.dumps(data, separators=(',', ':')))
    print(json.dumps({'outcome': 'Rendered', 'folder': str(args.output), 'breakdowns': len(data['incidents']),
                      'tables': len(data['tables'])}))


if __name__ == '__main__':
    main()
