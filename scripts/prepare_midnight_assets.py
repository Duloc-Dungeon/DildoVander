"""Export mobile artwork from the retained, text-free ImageGen source masters.

Requires Pillow. Run from any directory; no API calls or generated UI pixels.
Object crops separate the original atlas illustrations, never storyboard controls.
"""
from pathlib import Path
import hashlib
import json
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'design-system/assets/midnight'
OUTPUT = ROOT / 'mobilni-aplikace/src/assets/brand/midnight'
OUTPUT.mkdir(parents=True, exist_ok=True)
records = []


def export(name, master, purpose, crop=None, size=None, alpha=False):
    image = Image.open(SOURCE / master)
    if crop:
        image = image.crop(crop)
    if size:
        image = image.resize(size, Image.Resampling.LANCZOS)
    if not alpha:
        image = image.convert('RGB')
    image.save(OUTPUT / name, 'WEBP', quality=86, method=6, exact=True)
    content = (OUTPUT / name).read_bytes()
    records.append(dict(file=name, master=f'design-system/assets/midnight/{master}',
                        purpose=purpose, dimensions=list(image.size), alpha=alpha,
                        crop=crop, bytes=len(content), sha256=hashlib.sha256(content).hexdigest()))


export('tabletop.webp', 'tabletop.png', 'Quiet scene background; no live text baked in', size=(768, 1152))
export('invitation.webp', 'objects.png', 'Sealed arrival invitation, decorative', (40, 0, 780, 450), alpha=True)
export('access-key.webp', 'objects.png', 'Returning passkey illustration, decorative', (840, 0, 1536, 430), alpha=True)
export('paper.webp', 'objects.png', 'Quiet parchment texture for semantic form surfaces', (260, 560, 560, 860))
export('opening.webp', 'objects.png', 'Authorized opening; also the static reduced-motion equivalent', (850, 430, 1420, 1015), alpha=True)
export('frame.webp', 'frame.png', 'Fixed walnut frame and top valance; transparent portrait aperture', size=(800, 1000), alpha=True)
export('placeholder.webp', 'tabletop.png', 'Neutral felt placeholder; no private portrait', (300, 350, 700, 850), size=(400, 500))
if (SOURCE / 'curtains.png').exists():
    wings = Image.open(SOURCE / 'curtains.png')
    width, height = wings.size
    export('curtain-left.webp', 'curtains.png', 'Independent left velvet wing', (0, 0, width // 2, height), size=(400, 1000), alpha=True)
    export('curtain-right.webp', 'curtains.png', 'Independent right velvet wing', (width // 2, 0, width, height), size=(400, 1000), alpha=True)

(OUTPUT / 'manifest.json').write_text(json.dumps(dict(
    direction='The Midnight Table', revision='2026-10-26',
    provenance='Original built-in ImageGen artwork; retained PNG source masters. Deterministic crop, resize and WebP encoding with prepare_midnight_assets.py.',
    license='AGPL-3.0-only',
    geometry='Portrait stage 4:5. Aperture inset: top21%, right15.5%, bottom10%, left15.5%; width and height69%, exact4:5. Curtain halves animate only inside that aperture.',
    foreground='No independent particle effects. Hidden scenes pause existing presentation lifecycle.',
    assets=records), ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(f'Exported {len(records)} Midnight Table assets ({sum(r["bytes"] for r in records)} bytes).')
