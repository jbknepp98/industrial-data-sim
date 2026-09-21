#!/usr/bin/env python3
"""Run an explicit production command with a local environment file.

This helper does not print settings or automatically retry commands. It replaces
itself with the CLI so its PID receives Ctrl+C/SIGINT directly. The environment
file is optional, must remain ignored, and is never interpreted as shell code.
"""
import argparse
import os
from pathlib import Path

SETTINGS = {'TIMEBASE_PROFILE', 'TIMEBASE_BASE_URL', 'TIMEBASE_PULSE_URL',
            'TIMEBASE_CLIENT_ID', 'TIMEBASE_CLIENT_SECRET', 'TIMEBASE_AUDIENCE',
            'TIMEBASE_CA_BUNDLE'}


def environment(path):
    settings = os.environ.copy()
    if path:
        with path.open("rb") as source:
            raw = source.read(65537)
        if len(raw) > 65536:
            raise ValueError('production.environment_limit: Keep the local environment file within 64 KiB; remove unrelated content.')
        for line in raw.decode("utf-8").splitlines():
            line = line.strip()
            if not line or line.startswith('#') or '=' not in line:
                continue
            key, value = line.split('=', 1)
            key = key.strip()
            if key in SETTINGS:
                settings[key] = value.strip().strip('"').strip("'")
    return settings


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--environment-file', type=Path)
    parser.add_argument('arguments', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if not args.arguments:
        parser.error('Supply production arguments, for example follow state/production.db 3600.')
    try:
        root = Path(__file__).resolve().parents[1]
        executable = str(args.dotnet.resolve())
        command = [executable, str(root / 'src/IndustrialDataSim.Cli/bin/Release/net10.0/IndustrialDataSim.Cli.dll'),
                   'production', *args.arguments]
        os.execve(executable, command, environment(args.environment_file))
    except ValueError as error:
        if str(error).startswith('production.'):
            parser.exit(1, str(error) + '\n')
        parser.exit(1, 'production.environment_invalid: Check local environment file encoding and paths. No settings were printed or commands retried.\n')
    except OSError:
        parser.exit(1, 'production.launch_failed: Check the local SDK, Release build and environment-file permissions. Inspect existing durable state before restarting.\n')
