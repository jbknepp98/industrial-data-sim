#!/usr/bin/env python3
"""Read-only Pulse authentication and Historian Dataset-shape compatibility probe.

The only POST is OAuth token acquisition. No data publication or state migration.
Responses/tokens/configuration are never printed. Redirects are refused.
"""
import argparse
import json
from pathlib import Path
import ssl
from urllib.parse import urlencode, urlsplit
from urllib.request import Request, build_opener, HTTPSHandler, HTTPRedirectHandler
from local_production import environment


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def probe(settings):
    for key in ('TIMEBASE_BASE_URL', 'TIMEBASE_PULSE_URL'):
        origin = urlsplit(settings[key])
        if origin.scheme != 'https' or not origin.hostname or origin.path not in ('', '/') or origin.query or origin.fragment or origin.username or origin.password:
            raise ValueError('compatibility.origin: Use HTTPS origins without paths or embedded credentials.')
    context = ssl.create_default_context(cafile=settings.get('TIMEBASE_CA_BUNDLE') or None)
    opener = build_opener(HTTPSHandler(context=context), NoRedirect())

    def read(request):
        with opener.open(request, timeout=20) as response:
            if response.status != 200:
                raise ValueError('compatibility.status: Expected HTTP 200; check permissions and API compatibility.')
            data = response.read(65537)
            if len(data) > 65536:
                raise ValueError('compatibility.response_limit: Expected a bounded settings/token response; check service routing.')
            return json.loads(data)

    token = read(Request(settings['TIMEBASE_PULSE_URL'].rstrip('/') + '/auth/token',
                         data=urlencode({'grant_type': 'client_credentials', 'client_id': settings['TIMEBASE_CLIENT_ID'],
                                         'client_secret': settings['TIMEBASE_CLIENT_SECRET'], 'audience': settings['TIMEBASE_AUDIENCE']}).encode()))
    if not isinstance(token.get('access_token'), str) or not token['access_token']:
        raise ValueError('compatibility.token_shape: Pulse token response lacks a usable access token; check API compatibility.')
    headers = {'Authorization': 'Bearer ' + token['access_token']}
    base = settings['TIMEBASE_BASE_URL'].rstrip('/') + '/api/datasets/Test'
    if read(Request(base + '/exists', headers=headers)) is not True:
        raise ValueError('compatibility.dataset_missing: Test Dataset was not confirmed. Check its existence and client permissions; no Dataset was created.')
    result = read(Request(base, headers=headers))
    if not isinstance(result, dict) or any(type(result.get(key)) is not int or result[key] < 0 for key in ('pa', 'ps')):
        raise ValueError('compatibility.settings_shape: Dataset settings do not match the adapter contract; investigate without publishing.')
    return {'outcome': 'Passed', 'dataset': 'Test', 'authentication': 'Passed', 'tls': 'Verified',
            'datasetShape': 'Passed', 'purgeAgeDays': result['pa'], 'purgeSize': result['ps'],
            'historianWrites': 0, 'note': 'Read-only Python probe; exact packaged adapter publication/arrival remains RC acceptance.'}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--environment-file', type=Path, required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(probe(environment(args.environment_file))))
    except Exception as error:
        # Network/parser exceptions may embed service addresses or response data.
        message = str(error) if isinstance(error, ValueError) and str(error).startswith('compatibility.') else 'compatibility.connection: Read-only probe failed. Check HTTPS origins, PEM root trust, client credentials/audience, service availability and Test permissions. No writes or automatic retries occurred.'
        print(json.dumps({'outcome': 'Failed', 'location': '$', 'message': message}))
        raise SystemExit(1)
