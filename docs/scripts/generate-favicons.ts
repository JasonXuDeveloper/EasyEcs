/**
 * Generate the favicons and the navbar logo from public/icon.svg (the EasyEcs
 * icon: a column of entity cells beside component rows, forming an E). The
 * social cards are drawn by scripts/og.ts (`bun run og:default` redraws
 * public/og.png).
 *
 * Usage: bun run favicons
 */
import { writeFile } from 'node:fs/promises';
import path from 'node:path';
import sharp from 'sharp';
import ico from 'sharp-ico';

const publicDir = path.resolve(import.meta.dirname, '../public');
const source = path.join(publicDir, 'icon.svg');

const pngVariants = [
  { name: 'favicon-16x16.png', size: 16 },
  { name: 'favicon-32x32.png', size: 32 },
  { name: 'apple-touch-icon.png', size: 180 },
  { name: 'android-chrome-192x192.png', size: 192 },
  { name: 'android-chrome-512x512.png', size: 512 },
  { name: 'favicon.png', size: 512 },
  { name: 'logo.png', size: 128 },
];

function resize(size: number) {
  return sharp(source, { density: 72 * Math.max(1, size / 64) }).resize(
    size,
    size,
    {
      fit: 'contain',
      background: { r: 0, g: 0, b: 0, alpha: 0 },
    },
  );
}

async function main() {
  console.log('Generating icons from', path.relative('.', source));

  await Promise.all(
    pngVariants.map(async ({ name, size }) => {
      await resize(size)
        .png({ compressionLevel: 9 })
        .toFile(path.join(publicDir, name));
      console.log(`  ${name} (${size}x${size})`);
    }),
  );

  const icoSizes = [16, 32, 48];
  const buffers = await Promise.all(
    icoSizes.map((size) => resize(size).png().toBuffer()),
  );
  await writeFile(path.join(publicDir, 'favicon.ico'), ico.encode(buffers));
  console.log(`  favicon.ico (${icoSizes.join('+')})`);
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
