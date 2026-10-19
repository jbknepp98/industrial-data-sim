#!/usr/bin/env python3
"""Build a self-contained candidate from an explicit source-file allowlist.

No deployment state, credentials, certificates or private tooling is collected.
Output is a candidate, not an approved release. Run verify_package.py separately.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import zipfile

RIDS = ('linux-x64', 'osx-arm64', 'win-x64')


def digest(path):
    with path.open('rb') as stream:
        result = hashlib.sha256()
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            result.update(chunk)
        return result.hexdigest()


def build(dotnet, rid, version, destination):
    root = Path(__file__).resolve().parents[1]
    if rid not in RIDS or not re.fullmatch(r'0\.\d+\.\d+-phase4\.\d+', version):
        raise ValueError('Use a declared RID and a prerelease version such as 0.4.0-phase4.1; this tool does not approve v1.0.0.')
    # Never overwrite an existing candidate or blend it with prior outputs.
    destination.mkdir(parents=True, exist_ok=False)
    package = destination / ('industrial-data-sim-' + version + '-' + rid)
    package.mkdir()
    log = destination / 'build.log'
    command = [dotnet, 'publish', str(root / 'src/IndustrialDataSim.Cli'), '-c', 'Release', '-r', rid,
               '--self-contained', 'true', '-o', str(package / 'app'), '--disable-build-servers',
               '-p:Version=' + version, '-p:DebugType=None', '-p:DebugSymbols=false',
               '-p:NuGetAudit=true', '-p:NuGetAuditMode=all']
    with log.open('w') as output:
        subprocess.run(command, cwd=root, stdout=output, stderr=subprocess.STDOUT, check=True, timeout=600)
    # Only explicitly selected public documentation, examples and launch helpers.
    for name in ('constant-simulation.json',):
        target = package / 'examples' / name
        target.parent.mkdir(exist_ok=True)
        shutil.copyfile(root / 'examples' / name, target)
    for name in ('run_production_host.py', 'local_production.py'):
        target = package / 'scripts' / name
        target.parent.mkdir(exist_ok=True)
        shutil.copyfile(root / 'scripts' / name, target)
    for name in ('installation.md', 'state-backup-recovery.md', 'compatibility.md', 'phase-4-security.md', 'phase-4-qualification.md'):
        target = package / 'docs' / name
        target.parent.mkdir(exist_ok=True)
        shutil.copyfile(root / 'docs' / name, target)
    for source in sorted((root / 'deployment').glob('*')):
        if source.suffix in ('.json', '.service', '.plist', '.xml'):
            target = package / 'deployment' / source.name
            target.parent.mkdir(exist_ok=True)
            shutil.copyfile(source, target)
    deps = json.loads((package / 'app/IndustrialDataSim.Cli.deps.json').read_text())
    # Publish omits NuGet license files. Preserve metadata/notices explicitly,
    # including the bundled runtime's license, without collecting cache binaries.
    assets = json.loads((root / 'src/IndustrialDataSim.Cli/obj/project.assets.json').read_text())
    for identity, library in deps['libraries'].items():
        if library['type'] == 'project':
            continue
        relative = identity.removeprefix('runtimepack.').lower()
        source = next((Path(folder) / relative for folder in assets['packageFolders'] if (Path(folder) / relative).is_dir()), None)
        if source is None:
            raise ValueError('Dependency package metadata is unavailable; preserve the package cache and rebuild.')
        notice_dir = package / 'notices' / relative
        notice_dir.mkdir(parents=True)
        for candidate in source.iterdir():
            if candidate.is_file() and (candidate.suffix == '.nuspec' or 'license' in candidate.name.lower() or 'notices' in candidate.name.lower()):
                shutil.copyfile(candidate, notice_dir / candidate.name)
    shutil.copyfile(root / 'third-party/Apache-2.0.txt', package / 'notices/Apache-2.0.txt')
    inventory = {'libraries': deps['libraries'], 'runtimeTarget': deps['runtimeTarget'],
                 'note': 'Runtime components are in app and license metadata/notices are in notices; this inventory is not a vulnerability verdict.'}
    (package / 'dependencies.json').write_text(json.dumps(inventory, indent=2) + '\n')
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()
    dirty = bool(subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=normal'], cwd=root, text=True).strip())
    files = {p.relative_to(package).as_posix(): digest(p) for p in sorted(package.rglob('*')) if p.is_file()}
    manifest = {'format': 1, 'version': version, 'rid': rid, 'commit': commit, 'dirty': dirty,
                'selfContained': True, 'sqliteSchema': 10, 'archiveFormat': 3, 'files': files}
    (package / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    archive = destination / (package.name + '.zip')
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as output:
        for path in sorted(package.rglob('*')):
            if path.is_file():
                output.write(path, path.relative_to(package))
    (destination / (archive.name + '.sha256')).write_text(digest(archive) + '  ' + archive.name + '\n')
    print(json.dumps({'outcome': 'Built', 'rid': rid, 'version': version, 'commit': commit, 'dirty': dirty}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--rid', required=True, choices=RIDS)
    parser.add_argument('--version', default='0.4.0-phase4.1')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        build(args.dotnet, args.rid, args.version, args.output.resolve())
    except (OSError, ValueError, KeyError, subprocess.SubprocessError):
        print(json.dumps({'outcome': 'Failed', 'code': 'package.build', 'location': '$',
                          'message': 'Candidate build failed. Use a new output directory, supported RID and prerelease version. Check SDK/network access and the local build log. No existing candidate is overwritten.'}))
        sys.exit(1)
