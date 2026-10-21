/**
 * Site-wide facts used by the page metadata, the structured data (JSON-LD),
 * the sitemap, robots.txt and llms.txt. Change the domain here only.
 */
import type { Locale } from './i18n';

export const siteUrl = 'https://ecs.xgamedev.net';
export const siteName = 'EasyEcs';

export const githubUrl = 'https://github.com/JasonXuDeveloper/EasyEcs';
/** The docs live in the library's repository, under docs/ */
export const docsRepoUrl = githubUrl;
/** The content folder on GitHub: edit links and "Open in GitHub" */
export const contentBlobUrl = `${githubUrl}/blob/master/docs/content`;
export const contentEditUrl = `${githubUrl}/edit/master/docs/content`;
export const nugetUrl = 'https://www.nuget.org/packages/EasyEcs';
export const licenseUrl = `${githubUrl}/blob/master/LICENSE`;
export const author = {
  name: 'Jason Xu',
  url: 'https://github.com/JasonXuDeveloper',
};

declare const __DOCS_VERSION__: string;

/** The release the docs describe: major.minor of the changelog's newest section (vite.config.ts) */
export const docsVersion = __DOCS_VERSION__;

declare const __ANALYZER_RULES__: number;

/** How many rules the analyzer has: the EECS ids in src/EasyEcs.Generator (vite.config.ts) */
export const analyzerRules = __ANALYZER_RULES__;
export const installCommand = 'dotnet add package EasyEcs';

/** BCP 47 tags: `<html lang>`, hreflang, JSON-LD inLanguage */
export const languageTag: Record<Locale, string> = {
  en: 'en',
  zh: 'zh-CN',
};
/** Open Graph og:locale */
export const ogLocale: Record<Locale, string> = {
  en: 'en_US',
  zh: 'zh_CN',
};

/**
 * Search engine verification codes, read at build time from Vite env
 * variables (set them on the Cloudflare Pages project). Empty ones are not
 * rendered. See docs/CLAUDE.md, "SEO and GEO".
 */
export const siteVerification = {
  google: import.meta.env.VITE_GOOGLE_SITE_VERIFICATION as string | undefined,
  bing: import.meta.env.VITE_BING_SITE_VERIFICATION as string | undefined,
  baidu: import.meta.env.VITE_BAIDU_SITE_VERIFICATION as string | undefined,
};

/**
 * One-paragraph definition of EasyEcs, quoted by llms.txt and the JSON-LD.
 * Keep it true to the EasyEcs repository (README, src/EasyEcs.Core.csproj,
 * Directory.Build.props) and to the docs.
 */
export const productSummary: Record<Locale, string> = {
  en: 'EasyEcs is an open-source, high-performance archetype Entity Component System (ECS) for C# and .NET 10, built for speed first and zero allocation second. Components are plain structs stored in archetype tables with 64-byte-aligned columns; systems are [ForEach] methods that a Roslyn source generator compiles into JIT-inlined struct jobs, and an analyzer with code fixes reports ECS design mistakes and avoidable costs at compile time. Structural changes apply immediately, even in the middle of a pass, with no command buffer, and after warm-up updates, queries and structural changes allocate nothing. Change tracking, removal detection, prefabs, capacity reservation before spawn waves (Reserve), dynamic buffers, value indexes, relations and hierarchies, parallel systems, snapshots, saves and entity migration are built in. It fits any program that updates many entities every frame or tick: game servers, Godot 4 games, simulations and real-time backends. It targets net10.0 only, so it runs in any .NET 10 process, Godot 4 .NET projects that target net10.0 included; Unity is not supported yet. MIT licensed, installed from NuGet as EasyEcs.',
  zh: 'EasyEcs 是开源、高性能的 archetype（原型）实体组件系统（ECS），面向 C# 与 .NET 10：速度第一，零 GC 分配第二。组件是普通结构体，按原型存放在列式表中，列按 64 字节对齐；系统就是 [ForEach] 方法，由 Roslyn 源生成器编译成可被 JIT 内联的结构体 job，配套的分析器（带代码修复）在编译期报告 ECS 设计错误和可以省掉的开销。结构变更立即生效，遍历途中也可以增删组件、创建销毁实体，不需要命令缓冲；预热之后，更新、查询和结构变更都不再分配内存。内置变更追踪、移除检测、预制体、批量生成前的容量预留（Reserve）、动态缓冲、值索引、关系与层级、并行系统、快照、存档与实体迁移。适合每帧或每个 tick 都要更新大量实体的程序：游戏服务器、Godot 4 游戏、模拟与实时后端。只面向 net10.0，因此任何 .NET 10 进程都能用，包括面向 net10.0 的 Godot 4 .NET 项目；暂不支持 Unity。MIT 开源协议，通过 NuGet 包 EasyEcs 安装。',
};
