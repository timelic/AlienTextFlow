#!/usr/bin/env python3
"""Create a source + built-mod archive; never include proprietary game files."""
from pathlib import Path
import argparse
import hashlib
import json
import zipfile

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('output', type=Path)
options = parser.parse_args()
output = options.output.expanduser().resolve()
output.parent.mkdir(parents=True, exist_ok=True)
files = [root / name for name in ['.gitignore', 'Directory.Build.props', 'README.md', 'CHANGELOG.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt']]
files += [f for folder in ['scripts', 'fonts'] for f in (root / folder).rglob('*') if f.is_file() and '__pycache__' not in f.parts]
files += [f for f in (root / 'src').rglob('*') if f.is_file() and 'obj' not in f.parts and ('bin' not in f.parts or f.suffix in ['.dll', '.json'])]
for f in files:
    if f.name in ['Assembly-CSharp.dll', 'Unity.TextMeshPro.dll'] or f.name.startswith('UnityEngine'):
        raise RuntimeError('Game-supplied assembly must not be redistributed: ' + str(f))
report = root / 'work/self-test/report.json'
if not report.exists() or json.loads(report.read_text())['status'] != 'passed':
    raise RuntimeError('A passed Unity self-test report is required before packaging.')
with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
    for file in sorted(set(files)):
        archive.write(file, 'AlienTextFlow/' + str(file.relative_to(root)))
    archive.write(report, 'AlienTextFlow/verification/unity-font-test.json')
with zipfile.ZipFile(output) as archive:
    assert archive.testzip() is None
print(json.dumps({'archive': str(output), 'bytes': output.stat().st_size, 'sha256': hashlib.sha256(output.read_bytes()).hexdigest(), 'files': len(files) + 1}))
