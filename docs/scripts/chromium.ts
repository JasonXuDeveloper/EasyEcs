/**
 * First step of `bun run build`: rehype-mermaid draws the diagrams in a
 * headless Chromium (src/lib/mermaid.ts). Where Playwright's browser is not
 * installed yet (Cloudflare's build), install its headless shell.
 */
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';

async function launches() {
  try {
    await (await chromium.launch()).close();
    return true;
  } catch (error) {
    console.log(`[chromium] ${String(error).split('\n')[0]}`);
    return false;
  }
}

if (!(await launches())) {
  execFileSync('bunx', ['playwright', 'install', '--only-shell', 'chromium'], {
    stdio: 'inherit',
  });
  if (!(await launches())) {
    console.error(
      '[chromium] Playwright installed Chromium but it does not start (missing system libraries?): the diagrams cannot be drawn.',
    );
    process.exit(1);
  }
}
