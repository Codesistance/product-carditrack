# Third-party icon sources

Both sources are licensed under the Apache License, Version 2.0. The licence text is
vendored beside each: `LICENSE.iconpark` and `material/LICENSE`. Neither upstream ships a
NOTICE file; this file carries the attribution the licence asks for.

## IconPark

Copyright ByteDance. https://github.com/bytedance/IconPark

Consumed as the npm package `@icon-park/svg` at the version pinned in `package.json`; nothing
from it is vendored. `generate.mjs` calls its drawing functions with the colour slots, stroke
and size from `icons.json`, so every generated file is a derivative of the IconPark source
with those attributes set.

## Material Symbols

Copyright Google LLC. https://github.com/google/material-design-icons

The files under `material/` are the Material Symbols Rounded glyphs at weight 500, optical
size 24, copied unchanged from `symbols/web/<name>/materialsymbolsrounded/<name>_wght500_24px.svg`.
`generate.mjs` sets a fill colour and a declared size on the root element when it writes
them into the app; that is the only modification.
