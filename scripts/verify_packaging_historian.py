#!/usr/bin/env python3
"""Read-only demo evidence: compare returned records, never prove receipt by count.

Only token acquisition uses POST. This tool never publishes, retries a write or
changes user-review status. Its connection settings stay in the process environment.
"""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import ssl
import urllib.error
import urllib.parse
import urllib.request
from local_production import environment


def read_evidence(folder, settings):
    manifest = json.loads((folder / 'manifest.json').read_text())
    preview = json.loads((folder / 'preview-72h.json').read_text())
    if manifest['dataset'] != 'Test' or not manifest['tagPrefix'].startswith('Sim.Packaging.'):
        raise ValueError('demo.dataset_guard: This reader is limited to synthetic Sim.Packaging tags in Test. Supply the prepared demonstration folder.')
    names = [tag['name'] for tag in manifest['tags']]
    if len(names) != 9 or any(not name.startswith(manifest['tagPrefix'] + '.') for name in names):
        raise ValueError('demo.tag_guard: Manifest must contain the nine tags under its fresh demonstration prefix.')
    # The primary client enforces all production connection rules. This explicit
    # local diagnostic accepts HTTPS origins only and disables redirects too.
    for key in ('TIMEBASE_BASE_URL', 'TIMEBASE_PULSE_URL'):
        url = urllib.parse.urlsplit(settings[key])
        if url.scheme != 'https' or not url.hostname or url.username is not None or url.password is not None or url.path not in ('', '/') or url.query or url.fragment:
            raise ValueError('demo.connection_origin: Use HTTPS service origins without credentials, paths or query strings.')
    context = ssl.create_default_context(cafile=settings.get('TIMEBASE_CA_BUNDLE') or None)
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, request, fp, code, message, headers, newurl):
            return None
    opener = urllib.request.build_opener(NoRedirect(), urllib.request.HTTPSHandler(context=context))
    def request(url, data=None, token=None, limit=4 * 1024 * 1024):
        message = urllib.request.Request(url, data=data,
            headers={'Authorization': 'Bearer ' + token} if token else {})
        with opener.open(message, timeout=20) as response:
            raw = response.read(limit + 1)
        if len(raw) > limit:
            raise ValueError('demo.response_limit: Diagnostic response exceeded its bounded size. Reduce the read range before another read; no samples were published.')
        return json.loads(raw)
    body = urllib.parse.urlencode({'grant_type': 'client_credentials',
        'client_id': settings['TIMEBASE_CLIENT_ID'], 'client_secret': settings['TIMEBASE_CLIENT_SECRET'],
        'audience': settings['TIMEBASE_AUDIENCE']}).encode()
    token = request(settings['TIMEBASE_PULSE_URL'].rstrip('/') + '/auth/token', body, limit=65536)['access_token']
    base = settings['TIMEBASE_BASE_URL'].rstrip('/') + '/api/datasets/Test/data?'
    tags = [('tagname', name) for name in names]
    ranged = request(base + urllib.parse.urlencode(tags + [('start', manifest['startUtc']),
                     ('end', manifest['plannedCatchupUtc'])]), token=token)
    current = request(base + urllib.parse.urlencode(tags), token=token)
    def timestamp(value):
        return datetime.fromisoformat(value.replace('Z', '+00:00'))
    summaries = []
    for name in names:
        expected = {timestamp(p['t']): p for p in preview['data'][name]}
        returned = next((tag['d'] for tag in ranged['tl'] if tag['t']['n'] == name), [])
        matches, mismatches, unknown = [], 0, 0
        for point in returned:
            instant = timestamp(point['t'])
            if instant < timestamp(manifest['startUtc']) or instant >= timestamp(manifest['plannedCatchupUtc']):
                continue
            if instant not in expected:
                unknown += 1
            elif point.get('v') == expected[instant]['v'] and point['q'] == expected[instant]['q']:
                matches.append(point)
            else:
                mismatches += 1
        latest = next((tag['d'] for tag in current['tl'] if tag['t']['n'] == name), [])
        summaries.append({'tag': name, 'matchedHistoricalSamples': len(matches),
            'differentObservedSamples': mismatches, 'unexpectedHistoricalTimestamps': unknown,
            'observedHistoricalChanges': len({json.dumps(p.get('v'), sort_keys=True) for p in matches}) > 1,
            'currentNonNull': any(p.get('v') is not None for p in latest),
            'current': latest[-1] if latest else None})
    report = {'readUtc': datetime.now(timezone.utc).isoformat(), 'dataset': 'Test', 'tags': summaries,
        'allTagsHaveMatchingHistoricalEvidence': all(t['matchedHistoricalSamples'] > 0 for t in summaries),
        'allCurrentValuesNonNull': all(t['currentNonNull'] for t in summaries),
        'differentObservedSamples': sum(t['differentObservedSamples'] for t in summaries),
        'unexpectedHistoricalTimestamps': sum(t['unexpectedHistoricalTimestamps'] for t in summaries),
        'userReview': 'NotReviewed', 'message': 'Partial arrival/pattern evidence only. Missing samples and counts cannot establish receipt or authorize replay.'}
    (folder / 'historian-read-review.json').write_text(json.dumps(report, indent=2) + '\n')
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('folder', type=Path)
    parser.add_argument('--environment-file', type=Path)
    args = parser.parse_args()
    try:
        result = read_evidence(args.folder, environment(args.environment_file))
        print(json.dumps({key: value for key, value in result.items() if key != 'tags'}, indent=2))
        if result['differentObservedSamples'] or result['unexpectedHistoricalTimestamps']:
            parser.exit(1, 'demo.observed_mismatch: Inspect the saved read-only report. Do not replay historical samples.\n')
        if not result['allTagsHaveMatchingHistoricalEvidence'] or not result['allCurrentValuesNonNull']:
            parser.exit(4, 'demo.arrival_unconfirmed: Some arrival/current-value indicators are missing. Inspect the saved report and Dataset retention; no replay is authorized.\n')
    except ValueError as error:
        if str(error).startswith('demo.') or str(error).startswith('production.'):
            parser.exit(1, str(error) + '\n')
        parser.exit(1, 'demo.read_format: Diagnostic input or response was malformed. Inspect API compatibility and prepared files; no writes were attempted.\n')
    except (OSError, KeyError, TypeError, StopIteration):
        parser.exit(1, 'demo.read_failed: Check service availability, credentials, CA trust and prepared files. No writes were attempted or retried.\n')
