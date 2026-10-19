#!/usr/bin/env python3
"""Verify an extracted candidate with no SDK, source checkout or live credentials.

Uses synthetic temporary state only. Digest checks detect corruption, not a
malicious replacement of both archive and receipt; obtain receipts independently.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import zipfile
from package_release import digest


def require(condition, message):
    if not condition:
        raise ValueError(message)


def verify(archive, require_clean=False):
    receipt = archive.with_name(archive.name + '.sha256').read_text().split()[0]
    require(digest(archive) == receipt, 'Archive checksum mismatch')
    with tempfile.TemporaryDirectory(prefix='sim-package-') as folder:
        root = Path(folder)
        package = root / 'package'
        with zipfile.ZipFile(archive) as source:
            names = source.namelist()
            require(len(names) == len(set(names)), 'Duplicate archive entries')
            for name in names:
                require(not Path(name).is_absolute() and '..' not in Path(name).parts and '\\' not in name and ':' not in name, 'Unsafe archive entry')
            source.extractall(package)
        manifest = json.loads((package / 'manifest.json').read_text())
        require(not require_clean or manifest['dirty'] is False, 'Release candidate must have a clean source tree')
        actual = {p.relative_to(package).as_posix() for p in package.rglob('*') if p.is_file()} - {'manifest.json'}
        require(actual == set(manifest['files']), 'Unexpected or missing package file')
        for name, expected in manifest['files'].items():
            require(digest(package / name) == expected, 'File checksum mismatch')
            require(not name.endswith(('.pem', '.pfx', '.key', '.sqlite', '.db', '.log')) and '/.env' not in name, 'Private artifact included')
        runtime = json.loads((package / 'app/IndustrialDataSim.Cli.runtimeconfig.json').read_text())
        require(bool(runtime['runtimeOptions'].get('includedFrameworks')), 'Bundled runtime declaration missing')
        require(any(name.endswith('THIRD-PARTY-NOTICES.TXT') for name in actual), 'Runtime notices missing')
        require('notices/Apache-2.0.txt' in actual, 'SQLitePCLRaw license missing')
        # Only public text is scanned; never echo matching content in diagnostics.
        for name in actual:
            path = package / name
            if path.suffix in ('.json', '.py', '.md', '.xml', '.plist', '.service'):
                text = path.read_text(encoding='utf-8-sig')
                require('-----BEGIN PRIVATE KEY-----' not in text and '-----BEGIN RSA PRIVATE KEY-----' not in text, 'Private key material found')
        binary = package / 'app' / ('IndustrialDataSim.Cli.exe' if os.name == 'nt' else 'IndustrialDataSim.Cli')
        if os.name != 'nt':
            binary.chmod(0o700)
        env = {key: value for key, value in os.environ.items() if not key.startswith(('TIMEBASE_', 'DOTNET_', 'COREHOST_'))}
        # Apphost must use its bundled runtime even with no installed-runtime path.
        env.update(DOTNET_ROOT=str(root / 'no-runtime'), DOTNET_MULTILEVEL_LOOKUP='0')
        command = [str(binary)]
        database = str(root / 'state.sqlite')
        model = package / 'examples/constant-simulation.json'
        model_id = json.loads(model.read_text())['session']['sessionId']

        def invoke(*args, expected=0):
            result = subprocess.run(command + list(args), cwd=root, env=env, capture_output=True, text=True, timeout=30)
            require(result.returncode == expected, 'Unexpected CLI status')
            return json.loads(result.stdout)

        invoke('validate-simulation', str(model))
        invoke('session', 'start', database, str(model))
        invoke('session', 'pause', database, model_id)
        for _ in range(2):
            host = subprocess.Popen(command + ['host', 'run', database], cwd=root, env=env,
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                deadline = time.monotonic() + 20
                while time.monotonic() < deadline:
                    probe = subprocess.run(command + ['host', 'status', database], env=env, cwd=root, capture_output=True, timeout=5)
                    if probe.returncode == 0:
                        break
                    require(host.poll() is None, 'Host exited before ready')
                    time.sleep(.1)
                else:
                    raise ValueError('Host readiness deadline exceeded')
                refused = invoke('session', 'status', database, model_id, expected=1)
                require(refused['errors'][0]['code'] == 'runtime.owner_unavailable', 'Second owner was not refused')
                invoke('host', 'stop', database)
                host.communicate(timeout=30)
                require(host.returncode == 0, 'Graceful stop failed')
            finally:
                if host.poll() is None:
                    host.kill()
                    host.communicate(timeout=5)
        invoke('session', 'resume', database, model_id)
        invoke('session', 'run-simulated', database, '100')
        state = invoke('session', 'status', database, model_id)
        require(state['result']['session']['status'] == 'Complete', 'Synthetic session incomplete')
        print(json.dumps({'outcome': 'Passed', 'rid': manifest['rid'], 'version': manifest['version'],
                          'commit': manifest['commit'], 'dirty': manifest['dirty'], 'historianWrites': 0,
                          'checks': ['archive/file hashes', 'standalone runtime', 'native SQLite', 'exclusive owner', 'graceful stop', 'restart', 'synthetic completion']}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('archive', type=Path)
    parser.add_argument('--require-clean', action='store_true')
    args = parser.parse_args()
    try:
        verify(args.archive.resolve(), args.require_clean)
    except (OSError, ValueError, KeyError, IndexError, subprocess.SubprocessError, zipfile.BadZipFile):
        print(json.dumps({'outcome': 'Failed', 'code': 'package.verification', 'location': '$',
                          'message': 'Candidate verification failed. Check its independent checksum, platform/architecture, native OS prerequisites and temporary-directory permissions. Rebuild and inspect in an isolated test environment; no live credentials or database are used.'}))
        raise SystemExit(1)
