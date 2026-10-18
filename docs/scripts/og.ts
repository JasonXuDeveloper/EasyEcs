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
 * `vite-node --mode scripts scripts/og.ts --default`
 * redraws public/og.png.
 */
import { execFileSync } from 'node:child_process';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import sharp from 'sharp';
import type { Locale } from '../src/lib/i18n';

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
  },
) {
  const { data, info } = await sharp({
    text: {
      text: `<span foreground="${options.color}">${escapeMarkup(content)}</span>`,
      font: `${options.fonts[options.weight] ? options.family : 'sans-serif'} ${options.weight === 700 ? 'Bold' : ''} ${options.size}`,
      fontfile: options.fonts[options.weight],
      width: textWidth,
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

// Redraw the default card, public/og.png
if (process.argv.includes('--default')) {
  const card: Card = {
    url: '/default',
    lang: 'en',
    eyebrow: 'EasyEcs',
    title: 'The allocation-free ECS for C# and .NET 10 servers',
    description:
      'Source-generated [ForEach] systems, immediate structural changes, change tracking, indexes, relations and snapshots. 0 B per tick, for game servers and simulations.',
  };
  const fonts = await loadFonts([card]);
  if (!fonts.en) throw new Error('No font for the default card');
  await fs.writeFile(
    path.resolve('public/og.png'),
    await drawCard(card, fonts.en),
  );
  console.log('Wrote public/og.png');
}
