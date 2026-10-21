/**
 * Checks the terms of the Chinese pages against the glossary (tools/ApiDocs/zh/README.md, glossary.zh.mdx): a
 * word the glossary replaced, or a link to a page labelled with another name than the page's title, fails. Code
 * (fences and inline) is skipped: identifiers stay as they are.
 *
 * Usage: bun run lint:terms
 */
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';

const content = join(import.meta.dirname, '..', 'content');

interface Rule {
  /** What the glossary does not use. */
  wrong: RegExp;
  /** What it uses instead. */
  right: string;
}

const rules: Rule[] = [
  { wrong: /过滤器/g, right: '过滤条件 (filter)' },
  { wrong: /结构门/g, right: '结构闸门 (structural gate)' },
  { wrong: /所属线程/g, right: '所有者线程 (owner thread)' },
  { wrong: /父节点/g, right: '父实体 (parent)' },
  { wrong: /跟踪/g, right: '追踪 (tracking)' },
  { wrong: /世界更新/g, right: 'World 更新 (the World, not a 世界)' },
  // A link to a page is labelled with the page's title, as its sidebar entry is.
  {
    wrong: /\[(?:多线程|线程模型|线程)\]\(\/zh\/docs\/guide\/threading/g,
    right: '[多线程与并行](/zh/docs/guide/threading…)',
  },
  {
    wrong: /title="(?:多线程|线程模型)" href="\/zh\/docs\/guide\/threading/g,
    right: 'title="多线程与并行"',
  },
];

// Pages still being brought in line, with the rules they may break until then. Remove an entry once its page is
// fixed: the script then holds it to every rule.
const pending: Record<string, RegExp[]> = {};

function* pages(dir: string): Generator<string> {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) yield* pages(path);
    else if (entry.name.endsWith('.zh.mdx')) yield path;
  }
}

// Code keeps its spelling; blank it out with the same number of lines, so line numbers stay right.
function prose(text: string) {
  return text
    .replace(/```[\s\S]*?```/g, (block) => block.replace(/[^\n]/g, ' '))
    .replace(/`[^`\n]*`/g, (code) => ' '.repeat(code.length));
}

let problems = 0;
for (const path of pages(content)) {
  const file = relative(content, path).replaceAll('\\', '/');
  const allowed = pending[file] ?? [];
  const lines = prose(readFileSync(path, 'utf8')).split('\n');
  lines.forEach((line, index) => {
    for (const rule of rules) {
      for (const match of line.matchAll(rule.wrong)) {
        if (
          allowed.some((pattern) => new RegExp(pattern.source).test(match[0]))
        )
          continue;
        console.error(
          `content/${file}:${index + 1}: "${match[0]}": the glossary writes ${rule.right}`,
        );
        problems++;
      }
    }
  });
}

if (problems > 0) {
  console.error(
    `${problems} term problem(s): see the glossary in tools/ApiDocs/zh/README.md`,
  );
  process.exit(1);
}
console.log('Chinese terms follow the glossary');
