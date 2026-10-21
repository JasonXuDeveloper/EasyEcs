import { execFileSync, spawn } from 'node:child_process';
import { glob } from 'node:fs/promises';
import * as path from 'node:path';
import { createInterface } from 'node:readline';

/** Like source.config.ts: dates from a shallow clone would be wrong. */
function hasFullHistory() {
  try {
    return (
      execFileSync('git', ['rev-parse', '--is-shallow-repository'], {
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'ignore'],
      }).trim() === 'false'
    );
  } catch {
    return false;
  }
}

type Waiting = PromiseWithResolvers<string | undefined>;

/**
 * Starts reading the date of every file under content/, in the order the
 * prerender visits them (react-router.config.ts globs the same way), while
 * the pages render.
 */
function readDates() {
  const waiting = new Map<string, Waiting>();
  const entry = (file: string) => {
    let date = waiting.get(file);
    if (!date) {
      date = Promise.withResolvers();
      waiting.set(file, date);
    }
    return date;
  };
  // Once the process ends, a file it did not print has no date.
  let finished = false;
  const done = () => {
    finished = true;
    for (const date of waiting.values()) date.resolve(undefined);
  };
  if (!hasFullHistory()) done();
  else
    (async () => {
      const files: string[] = [];
      for await (const file of glob('**/*.{md,mdx}', { cwd: 'content' }))
        files.push(`content/${file}`);
      const child = spawn(
        process.execPath,
        [path.resolve('scripts/first-commit-dates.mjs'), ...files],
        { stdio: ['ignore', 'pipe', 'ignore'] },
      );
      createInterface({ input: child.stdout }).on('line', (line) => {
        const [file, date] = line.split('\t');
        entry(file).resolve(date || undefined);
      });
      child.on('close', done).on('error', done);
    })().catch(done);
  return (file: string) => {
    const date = entry(file);
    if (finished) date.resolve(undefined);
    return date.promise;
  };
}

let dateOf: ((file: string) => Promise<string | undefined>) | undefined;

/**
 * ISO date of the commit that added a file under content/ (following
 * renames), for `datePublished`. Undefined without git, in a shallow clone,
 * or for a file that is not committed yet. Build time only (the docs loader).
 * The first call starts reading every file's date in a process of its own
 * (scripts/first-commit-dates.mjs: one `git log` per file, which one after
 * the other were more than half of the prerender); a page waits for its own.
 */
export function firstCommitDate(file: string) {
  dateOf ??= readDates();
  return dateOf(file);
}
