#!/usr/bin/env python3
"""Launch a production host from explicit local configuration; never re-admit models.

A bounded restart policy may restart the process, not a failed/uncertain batch.
The durable runtime preserves delivery uncertainty. Ctrl+C/TERM requests graceful
child shutdown and never triggers an automatic restart.
"""
import argparse
import json
from pathlib import Path
import signal
import subprocess
import threading
from local_production import environment


def load_config(path):
    with path.open('rb') as source:
        raw = source.read(65537)
    if len(raw) > 65536:
        raise ValueError('host.config_limit: Keep startup configuration within 64 KiB.')
    config = json.loads(raw)
    allowed = {'database', 'environmentFile', 'dotnet', 'maximumRestarts'}
    if not isinstance(config, dict) or set(config) - allowed or any(not isinstance(config.get(key), str) or not config[key] for key in ('database', 'environmentFile', 'dotnet')):
        raise ValueError('host.config_fields: Supply database, environmentFile and dotnet paths, plus optional maximumRestarts; do not put credentials in startup JSON.')
    restarts = config.get('maximumRestarts', 0)
    if type(restarts) is not int or not 0 <= restarts <= 3:
        raise ValueError('host.restart_limit: maximumRestarts must be an integer from 0 through 3; zero disables automatic process restart.')
    config['maximumRestarts'] = restarts
    for key in ('database', 'environmentFile', 'dotnet'):
        config[key] = str((path.parent / config[key]).resolve())
    if not Path(config['database']).is_file():
        raise ValueError('host.state_missing: Admit the first production model before hosting; startup never creates or replaces state.')
    return config


def run(config):
    root = Path(__file__).resolve().parents[1]
    settings = environment(Path(config['environmentFile']))
    command = [config['dotnet'], str(root / 'src/IndustrialDataSim.Cli/bin/Release/net10.0/IndustrialDataSim.Cli.dll'),
               'production-host', 'run', config['database']]
    stopping = threading.Event()
    child = None
    def stop(*_):
        stopping.set()
        if child is not None and child.poll() is None:
            child.send_signal(signal.CTRL_BREAK_EVENT if hasattr(signal, 'CTRL_BREAK_EVENT') else signal.SIGINT)
    signal.signal(signal.SIGINT, stop)
    signal.signal(signal.SIGTERM, stop)
    for attempt in range(config['maximumRestarts'] + 1):
        if stopping.is_set():
            return 130
        options = {'creationflags': subprocess.CREATE_NEW_PROCESS_GROUP} if hasattr(subprocess, 'CREATE_NEW_PROCESS_GROUP') else {'start_new_session': True}
        child = subprocess.Popen(command, env=settings, **options)
        if stopping.is_set():
            stop()
        code = child.wait()
        if stopping.is_set() or code in (0, 130) or attempt == config['maximumRestarts']:
            return code if code >= 0 else 1
        print('host.process_restart: Host exited unexpectedly. Reopening existing durable state after backoff; failed/uncertain sessions are not reset.', flush=True)
        if stopping.wait(2 ** attempt):
            return 130
    return 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('config', type=Path)
    args = parser.parse_args()
    try:
        raise SystemExit(run(load_config(args.config.resolve())))
    except ValueError as error:
        parser.exit(1, str(error) + '\n' if str(error).startswith('host.') else 'host.config_invalid: Correct the startup JSON; values were not printed.\n')
    except (OSError, KeyError, TypeError):
        parser.exit(1, 'host.startup_access: Check startup paths, file permissions and the Release build. Preserve existing state before another attempt.\n')
