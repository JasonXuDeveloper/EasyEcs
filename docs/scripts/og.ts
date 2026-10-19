/**
 * Social cards (Open Graph / Twitter, 1200×630 PNG) for every page, drawn at
 * build time with sharp, so they cost nothing at run time:
 * build/client/og/en/docs/guide/queries.png for /en/docs/guide/queries.
 *
 * Fonts: Inter (English) and Noto Sans SC (Chinese) are downloaded from Google
 * Fonts, subset to the characters on the cards, into node_modules/.cache. If
 * that fails, the system fonts are used when they cover the language;
 * otherwise a page gets the default card (public/og.png) instead of a card
 * with missing glyphs. Run with Node (vite-node), not Bun: sharp's text
 * rendering hangs under Bun.
 *
 * `vite-node --mode scripts scripts/og.ts --default` (`bun run og:default`)
 * redraws public/og.png; `--github` (`bun run og:github`) draws the
 * repository's social preview, ../.github/social-preview.png (1280×640).
 */
import { execFileSync } from 'node:child_process';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import sharp from 'sharp';
import { benchNumber, formatRatio } from '../src/lib/benchmarks';
import { featureComparison } from '../src/lib/feature-comparison';
import type { Locale } from '../src/lib/i18n';
import { analyzerRules } from '../src/lib/site';

export interface Card {
  /** Page URL, e.g. /en/docs/guide/queries */
  url: string;
  lang: Locale;
  /** Small line above the title, e.g. "EasyEcs Docs" */
  eyebrow: string;
  title: string;
  description?: string;
}

const width = 1200;
const height = 630;
const margin = 80;
const textWidth = width - margin * 2;
// Neutral near-black, like the icon; the site's accent for the eyebrow and rule.
const colors = {
  background: '#0a0a0a',
  accent: '#8fb0f5',
  title: '#fafafa',
  description: '#a3a3a3',
  footer: '#737373',
};

const cacheDir = path.resolve('node_modules/.cache/easyecs-og');
const logo = path.resolve('public/icon.svg');

type Weight = 400 | 700;
type FontSet = Record<Weight, string | undefined>;

function escapeMarkup(text: string) {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

/** Download a Google Fonts subset (TrueType) with just these characters. */
async function googleFont(family: string, weight: Weight, chars: string) {
  const text = [...new Set(chars)].sort().join('');
  const key = `${family.replace(/\s+/g, '-')}-${weight}-${Buffer.from(text).toString('base64url').slice(0, 24)}-${text.length}`;
  const file = path.join(cacheDir, `${key}.ttf`);
  try {
    await fs.access(file);
    return file;
  } catch {}
  const css = await fetch(
    `https://fonts.googleapis.com/css2?family=${encodeURIComponent(family)}:wght@${weight}&text=${encodeURIComponent(text)}`,
    { signal: AbortSignal.timeout(15_000) },
  ).then((res) => {
    if (!res.ok) throw new Error(`Google Fonts: HTTP ${res.status}`);
    return res.text();
  });
  const url = /src:\s*url\(([^)]+)\)\s*format\('truetype'\)/.exec(css)?.[1];
  if (!url) throw new Error('Google Fonts: no TrueType source');
  const font = await fetch(url, { signal: AbortSignal.timeout(15_000) }).then(
    (res) => {
      if (!res.ok) throw new Error(`Google Fonts: HTTP ${res.status}`);
      return res.arrayBuffer();
    },
  );
  await fs.mkdir(cacheDir, { recursive: true });
  await fs.writeFile(file, Buffer.from(font));
  return file;
}

/** Whether fontconfig has a font for this language ("en", "zh"). */
function systemHasFont(lang: string) {
  try {
    return (
      execFileSync('fc-list', [`:lang=${lang}`, 'family'], {
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'ignore'],
        timeout: 30_000,
      }).trim().length > 0
    );
  } catch {
    return false;
  }
}

const families: Record<Locale, string> = {
  en: 'Inter',
  zh: 'Noto Sans SC',
};

/** Font files per language, or null when the language cannot be drawn. */
async function loadFonts(cards: Card[]) {
  const fonts: Partial<Record<Locale, FontSet | null>> = {};
  for (const lang of ['en', 'zh'] as const) {
    const chars = cards
      .filter((card) => card.lang === lang)
      .map((card) => `${card.eyebrow}${card.title}${card.description ?? ''}`)
      .join('')
      .concat('ecs.xgamedev.net…');
    try {
      fonts[lang] = {
        400: await googleFont(families[lang], 400, chars),
        700: await googleFont(families[lang], 700, chars),
      };
    } catch (error) {
      const fallback = systemHasFont(lang);
      console.warn(
        `[og] ${families[lang]} unavailable (${(error as Error).message}); ${fallback ? 'using system fonts' : 'using the default card'}`,
      );
      fonts[lang] = fallback ? { 400: undefined, 700: undefined } : null;
    }
  }
  return fonts;
}

