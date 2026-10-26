"""Export the approved fictional game character sheets to bounded runtime WebP."""
from pathlib import Path
import hashlib
import json
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'design-system/assets/game'
TARGET = ROOT / 'mobilni-aplikace/src/assets/brand/game'
TARGET.mkdir(parents=True, exist_ok=True)
manifest = []
for name in ('commentator', 'boss'):
    source = SOURCE / f'{name}.png'
    target = TARGET / f'{name}.webp'
    with Image.open(source) as image:
        image.thumbnail((1152, 768))
        image.save(target, 'WEBP', quality=82, method=6)
    manifest.append({'name': name, 'synthetic': True,
                     'sourceSha256': hashlib.sha256(source.read_bytes()).hexdigest(),
                     'runtimeSha256': hashlib.sha256(target.read_bytes()).hexdigest(),
                     'runtimeBytes': target.stat().st_size})
(TARGET / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
