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
export const migrationGuideUrl = `${githubUrl}/blob/master/MIGRATION_GUIDE.md`;
export const author = {
  name: 'Jason Xu',
  url: 'https://github.com/JasonXuDeveloper',
};

declare const __DOCS_VERSION__: string;

/** The release the docs describe: major.minor of the changelog's newest section (vite.config.ts) */
export const docsVersion = __DOCS_VERSION__;
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
  en: 'EasyEcs is an open-source, high-performance archetype Entity Component System (ECS) for C# and .NET 10 servers, built for speed first and zero allocation second. Components are plain structs stored in archetype tables with 64-byte-aligned columns; systems are [ForEach] methods that a Roslyn source generator compiles into JIT-inlined struct jobs, and an analyzer with code fixes reports ECS design mistakes at compile time. Structural changes apply immediately, even in the middle of a pass, with no command buffer, and after warm-up updates, queries and structural changes allocate nothing. Change tracking, removal detection, prefabs, dynamic buffers, value indexes, relations and hierarchies, parallel systems, snapshots, saves and entity migration are built in. It targets net10.0 only (no Unity) and is built for game servers, simulations and other long-running real-time backends. MIT licensed, installed from NuGet as EasyEcs.',
  zh: 'EasyEcs 是开源、高性能的 archetype（原型）实体组件系统（ECS），面向 C# 与 .NET 10 服务端：速度第一，零 GC 分配第二。组件是普通结构体，按原型存放在列式表中，列按 64 字节对齐；系统就是 [ForEach] 方法，由 Roslyn 源生成器编译成可被 JIT 内联的结构体 job，配套的分析器（带代码修复）在编译期报告 ECS 设计错误。结构变更立即生效，遍历途中也可以增删组件、创建销毁实体，不需要命令缓冲；预热之后，更新、查询和结构变更都不再分配内存。内置变更追踪、移除检测、预制体、动态缓冲、值索引、关系与层级、并行系统、快照、存档与实体迁移。只面向 net10.0（不支持 Unity），专为游戏服务器、模拟以及其他长时间运行的实时后端打造。MIT 开源协议，通过 NuGet 包 EasyEcs 安装。',
};
