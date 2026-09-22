#!/usr/bin/env python3
"""Regenerate docs/technical/mobile_icon_registry.md from the mobile icon set.

Walks src/Presentation/CardiTrack.Mobile/Resources/Images for SVGs, cross-references every
.xaml and .cs under src/ for each file name as a literal, and reads every hard-coded colour
out of the markup.

    python scripts/generate-icon-registry.py

Run it after adding, removing or recolouring an icon. The registry claims to be generated, so
it has to be generatable — a hand-edited copy drifts from the tree and is worse than none.
"""

import collections
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
IMAGES = os.path.join(ROOT, 'src', 'Presentation', 'CardiTrack.Mobile', 'Resources', 'Images')
OUT = os.path.join(ROOT, 'docs', 'technical', 'mobile_icon_registry.md')

# Only where a colour token has a name in Colors.xaml worth quoting. Anything absent is listed
# with a dash rather than guessed at — a wrong name here sends a redesign to the wrong token.
NAMED = {
    '#1884DC': 'Primary',
    '#174E86': 'PrimaryDark',
    '#939DAA': 'MutedText',
    '#727272': 'Body / Body2 ink',
    '#343434': 'Body2Dark ink',
    '#E53E3E': 'ErrorRed',
    '#C42F2F': 'DangerRed',
    '#36C09B': 'StatusGreen',
    '#1F8A72': 'MetricTemperatureInk / DatasetBodyText',
    '#F0A92E': 'warning amber',
    '#FFFFFF': 'White',
    # IconPark colour groups (scripts/icons/icons.json) and the Material metric inks.
    '#C9E1FF': 'MetricTileTint, opaque (brand internal fill)',
    '#B93A55': 'DatasetHeartText',
    '#5A4EBF': 'DatasetSleepText',
    '#10659F': 'DatasetActivityText',
    '#166B58': 'derived: DatasetBodyText darkened',
    '#7C6FDC': 'MetricSleepInk',
    '#3E8AC7': 'MetricBreathingInk',
    '#E4F6F2': 'DatasetBodyBackground',
    '#FFF3DE': 'DatasetWarningBackground',
    '#A9741A': 'DatasetWarningText',
    '#B45309': 'severity orange ink',
    '#FCEDE2': 'derived: StatusOrange at 14% over white',
    '#FBE4E4': 'derived: StatusRed at 14% over white',
    '#EEF1F5': 'DatasetOtherBackground',
    '#E8F3FD': 'DatasetActivityBackground',
    '#EEECFB': 'DatasetSleepBackground',
    '#FDEEF1': 'DatasetHeartBackground',
}

GROUPS = [
    ('Tab bar', lambda n: n.startswith('icon_tab_')),
    ('Status and severity',
     lambda n: n.startswith(('icon_status_', 'icon_caution_', 'icon_trend_'))),
    ('Metrics', lambda n: n.startswith('icon_metric_')),
    ('Actions', lambda n: n.startswith('icon_action_')),
    ('Alert reasons', lambda n: n.startswith('icon_reason_')),
    ('Navigation and chrome',
     lambda n: n.startswith(('icon_chevron', 'icon_caret', 'icon_back', 'icon_close', 'icon_menu'))),
    ('Brand and third party',
     lambda n: any(k in n for k in ('google', 'apple', 'fitbit', 'logo'))),
]


def source_files():
    for base, _dirs, files in os.walk(os.path.join(ROOT, 'src')):
        if os.sep + 'obj' in base or os.sep + 'bin' in base:
            continue
        for f in files:
            if f.endswith(('.xaml', '.cs')):
                yield os.path.join(base, f)


