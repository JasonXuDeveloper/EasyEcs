# Chinese API reference

The Chinese text of `docs/content/reference/*.zh.mdx`: one file per reference page (`world.xml` for `world.zh.mdx`),
one `<member name="<doc id>" source="<hash>">` per documented member, with the elements of the English XML doc.
`tools/ApiDocs` checks every entry against the English (see `../README.md`).

## Changing a doc comment

1. Change the XML doc comment in `src/EasyEcs.Core`.
2. `dotnet run --project tools/ApiDocs`: it names each entry whose English changed and prints the new English.
3. Rewrite the Chinese entry to match it and set `source` to the hash the message gives. A new member:
   `dotnet run --project tools/ApiDocs -- --zh-scaffold` adds it with the English text to translate.
4. Run the tool again (0 problems) and commit the XML with the regenerated pages.

## Markup

- Keep the markup of the English: `<see cref="..."/>` (the same ids), `<paramref>`, `<typeparamref>`, `<c>`, `<para>`,
  `<br/>`. A `<see cref>` may carry a Chinese label: ``<see cref="T:EasyEcs.UpdateInGroupAttribute`1">分组</see>``.
- `<code>` blocks are copied unchanged, comments included.
- A plain path in the English ("see docs/content/guide/threading.md") becomes a link:
  `（见<see href="/zh/docs/guide/threading">多线程与并行</see>）`.
- Wrap lines freely: a line break between two Chinese characters, or next to full-width punctuation, is dropped; between
  Chinese and Latin text it is one space.

## Style

Write as a Chinese .NET server developer writes docs (`docs/WRITING.md`, "Tone: Chinese"): short, direct sentences, one
point each; no 被 where an active sentence works, no 进行 / 对……进行 / 该 / 此 / 其 chains, no 值得注意的是 / 从而 / 以便 /
能够有效 filler, no English word order. Identifiers, type names, code, `[ForEach]` and EECS ids stay English, as code
where the English has code. Full-width punctuation in prose（，。：；（）、“”）, half-width inside code; a space between
Chinese and Latin text or numbers, as the other Chinese pages do. Complexity stays a formula with Chinese names:
O(实体数 × 组件数), O(匹配的原型数 + 行数), 均摊 O(1).

## Glossary

The terms of the Chinese guide pages (`docs/content/glossary.zh.mdx`). Use these and no others.

| English | 中文 |
| --- | --- |
| world | World |
| entity / entity handle | 实体 / 句柄 |
| id, index, version (incarnation) | id、下标、版本号（这个版本的实体） |
| stale handle / id | 失效的句柄 / id |
| alive | 存活 |
| component / tag / singleton | 组件 / 标签 / 单例 |
| archetype | 原型 |
| table, column, row | 表、列、行 |
| presence bit | 存在位 |
| run of rows | 一段连续的行 |
| chunk (parallel) | 块 |
| hole / tombstone | 空洞 / 墓碑 |
| query, filter; any-set (`ChangedAny` / `RemovedAny`) | 查询、过滤条件；集合 |
| pass (an iteration in progress) | 遍历（遍历进行中） |
| system, system group, default group | 系统、系统分组（分组）、默认分组 |
| stage | 阶段 |
| update / tick | 更新 / tick |
| struct job, batch job | 结构体 job、批处理 job |
| structural change | 结构变更 |
| structural gate | 结构闸门 |
| owner thread | 所有者线程 |
| thread pool | 线程池 |
| change tick | 变更 tick |
| tracked component; counts as a write; stamped | 被追踪的组件；算一次写入；打 tick |
| removal | 移除 |
| access (of a system) / runs alone | 读写 / 独占运行 |
| prefab, member, instantiate | 预制体、成员、实例化 |
| dynamic buffer | 动态缓冲（缓冲） |
| value index, key | 值索引、键 |
| relation, link, target, source | 关系、链接、目标、来源实体 |
| hierarchy, parent, child, root | 层级、父实体、子实体、根 |
| group (`IGroup`) | 分组 |
| cleanup component; retired (entity); destroyed (freed) | 清理组件；退休（的实体）；销毁 |
| disable / enable | 禁用 / 启用 |
| snapshot, capture, restore | 快照、拍快照、恢复 |
| save, load | 存档、加载 |
| collectible context | 可回收上下文 |
| allocation-free | 零分配 |
| amortized | 均摊 |
| cold (path) | 冷路径 |
| diagnostics | 诊断 |
| bookkeeping | 簿记 |
