"""Sets each figure's class and print width in the user guides from its image, after a recapture.

Every screenshot prints at one scale: its capture viewport (see capture-screenshots.cjs) fills the 7.2 in text width,
so text is the same size in every image. Tall images are capped. Images narrower than 5 in take their caption beside
them (class "side"); diagrams fill the width. Usage: python docs/guides/tools/size-figures.py (needs Pillow).
"""
import re
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]          # docs/
IMG = ROOT / 'images' / 'guide'
VIEWPORT = {'01-home': 1280, '02-intake': 1280, '02b-batch-card': 1280, '03-held': 1280, '03b-held-correction': 1280,
            '11-case': 1280, '13-queues': 1280, '18-results': 1280, '21-users': 1280}
MAX_HEIGHT = {'15-map': 4.2, '16-reports': 4.6, '05b-reporter-form': 4.8, '05-reporters': 3.1}
def size(name):
    w, h = Image.open(next(IMG.glob(f'{name}.*'))).size
    cw, ch = w / 2, h / 2
    width = 7.2 if name.startswith('diagram-') else min(7.2, cw * 7.2 / VIEWPORT.get(name, 1040))
    height = width * ch / cw
    cap = MAX_HEIGHT.get(name, 6.3)
    return (width * cap / height, cap) if height > cap else (width, height)
pat = re.compile(r'<figure(?: class="[^"]*")?><img src="\.\./images/guide/([\w-]+)\.(jpg|png)"( style="[^"]*")?')
for f in ['operations-guide.html', 'npi-clia-import-guide.html']:
    p = ROOT / 'guides' / f
    s = p.read_text(encoding='utf-8')
    def repl(m):
        w, _ = size(m.group(1))
        cls = 'shot diagram' if m.group(1).startswith('diagram-') else ('shot side' if w < 5.0 else 'shot')
        return f'<figure class="{cls}"><img src="../images/guide/{m.group(1)}.{m.group(2)}" style="width:{w:.2f}in"'
    p.write_text(pat.sub(repl, s), encoding='utf-8')
print('Figures sized in docs/guides.')
