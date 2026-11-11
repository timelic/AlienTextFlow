#!/usr/bin/env python3
"""Build, install and restore AlienTextFlow without changing save files."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
DOTNET = shutil.which('dotnet') or str(Path.home() / '.dotnet/dotnet')
DEFAULT_APP = Path.home() / 'Library/Application Support/Steam/steamapps/common/Message From Aliens/mac.app'
RUNTIME = ROOT / 'src/AlienTextFlow/bin/Release/netstandard2.1/AlienTextFlow.dll'
PATCHER = ROOT / 'src/AlienTextFlow.Patcher/bin/Release/net10.0/AlienTextFlow.Patcher.dll'
CONFIG = {'Enabled': True, 'MinWidthPercent': 75, 'FontSizeMultiplier': 1.0, 'MinFontSizePixels': 1.0, 'AllowTwoLines': True, 'TwoLineMinimumGain': 1.12}

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def run(*args, **kwargs):
    return subprocess.run([str(a) for a in args], check=True, **kwargs)

def build():
    for project in ['AlienTextFlow', 'AlienTextFlow.Patcher']:
        run(DOTNET, 'build', ROOT / f'src/{project}/{project}.csproj', '-c', 'Release', '--nologo')

def paths(app):
    managed = app / 'Contents/Resources/Data/Managed'
    if not (managed / 'Assembly-CSharp.dll').is_file():
        raise RuntimeError('Game assembly not found under ' + str(app))
    return managed, app.parent / 'AlienTextFlow-install'

def ensure_stopped(app):
    executable = app / 'Contents/MacOS/MessageFromAliens'
    processes = subprocess.check_output(['ps', '-axo', 'comm='], text=True)
    if any(line.strip() == str(executable) for line in processes.splitlines()):
        raise RuntimeError('Please exit this game instance before installing or restoring: ' + str(executable))

def atomic_copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    handle, name = tempfile.mkstemp(prefix='.atf-', dir=target.parent)
    os.close(handle)
    try:
        shutil.copy2(source, name)
        os.replace(name, target)
    finally:
        Path(name).unlink(missing_ok=True)

def sign(app):
    # Keep the game's existing entitlements. No loader, executable-memory or
    # library-validation exceptions are added by this managed patch.
    run('/usr/bin/codesign', '--force', '--sign', '-', '--preserve-metadata=entitlements,identifier,requirements', app)
    run('/usr/bin/codesign', '--verify', '--deep', '--strict', app)

def install(app):
    ensure_stopped(app)
    managed, store = paths(app)
    original = managed / 'Assembly-CSharp.dll'
    state_path = store / 'state.json'
    state = json.loads(state_path.read_text()) if state_path.exists() else None
    current_hash = digest(original)
    if state and current_hash == state['patchedSha256']:
        source = store / state['backup']
        if digest(source) != state['originalSha256']: raise RuntimeError('Original backup hash mismatch.')
    elif state and current_hash != state['originalSha256']:
        # Patch today's game as a new baseline. The patcher rejects any assembly
        # that still contains our reference, so changed patched binaries cannot
        # silently get a second hook layer. Never restore the previous build.
        source = original
    else:
        source = original
    manifest = json.loads((ROOT / 'fonts/manifest.json').read_text())
    for font in manifest['fonts']:
        if digest(ROOT / 'fonts' / font['file']) != font['sha256']:
            raise RuntimeError('Packaged font hash mismatch: ' + font['file'])
    metrics = manifest.get('advanceMetrics')
    if not metrics or digest(ROOT / 'fonts' / metrics['file']) != metrics['sha256']:
        raise RuntimeError('Packaged advance metrics missing or hash mismatch.')
    if not RUNTIME.exists() or not PATCHER.exists(): raise RuntimeError('Run build first.')
    store.mkdir(parents=True, exist_ok=True)
    backup = store / ('original-' + digest(source) + '.dll')
    if not backup.exists(): atomic_copy(source, backup)
    with tempfile.TemporaryDirectory(prefix='atf-install-') as directory:
        staged = Path(directory) / 'Assembly-CSharp.dll'
        run(DOTNET, PATCHER, 'patch', backup, staged, RUNTIME)
        destinations = [(RUNTIME, managed / 'AlienTextFlow.dll')]
        for f in (ROOT / 'fonts').iterdir():
            if f.is_file(): destinations.append((f, managed / 'AlienTextFlow/Fonts' / f.name))
        config = managed / 'AlienTextFlow/config.json'
        existing_config = json.loads(config.read_text()) if config.exists() else {}
        merged_config = dict(existing_config)
        for key, value in CONFIG.items(): merged_config.setdefault(key, value)
        if not config.exists() or merged_config != existing_config:
            template = Path(directory) / 'config.json'
            template.write_text(json.dumps(merged_config, indent=2) + '\n')
            destinations.append((template, config))
        # Retain a transactional snapshot so failure can restore a prior mod too.
        snapshot = []
        for index, (_, target) in enumerate(destinations + [(staged, original)]):
            before = Path(directory) / ('previous-' + str(index))
            existed = target.exists()
            if existed: shutil.copy2(target, before)
            snapshot.append((target, existed, before))
        try:
            for source_file, target in destinations: atomic_copy(source_file, target)
            atomic_copy(staged, original)
            run(DOTNET, PATCHER, 'verify', original)
            sign(app)
        except BaseException:
            for target, existed, before in reversed(snapshot):
                if existed: atomic_copy(before, target)
                else: target.unlink(missing_ok=True)
            sign(app)
            raise
        installed = sorted(set((state or {}).get('installedFiles', [])) | {str(p.relative_to(managed)) for _, p in destinations})
        next_state = {'version': '0.2.0', 'app': str(app), 'originalSha256': digest(backup), 'patchedSha256': digest(original), 'backup': backup.name, 'installedFiles': installed}
        state_path.write_text(json.dumps(next_state, indent=2) + '\n')
    print('Installed AlienTextFlow:', app)
    print('Verified original backup:', backup)

def uninstall(app):
    ensure_stopped(app)
    managed, store = paths(app)
    state_path = store / 'state.json'
    state = json.loads(state_path.read_text())
    original = managed / 'Assembly-CSharp.dll'
    if digest(original) != state['patchedSha256']:
        raise RuntimeError('Game assembly changed. Refusing to overwrite it with an old backup.')
    backup = store / state['backup']
    if digest(backup) != state['originalSha256']: raise RuntimeError('Original backup is not intact.')
    atomic_copy(backup, original)
    # Keep user-edited config in the backup folder when removing the mod.
    config = managed / 'AlienTextFlow/config.json'
    if config.exists(): atomic_copy(config, store / 'last-config.json')
    for name in state['installedFiles']:
        target = managed / name
        if not target.is_relative_to(managed): raise RuntimeError('Invalid installed-file record.')
        target.unlink(missing_ok=True)
    for folder in [managed / 'AlienTextFlow/Fonts', managed / 'AlienTextFlow']:
        if folder.exists() and not any(folder.iterdir()): folder.rmdir()
    sign(app)
    state_path.rename(store / 'uninstalled-state.json')
    print('Restored original game assembly. Save files were not changed.')

def status(app):
    managed, store = paths(app)
    state_path = store / 'state.json'
    if not state_path.exists(): print('Not installed:', app); return
    state = json.loads(state_path.read_text())
    print(json.dumps({'version': state['version'], 'app': str(app), 'assemblyMatches': digest(managed / 'Assembly-CSharp.dll') == state['patchedSha256'], 'backupMatches': digest(store / state['backup']) == state['originalSha256']}, indent=2))

def self_test(app):
    # Always use a cloned bundle: this test's bootstrap returns before
    # Global.Init / Steam initialization and never loads or writes saves.
    test_root = ROOT / 'work/self-test'
    test_root.mkdir(parents=True, exist_ok=True)
    clone = test_root / 'mac.app'
    if clone.exists(): raise RuntimeError('Self-test bundle already exists; inspect it before removing/reusing it.')
    run('/bin/cp', '-cR', app, clone)
    source_managed, source_store = paths(app)
    source_state_path = source_store / 'state.json'
    if source_state_path.exists():
        source_state = json.loads(source_state_path.read_text())
        if digest(source_managed / 'Assembly-CSharp.dll') == source_state['patchedSha256']:
            source_backup = source_store / source_state['backup']
            if digest(source_backup) != source_state['originalSha256']:
                raise RuntimeError('Cannot create clean test clone: original backup hash mismatch.')
            clone_managed, _ = paths(clone)
            atomic_copy(source_backup, clone_managed / 'Assembly-CSharp.dll')
            for relative in source_state['installedFiles']:
                target = clone_managed / relative
                if not target.is_relative_to(clone_managed): raise RuntimeError('Invalid installed-file record.')
                target.unlink(missing_ok=True)
    install(clone)
    report = test_root / 'report.json'
    log = test_root / 'unity.log'
    command = [str(clone / 'Contents/MacOS/MessageFromAliens'), '-batchmode', '-nographics', '-logFile', str(log), '--alien-text-flow-self-test', '--alien-text-flow-report', str(report)]
    try:
        with (test_root / 'bootstrap.log').open('w') as stdout:
            result = subprocess.run(command, timeout=90, stdout=stdout, stderr=subprocess.STDOUT)
    except subprocess.TimeoutExpired:
        raise RuntimeError('Self-test timed out. Inspect ' + str(log))
    if not report.exists(): raise RuntimeError('Unity did not produce a test report. Inspect ' + str(log))
    data = json.loads(report.read_text())
    print(json.dumps(data, ensure_ascii=False, indent=2))
    if result.returncode != 0 or data['status'] != 'passed': raise RuntimeError('Runtime rendering test failed.')

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('command', choices=['build', 'install', 'uninstall', 'status', 'self-test'])
parser.add_argument('--game', type=Path, default=DEFAULT_APP, help='Path to mac.app')
options = parser.parse_args()
try:
    if options.command == 'build': build()
    else: {'install': install, 'uninstall': uninstall, 'status': status, 'self-test': self_test}[options.command](options.game.expanduser().resolve())
except Exception as exc:
    print(str(exc), file=sys.stderr)
    sys.exit(1)
