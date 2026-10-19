/**
 * Line breaks between Chinese characters. The Chinese pages wrap their source
 * lines like the English ones, and a line break inside a paragraph is a space
 * in HTML: Chromium draws it between two Chinese characters ("正面 对比"),
 * where Chinese text has none. This drops the break (and the spaces around it)
 * when the characters on both sides are Chinese or full-width punctuation, also
 * across a link or emphasis boundary. A break next to Latin text, code or a
 * number stays a space, as the Chinese pages write them. Build time only
 * (imported by source.config.ts).
 */
import type { Nodes, Root } from 'mdast';

const wide = '\\p{Script=Han}\\u3000-\\u303f\\uff00-\\uffef\\u201c\\u201d';
const inside = new RegExp(`([${wide}])[ \\t]*\\n[ \\t]*(?=[${wide}])`, 'gu');
const trailing = new RegExp(`([${wide}])[ \\t]*\\n[ \\t]*$`, 'u');
const leading = new RegExp(`^[ \\t]*\\n[ \\t]*(?=[${wide}])`, 'u');
const isWide = new RegExp(`^[${wide}]$`, 'u');

/** The first (or last) character of a node's text, looking into its children. */
function edge(node: Nodes, last: boolean): string | undefined {
  if (node.type === 'text') {
    const chars = Array.from(node.value);
    return last ? chars.at(-1) : chars[0];
  }
  if (!('children' in node) || node.children.length === 0) return undefined;
  const child = node.children[last ? node.children.length - 1 : 0];
  return edge(child as Nodes, last);
}

function join(parent: Nodes) {
  if (!('children' in parent)) return;
  const children = parent.children as Nodes[];
  for (let i = 0; i < children.length; i++) {
    const node = children[i];
    if (node.type === 'text') {
      node.value = node.value.replace(inside, '$1');
      const next = children[i + 1];
      if (
        next &&
        trailing.test(node.value) &&
        isWide.test(edge(next, false) ?? '')
      ) {
        node.value = node.value.replace(trailing, '$1');
      }
      const previous = children[i - 1];
      if (
        previous &&
        leading.test(node.value) &&
        isWide.test(edge(previous, true) ?? '')
      ) {
        node.value = node.value.replace(leading, '');
      }
    } else if (node.type !== 'code' && node.type !== 'inlineCode') {
      join(node);
    }
  }
}

export function remarkCjkLineBreaks() {
  return (tree: Root) => join(tree);
}
