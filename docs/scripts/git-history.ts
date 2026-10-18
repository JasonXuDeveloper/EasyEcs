/**
 * Runs before every build: makes sure the full git history is there, so the
 * "last updated" date of each page (fumadocs-mdx `lastModified`, read from
 * `git log` in source.config.ts) is the date of the last commit that touched
 * the page.
 *
 * In a shallow clone, the oldest commit we have looks like it added every
 * file, so pages that did not change since would all show that commit's date.
 * This script fetches the rest of the history. It never fails the build: if
 * there is no git, no remote or no network, it prints a warning, and
 * source.config.ts leaves the date out while the clone is still shallow.
 */
import { spawnSync } from 'node:child_process';

function git(...args: string[]) {
  const result = spawnSync('git', args, {
    encoding: 'utf8',
    timeout: 120_000,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  return {
    ok: result.status === 0,
    out: (result.stdout ?? '').trim(),
    err: (result.stderr ?? '').trim() || result.error?.message || '',
  };
}

function isShallow() {
  const result = git('rev-parse', '--is-shallow-repository');
  return result.ok ? result.out === 'true' : undefined;
}

const shallow = isShallow();
if (shallow === undefined) {
  console.warn(
    '[git-history] Not a git repository (or git is missing): pages will show no "last updated" date.',
  );
} else if (!shallow) {
  console.log('[git-history] Full history present.');
} else {
  console.log('[git-history] Shallow clone: fetching the full history...');
  const fetched = git('fetch', '--unshallow', '--no-tags', '--quiet');
  if (!fetched.ok)
    console.warn(`[git-history] git fetch failed: ${fetched.err}`);
  if (isShallow() === false) {
    console.log('[git-history] Full history fetched.');
  } else {
    console.warn(
      '[git-history] Still a shallow clone: pages will show no "last updated" date.',
    );
  }
}
