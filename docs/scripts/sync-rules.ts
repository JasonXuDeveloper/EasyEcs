// The rules file for AI assistants has one source: the public skill (skills/easyecs/SKILL.md, installed with
// `npx skills add JasonXuDeveloper/EasyEcs`). The site serves its body without the frontmatter at /easyecs-rules.md.
// `bun scripts/sync-rules.ts` writes public/easyecs-rules.md; `--check` fails when it is stale.
import { readFileSync, writeFileSync } from 'node:fs';

const skill = readFileSync(
  new URL('../../skills/easyecs/SKILL.md', import.meta.url),
  'utf8',
);
const match = /^---\n[\s\S]*?\n---\n+/.exec(skill);
if (!match) throw new Error('skills/easyecs/SKILL.md: missing frontmatter');
const body = skill.slice(match[0].length);
const target = new URL('../public/easyecs-rules.md', import.meta.url);

if (process.argv.includes('--check')) {
  if (readFileSync(target, 'utf8') !== body) {
    console.error(
      'public/easyecs-rules.md is stale: run `bun run sync:rules` (edit skills/easyecs/SKILL.md, not the copy)',
    );
    process.exit(1);
  }
} else {
  writeFileSync(target, body);
}