async function text(
  content: string,
  options: {
    fonts: FontSet;
    family: string;
    weight: Weight;
    size: number;
    color: string;
    maxHeight: number;
    spacing?: number;
    /** Wrap width, the text column of a page card by default */
    width?: number;
  },
) {
  const { data, info } = await sharp({
    text: {
      text: `<span foreground="${options.color}">${escapeMarkup(content)}</span>`,
      font: `${options.fonts[options.weight] ? options.family : 'sans-serif'} ${options.weight === 700 ? 'Bold' : ''} ${options.size}`,
      fontfile: options.fonts[options.weight],
      width: options.width ?? textWidth,
      dpi: 72,
      rgba: true,
      wrap: 'word-char',
      spacing: options.spacing ?? Math.round(options.size * 0.25),
    },
  })
    .png()
    .toBuffer({ resolveWithObject: true });
  if (info.height <= options.maxHeight) return { data, height: info.height };
  // Too many lines: keep the ones that fit.
  const cropped = await sharp(data)
    .extract({ left: 0, top: 0, width: info.width, height: options.maxHeight })
    .png()
    .toBuffer();
  return { data: cropped, height: options.maxHeight };
}

/** Cut to `max` characters; English at a word boundary. */
function shorten(value: string, max: number, words: boolean) {
  const chars = [...value];
  if (chars.length <= max) return value;
  const cut = chars.slice(0, max - 1).join('');
  return `${words ? cut.replace(/[\s,.;:]+\S*$/u, '') : cut.replace(/[，。；：、\s]+$/u, '')}…`;
}

async function drawCard(card: Card, fonts: FontSet) {
  const zh = card.lang === 'zh';
  const family = families[card.lang];
  const background = Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}"><rect width="${width}" height="${height}" fill="${colors.background}"/><rect x="0" y="${height - 12}" width="${width}" height="12" fill="${colors.accent}"/></svg>`,
  );
  const icon = await sharp(logo).resize(72, 72).png().toBuffer();

  const eyebrow = await text(card.eyebrow, {
    fonts,
    family,
    weight: 700,
    size: 34,
    color: colors.accent,
    maxHeight: 60,
  });
  // Long titles get a smaller size before they are cut to three lines.
  let title = await text(card.title, {
    fonts,
    family,
    weight: 700,
    size: 72,
    color: colors.title,
    maxHeight: 400,
  });
  if (title.height > 190) {
    title = await text(card.title, {
      fonts,
      family,
      weight: 700,
      size: 56,
      color: colors.title,
      maxHeight: 220,
    });
  }
  const titleTop = 200;
  const descriptionTop = titleTop + title.height + 40;
  const layers: Parameters<ReturnType<typeof sharp>['composite']>[0] = [
    { input: icon, left: margin, top: 64 },
    {
      input: eyebrow.data,
      left: margin + 72 + 24,
      top: 64 + Math.round((72 - eyebrow.height) / 2),
    },
    { input: title.data, left: margin, top: titleTop },
  ];
  const room = height - 110 - descriptionTop;
  if (card.description && room > 40) {
    const description = await text(
      shorten(card.description, zh ? 84 : 150, !zh),
      {
        fonts,
        family,
        weight: 400,
        size: 30,
        color: colors.description,
        maxHeight: room,
      },
    );
    layers.push({ input: description.data, left: margin, top: descriptionTop });
  }
  const footer = await text('ecs.xgamedev.net', {
    fonts,
    family,
    weight: 400,
    size: 26,
    color: colors.footer,
    maxHeight: 40,
  });
  layers.push({ input: footer.data, left: margin, top: height - 76 });

  return sharp(background)
    .composite(layers)
    .png({ compressionLevel: 9, palette: true, quality: 90 })
    .toBuffer();
}

/** Write one card per page under `outDir`/og; pages that fail get `fallback`. */
export async function writeCards(
  cards: Card[],
  outDir: string,
  fallback: string,
) {
  const fonts = await loadFonts(cards);
  let drawn = 0;
  let copied = 0;
  const queue = [...cards];
  async function worker() {
    for (let card = queue.shift(); card; card = queue.shift()) {
      const target = path.join(outDir, 'og', `${card.url.slice(1)}.png`);
      await fs.mkdir(path.dirname(target), { recursive: true });
      const set = fonts[card.lang];
      try {
        if (!set) throw new Error('no font');
        await fs.writeFile(target, await drawCard(card, set));
        drawn++;
      } catch (error) {
        if (set) console.warn(`[og] ${card.url}: ${(error as Error).message}`);
        await fs.copyFile(fallback, target);
        copied++;
      }
    }
  }
  await Promise.all(Array.from({ length: 4 }, worker));
  console.log(
    `[og] ${drawn} social cards drawn${copied ? `, ${copied} use the default card` : ''}`,
  );
}

