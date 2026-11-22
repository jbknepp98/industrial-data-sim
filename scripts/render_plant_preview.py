#!/usr/bin/env python3
"""Turn a `plant preview` JSON file into a review page folder.

Usage: render_plant_preview.py <preview.json> <output-folder> [--title TEXT] [--note TEXT ...]
Standard library only. Writes index.html, plant-preview.js, plant-preview.css
(copied from tools/plant-preview) and page-data.json ({title, notes, preview}).
The shared renderer (plant-preview.js, also used by the web container's
interview page) turns the preview into each line's states, every breakdown
with its signals and records side by side, and every table's final rows.
Publish the folder as an artifact for owner review.
"""
import argparse
import json
import shutil
from pathlib import Path

TEMPLATE_FILES = ('index.html', 'plant-preview.js', 'plant-preview.css')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('preview', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--title')
    parser.add_argument('--note', action='append', default=[])
    args = parser.parse_args()
    preview = json.loads(args.preview.read_text())
    template = Path(__file__).resolve().parents[1] / 'tools/plant-preview'
    args.output.mkdir(parents=True, exist_ok=True)
    for name in TEMPLATE_FILES:
        shutil.copyfile(template / name, args.output / name)
    page = {'title': args.title, 'notes': args.note, 'preview': preview}
    (args.output / 'page-data.json').write_text(json.dumps(page, separators=(',', ':')))
    print(json.dumps({'outcome': 'Rendered', 'folder': str(args.output), 'breakdowns': len(preview['failures']),
                      'tables': len(preview['tables'])}))


if __name__ == '__main__':
    main()
