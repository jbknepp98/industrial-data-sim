#!/usr/bin/env python3
"""Run the October 7 audit's expected-failing regressions without live services.

The probe source lives outside the normal test glob. Temporarily include it,
run only those probes, then remove the inclusion and rebuild the normal suite.
Exit 1 means at least one desired behavior still fails; inspect the test output.
No existing source file is overwritten. Do not run concurrently with other builds.
"""
import argparse
from pathlib import Path
import subprocess


def run(dotnet):
    root = Path(__file__).resolve().parents[1]
    source = root / 'scripts/audit_probes/2026-10-07.cs'
    temporary = root / 'tests/IndustrialDataSim.Tests/Audit20261007Probes.cs'
    build = [dotnet, 'build', 'IndustrialDataSim.slnx', '-c', 'Release', '--no-restore', '--disable-build-servers']
    created = False
    result = 3
    try:
        with temporary.open('xb') as target:
            created = True
            target.write(source.read_bytes())
        result = subprocess.run([dotnet, 'test', 'IndustrialDataSim.slnx', '-c', 'Release', '--no-restore',
                                 '--disable-build-servers', '--filter', 'FullyQualifiedName~Audit20261007Probes',
                                 '--logger', 'console;verbosity=detailed'], cwd=root, timeout=180).returncode
    finally:
        if created:
            temporary.unlink()
            rebuilt = subprocess.run(build, cwd=root, timeout=180)
            if rebuilt.returncode:
                print('audit.rebuild_failed: The temporary probe source was removed. Rebuild the normal Release solution before using --no-build tests.')
                result = 3
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    args = parser.parse_args()
    try:
        raise SystemExit(run(args.dotnet))
    except (OSError, subprocess.TimeoutExpired):
        parser.exit(3, 'audit.execution_failed: Check the SDK, restored dependencies and temporary probe filename; preserve existing files and rebuild before retrying.\n')
