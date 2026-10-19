#!/usr/bin/env python3
"""Fail closed on audit findings/feed problems without publishing raw paths."""
import json
from pathlib import Path
import sys


def validate(report):
    if report.get('version') != 1 or not report.get('projects') or not report.get('sources'):
        raise ValueError('Missing audit evidence')
    def inspect(value):
        if isinstance(value, dict):
            for key, item in value.items():
                if key in ('vulnerabilities', 'problems', 'errors') and item:
                    raise ValueError('Audit findings need review')
                inspect(item)
        elif isinstance(value, list):
            for item in value:
                inspect(item)
    inspect(report)


if __name__ == '__main__':
    try:
        validate(json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig')))
    except (OSError, ValueError, IndexError, TypeError, AttributeError):
        print(json.dumps({'outcome': 'Failed', 'code': 'package.dependency_audit', 'location': '$',
                          'message': 'Audit evidence is missing, malformed or contains findings/feed errors. Review the local audit report and resolve the cause before packaging; do not suppress vulnerability warnings.'}))
        raise SystemExit(1)
    print(json.dumps({'outcome': 'Passed', 'message': 'No vulnerabilities or feed problems reported by this NuGet audit; repeat for the release candidate.'}))