/* ------------------------------------------------------------------ */
/* The home cards, the default card and the GitHub social preview      */
/* ------------------------------------------------------------------ */

/**
 * The biggest leads of the head-to-head comparisons (src/lib/feature-comparison.ts):
 * the cards name what EasyEcs is fastest at, by how much, from the data.
 */
function biggestLeads(n: number) {
  return featureComparison('lead').slice(0, n);
}

/** The one-line claim of the home cards and the GitHub preview: speed first, then garbage */
const claim: Record<Locale, string> = {
  en: 'High-performance, allocation-free ECS for C# and .NET',
  zh: '高性能、零分配的 C# / .NET ECS',
};

/**
 * The cards of /en and /zh; the English one is also the default card
 * (public/og.png), so every share of the site says the same thing.
 */
export function homeCards(): Card[] {
  const alloc = benchNumber('tick.oneWorld.alloc')?.text;
  const [first, second] = biggestLeads(2);
  return [
    {
      url: '/en',
      lang: 'en',
      eyebrow: 'EasyEcs',
      title: claim.en,
      description: [
        first && second
          ? `${formatRatio(first.ratio)} faster ${first.short.en} and ${formatRatio(second.ratio)} faster ${second.short.en} than the next-fastest of Friflo.Engine.ECS, Arch and DefaultEcs`
          : 'Source-generated systems for .NET 10 servers',
        alloc ? `, with ${alloc} allocated per tick.` : '.',
      ].join(''),
    },
    {
      url: '/zh',
      lang: 'zh',
      eyebrow: 'EasyEcs',
      title: claim.zh,
      description: [
        first && second
          ? `对比 Friflo.Engine.ECS、Arch、DefaultEcs 中次快的库，${first.short.zh}快 ${formatRatio(first.ratio)}，${second.short.zh}快 ${formatRatio(second.ratio)}`
          : '为 .NET 10 服务端打造的源生成系统',
        alloc ? `，每个 tick 分配 ${alloc}。` : '。',
      ].join(''),
    },
  ];
}

/**
 * The repository's social preview (Settings -> Social preview), 1280×640:
 * the icon and name, the claim, three facts and the four biggest leads over
 * the next-fastest library as bars, centred so a square crop keeps the name, inside
 * GitHub's 40 px safe margin. Numbers come from src/data/bench.json and the
 * analyzer's source, like every number the site shows.
 */
