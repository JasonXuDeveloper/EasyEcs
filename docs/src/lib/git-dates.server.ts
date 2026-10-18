import { execFileSync } from 'node:child_process';

let fullHistory: boolean | undefined;

/** Like source.config.ts: dates from a shallow clone would be wrong. */
function hasFullHistory() {
  if (fullHistory === undefined) {
    try {
      fullHistory =
        execFileSync('git', ['rev-parse', '--is-shallow-repository'], {
          encoding: 'utf8',
          stdio: ['ignore', 'pipe', 'ignore'],
        }).trim() === 'false';
    } catch {
      fullHistory = false;
    }
  }
  return fullHistory;
}

const cache = new Map<string, string | undefined>();

/**
 * ISO date of the commit that added a file (following renames), for
 * `datePublished`. Undefined without git, in a shallow clone, or for a file
 * that is not committed yet. Build time only (the docs loader).
 */
export function firstCommitDate(file: string) {
  if (cache.has(file)) return cache.get(file);
  let date: string | undefined;
  if (hasFullHistory()) {
    try {
      const out = execFileSync(
        'git',
        ['log', '--follow', '--diff-filter=A', '--format=%aI', '--', file],
        { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] },
      ).trim();
      const last = out.split('\n').pop();
      if (last && !Number.isNaN(Date.parse(last)))
        date = new Date(last).toISOString();
    } catch {}
  }
  cache.set(file, date);
  return date;
}
