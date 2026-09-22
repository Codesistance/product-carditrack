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

// The markup between an IconPark <svg> and its closing tag. Every glyph it draws uses the same
// 48-unit viewBox, so two of them compose by transform alone.
const innerMarkup = (svg) => svg.replace(/^[\s\S]*?<svg[^>]*>/, '').replace(/<\/svg>\s*$/, '');

function drawIconPark(name, entry, group, size, strokeWidthOverride) {
  const draw = iconPark[exportName(name)];
  if (!draw) throw new Error(`${entry.file}: no IconPark icon named ${name}`);
  const multi = group.style === 'Multicolor';
  const { strokeWidth, strokeLinecap, strokeLinejoin } = config.iconpark;
  return draw({
    theme: multi ? 'multi-color' : 'outline',
    fill: multi ? group.slots : group.slots[0],
    size,
    strokeWidth: strokeWidthOverride ?? strokeWidth,
    strokeLinecap,
    strokeLinejoin,
  });
}

// Fills the shapes IconPark left hollow, so a glyph with no internal fill of its own still
// reads as a solid mark rather than as a wire outline. Only paths and rects that carry no fill
// are touched: the ones the template did fill are already saying something with that colour.
// The fill is the group's external-fill slot — the same colour these glyphs fill with where the
// template does it for them — so a solid icon stays in the palette it belongs to.
const fillHollow = (svg, colour) =>
  svg
    // A shape with no fill at all inherits the root's fill="none" ...
    .replace(/<(path|rect|circle|ellipse|polygon)(?![^>]*fill=)/g, `<$1 fill="${colour}"`)
    // ... and one that says fill="none" is hollow just as deliberately. Both are the
    // template declining to fill a shape, which is exactly what a solid glyph overrides.
    // The root <svg>'s own fill="none" survives, because only these five elements match.
    .replace(/(<(?:path|rect|circle|ellipse|polygon)[^>]*?)fill="none"/g, `$1fill="${colour}"`);

function fromIconPark(entry) {
  const group = config.groups[entry.group];
  if (!group) throw new Error(`${entry.file}: unknown group ${entry.group}`);
  let base = drawIconPark(entry.name, entry, group, entry.size);
  if (entry.solid) base = fillHollow(base, group.slots[group.slots.length > 1 ? 1 : 0]);
  if (!entry.badge) return base;

  // A badged glyph: one icon, two objects. The subject is drawn smaller and anchored to the
  // bottom-left, and the badge — the thing that says which *kind* of row this is — sits at
  // a little over half size in the top-right, where it modifies the subject the way a
  // superscript modifies a number.
  //
  // The badge is drawn first and the subject over it, so where the two cross the subject wins
  // and the badge reads as something behind the mark rather than stuck on top of it. That is
  // also what lets the disc go: a knockout only ever existed to stop the badge colliding with
  // the outline it sat on, and behind the subject there is nothing to knock out. It carries its
  // own blue and a thinner stroke instead, which is the whole of what makes it a superscript —
  // same weight as the subject and it competes, lighter and it recedes.
  const b = config.iconpark.badge;
  const badgeGroup = { style: group.style, slots: group.slots.map(() => b.ink) };
  let badge = drawIconPark(entry.badge, entry, badgeGroup, entry.size, b.strokeWidth);
  // Solid, like the subject: at badge size an outline is a handful of hairlines that read as
  // noise beside a mark drawn at full weight, and filling it is what makes the small glyph
  // legible rather than merely present.
  if (b.solid) badge = fillHollow(badge, b.ink);
  return [
    `<?xml version="1.0" encoding="UTF-8"?>`,
    `<svg width="${entry.size}" height="${entry.size}" viewBox="0 0 48 48" fill="none" xmlns="http://www.w3.org/2000/svg">`,
    `<g transform="translate(${b.x} ${b.y}) scale(${b.scale})">${innerMarkup(badge)}</g>`,
    `<g transform="translate(0 ${b.baseY}) scale(${b.baseScale})">${innerMarkup(base)}</g>`,
    `</svg>`,
  ].join('');
}

function fromMaterial(entry) {
  const src = fs.readFileSync(path.join(here, config.material.folder, `${entry.name}.svg`), 'utf8');
  // Material Symbols ship with no fill on the root, so the paths inherit; set the ink there and
  // the declared size, leaving the 960-unit viewBox to scale it. The output is a modified copy
  // of an Apache 2.0 file, and section 4(b) of that licence wants each modified file to say so,
  // hence the comment ahead of the root element; NOTICE.md carries the attribution.
  const notice = `<!-- Material Symbols Rounded "${entry.name}" by Google LLC, Apache License 2.0 `
    + `(see scripts/icons/NOTICE.md). Modified by scripts/icons/generate.mjs: root width, height `
    + `and fill set to ${entry.size}dp and ${entry.fill}. -->\n`;
  return notice + src.replace(
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
