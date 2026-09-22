// Regenerates the mobile icon set from icons.json.
//
//   cd scripts/icons && npm ci && npm run generate
//
// Every file named in icons.json is written to the mobile Resources/Images folder under
// its existing name, so the XAML that references it does not change. Two sources:
//
//   iconpark  — drawn by the @icon-park/svg package (Apache 2.0; see NOTICE.md) with the
//               colour group's slots: Multicolor takes four colours (outer stroke, external
//               fill, internal outline, internal fill); linear takes one.
//   material  — a Material Symbols Rounded glyph vendored under material/ (Apache 2.0; see
//               NOTICE.md), tinted with the entry's fill.
//
// The size is the SVG's declared width/height in dp. Resizetizer reads that as the image's
// base size, so it has to match what the hand-drawn file declared or the layout shifts.
// Run this after editing icons.json, then regenerate docs/technical/mobile_icon_registry.md.
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const iconPark = require('@icon-park/svg');
const config = JSON.parse(fs.readFileSync(path.join(here, 'icons.json'), 'utf8'));
const outDir = path.resolve(here, '../../src/Presentation/CardiTrack.Mobile/Resources/Images');

const installed = require('@icon-park/svg/package.json').version;
if (installed !== config.iconpark.version) {
  throw new Error(`icons.json pins @icon-park/svg ${config.iconpark.version} but ${installed} is installed; run npm ci`);
}

const exportName = (name) => name.split('-').map((s) => s[0].toUpperCase() + s.slice(1)).join('');

function fromIconPark(entry) {
  const group = config.groups[entry.group];
  if (!group) throw new Error(`${entry.file}: unknown group ${entry.group}`);
  const draw = iconPark[exportName(entry.name)];
  if (!draw) throw new Error(`${entry.file}: no IconPark icon named ${entry.name}`);
  const multi = group.style === 'Multicolor';
  const { strokeWidth, strokeLinecap, strokeLinejoin } = config.iconpark;
  return draw({
    theme: multi ? 'multi-color' : 'outline',
    fill: multi ? group.slots : group.slots[0],
    size: entry.size,
    strokeWidth,
    strokeLinecap,
    strokeLinejoin,
  });
}

function fromMaterial(entry) {
  const src = fs.readFileSync(path.join(here, config.material.folder, `${entry.name}.svg`), 'utf8');
  // Material Symbols ship with no fill on the root, so the paths inherit; set the ink there and
  // the declared size, leaving the 960-unit viewBox to scale it.
  return src.replace(
    /<svg\b[^>]*>/,
    (tag) => tag
      .replace(/\s(width|height)="[^"]*"/g, '')
      .replace('<svg', `<svg width="${entry.size}" height="${entry.size}" fill="${entry.fill}"`),
  );
}

let written = 0;
for (const entry of config.icons) {
  const svg = entry.source === 'iconpark' ? fromIconPark(entry)
    : entry.source === 'material' ? fromMaterial(entry)
    : (() => { throw new Error(`${entry.file}: unknown source ${entry.source}`); })();
  fs.writeFileSync(path.join(outDir, entry.file), svg.endsWith('\n') ? svg : svg + '\n');
  written++;
}
console.log(`wrote ${written} icons to ${path.relative(process.cwd(), outDir)}`);
