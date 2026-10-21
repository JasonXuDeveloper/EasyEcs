/**
 * Prints `<file>\t<ISO date>` for each given file, the date of the commit
 * that added it (following renames; empty without one), as soon as it is
 * known: one `git log` per file, one per core at a time, in the order given.
 * Run by src/lib/git-dates.server.ts in this small process of its own:
 * starting a process from the prerendering one costs its main thread a
 * fiftieth of a second each.
 */
import { execFile } from 'node:child_process';
import { availableParallelism } from 'node:os';
import { promisify } from 'node:util';

const run = promisify(execFile);
const queue = process.argv.slice(2);

async function addedDate(file) {
  try {
    const { stdout } = await run('git', [
      'log',
      '--follow',
      '--diff-filter=A',
      '--format=%aI',
      '--',
      file,
    ]);
    const last = stdout.trim().split('\n').pop();
    if (last && !Number.isNaN(Date.parse(last)))
      return new Date(last).toISOString();
  } catch {}
  return '';
}

await Promise.all(
  Array.from({ length: availableParallelism() }, async () => {
    for (let file = queue.shift(); file !== undefined; file = queue.shift())
      process.stdout.write(`${file}\t${await addedDate(file)}\n`);
  }),
);