def main():
    if not os.path.isdir(IMAGES):
        print('no image directory at', IMAGES, file=sys.stderr)
        return 1

    icons = sorted(f for f in os.listdir(IMAGES) if f.lower().endswith('.svg'))

    hay = {}
    for p in source_files():
        try:
            hay[p] = io.open(p, encoding='utf-8-sig', errors='ignore').read()
        except OSError:
            pass

    def used_by(icon):
        # BottomNavBar builds the selected tab's file at runtime from the stem:
        # $"{iconStem}_active.svg". The literal in code is the stem alone, so an _active
        # file counts as used wherever its stem appears as a quoted string.
        needles = [icon]
        if icon.endswith('_active.svg'):
            needles.append('"' + icon[: -len('_active.svg')] + '"')
        names = set()
        for p, text in hay.items():
            if any(n in text for n in needles):
                stem = os.path.basename(p)
                for suffix in ('.xaml.cs', '.xaml', '.cs'):
                    if stem.endswith(suffix):
                        stem = stem[: -len(suffix)]
                        break
                names.add(stem)
        return sorted(names)

    def colours(icon):
        svg = io.open(os.path.join(IMAGES, icon), encoding='utf-8', errors='ignore').read()
        seen = []
        for c in re.findall(r'(?:fill|stroke|stop-color)="(#[0-9A-Fa-f]{3,8})"', svg):
            u = c.upper()
            if u not in seen:
                seen.append(u)
        return seen

    unused = [i for i in icons if not used_by(i)]
    palette = collections.Counter(c for i in icons for c in colours(i))

    assigned = set()
    grouped = collections.OrderedDict()
    for title, test in GROUPS:
        members = [i for i in icons if test(i) and i not in assigned]
        assigned.update(members)
        if members:
            grouped[title] = members
    rest = [i for i in icons if i not in assigned]
    if rest:
        grouped['Everything else'] = rest

    out = []
    w = out.append
    w('# Mobile icon registry')
    w('')
    w('Every SVG in `src/Presentation/CardiTrack.Mobile/Resources/Images`, what uses it, and every')
    w('colour it hard-codes. Written so a redesign can be scoped without grepping: the question')
    w('"what breaks if this icon changes" has an answer here.')
    w('')
    w('**Generated, not hand-maintained.** Regenerate with:')
    w('')
    w('```bash')
    w('python scripts/generate-icon-registry.py')
    w('```')
    w('')
    w('A row that disagrees with the code means this file is stale, not that the code is wrong.')
    w('')
    w(f'**{len(icons)} icons**, **{len(palette)} distinct colours**, '
      f'**{len(unused)} referenced nowhere**.')
    w('')
    w('## Why the colours matter')
    w('')
    w('Every icon hard-codes its own hex. None read `Colors.xaml`, because MAUI cannot tint an')
    w('`<Image>` source from a resource — so a palette change is a change to this many files, not')
    w('to one. That is the largest cost in any redesign of this set, and the reason this table')
    w('lists every colour rather than only the common ones: a colour used once still pins a file.')
    w('')
    w('| Colour | Icons | Also known as |')
    w('| --- | --- | --- |')
    for colour, n in sorted(palette.items(), key=lambda kv: (-kv[1], kv[0])):
        w(f'| `{colour}` | {n} | {NAMED.get(colour, "—")} |')
    w('')
    w('## Unreferenced')
    w('')
    if unused:
        w('In the tree, used by nothing. Candidates for deletion — but check the history first: an')
        w('icon can be staged ahead of a screen that has not shipped.')
        w('')
        for i in unused:
            w(f'- `{i}`')
    else:
        w('None.')
    w('')
    w('## The set')
    w('')
    for title, members in grouped.items():
        w(f'### {title}')
        w('')
        w('| Icon | Colours | Used by |')
        w('| --- | --- | --- |')
        for i in members:
            cols = ' '.join(f'`{c}`' for c in colours(i)) or '—'
            uses = used_by(i)
            w(f'| `{i}` | {cols} | {", ".join(uses) if uses else "**nothing**"} |')
        w('')
    w('## How "used by" is decided')
    w('')
    w('Every `.xaml` and `.cs` under `src/` is searched for the file name as a literal, which is')
    w('how icons are referenced throughout — `Source="icon_x.svg"`, or a string constant as in')
    w('`FindingsList.CheckMarker`. A name mentioned only in a comment therefore counts as a use, so')
    w('a row claiming a single caller is worth reading before trusting. The one runtime-built name,')
    w('the bottom nav\'s `{stem}_active.svg`, is matched on its stem.')
    w('')
    w('## How the files are made')
    w('')
    w('Everything except the vendor marks, the splash gradient, the Android notification icon, the')
    w('240dp chat launcher illustration (`icon_chatbot.svg`) and the bottom-nav tab icons is generated')
    w('by `scripts/icons/generate.mjs` from `scripts/icons/icons.json`,')
    w('which holds the icon name, source (IconPark or Material Symbols), colour group and size per')
    w('file. Edit the JSON, run `npm ci && npm run generate` in `scripts/icons`, then regenerate this file.')
    w('')

    io.open(OUT, 'w', encoding='utf-8', newline='\r\n').write('\n'.join(out))
    print(f'wrote {os.path.relpath(OUT, ROOT)}: {len(icons)} icons, '
          f'{len(palette)} colours, {len(unused)} unused')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