async function drawGithubPreview() {
  const W = 1280;
  const H = 640;
  const leads = biggestLeads(4);
  const perEntity = benchNumber('forEach.perEntity');
  const alloc = benchNumber('tick.oneWorld.alloc');
  if (leads.length < 4 || !perEntity || !alloc)
    throw new Error('Benchmark data is missing the comparisons');
  const facts = [
    { value: perEntity.text, label: 'per entity update, [ForEach]' },
    { value: alloc.text, label: 'allocated per server tick' },
    {
      value: String(analyzerRules),
      label: 'analyzer rules, code fixes',
    },
  ];
  // The biggest leads as bars: how many times longer the next-fastest library takes
  const bars = leads.map((l) => ({
    name: l.short.en.charAt(0).toUpperCase() + l.short.en.slice(1),
    ratio: l.ratio,
    text: formatRatio(l.ratio),
    note: `vs ${l.versus.library}`,
  }));
  const chartTitle =
    'Times faster than the next-fastest of Friflo, Arch and DefaultEcs (log scale)';
  const footer = 'ecs.xgamedev.net';

  const fonts = await loadFonts([
    {
      url: '/github',
      lang: 'en',
      eyebrow: 'EasyEcs',
      title: claim.en,
      description: [
        ...facts.map((f) => f.value + f.label),
        ...bars.map((b) => b.name + b.text + b.note),
        chartTitle,
        footer,
      ].join(''),
    },
  ]);
  const set = fonts.en;
  if (!set) throw new Error('No font for the social preview');
  const draw = (content: string, weight: Weight, size: number, color: string) =>
    text(content, {
      fonts: set,
      family: families.en,
      weight,
      size,
      color,
      maxHeight: size * 2,
      width: W - 160,
    });
  const widthOf = async (image: Buffer) =>
    (await sharp(image).metadata()).width ?? 0;
  const layers: Parameters<ReturnType<typeof sharp>['composite']>[0] = [];
  const shapes: string[] = [];
  const centred = async (image: Buffer, top: number, centre = W / 2) =>
    layers.push({
      input: image,
      top,
      left: Math.round(centre - (await widthOf(image)) / 2),
    });

  // Icon + name on one line
  const iconSize = 84;
  const icon = await sharp(logo).resize(iconSize, iconSize).png().toBuffer();
  const name = await draw('EasyEcs', 700, 80, colors.title);
  const nameWidth = await widthOf(name.data);
  const gap = 24;
  const brandLeft = Math.round((W - (iconSize + gap + nameWidth)) / 2);
  const brandTop = 58;
  layers.push(
    { input: icon, left: brandLeft, top: brandTop },
    {
      input: name.data,
      left: brandLeft + iconSize + gap,
      top: brandTop + Math.round((iconSize - name.height) / 2) - 4,
    },
  );
  shapes.push(
    `<rect x="${brandLeft - 0.5}" y="${brandTop - 0.5}" width="${iconSize + 1}" height="${iconSize + 1}" rx="${(iconSize * 14) / 64}" fill="none" stroke="#2a2a2a"/>`,
  );

  // The claim
  await centred((await draw(claim.en, 400, 34, colors.description)).data, 172);

  // Three facts with hairline dividers
  const column = 330;
  const rowLeft = Math.round((W - column * facts.length) / 2);
  const factsTop = 240;
  for (let i = 0; i < facts.length; i++) {
    const centre = rowLeft + column * i + column / 2;
    await centred(
      (await draw(facts[i].value, 700, 46, colors.title)).data,
      factsTop,
      centre,
    );
    await centred(
      (await draw(facts[i].label, 400, 21, colors.footer)).data,
      factsTop + 64,
      centre,
    );
    if (i > 0)
      shapes.push(
        `<rect x="${rowLeft + column * i}" y="${factsTop + 4}" width="1" height="84" fill="#262626"/>`,
      );
  }

  // The leads as bars on a log axis, in the accent
  const chartTop = 372;
  const labelRight = 375;
  const barLeft = labelRight + 20;
  const barMax = 480;
  const rowHeight = 36;
  const longest = Math.log(Math.max(...bars.map((b) => b.ratio)));
  await centred(
    (await draw(chartTitle, 400, 20, colors.footer)).data,
    chartTop,
  );
  for (let i = 0; i < bars.length; i++) {
    const bar = bars[i];
    const top = chartTop + 36 + rowHeight * i;
    const label = await draw(bar.name, 700, 22, colors.title);
    layers.push({
      input: label.data,
      top: top + Math.round((22 - label.height) / 2) + 1,
      left: labelRight - (await widthOf(label.data)),
    });
    const length = Math.max(
      4,
      Math.round((Math.log(bar.ratio) / longest) * barMax),
    );
    shapes.push(
      `<rect x="${barLeft}" y="${top}" width="${length}" height="22" rx="4" fill="${colors.accent}"/>`,
    );
    const value = await draw(
      `${bar.text}  ·  ${bar.note}`,
      700,
      20,
      colors.accent,
    );
    layers.push({
      input: value.data,
      top: top + Math.round((22 - value.height) / 2) + 1,
      left: barLeft + length + 14,
    });
  }

  await centred((await draw(footer, 700, 24, colors.accent)).data, 568);

  const background = Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}"><rect width="${W}" height="${H}" fill="${colors.background}"/>${shapes.join('')}<rect x="0" y="${H - 10}" width="${W}" height="10" fill="${colors.accent}"/></svg>`,
  );
  return sharp(background)
    .composite(layers)
    .png({ compressionLevel: 9, palette: true, quality: 90 })
    .toBuffer();
}

// `--default`: redraw public/og.png, the card of /en
if (process.argv.includes('--default')) {
  const [card] = homeCards();
  const fonts = await loadFonts([card]);
  if (!fonts.en) throw new Error('No font for the default card');
  await fs.writeFile(
    path.resolve('public/og.png'),
    await drawCard(card, fonts.en),
  );
  console.log('Wrote public/og.png');
}

// `--github`: redraw ../.github/social-preview.png (upload it in the
// repository's Settings -> General -> Social preview)
if (process.argv.includes('--github')) {
  const target = path.resolve('../.github/social-preview.png');
  const image = await drawGithubPreview();
  await fs.writeFile(target, image);
  console.log(
    `Wrote ${path.relative(process.cwd(), target)} (${Math.round(image.length / 1024)} KB)`,
  );
}
