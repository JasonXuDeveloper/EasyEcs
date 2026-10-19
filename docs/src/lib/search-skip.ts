import type { Root, RootContent } from 'mdast';
import type { VFile } from 'vfile';

/** `{/* search: skip *\/}` on its own line: the block right after it stays out of the search index */
const marker = /^\s*\/\*\s*search:\s*skip\s*\*\/\s*$/;

/**
 * Keeps the block after a `{/* search: skip *\/}` comment (a table, a list, a
 * paragraph, a component) out of the search index (/api/search/{lang}.json),
 * for content whose words would outrank the pages about them: the
 * performance page's list of test and benchmark names matches nearly every
 * feature query. The block still renders and stays in the Markdown copies and
 * llms files; only search loses it.
 *
 * It uses remark-structure's own hook: a node that already carries
 * `data.structuredData` is recorded with those records (none here) instead of
 * its text, and its children are skipped. Runs before remark-structure
 * (Fumadocs puts the configured remark plugins first).
 */
export function remarkSearchSkip() {
  return (tree: Root, file: VFile) => {
    const children = tree.children;
    for (let i = 0; i < children.length; i++) {
      const node = children[i];
      if (node.type !== 'mdxFlowExpression' || !marker.test(node.value))
        continue;
      const next: RootContent | undefined = children[i + 1];
      if (!next)
        throw new Error(
          `${file.path}: {/* search: skip */} must be followed by the block to leave out of search`,
        );
      next.data = { ...next.data, structuredData: { contents: [] } };
    }
  };
}
