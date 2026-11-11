"""Pin the fonts' real wdth/wght axes into Unity-compatible static instances."""
from pathlib import Path
import hashlib
import json
import shutil
import struct
import urllib.request
import zipfile
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

ROOT = Path(__file__).resolve().parents[1]
SOURCES = ROOT / 'work/font-sources'
OUTPUT = ROOT / 'fonts'
SOURCES.mkdir(parents=True, exist_ok=True)
OUTPUT.mkdir(exist_ok=True)
URLS = {
    'WenYuanSansSCFlexVF.ttf': 'https://github.com/takushun-wu/WenYuanFonts/releases/download/v1.110/WenYuanSansSCFlexVF.ttf',
    'mona-sans-variable-v2.0.27.zip': 'https://github.com/github/mona-sans/releases/download/v2.0.27/mona-sans-variable-v2.0.27.zip',
    'WenYuan-LICENSE.txt': 'https://raw.githubusercontent.com/takushun-wu/WenYuanFonts/main/LICENSE.md',
    'MonaSans-OFL.txt': 'https://raw.githubusercontent.com/github/mona-sans/main/OFL.txt',
}

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

for name, url in URLS.items():
    target = SOURCES / name
    if not target.exists():
        print('Downloading', name, flush=True)
        request = urllib.request.Request(url, headers={'User-Agent': 'AlienTextFlow-build'})
        with urllib.request.urlopen(request, timeout=120) as response, target.with_suffix('.part').open('wb') as stream:
            shutil.copyfileobj(response, stream)
        target.with_suffix('.part').replace(target)
with zipfile.ZipFile(SOURCES / 'mona-sans-variable-v2.0.27.zip') as archive:
    entry = next(n for n in archive.namelist() if n.endswith('.ttf') and 'MonaSansVF' in n)
    (SOURCES / 'MonaSansVF.ttf').write_bytes(archive.read(entry))

manifest = {'sources': {name: {'url': url, 'sha256': sha(SOURCES / name)} for name, url in URLS.items()}, 'fonts': []}
for filename, family in [('MonaSansVF.ttf', 'MonaSans'), ('WenYuanSansSCFlexVF.ttf', 'WenYuanSansSC')]:
    original = TTFont(SOURCES / filename)
    axes = {a.axisTag: (a.minValue, a.defaultValue, a.maxValue) for a in original['fvar'].axes}
    print(family, axes, flush=True)
    if 'wdth' not in axes or axes['wdth'][0] > 75 or axes['wdth'][2] < 100:
        raise RuntimeError('Unexpected width axis: ' + repr(axes))
    for width in [100, 95, 90, 85, 80, 75]:
        target = OUTPUT / f'{family}-w{width}.ttf'
        if not target.exists():
            font = TTFont(SOURCES / filename)
            coordinates = {a.axisTag: a.defaultValue for a in font['fvar'].axes}
            coordinates.update(wght=400, wdth=width)
            if 'opsz' in coordinates: coordinates['opsz'] = 20
            instance = instantiateVariableFont(font, coordinates, inplace=True)
            # Modified instances use a project-specific family name and retain
            # copyright / license records. Do not reuse reserved original names.
            for name in instance['name'].names:
                if name.nameID in (1, 4, 6, 16, 17, 2):
                    value = ('Regular' if name.nameID in (2, 17) else f'ATF {family} W{width}')
                    if name.nameID == 6: value = f'ATF-{family}-W{width}'
                    name.string = value.encode(name.getEncoding(), errors='replace')
            instance.save(target)
        with TTFont(target) as check:
            assert 'fvar' not in check
            cmap = check.getBestCmap()
            expected = '中文译注字体' if family == 'WenYuanSansSC' else 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
            assert all(ord(c) in cmap for c in expected)
        manifest['fonts'].append({'file': target.name, 'width': width, 'sha256': sha(target), 'bytes': target.stat().st_size})
        print('Ready', target.name, target.stat().st_size, flush=True)
for license in ['WenYuan-LICENSE.txt', 'MonaSans-OFL.txt']:
    shutil.copy2(SOURCES / license, OUTPUT / license)
# Pure advance metrics let the runtime predict an axis instance without
# rasterizing the text in every possible SDF atlas just to measure it.
metrics = OUTPUT / 'advances.bin'
with metrics.open('wb') as stream:
    stream.write(b'ATFADV1\0')
    for family in ['MonaSans', 'WenYuanSansSC']:
        faces = [TTFont(OUTPUT / f'{family}-w{width}.ttf', lazy=True) for width in [100, 95, 90, 85, 80, 75]]
        units = faces[0]['head'].unitsPerEm
        cmap = faces[0].getBestCmap()
        stream.write(struct.pack('<HI', units, len(cmap)))
        maps = [f.getBestCmap() for f in faces]
        advances = [f['hmtx'].metrics for f in faces]
        for codepoint in sorted(cmap):
            values = [table[mapping[codepoint]][0] for table, mapping in zip(advances, maps)]
            stream.write(struct.pack('<I6H', codepoint, *values))
        for face in faces: face.close()
manifest['advanceMetrics'] = {'file': metrics.name, 'sha256': sha(metrics), 'bytes': metrics.stat().st_size}
(OUTPUT / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
