#!/usr/bin/env python3
"""Validate startup template structure/native syntax without installing a service."""
import argparse
import json
from pathlib import Path
import plistlib
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET


def verify(package):
    deployment = package / 'deployment'
    config = json.loads((deployment / 'host.example.json').read_text())
    if config['maximumRestarts'] != 0 or 'executable' not in config:
        raise ValueError('Template must select one restart owner and packaged launcher')
    plist = plistlib.loads((deployment / 'com.timebase.simulator.plist').read_bytes())
    if plist['KeepAlive'] is not False or plist['ExitTimeOut'] < 120:
        raise ValueError('Launchd template must not layer automatic retries')
    task = ET.parse(deployment / 'industrial-data-sim.task.xml')
    ns = {'t': 'http://schemas.microsoft.com/windows/2004/02/mit/task'}
    if task.findtext('.//t:MultipleInstancesPolicy', namespaces=ns) != 'IgnoreNew' or task.findtext('.//t:AllowHardTerminate', namespaces=ns) != 'false':
        raise ValueError('Scheduled task must prohibit overlapping/hard-stopped instances')
    with tempfile.TemporaryDirectory(prefix='sim-template-') as folder:
        if sys.platform == 'darwin':
            command = ['plutil', '-lint', str(deployment / 'com.timebase.simulator.plist')]
        elif sys.platform == 'win32':
            # Parse through Task Scheduler's native schema, but never RegisterTask.
            # Pass the path as an argument, not interpolated PowerShell code.
            script = Path(folder) / 'validate.ps1'
            script.write_text('param([string]$Template)\n$ErrorActionPreference="Stop"\n$service=New-Object -ComObject "Schedule.Service"\n$service.Connect()\n$task=$service.NewTask(0)\n$task.XmlText=[IO.File]::ReadAllText($Template)\n')
            command = ['powershell', '-NoProfile', '-File', str(script), str(deployment / 'industrial-data-sim.task.xml')]
        else:
            service = Path(folder) / 'industrial-data-sim.service'
            content = (deployment / 'industrial-data-sim.service').read_text()
            content = content.replace('/absolute/python3', sys.executable).replace('/absolute/package', str(package)).replace('/absolute/private', folder)
            service.write_text(content)
            command = ['systemd-analyze', 'verify', str(service)]
        result = subprocess.run(command, capture_output=True, timeout=30)
        if result.returncode:
            raise ValueError('Native template parser rejected the template')
    print(json.dumps({'outcome': 'Passed', 'checks': ['restart ownership', 'native template syntax'], 'servicesInstalled': 0}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('package', type=Path)
    args = parser.parse_args()
    try:
        verify(args.package.resolve())
    except (OSError, ValueError, KeyError, ET.ParseError, subprocess.SubprocessError):
        print(json.dumps({'outcome': 'Failed', 'code': 'package.startup_template', 'location': '$',
                          'message': 'Startup template validation failed. Review template structure, restart ownership and the platform native parser. No service was registered or started.'}))
        raise SystemExit(1)
