# Unity SBP Lab Wiki

> 面向 **Unity 2022.3 + Scriptable Build Pipeline 1.23.4** 的动手实验手册。
> 工程：`_e2e/UnityGradleDemo`（隔离的演示工程，不进入正式项目）。
> 所有示例都带详细中文注释，可以直接在 Unity 菜单里跑，也可以用 batchmode 跑。

---

## 目录

- [0. 这是什么](#0-这是什么)
- [1. 快速开始](#1-快速开始)
- [2. 心智模型](#2-心智模型)
- [3. 案例总览](#3-案例总览)
- [4. 案例详解](#4-案例详解)
  - [01 Compatibility 最小平替](#01-compatibility-最小平替)
  - [02 ContentPipeline 显式布局](#02-contentpipeline-显式布局)
  - [03 压缩方式对比矩阵](#03-压缩方式对比矩阵)
  - [04 每个 bundle 独立压缩](#04-每个-bundle-独立压缩)
  - [05 自定义 BuildTask](#05-自定义-buildtask)
  - [06 任务链插入 + 自定义上下文](#06-任务链插入--自定义上下文)
  - [07 后处理：交付目录与清单](#07-后处理交付目录与清单)
  - [08 增量缓存实验](#08-增量缓存实验)
  - [09 自定义 BuildLogger（性能剖析）](#09-自定义-buildlogger性能剖析)
  - [10 Content File 管线（Addressables CCD 同款）](#10-content-file-管线addressables-ccd-同款)
  - [11 加密交付 + 运行时加载验证](#11-加密交付--运行时加载验证)
  - [12 自定义打包策略（合并小 bundle）](#12-自定义打包策略合并小-bundle)
  - [13 多配置构建矩阵 + 报告聚合](#13-多配置构建矩阵--报告聚合)
  - [14 旧管线 vs SBP 产物对比](#14-旧管线-vs-sbp-产物对比)
- [5. 源码阅读路线](#5-源码阅读路线)
- [6. 观测与调试](#6-观测与调试)
- [7. 常见坑（实测）](#7-常见坑实测)
- [8. 从 Lab 到生产](#8-从-lab-到生产)
- [9. 清理与恢复](#9-清理与恢复)
- [10. 子 Wiki：任务链 / 确定性构建 / 构建缓存](#10-子-wiki任务链--确定性构建--构建缓存)
- [附录 A. 术语表](#附录-a-术语表)
- [附录 B. API 速查](#附录-b-api-速查)
- [附录 C. 任务链逐任务详解（源码级）](#附录-c-任务链逐任务详解源码级)
- [附录 D. 参考资料与延伸阅读](#附录-d-参考资料与延伸阅读)
- [附录 E. 产物清单（实测）](#附录-e-产物清单实测)

---

## 0. 这是什么

这是一个**最小可运行**的 Scriptable Build Pipeline（SBP）学习实验场。目标不是教你“怎么打包”，而是让你看清：

1. SBP 到底是怎么把一个资源变成 bundle 的（数据流）；
2. 它比内置 `BuildPipeline.BuildAssetBundles` 多了哪些可编程节点；
3. 在真实项目里，增量、压缩、后处理、交付这些需求该挂在哪。

> SBP 官方定位（摘自社区文章）：把原本在 C++ 里的内容构建流程移到 C#，从而
> **自定义构建内容、加速 AssetBundle 构建、改善增量构建、提供更大灵活性**。
> 代价是它不再是黑盒：流程、缓存、清单都需要你自己理解和维护——这正是本 lab 存在的意义。

### 前置条件

| 项 | 值 |
|---|---|
| Unity | 2022.3.17f1c1 |
| SBP 包 | `com.unity.scriptablebuildpipeline` 1.23.4（已装入 `Packages/manifest.json`） |
| 目标平台 | Android（工程自带，IL2CPP/ARM64） |
| 目录 | 所有产物在 `Artifacts/SBP/`，不污染 `Assets/` |

> 版本对应：SBP **1.23.x** 支持 Unity 2021.3+；**2.x/3.x/4.x** 需要 Unity 2023.1+ / Unity 6。
> 别在 2022.3 里装 2.x。

### 文件结构

```
Assets/Editor/
├── SbpLabBuild.cs                      # 核心 lab：0-4 号菜单（建资源/构建/改资源/diff）
└── SbpLab/
    ├── SbpLabCommon.cs                 # 公共工具：路径、内容构造、结果打印、batch 包装
    ├── SbpLabEx01_CompatibilityMinimal.cs
    ├── SbpLabEx02_ExplicitLayout.cs
    ├── SbpLabEx03_CompressionMatrix.cs
    ├── SbpLabEx04_PerBundleCompression.cs
    ├── SbpLabEx05_CustomBuildTask.cs
    ├── SbpLabEx06_CustomTaskChain.cs
    ├── SbpLabEx07_DeliveryPostProcess.cs
    ├── SbpLabEx08_CacheExperiment.cs
    ├── SbpLabEx09_CustomLogger.cs          # 自定义 IBuildLogger（性能剖析）
    ├── SbpLabEx10_ContentFilePipeline.cs   # Content File / Cluster 管线（CCD 同款）
    ├── SbpLabEx11_EncryptedDelivery.cs     # 加密交付 + 运行时解密加载验证
    ├── SbpLabEx12_PackingStrategy.cs       # 布局改写（合并小 bundle）
    ├── SbpLabEx13_BuildMatrix.cs           # 多配置构建矩阵 + 报告聚合
    ├── SbpLabEx14_LegacyVsSbpCompare.cs    # 旧 BuildPipeline vs SBP 产物对比
    ├── SbpLabEx15_PresetComparison.cs      # Preset 对比：内置 shader 抽取
    ├── SbpLabEx16_HeavyCompression.cs      # 重内容压缩对比（约 179 MB 源资源）
    ├── SbpLabEx17_Determinism.cs           # 确定性构建 + 缓存一致性验证
    ├── SbpLabEx18_StageProbe.cs            # 全流程阶段探针（19 任务逐阶段抓数据）
    ├── SbpLabHeavySetup.cs                 # 生成 8 张 2048² 未压缩贴图（体积实验用）
    └── SbpLabExamplesRunner.cs         # 批量入口
Assets/Bundles/                          # 轻量测试资源（材质 + 3 个 prefab）
Assets/Bundles/Heavy/                    # 重内容（约 179 MB，仅本地实验）：
                                         #   Audio/  ← ARPGTutorial 拷贝（26 MB）
                                         #   Art/    ← ARPGTutorial 动画 FBX 60 个（153 MB）
                                         #   Textures/ ← 程序生成的未压缩大贴图（约 96 MB packed）
Artifacts/SBP/                          # 所有构建产物
Library/PackageCache/com.unity.scriptablebuildpipeline@1.23.4/   # 源码（读它的地方）
```

---

## 1. 快速开始

### 1.1 GUI 方式（推荐）

1. 用 Unity 打开 `_e2e/UnityGradleDemo`；
2. 菜单 `SBP Lab/0 - Setup Test Assets` 生成测试资源（材质 + 3 个 prefab + bundle 标签）；
3. 按 `SBP Lab/Examples/01` ~ `08` 顺序点；
4. 每次构建完，看 Console 里的 `[Ex..]` 日志 + `Artifacts/SBP/` 下的产物。

### 1.2 命令行方式（和你现有 e2e 流程一致）

> 本文所有示例均在 2026-11-07 用 `SbpLabExamplesRunner.RunAllExamples` 全量跑通（退出码 0）；
> 案例 14（旧管线对比）、15（Preset 对比）、16（重内容压缩）不在批量入口里，需要单独执行：
> `-executeMethod SbpLabEx14_LegacyVsSbpCompare.Run` / `SbpLabEx15_PresetComparison.Run` /
> `SbpLabEx16_HeavyCompression.Run`。
> 下面各案例的“预期输出”都是那次运行的真实日志（数字随机器/平台浮动）。

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\2022.3.17f1c1\Editor\Unity.exe'
$proj  = '<工程路径>\UnityGradleDemo'

# 全部示例
& $unity -batchmode -quit -nographics -projectPath $proj `
  -executeMethod SbpLabExamplesRunner.RunAllExamples -logFile -

# 只跑某一个
& $unity -batchmode -quit -nographics -projectPath $proj `
  -executeMethod SbpLabEx03_CompressionMatrix.Run -logFile -
```

### 1.3 原始 0-4 号流程（入门先跑这个）

| 菜单 | 作用 |
|---|---|
| `SBP Lab/0 - Setup Test Assets` | 建 `Assets/Bundles/` 下的材质和 prefab，并打 `shared` / `props_a` / `props_b` 标签 |
| `SBP Lab/1 - Compatibility Build` | 用 `CompatibilityBuildPipeline` 平替内置管线 |
| `SBP Lab/2 - ContentPipeline Build` | 自组管线构建，打印 hash/crc/大小/依赖，写 `Artifacts/SBP/last-report.json` |
| `SBP Lab/3 - Touch CubeA` | 每次把 CubeA 放大 0.1（可重复做增量实验） |
| `SBP Lab/4 - Rebuild & Diff` | 重新构建并与上次报告 diff |

---

## 2. 心智模型

### 2.1 一次构建发生了什么

```
调用方
  │  new BundleBuildParameters(target, group, outputPath)   // 参数
  │  new BundleBuildContent(AssetBundleBuild[])             // 内容 + 布局
  ▼
ContentPipeline.BuildAssetBundles(parameters, content, out results, taskList?, contextObjects?)
  │ 1. 参数/平台校验
  │ 2. new BuildContext(contextObjects)      ← 上下文就是一袋 IContextObject
  │ 3. 把 parameters / content / results / cache / callbacks 等注册进上下文
  │ 4. BuildTasksRunner.Validate(taskList, context)
  │ 5. BuildTasksRunner.Run(taskList, context)
  ▼
  对每个 IBuildTask：
     ContextInjector.Inject(context, task)    // 私有字段 [InjectContext] 从上下文取
     task.Run()
     ContextInjector.Extract(context, task)   // InOut / Out 写回上下文
  ▼
  阶段：Setup → Scripts → Dependency → Packing → Writing → （可插自定义）
  ▼
  产物写入 OutputFolder；trace 写入 <OutputFolder>/buildlogtep.json
```

### 2.2 核心接口对照

| 角色 | 接口 | 默认实现 | 你在哪里碰它 |
|---|---|---|---|
| 参数 | `IBuildParameters` / `IBundleBuildParameters` | `BuildParameters` / `BundleBuildParameters` | 继承后重写 `GetCompressionForIdentifier` |
| 内容 | `IBuildContent` / `IBundleBuildContent` | `BuildContent` / `BundleBuildContent` | 显式布局、打印 `BundleLayout` |
| 结果 | `IBuildResults` / `IBundleBuildResults` | `BuildResults` / `BundleBuildResults` | 取 `BundleInfos` 里的 hash/crc/大小 |
| 任务 | `IBuildTask` | `Editor/Tasks/*.cs` | 自定义任务、插任务链 |
| 上下文 | `IBuildContext` / `IContextObject` | `BuildContext` | 自定义上下文传数据 |
| 缓存 | `IBuildCache` | `BuildCache` | `PurgeCache`、增量实验 |
| 日志 | `IBuildLogger` | `BuildLog` | `buildlogtep.json` |

### 2.3 默认任务链（AssetBundleCompatible 预设）

```
Setup       SwitchToBuildPlatform → RebuildSpriteAtlasCache
Scripts     BuildPlayerScripts → PostScriptsCallback
Dependency  CalculateSceneDependencyData → CalculateCustomDependencyData
            → CalculateAssetDependencyData → StripUnusedSpriteSources → PostDependencyCallback
Packing     GenerateBundlePacking → GenerateBundleCommands → GenerateSubAssetPathMaps
            → GenerateBundleMaps → PostPackingCallback
Writing     WriteSerializedFiles → ArchiveAndCompressBundles → AppendBundleHash
            → GenerateLinkXml → PostWritingCallback
Manifest    （官方 TODO：SBP 核心不生成 .manifest，见附录 C.10）
```

> `DefaultBuildTasks` 还有另外两个预设：`AssetBundleBuiltInShaderExtraction`（额外抽内置 shader）、
> `AssetBundleShaderAndScriptExtraction`（再抽 MonoScript）。
> 还有 `ContentFileCompatible()`（2022.2+，用 `ClusterBuildLayout` 替代 `GenerateBundle*` 系列）。
>
> 每个任务具体在读什么、写什么、有哪些扩展点，见 **[附录 C. 任务链逐任务详解（源码级）](#附录-c-任务链逐任务详解源码级)**。

---

## 3. 案例总览

| # | 文件 | 难度 | 一句话 | 关键 API |
|---|---|---|---|---|
| 01 | `SbpLabEx01_CompatibilityMinimal.cs` | ★ | 一行换管线 | `CompatibilityBuildPipeline.BuildAssetBundles` |
| 02 | `SbpLabEx02_ExplicitLayout.cs` | ★★ | 代码定义 bundle 布局 | `BundleBuildContent(AssetBundleBuild[])` |
| 03 | `SbpLabEx03_CompressionMatrix.cs` | ★★ | 三种压缩对比 | `BundleCompression` |
| 04 | `SbpLabEx04_PerBundleCompression.cs` | ★★★ | 每个 bundle 不同压缩 | `override GetCompressionForIdentifier` |
| 05 | `SbpLabEx05_CustomBuildTask.cs` | ★★★ | 第一个自定义任务 | `IBuildTask` + `[InjectContext]` |
| 06 | `SbpLabEx06_CustomTaskChain.cs` | ★★★★ | 在指定阶段插任务 + 自定义上下文 | `DefaultBuildTasks.Create` + `tasks.Insert` |
| 07 | `SbpLabEx07_DeliveryPostProcess.cs` | ★★★★ | 产出交付目录 + JSON 清单 | `IBundleBuildResults.BundleInfos` |
| 08 | `SbpLabEx08_CacheExperiment.cs` | ★★★ | 量化增量缓存收益（重内容，约 179 MB） | `UseCache` + `BuildCache.PurgeCache` |
| 09 | `SbpLabEx09_CustomLogger.cs` | ★★★★ | 接管构建埋点做性能分析 | 自定义 `IBuildLogger` |
| 10 | `SbpLabEx10_ContentFilePipeline.cs` | ★★★★★ | Content File / Cluster 管线 | `ContentFileCompatible` + `ContentFileIdentifiers` + `ClusterOutput` |
| 11 | `SbpLabEx11_EncryptedDelivery.cs` | ★★★★★ | 加密交付 + 端到端加载验证 | 后处理任务 + `AssetBundle.LoadFromFile` |
| 12 | `SbpLabEx12_PackingStrategy.cs` | ★★★★ | 在 Packing 前改写 bundle 布局 | `IBundleBuildContent.BundleLayout`（InOut） |
| 13 | `SbpLabEx13_BuildMatrix.cs` | ★★★★ | 多配置矩阵 + CI 报告聚合 | 组合 `BundleBuildParameters` / 任务链 |
| 14 | `SbpLabEx14_LegacyVsSbpCompare.cs` | ★★★ | 同一内容用旧管线/SBP 各构建一次，对比产物 | `BuildPipeline.BuildAssetBundles` vs `CompatibilityBuildPipeline` |
| 15 | `SbpLabEx15_PresetComparison.cs` | ★★★ | Preset 对比：内置 shader / MonoScript 抽取的影响 | `DefaultBuildTasks.Preset.*` |
| 16 | `SbpLabEx16_HeavyCompression.cs` | ★★★ | 重内容（约 179 MB 源资源）压缩对比（冷+热） | `BuildCompression` + `HeavyLayout` |
| 17 | `SbpLabEx17_Determinism.cs` | ★★★★ | 确定性构建 + 缓存一致性验证 | `IDeterministicIdentifiers` + `BuildCache` |
| 18 | `SbpLabEx18_StageProbe.cs` | ★★★★★ | 全流程阶段探针：19 个任务逐阶段抓取 BuildContext 数据 | `IBuildTask` 探针 + `[InjectContext]`（配合任务链子 Wiki） |

建议顺序：**01 → 02 → 03 → 05 → 04 → 06 → 07 → 08 → 09 → 12 → 11 → 10 → 13 → 15 → 16 → 17 → 18**。
（09/12 是“扩展点”练习，11/10 是“端到端/新产物形态”，13 是工程化收尾，15/16 是体积与压缩专题，17/18 是确定性与内部阶段数据。）

---

## 4. 案例详解

### 01 Compatibility 最小平替

- **文件**：`Assets/Editor/SbpLab/SbpLabEx01_CompatibilityMinimal.cs`
- **做什么**：把 `BuildPipeline.BuildAssetBundles` 换成 `CompatibilityBuildPipeline.BuildAssetBundles`，
  其它参数一字不改；再用返回的 `AssetBundleManifest` 打印 bundle 和依赖。
- **预期输出**（数字随机器/平台变化，看趋势）：
  ```
  [Ex01] 构建完成，耗时 866 ms，输出 Artifacts/SBP/ex01-compat-Android
  [Ex01] bundle=props_a    依赖=shared
  [Ex01] bundle=props_b    依赖=shared
  [Ex01] bundle=shared     依赖=(无)
  ```
- **对应源码**：`Editor/CompatibilityBuildPipeline.cs`（看它如何把 `BuildAssetBundleOptions`
  翻译成 `BundleBuildParameters`）
- **思考题**：
  1. 这个方法的返回值为什么还能是 `AssetBundleManifest`？
  2. 只做“平替”能不能拿到 SBP 的增量缓存？

### 02 ContentPipeline 显式布局

- **文件**：`Assets/Editor/SbpLab/SbpLabEx02_ExplicitLayout.cs`
- **做什么**：不依赖 Project 窗口里的 AssetBundle 标签，用 `AssetBundleBuild[]` 在代码里
  定义 `props_a ← CubeA+CubeB`、`props_b ← CubeC`、`shared ← Mat`。
- **预期输出**：
  ```
  [Ex02] 显式布局明细：
  [Ex02]   bundle=props_a    资源数=2
  [Ex02]   bundle=props_b    资源数=1
  [Ex02]   bundle=shared     资源数=1
  [Ex02]   参与构建的资源总数=4，address 映射数=4
  ```
- **对应源码**：`Editor/Shared/BuildContent.cs`（`BundleBuildContent` 构造函数如何把
  `AssetBundleBuild` 拆成 `BundleLayout` + `Addresses`）
- **思考题**：
  1. 把 Project 里的标签删掉，这个示例还能构建吗？
  2. `BundleLayout` 存 GUID 而不是路径，好处是什么？

### 03 压缩方式对比矩阵

- **文件**：`Assets/Editor/SbpLab/SbpLabEx03_CompressionMatrix.cs`
- **做什么**：同一份内容分别用 `LZMA` / `LZ4` / `Uncompressed` 构建到三个目录，
  打印每个 bundle 的体积和总耗时。
- **预期输出**（2026-11-07 实测）：
  ```
  [Ex03]   props_a    LZMA=2235  LZ4=3323  Uncompressed=9168
  [Ex03]   props_b    LZMA=2129  LZ4=3164  Uncompressed=8512
  [Ex03]   shared     LZMA=103098  LZ4=137005  Uncompressed=241888
  [Ex03]   总大小 LZMA            104.9 KB   构建耗时 628 ms
  [Ex03]   总大小 LZ4             140.1 KB   构建耗时 625 ms
  [Ex03]   总大小 Uncompressed    253.5 KB   构建耗时 630 ms
  ```
- **对应源码**：`Editor/Tasks/ArchiveAndCompressBundles.cs`
- **思考题**：
  1. 为什么 LZMA 构建更慢、但体积更小？
  2. 移动端为什么通常选 LZ4？

### 04 每个 bundle 独立压缩

- **文件**：`Assets/Editor/SbpLab/SbpLabEx04_PerBundleCompression.cs`
- **做什么**：继承 `BundleBuildParameters`，重写 `GetCompressionForIdentifier`，
  让 `props_*` 用 LZ4、`shared` 用 LZMA。
- **关键点**：`ArchiveAndCompressBundles` 传入的 `identifier` 是 **bundle 名**。
- **预期输出**（实测）：
  ```
  [Ex04]   props_a    压缩=LZ4           大小=   3.2 KB
  [Ex04]   props_b    压缩=LZ4           大小=   3.1 KB
  [Ex04]   shared     压缩=LZMA          大小= 100.7 KB
  ```
- **对应源码**：`Editor/Tasks/ArchiveAndCompressBundles.cs`（第 ~258 行
  `item.Compression = input.GetCompressionForIdentifier(bundleName)`）
- **思考题**：
  1. 压缩方式变了，bundle hash 会变吗？
  2. 这个 override 点还能用来做什么（例如按平台/渠道差异化）？

### 05 自定义 BuildTask

- **文件**：`Assets/Editor/SbpLab/SbpLabEx05_CustomBuildTask.cs`
- **做什么**：实现 `LabSummaryTask : IBuildTask`，通过 `[InjectContext(ContextUsage.In)]`
  拿到 `IBundleBuildContent` 和 `IBundleBuildResults`，在链尾打印布局和结果。
- **三个必须记住的点**：
  1. 注入字段必须是 **private**（`ContextInjector` 只扫 `NonPublic` 字段）；
  2. 任务在链尾时 `IBundleBuildResults` 才是完整的；
  3. `Version` 参与缓存 key，改逻辑要 +1。
- **预期输出**：
  ```
  [Ex05] 当前任务链共 20 个任务，最后一个是 LabSummaryTask
  [Ex05] LabSummaryTask 开始执行（任务链末尾）
  [Ex05]   layout: props_a    资源数=2
  [Ex05]   result: props_a    hash=... deps=1
  ```
- **对应源码**：`Editor/Shared/BuildTasksRunner.cs`、`Editor/Injector/ContextInjector.cs`
- **思考题**：
  1. 把任务插到 `BuildPlayerScripts` 之前会怎样？
  2. 两个自定义任务之间传数据用 `In` 还是 `Out`？

### 06 任务链插入 + 自定义上下文

- **文件**：`Assets/Editor/SbpLab/SbpLabEx06_CustomTaskChain.cs`
- **做什么**：
  1. 取默认任务链；
  2. 找到 `PostDependencyCallback`，在它后面插入 `DependencyReportTask`；
  3. 通过 `ContentPipeline.BuildAssetBundles(..., tasks, stats)` 传入自定义
     `LabStatsContext`，任务把统计写进去，调用方再读回来。
- **易错点**：自定义上下文必须通过 **接口** 注入（`ILabStats`），
  直接用 class 类型会在任务结束后抛异常（见 [常见坑 #11](#7-常见坑实测)）。
- **预期输出**（实测）：
  ```
  [Ex06] 任务链共 20 个任务，DependencyReportTask 插在下标 9
  [Ex06] 依赖计算完成：资源=4，场景=0
  [Ex06] 已把统计结果写入自定义上下文 LabStatsContext
  [Ex06] 调用方读回上下文：资源=4，场景=0，bundle=3
  ```
- **对应源码**：`Editor/Shared/DefaultBuildTasks.cs`、`Editor/Tasks/CalculateAssetDependencyData.cs`
- **思考题**：
  1. 插在 `PostDependencyCallback` 之前还能拿到完整 `IDependencyData` 吗？
  2. 为什么要实现 `IContextObject` 这个空接口？

### 07 后处理：交付目录与清单

- **文件**：`Assets/Editor/SbpLab/SbpLabEx07_DeliveryPostProcess.cs`
- **做什么**：自定义 `DeliveryTask` 追加到链尾，把 bundle 复制到
  `Artifacts/SBP/ex07-delivery/<时间戳>/`，并生成 `delivery-manifest.json`
  （name / hash / crc / size / dependencies）。
- **重要差异**：SBP 的 `ContentPipeline` **不会**自动生成 `.manifest` 文件；
  清单信息在 `IBundleBuildResults.BundleInfos` 里，要自己导出。
- **预期输出**：
  ```
  [Ex07] 已交付 props_a    -> Artifacts/SBP/ex07-delivery/20261107-121500/props_a
  [Ex07] 清单已生成：.../delivery-manifest.json
  ```
- **对应源码**：`Editor/Shared/BuildResults.cs`、`Runtime/Shared/BundleDetails.cs`
- **思考题**：
  1. 如果要做差异更新，除了 hash 还需要什么？
  2. 为什么后处理任务要放在 `AppendBundleHash` 之后？

### 08 增量缓存实验

- **文件**：`Assets/Editor/SbpLab/SbpLabEx08_CacheExperiment.cs`
- **做什么**：清空 `Library/BuildCache` → 构建（冷）→ 再构建（热）→ 对比耗时。
- **使用内容**：`Assets/Bundles/Heavy`（约 179 MB 源资源）：
  - 音频（来自 ARPGTutorial，26 MB）
  - 动画 FBX（来自 ARPGTutorial，153 MB，拆成两个 bundle）
  - 8 张合成 2048² 未压缩贴图（约 96 MB packed，由 `SBP Lab/0b` 生成）
  - 为了放大差异，示例故意用 **LZMA** 压缩（生产通常用 LZ4）。
- **预期输出**（2026-11-08 实测）：
  ```
  [Ex08] 缓存目录（清空前）：129.11 MB
  [Ex08] 清空 SBP 缓存...
  [Ex08] 冷构建耗时 6024 ms，缓存体积 123.16 MB，产物总大小 8.52 MB
  [Ex08] 热构建耗时 1428 ms
  [Ex08] 对比：冷=6024 ms，热=1428 ms，加速比=4.2x
  ```
- **注意**：`PurgeCache(false)` 会清掉整个工程的 SBP 缓存（本地缓存，删了不坏资源），
  跑完其它构建第一次会变慢；重内容资源较重，首次导入/首次生成贴图会额外花时间（不计入计时）。
- **对应源码**：`Editor/Utilities/BuildCache.cs`、`Editor/Utilities/TaskCachingUtility.cs`
- **思考题**：
  1. 缓存 key 由哪些部分组成？
  2. 改自定义任务的 `Version` 后，热构建还会快吗？

### 09 自定义 BuildLogger（性能剖析）

- **文件**：`Assets/Editor/SbpLab/SbpLabEx09_CustomLogger.cs`
- **做什么**：实现 `IBuildLogger` 的三个方法，把自定义 logger 作为 contextObject 传进去，
  构建后输出“单步耗时 Top 10”和“按名称聚合 Top 10”。
- **关键机制**：`ContentPipeline` 先查 `BuildContext` 里有没有 `IBuildLogger`，有就用你的，
  没有才 new 默认 `BuildLog()`。所以这是**官方预留**的埋点接管点。
- **实现要点**：
  1. `BeginBuildStep` / `EndBuildStep` 必须用 `[ThreadStatic]` 栈配对（缓存线程会并发调用）；
  2. `AddEntry` 没有配对的 End，不要在里面做计时；
  3. 结果列表加锁，避免多线程写坏 `List`。
- **预期输出**（实测）：
  ```
  [Ex09] 构建完成，共采集 76 个 step
  [Ex09]      593.7 ms  BuildPlayerScripts  (thread=1)
  [Ex09]        9.5 ms  WriteSerializedFiles  (thread=1)
  [Ex09] ===== 按名称聚合 Top 10 =====
  [Ex09]      593.7 ms  BuildPlayerScripts
  [Ex09]       20.0 ms  LoadCachedData
  ```
- **对应源码**：`Editor/ContentPipeline.cs`（logger 查找逻辑）、`Editor/Interfaces/IBuildLog.cs`
- **思考题**：
  1. 为什么要区分“单步耗时”和“同名聚合”？
  2. 如果要把慢步骤上报监控，`buildlogtep.json` 和自定义 logger 各适合什么场景？

### 10 Content File 管线（Addressables CCD 同款）

- **文件**：`Assets/Editor/SbpLab/SbpLabEx10_ContentFilePipeline.cs`
- **难点**：这是 SBP 里和 AssetBundle 管线**产物形态完全不同**的一条管线。
- **做什么**：用 `DefaultBuildTasks.ContentFileCompatible()` + `ContentFileIdentifiers` +
  `ClusterOutput` 构建，产物是按“资源共现关系”聚类出来的 content archive。
- **关键点**：
  1. `ClusterBuildLayout` 取代了 `GenerateBundlePacking/Commands/Maps`；
  2. 必须传 `IDeterministicIdentifiers`（用 `ContentFileIdentifiers`）和 `IClusterOutput`；
  3. 要求 Unity 2022.2+；
  4. 产物文件名是 cluster hash，不是 bundle 名——运行时靠 Addressables catalog 映射。
- **预期输出**（实测）：
  ```
  [Ex10] 任务链共 15 个任务（最后一个 PostWritingCallback）
  [Ex10] content 构建完成：产物 5 个，Object->Cluster 映射 18 条，Object->LocalID 映射 18 条
  [Ex10]   archive=cae7b92f6c8170ebff679d4d4a65a4be ... size=2.3 KB
  [Ex10]   archive=6a0110f9445406e5abb2d09864129da8 ... size=131.7 KB
  ```
- **对应源码**：`Editor/Tasks/ClusterBuildLayout.cs`、`Editor/Utilities/ContentFileIdentifiers.cs`
- **思考题**：
  1. 为什么 content file 管线需要 `ContentFileIdentifiers` 而不是默认 identifiers？
  2. 这条管线和“更新构建只发布增量”之间是什么关系？

### 11 加密交付 + 运行时加载验证

- **文件**：`Assets/Editor/SbpLab/SbpLabEx11_EncryptedDelivery.cs`
- **做什么**：构建后处理任务把产物 XOR 加密进交付目录 → 解密到 Temp → 按依赖拓扑顺序
  `AssetBundle.LoadFromFile` → 实例化 prefab → 检查材质颜色 → 清理。
- **三个容易翻车的点**（都已在代码里处理）：
  1. **平台限制**：Bundle 只能被构建目标平台的运行时加载。所以本示例显式按 Editor 平台
     （Windows64）构建，否则在 Editor 里加载 Android bundle 会报平台不匹配；
  2. **资源内部名可能是完整路径**：`LoadAsset<GameObject>("CubeA")` 可能返回 null，
     实测 `GetAllAssetNames()` 返回的是 `Assets/Bundles/Props/CubeA.prefab`；
  3. **依赖加载顺序**：必须先加载 `shared` 再加载 `props_a`，否则材质无法解析。
- **隐藏副作用（重要）**：SBP 的 `SwitchToBuildPlatform` 任务会**真的切换 Editor 的活动平台**，
  而且构建结束后**不会自动切回**。本示例在 `finally` 里恢复原平台，否则你的工程会被留在
  StandaloneWindows64，影响后续 Android 出包。
  如果中途异常没恢复，执行：
  ```
  Unity.exe -batchmode -quit -executeMethod SbpLabExamplesRunner.RestoreAndroidTarget -projectPath <工程>
  ```
- **预期输出**（实测）：
  ```
  [Ex11] 加密完成：3 个文件 -> Artifacts/SBP/ex11-encrypted-delivery
  [Ex11] 加载顺序：shared -> props_b -> props_a
  [Ex11] props_a 内的资源名：Assets/Bundles/Props/CubeA.prefab, ...
  [Ex11] 实例化 CubeA 成功，材质=Mat，颜色=(0.20, 0.70, 1.00)
  [Ex11] 端到端验证通过 ✅
  ```
- **思考题**：
  1. 加密后 `hash` 还能用来校验文件吗？应该改成校验什么？
  2. 为什么后处理加密不影响 SBP 的增量缓存？边界在哪里？

### 12 自定义打包策略（合并小 bundle）

- **文件**：`Assets/Editor/SbpLab/SbpLabEx12_PackingStrategy.cs`
- **做什么**：实现 `MergeBundlesTask`，插在 `PostDependencyCallback` 之后（Packing 之前），
  直接改写 `IBundleBuildContent.BundleLayout`，把 `props_a` + `props_b` 合并成 `props_all`。
- **关键点**：
  1. 布局就是普通的 `Dictionary<string, List<GUID>>`，可以增删；
  2. 必须是 `InOut` 注入，改的才是上下文里同一个对象，后续任务立刻可见；
  3. 位置错了就无效：放到 Packing 之后就来不及了。
- **预期输出**（实测）：
  ```
  [Ex12] 合并前：props_a(2), props_b(1), shared(1)
  [Ex12] 合并后：props_all(3), shared(1)
  [Ex12] 构建结果：2 个 bundle
  [Ex12]   props_all  hash=5774eea2... size=   3.4 KB deps=1
  [Ex12]   shared     hash=1287c555... size= 133.8 KB deps=0
  ```
- **思考题**：
  1. 合并的代价是什么？如何决定“合并阈值”？
  2. 如果要按资源字节数自动合并，应该在哪一步统计大小？

### 13 多配置构建矩阵 + 报告聚合

- **文件**：`Assets/Editor/SbpLab/SbpLabEx13_BuildMatrix.cs`
- **做什么**：一次跑三套配置（3-bundle+LZ4 / merged+LZ4 / 3-bundle+LZMA），
  聚合 bundle 数、总大小、耗时，写 `Artifacts/SBP/ex13-summary.json`。
- **关键点**：每次构建都必须 new 一整套 `BundleBuildParameters` / `BundleBuildContent` /
  任务链——它们是“一次构建”的状态，跨构建复用会导致布局被上一轮污染。
- **预期输出**（实测）：
  ```
  [Ex13]   A-per-bundle-LZ4     bundles=3 总大小= 140.1 KB 耗时=614 ms
  [Ex13]   B-merged-LZ4         bundles=2 总大小= 137.2 KB 耗时=624 ms
  [Ex13]   C-per-bundle-LZMA    bundles=3 总大小= 104.9 KB 耗时=660 ms
  [Ex13] 汇总报告 -> Artifacts/SBP/ex13-summary.json
  ```
- **思考题**：
  1. 配置笛卡尔积如何治理？
  2. CI 里除了体积/耗时，还应记录哪些指标？

### 14 旧管线 vs SBP 产物对比

- **文件**：`Assets/Editor/SbpLab/SbpLabEx14_LegacyVsSbpCompare.cs`
- **做什么**：同一份内容、同一套选项，分别用 `BuildPipeline.BuildAssetBundles`（旧）
  和 `CompatibilityBuildPipeline.BuildAssetBundles`（SBP）构建，逐文件对比。
- **实测结果（Unity 2022.3 / StandaloneWindows64 / LZ4）**：

  | 项 | 旧管线 | SBP |
  |---|---|---|
  | props_a 大小 | 3335 B | 3323 B |
  | props_b 大小 | 3172 B | 3166 B |
  | shared 大小 | 41771 B | 46399 B |
  | manifest bundle（文件名=目录名） | ✅ 1632 B | ❌ 没有 |
  | 每 bundle `.manifest` YAML | ✅ props_a.manifest 等 | ❌ 没有 |
  | 顶层 `.manifest` | ✅ 296 B | ✅ 500 B（内容格式不同） |
  | 构建 trace | ❌ | ✅ `buildlogtep.json` |
  | bundle 内资源名 | `assets/bundles/...`（小写） | `Assets/Bundles/...`（保留大小写） |
  | 依赖关系 | props_a→shared、props_b→shared | 相同 |

- **结论**：
  1. **逻辑结构一致**（bundle 名、依赖、资源集合相同）；
  2. **字节不一致**（hash、大小都不同），原因来自打包布局/序列化细节，不是资源内容不同；
  3. **清单产物差异最大**：旧管线的 manifest bundle + 每 bundle `.manifest` 在 SBP 里没有，
     SBP 只给内存里的 `CompatibilityAssetBundleManifest` + 一个文本 `.manifest`；
  4. **资源内部名大小写不同**，这正是官方文档说的“加载路径语义变化”：SBP 严格按传入的
     address 加载，旧管线会做小写化/短名匹配。
- **踩坑记录**：旧管线返回的 `AssetBundleManifest` 是从刚构建的 manifest bundle 加载的
  `UnityEngine.Object`。如果在同一 session 里紧接着跑另一次构建（触发 `UnloadUnusedAssets`），
  这个对象会变成“假 null”（`== null` 为 true）。所以本示例在构建返回后**立刻快照**成托管数据。
- **对应源码**：`Editor/CompatibilityBuildPipeline.cs`（翻译层）、`Editor/ContentPipeline.cs`（默认 preset）
- **思考题**：
  1. 如果 bundle 字节不同但结构一致，迁移风险主要在哪？（清单/加载语义/回调/选项）
  2. 为什么 SBP 不产 manifest bundle？运行时依赖信息应该怎么落地？

---

## 5. 源码阅读路线

包源码在：`Library/PackageCache/com.unity.scriptablebuildpipeline@1.23.4/`

按顺序读，每个文件只回答一个问题：

| 顺序 | 文件 | 只回答一个问题 |
|---|---|---|
| 1 | `Editor/ContentPipeline.cs` | `taskList == null` 时用哪个 preset？往 context 里注册了哪些对象？ |
| 2 | `Editor/Shared/DefaultBuildTasks.cs` | 三个 preset 的任务顺序差在哪？`ContentFileCompatible` 为什么换掉 `GenerateBundle*`？ |
| 3 | `Editor/Shared/BuildTasksRunner.cs` | `Validate` 和 `Run` 分别做什么？任务失败如何中断？ |
| 4 | `Editor/Injector/ContextInjector.cs` | `Inject`/`Extract` 如何用反射处理 `[InjectContext]`？为什么只扫私有字段？ |
| 5 | `Editor/Tasks/CalculateAssetDependencyData.cs` | 依赖数据（`IDependencyData`）是怎么算出来的？ |
| 6 | `Editor/Tasks/GenerateBundlePacking.cs` → `GenerateBundleCommands.cs` → `WriteSerializedFiles.cs` → `ArchiveAndCompressBundles.cs` | 一个资源从“被收集”到“变成 bundle 文件”经过哪几次数据形态变化？ |
| 7 | `Editor/Utilities/BuildCache.cs` + `TaskCachingUtility.cs` | 缓存 key 的组成？`Version` 为什么能失效缓存？ |
| 8 | `Editor/Tasks/ClusterBuildLayout.cs` | cluster 是怎么算出来的？content file 管线为什么需要它？ |
| 9 | `Editor/Utilities/ContentFileIdentifiers.cs` | 为什么要把 MonoScript 的 localID 最高位置 1？ |
| 10 | `Tests/Editor/ScriptableBuildPipelineTests.cs` | 官方测试里是怎么构造最小构建的？ |

> 读源码时对照 `buildlogtep.json`（用 [Perfetto](https://ui.perfetto.dev) 打开），
> 把文件里的任务名和 trace 里的耗时段一一对上，理解会快很多。

---

## 6. 观测与调试

| 观测对象 | 位置 | 用途 |
|---|---|---|
| 构建报告 | `Artifacts/SBP/last-report.json` | 每个 bundle 的 hash/crc/size/deps，可 diff |
| 构建 trace | `Artifacts/SBP/<目录>/buildlogtep.json` | Chrome Trace 格式，看每个任务耗时 |
| 构建缓存 | `Library/BuildCache` | 缓存体积；`PurgeCache(false)` 可清 |
| 构建日志 | `_e2e/logs/*.log` | batchmode 的完整日志 |
| 交付清单 | `Artifacts/SBP/ex07-delivery/<时间戳>/delivery-manifest.json` | 模拟线上清单 |

---

## 7. 常见坑（实测）

1. **`BuildCompression` 二义性（CS0104）**
   同时 `using UnityEditor.Build.Content;` 时，`BuildCompression` 有两个同名类型。
   写全 `UnityEngine.BuildCompression.LZ4`。

2. **`BundleDetails.Hash` 是 `Hash128` 不是 `string`**
   要打印/序列化时 `.ToString()`。

3. **`BundleDetails.FileName` 已包含输出目录**
   1.23 里它是形如 `Artifacts/SBP/sbp-Android/props_a` 的相对路径，
   直接 `File.Exists` 即可；再 `Path.Combine(outPath, fileName)` 会拼出错误路径。

4. **SBP 不生成 `.manifest`**
   内置管线会写 `xxx.manifest`；SBP 的清单在 `IBundleBuildResults` 里，需自己导出（见案例 07）。

5. **标签 vs 显式布局**
   `ContentBuildInterface.GenerateAssetBundleBuilds()` 读的是 AssetBundle 标签；
   显式布局则完全忽略标签。两者混用时，传进 `BundleBuildContent` 的显式布局优先。

6. **目标平台模块必须安装**
   否则会用 Editor 版 assembly 构建，产物内容可能是错的（官方 remark）。

7. **自定义任务的注入字段必须 private**
   `ContextInjector` 用 `BindingFlags.NonPublic` 扫字段，public 字段不会被注入。

8. **任务 `Version` 与缓存**
   修改任务逻辑后不 +1，可能复用旧缓存，表现为“代码改了但结果没变”。

9. **`BuildCache.PurgeCache(false)` 影响全局**
   清的是整个工程的 `Library/BuildCache`，其它构建第一次会变慢；不要放进常规 CI。

10. **batchmode 退出码**
    脚本异常不保证进程返回非 0；示例统一用 `EditorApplication.Exit(1)` 兜底。

11. **自定义上下文必须通过“接口”注入（实测掉坑）**
    `[InjectContext(ContextUsage.InOut)] LabStatsContext m_Stats;` 会在任务结束后
    抛 `Passed in type 'LabStatsContext' is not an interface`。
    原因：`ContextInjector.Extract` 写回上下文时调用
    `BuildContext.SetContextObject(Type, object)`，该方法要求 Type 是接口。
    正确做法：定义 `interface ILabStats : IContextObject`，注入 `ILabStats`（见案例 06）。
    `In` 模式的 class 类型注入不会触发写回，所以能跑，但不推荐依赖这种“恰好”。

12. **`BuildCompression.ToString()` 只返回类型名**
    直接打印会得到 `UnityEngine.BuildCompression`，看不出是 LZMA 还是 LZ4；
    需要自己按 `compression.Equals(BuildCompression.LZMA)` 映射成可读名称（见案例 04）。

13. **自定义 `IBuildLogger` 必须“提前”塞进上下文**
    `ContentPipeline` 只在 BuildContext 里找不到 `IBuildLogger` 时才 new 默认 logger。
    把 logger 作为 `contextObjects` 传进去即可接管；但要注意 Begin/End 可能来自不同线程，
    必须用 `[ThreadStatic]` 栈配对，否则轻则耗时错乱，重则栈空异常。

14. **Content File 管线不是 AssetBundle 管线的“换皮”**
    `ContentFileCompatible` 产出的是按 cluster hash 命名的 content archive，
    必须传 `ContentFileIdentifiers` + `ClusterOutput`，且要求 Unity 2022.2+。
    它服务于 Addressables 的差异更新，不能像 bundle 那样直接 `LoadFromFile` 用。

15. **Bundle 只能被“构建目标平台”的运行时加载**
    在 Editor 里验证时，要么按 Editor 平台构建（案例 11 的做法），
    要么只做产物检查不要加载；用 Android 目标构建的 bundle 在 Windows Editor 里加载会失败。

16. **`LoadAsset("名字")` 可能找不到，内部名可能是完整路径**
    实测 SBP 产物里 `GetAllAssetNames()` 返回 `Assets/Bundles/Props/CubeA.prefab`。
    稳妥写法：先按短名加载，失败再遍历 `GetAllAssetNames()` 兜底（案例 11）。

17. **布局改写任务的位置决定成败**
    想改 bundle 布局，必须插在 `PostDependencyCallback` 之后、`GenerateBundlePacking` 之前；
    放到 Packing 之后只会改到“已经不用的数据”，构建结果不变。

18. **每次构建都要全新的 parameters / content / tasks**
    这些对象是一次构建的状态（布局会被任务改写、缓存句柄会被释放）。
    多配置循环构建时复用一个 `BundleBuildContent`，会出现“上一轮布局污染下一轮”的诡异现象（案例 13）。

19. **`SwitchToBuildPlatform` 会切换 Editor 活动平台且不切回**
    SBP 每个构建都会先执行这个任务，把 Editor 的 Active Build Target 切到 `parameters.Target`。
    跨平台构建（例如在 Android 工程里构建 Standalone 做加载验证）之后，
    **工程会停留在新平台**，影响后续出包。务必在构建后恢复（案例 11 的 `finally`），
    或提供 `SbpLabExamplesRunner.RestoreAndroidTarget` 这类修复入口。

20. **旧管线的 `AssetBundleManifest` 会变成“假 null”**
    旧 `BuildPipeline.BuildAssetBundles` 返回的 manifest 是从刚构建的 manifest bundle 里
    加载的 `UnityEngine.Object`。同一 session 里再跑一次构建（触发 `UnloadUnusedAssets`）后，
    这个对象 `== null` 会变成 true。要么构建完立刻用完，要么先快照成托管数据（案例 14）。

21. **bundle 内部资源名的“大小写/短名”语义不同**
    实测旧管线的内部名是小写路径（`assets/bundles/props/cubea.prefab`），
    SBP 保留原始大小写（`Assets/Bundles/Props/CubeA.prefab`）。
    从旧管线迁到 SBP 时，所有按“小写/短文件名”`LoadAsset` 的代码都要检查；
    SBP 的规则是“传什么就按什么加载”。

---

## 8. 从 Lab 到生产

学完这 8 个案例，下一步通常是三条路线之一：

| 路线 | 学什么 | 入口 |
|---|---|---|
| 用官方高层方案 | Addressables 的 `BuildScriptPackedMode`、`BuildLayoutReport`、内容更新 | `com.unity.addressables` 源码 + 文档 |
| 自研资源系统 | YooAsset 的 `BundleBuilder/BuildPipeline`（内置/SBP/自定义三种管线可切换） | `C:/UGit/YooAsset` + [中文文档](https://www.yooasset.com/docs/guide-editor/AssetBundleBuilder) |
| 热更/脚本裁剪 | `PlayerBuildInterface.CompilePlayerScripts`、AOT 元数据 | HybridCLR |

生产环境还要补：

- **CI**：`-batchmode -executeMethod` + 构建报告 + 产物归档；
- **差异更新**：hash 清单 + 依赖图 + 本地 vs 远端对比；
- **加密**：压缩后按 bundle 加密，运行时对应解密（注意 hash/缓存语义）；
- **缓存共享**：SBP 的 Cache Server 已不推荐，资源导入用 Unity Accelerator。

---

## 9. 清理与恢复

想在工程里彻底移除这个 lab：

```powershell
# 删除教学脚本与测试资源
Remove-Item 'Assets\Editor\SbpLab' -Recurse -Force
Remove-Item 'Assets\Editor\SbpLabBuild.cs' -Force
Remove-Item 'Assets\Bundles' -Recurse -Force
# 删除产物
Remove-Item 'Artifacts\SBP' -Recurse -Force
```

只删产物、保留脚本也可以：直接删 `Artifacts/SBP`，示例会重新生成。

---

## 10. 子 Wiki：任务链 / 确定性构建 / 构建缓存

主 Wiki 的 C.13 是“结论 + 实测速览”；下面的子 Wiki 是“源码级拆解 + 真实阶段数据”，适合按主题深读：

| 子 Wiki | 内容 | 适合排查的问题 |
|---|---|---|
| [SBP-Pipeline/README.md](../wiki/task-chain/README.md) | **19 个任务逐阶段**：作用/输入输出/数据结构、Ex18 全流程探针的 20 个阶段真实数据、上下文对象表、与“魔改 + 自定义 DB”管线的对照清单 | 看不懂某个 Task 在干什么、换写入方式该改哪一层、魔改任务链怎么对齐 |
| [SBP-Pipeline/Dependency-Data-Structures.md](../wiki/task-chain/Dependency-Data-Structures.md) | **依赖数据结构详解**：`ObjectIdentifier` 的 GUID/fileID/filePath、FileType 四值、included vs referenced、prefabA 完整案例、调试反查与 FAQ | 看 `AssetInfo` 时 GUID/fileID/filePath 分不清、不知道对象算 included 还是 referenced |
| [SBP-Pipeline/Bundle-Write-Data.md](../wiki/task-chain/Bundle-Write-Data.md) | **IBundleWriteData 与装箱**：file/bundle/SerializedFile 概念、`AssetToFiles`/`FileToBundle`/`FileToObjects`、内部文件名生成、单个对象从依赖到 bundle 的走查 | 看不懂 WriteData 三组字典、一个 asset 为什么对应多个 CAB、对象最后进了哪个 bundle |
| [SBP-Determinism.md](Docs/SBP-Determinism.md) | `IDeterministicIdentifiers` 三种实现、对象/asset 排序、`ContentHash` vs `RawFileHash`、`BundleDetails.Hash`、回调版本 hash、构建指纹、Ex17 实测、CI 门禁 | 同一份代码两次构建结果不同、diff 误报、跨平台 hash 对不上 |
| [SBP-BuildCache.md](Docs/SBP-BuildCache.md) | `CacheEntry` / `CachedInfo`、`Library/BuildCache` 布局、Load/Save/校验、各任务 key 构造、Purge/Prune、自定义任务缓存模板、冷/热构建实测（Ex20/Ex21） | 缓存不命中、错误命中、改完代码没生效、缓存过大 |
| [SBP-Cache-Invalidation.md](Docs/SBP-Cache-Invalidation.md) | **缓存失效图谱**：GlobalHash / Guid / Version / Hash / 依赖校验五层，逐 case 说明哪些改动会让缓存失效（含实测） | 不知道改了什么导致缓存全失效、判断某次改动会不会 miss |

建议读法：

1. 先读 C.13 的结论和 Ex17 实测，建立“确定/不确定”的边界；
2. 想搞清“每个 Task 具体做什么、数据长什么样” → 读任务链子 Wiki（含 Ex18 真实快照）；
3. 按问题选主题子 Wiki（确定性 or 缓存）；
4. 对着子 Wiki 末尾的源码索引逐个文件读；
5. 用 Ex17（确定性）、Ex08/Ex16（缓存与压缩）、Ex18（阶段探针）做验证。

---

## 附录 A. 术语表

| 术语 | 含义 |
|---|---|
| **Asset** | `Assets/` 下的源文件；导入后对应一个或多个 Object |
| **Object** | Unity 可序列化单元（GameObject、Material、Shader…） |
| **Includes** | 构造某个 Asset 所需的 Object 集合 |
| **References** | Asset 依赖但不包含的 Object 集合 |
| **GUID** | 资源唯一 ID（`.meta` 里那个），`BundleLayout` 存的就是它 |
| **BundleLayout** | `bundle 名 -> List<GUID>` 的映射 |
| **AddressableName** | bundle 内资源的内部加载 id，默认是资源路径 |
| **Packing** | 决定哪些 Object 进哪个 bundle |
| **Writing** | 把 Object 序列化成文件（SerializedFile） |
| **Archive & Compress** | 把文件归档并按 `BuildCompression` 压缩成最终 bundle |

## 附录 B. API 速查

```csharp
// 平替构建
AssetBundleManifest CompatibilityBuildPipeline.BuildAssetBundles(
    string outputPath, BuildAssetBundleOptions options, BuildTarget target);

// 自组构建
ReturnCode ContentPipeline.BuildAssetBundles(
    IBundleBuildParameters parameters,
    IBundleBuildContent content,
    out IBundleBuildResults results);

// 带自定义任务链 / 上下文
ReturnCode ContentPipeline.BuildAssetBundles(
    IBundleBuildParameters parameters,
    IBundleBuildContent content,
    out IBundleBuildResults results,
    IList<IBuildTask> taskList,
    params IContextObject[] contextObjects);

// 参数
var parameters = new BundleBuildParameters(target, group, outputFolder)
{
    BundleCompression = UnityEngine.BuildCompression.LZ4, // LZMA / LZ4 / Uncompressed
    UseCache = true,
    AppendHash = false,
    ContiguousBundles = true,
};

// 内容
new BundleBuildContent(ContentBuildInterface.GenerateAssetBundleBuilds()); // 标签
new BundleBuildContent(new[] { new AssetBundleBuild { assetBundleName = "x", assetNames = new[] {"Assets/a.prefab"} } }); // 显式

// 结果
foreach (var kv in results.BundleInfos) {
    BundleDetails d = kv.Value;   // d.FileName / d.Hash(Hash128) / d.Crc / d.Dependencies
}

// 任务
public class MyTask : IBuildTask {
    public int Version => 1;                       // 缓存 key 的一部分
    [InjectContext(ContextUsage.In)] IBuildResults m_Results;  // 字段必须 private
    public ReturnCode Run() { return ReturnCode.Success; }
}

// 缓存
BuildCache.PurgeCache(false);
```

---

## 附录 C. 任务链逐任务详解（源码级）

> 本附录把 §2.3 的六阶段展开成“每个任务做什么、关键实现、可扩展点、对应本 lab 哪个案例”。
> 主要参考：**SBP 1.23.4 源码** + Unity 中文社区文章
> [《【Unity】SBP - Scriptable Build Pipeline》](https://developer.unity.cn/projects/62551c68edbc2a4e5071d760)（2022，郡墙）。
> 源码路径省略公共前缀 `Library/PackageCache/com.unity.scriptablebuildpipeline@1.23.4/`。

### C.1 六阶段总览

| 阶段 | 任务 | 上下文里多了什么 |
|---|---|---|
| 1 Setup | `SwitchToBuildPlatform` → `RebuildSpriteAtlasCache` | 目标平台、按平台重打的 SpriteAtlas |
| 2 Player Scripts | `BuildPlayerScripts` → `PostScriptsCallback` | `ScriptCompilationResult`、`TypeDB` |
| 3 Dependency | `CalculateSceneDependencyData` → `CalculateCustomDependencyData`* → `CalculateAssetDependencyData` → `StripUnusedSpriteSources` → `PostDependencyCallback` | `IDependencyData`（includes / references / usage） |
| 4 Packing | `GenerateBundlePacking` → `GenerateBundleCommands` → `GenerateSubAssetPathMaps` → `GenerateBundleMaps` → `PostPackingCallback` | `IWriteData`（bundle→文件→对象、ReferenceMap、UsageTagSet） |
| 5 Writing | `WriteSerializedFiles` → `ArchiveAndCompressBundles` → `AppendBundleHash` → `GenerateLinkXml` → `PostWritingCallback` | 磁盘上的 bundle 文件 + `IBundleBuildResults` |
| 6 Manifest | **官方 TODO** | SBP 核心不生成，详见 C.10 |

\* `CalculateCustomDependencyData` 仅 `UNITY_2019_3_OR_NEWER`；`GenerateLinkXml` 仅在 `WriteLinkXML=true` 时真正写文件。

### C.2 Setup

**`SwitchToBuildPlatform`**（`Editor/Tasks/SwitchToBuildPlatform.cs`）
- 调 `EditorUserBuildSettings.SwitchActiveBuildTarget(params.Group, params.Target)` 切换 Editor 活动平台；
- 切换成功后重新初始化 Unity 的构建回调（`IEditorBuildCallbacks.InitializeCallbacks()` →
  `BuildInterfacesWrapper` → 反射调用 `UnityEditor.Build.BuildPipelineInterfaces.InitializeBuildCallbacks`），
  这样 `IPreprocessShaders` / `IPreprocessComputeShaders` / `IProcessScene(WithReport)` 才会对新平台生效；
- 同时会收集带 `VersionedCallbackAttribute` 的回调版本 hash，**回调实现变了 → 相关缓存失效**；
- ⚠️ 副作用：构建结束后不会自动切回原平台（见 [坑 #19](#7-常见坑实测)）。

**`RebuildSpriteAtlasCache`**（`Editor/Tasks/RebuildSpriteAtlasCache.cs`）
- 调 `SpriteAtlasUtility.PackAllAtlases(params.Target)`，把工程里**所有** SpriteAtlas 按目标平台重打；
- 图集是“构建期生成资源”，必须在依赖计算前就绪，否则依赖图会引用旧/异平台的图集数据；
- 2020.1+ 之后旧版 `Packer.RebuildAtlasCacheIfNeeded` 被跳过，统一由 `PackAllAtlases` 覆盖 V1/V2。

### C.3 Player Scripts

**`BuildPlayerScripts`**
- 调 `PlayerBuildInterface.CompilePlayerScripts(params.GetScriptCompilationSettings(), params.ScriptOutputFolder)`；
- 产物是 `ScriptCompilationResult` + **`TypeDB`**：记录类型/字段信息，供后续序列化写 type tree；
- 这一步是大项目构建耗时的大头（我们实测 575 ms，占总耗时的绝大部分）。

**`PostScriptsCallback`**
- 调 `IScriptsCallback.PostScripts(params, results)`；
- 注册方式：设置 `ContentPipeline.BuildCallbacks.PostScriptsCallbacks`（见 C.7）。

### C.4 Dependency

**`CalculateSceneDependencyData`**
- 对每个场景调 `ContentBuildInterface.CalculatePlayerDependenciesForScene(scenePath, settings, usageTags, cache)`（2019.3+）；
- 产出场景的 includes / references；缓存主要覆盖预制相关依赖。

**`CalculateCustomDependencyData`**（2019.3+）
- 遍历 `IBundleBuildContent.CustomAssets`，对每项执行 `Processor(asset, this)`；
- 用于“AssetDatabase 追踪不到”的自定义资源（例如运行时生成的贴图/数据），把它塞进依赖计算。

**`CalculateAssetDependencyData`**
- 输入：资源 GUID/路径、TypeDB、GraphicsSettings、场景 LightSettings、目标平台；
- `included`：`ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(guid, target)`，
  对应资源 YAML 序列化里的 `!u!<ClassID>` 段；
- `referenced`：`GetPlayerDependenciesForObjects(included, target, typeDB)`；
- `representations`：`GetPlayerAssetRepresentations` 的 index>0 部分作为 SubAsset 扩展数据；
- Sprite 类型资源会带上 SpriteAtlas 打包信息；
- 最终写进 `IDependencyData`（`AssetInfo` / `SceneInfo` / `AssetUsage` / `SceneUsage`）。

**`StripUnusedSpriteSources`**
- 找出“已被 SpritePacker 打进图集”的 SourceTexture，从依赖里剔除；
- 但仍被 asset/scene 引用的会保留，避免误删；
- 作用：防止 source texture 被重复打进包。

**`PostDependencyCallback`**
- 调 `IDependencyCallback.PostDependency(params, dependencyData)`；
- 此时 `IDependencyData` 完整，适合做自定义依赖分析（对应 Ex06）。

### C.5 Packing

**`GenerateBundlePacking`**
- 校验 bundle 内容：asset bundle vs scene bundle（`ValidationMethods.ValidAssetFake` 可接管）；
- 计算加载依赖列表：
  - 剔除 Unity 默认内置资源引用；
  - 处理递归依赖去重；
  - **打破环形依赖**（比较 GUID 顺序决定保留哪边）；
  - scene bundle 复用 sharedAssets / 前一个 scene 的对象；
- 产出 `IWriteData` 的 `AssetToFiles` / `FileToObjects` 等。

**`GenerateBundleCommands`**
- 为每个 bundle 创建 `WriteOperation`：
  - `AssetBundleCommand`：建立“加载入参 ↔ Object”映射，记录依赖对象 hash/序列化组织并**排序**，
    保证每次序列化稳定（确定性构建的关键之一）；
  - `SceneBundleCommand` / `SceneDataCommand`：场景用，记录 `preloadObjects`。

**`GenerateSubAssetPathMaps`**
- 把 `ExtendedAssetData` 里的 SubAsset representations 插入 `bundleAssets` 列表（带 offset），
  这样 SubAsset 也能被正确加载。

**`GenerateBundleMaps`**
- 计算**前向依赖**：本 bundle 的 Object 引用了哪些其他 bundle 的 Object → 生成 `BuildReferenceMap`
  （file + serializationIndex + ObjectIdentifier）；
- 计算**反向依赖**：哪些其他 bundle 引用了本 bundle → 合并它们用到的 `BuildUsageTagSet`
  （shader 变体等条件数据）；
- 这样“别的 bundle 新增/删除对象”不会导致本 bundle 无谓重建（缓存友好）。

**`PostPackingCallback`**
- 调 `IPackingCallback.PostPacking(params, dependencyData, writeData)`；
- 想改 bundle 布局要在 Packing **之前**（Ex12 的 `MergeBundlesTask` 就插在 `PostDependencyCallback` 之后）。

### C.6 Writing

**`WriteSerializedFiles`**
- 输入：GraphicsSettings、TypeDB、目标平台、`ContentBuildFlags`
  （`DisableWriteTypeTree` / `StripUnityVersion` / `DevelopmentBuild`）；
- 先查缓存决定哪些 SerializedFile 需要重写；
- 计算 `SerializedFileMetaData`：
  - `RawFileHash`：整个文件 hash；
  - `ContentHash`：**跳过文件 header** 的 hash，避免 Unity 升级导致全量重建；
- Hash 算法默认 SpookyHash，也支持 MD5/MD4，可用 `HashingMethods.GetHashAlgorithm` 扩展。

**`ArchiveAndCompressBundles`**
- 构造可线程化的 `Task`，多 bundle 并行归档压缩；
- 核心调用 `ContentBuildInterface.ArchiveAndCompress(resourceFiles, writePath, compression)`，返回 **CRC**；
- 命中缓存时直接从缓存路径拷贝（`CopyFileWithTimestampIfDifferent`），避免重复压缩；
- 产出 `BundleDetails`（`FileName` / `Hash` / `Crc` / `Dependencies`），也就是 `IBundleBuildResults.BundleInfos`。

**`AppendBundleHash`**
- 仅在 `AppendHash = true` 时执行：文件名改成 `<name>_<hash>`；
- ⚠️ 文件名变了，运行时加载路径也要跟着变；做 CDN/清单时注意区分“逻辑 bundle 名”和“真实文件名”。

**`GenerateLinkXml`**
- 仅在 `WriteLinkXML = true` 时执行；
- 用 `LinkXmlGenerator` 收集 `includedTypes` 和 `includedSerializeReferenceFQN`（2021.1+），
  写 `link.xml`，用于 Player 构建时的代码裁剪；
- 这是 SBP 替代旧 manifest 裁剪用途的方式。

**`PostWritingCallback`**
- 调 `IWritingCallback.PostWriting(params, dependencyData, writeData, results)`；
- 此时所有打包文件已落盘，适合做加密/签名/交付（Ex07、Ex11）。

### C.7 回调点与扩展手段

| 扩展点 | 接口 / API | 触发任务 | 注册方式 |
|---|---|---|---|
| 脚本编译后 | `IScriptsCallback` | `PostScriptsCallback` | `ContentPipeline.BuildCallbacks.PostScriptsCallbacks = ...` |
| 依赖计算后 | `IDependencyCallback` | `PostDependencyCallback` | `ContentPipeline.BuildCallbacks.PostDependencyCallback = ...` |
| 打包后 | `IPackingCallback` | `PostPackingCallback` | `ContentPipeline.BuildCallbacks.PostPackingCallback = ...` |
| 写盘后 | `IWritingCallback` | `PostWritingCallback` | `ContentPipeline.BuildCallbacks.PostWritingCallback = ...` |
| Unity 构建回调 | `IPreprocessShaders` / `IPreprocessComputeShaders` / `IProcessScene(WithReport)` | `SwitchToBuildPlatform` 后初始化 | 实现接口即可（`BuildInterfacesWrapper` 反射初始化） |
| 资源合法性 | `ValidationMethods.ValidAssetFake` / `ValidSceneBundle` | `GenerateBundlePacking` | 静态委托 |
| Hash 算法 | `HashingMethods.GetHashAlgorithm` | 缓存 / 写盘 | 静态委托 |
| 确定性 ID | `IDeterministicIdentifiers` | Packing / Writing | 作为 `contextObject` 传入 |
| 内容与布局 | `IBundleBuildContent` | 全链路 | 构造 `BundleBuildContent` |
| 任务链 | `IList<IBuildTask>` | 全链路 | `DefaultBuildTasks.Create` 后增删插 |
| 参数 | 自定义 `BundleBuildParameters` 子类 | 全链路 | 重写 `GetCompressionForIdentifier` 等 |

> 优先级建议：**外部 hook > 增删任务 > 替换/子类化默认任务**（升级风险递增）。

### C.8 关键数据结构

| 结构 | 出现位置 | 作用 |
|---|---|---|
| `TypeDB` | `BuildPlayerScripts` | 类型/字段信息，序列化写 type tree 用 |
| `IDependencyData` | Dependency 阶段 | `AssetInfo`（includedObjects/referencedObjects）、`SceneInfo`、`AssetUsage/SceneUsage` |
| `IWriteData` | Packing 阶段 | `AssetToFiles`、`FileToObjects`、`WriteOperations`、`FileToReferenceMap`、`FileToUsageSet` |
| `BuildReferenceMap` | GenerateBundleMaps | 前向依赖：bundle 之间 Object 级别引用（file + localID） |
| `BuildUsageTagSet` | GenerateBundleMaps | 反向依赖：为 shader 变体等条件数据收集 usage |
| `SerializedFileMetaData` | WriteSerializedFiles | `RawFileHash` + `ContentHash`（跳过 header，跨 Unity 版本稳定） |
| `BundleDetails` | ArchiveAndCompressBundles | `FileName` / `Hash` / `Crc` / `Dependencies` |
| `IBundleBuildResults` | 构建返回 | `BundleInfos` / `AssetResults` / `WriteResults` |

### C.9 任务 ↔ lab 案例 / 源码对应

| 任务 | 对应案例 | 备注 |
|---|---|---|
| `SwitchToBuildPlatform` | 坑 #19 | 构建后恢复平台 |
| `RebuildSpriteAtlasCache` | 图集平台差异 | `PackAllAtlases` |
| `CalculateAssetDependencyData` | Ex05 / Ex06 | 自定义任务读 `IDependencyData` |
| `GenerateBundlePacking` | **Ex12** | 在 Packing 前改写布局 |
| `GenerateBundleCommands` / `GenerateBundleMaps` | 无单独案例 | 读源码 + trace 看耗时（`Temporary Map Creations`、`Populate ...`） |
| `WriteSerializedFiles` | Ex03 / Ex04 | 压缩前后的体积变化 |
| `ArchiveAndCompressBundles` | Ex03 / Ex04 / Ex11 | `BundleDetails`、CRC |
| `AppendBundleHash` | Ex11（加密交付） | 文件名与 hash 的关系 |
| `GenerateLinkXml` | 未覆盖 | 可加一个 `WriteLinkXML=true` 的对比实验 |
| 自定义任务/回调 | Ex05/06/07/09/12/13 | 任务链、上下文、logger、报告 |
| 清单产出 | **Ex07 / Ex14** | SBP 不自动写 `.manifest`，自己生成 |

### C.10 专题：产物清单（`.manifest`）到底去哪了

旧 `BuildPipeline.BuildAssetBundles` 会产出三种东西：

1. 二进制 **manifest bundle**（文件名 = 输出目录名，运行时 `AssetBundleManifest` 靠它）；
2. **每个 bundle 一份** `.manifest` YAML 文本；
3. 根目录一份顶层 `.manifest`。

SBP 的情况：

| 产物 | 旧管线 | `CompatibilityBuildPipeline` | `ContentPipeline` |
|---|---|---|---|
| manifest bundle | ✅ | ❌ | ❌ |
| 每 bundle `.manifest` | ✅ | ❌ | ❌ |
| 顶层 `.manifest` | ✅ | ✅（唯一，内容格式不同） | ❌ |
| 清单数据 | 文件 + 内存 | 返回 `CompatibilityAssetBundleManifest` | `IBundleBuildResults` |

官方社区文章把 **“6. Generate manifest files” 标为 TODO**，和我们的实测一致：SBP 核心不再自动导出清单。
落地方式（三选一）：

- 自己从 `IBundleBuildResults` 生成清单（推荐，Ex07 是样板）；
- 持久化 `CompatibilityAssetBundleManifest`（它是运行时程序集里的 ScriptableObject，API 同 `AssetBundleManifest`）；
- 用 Addressables 的 catalog 作为清单。

> 详细实测对比见 [案例 14](#14-旧管线-vs-sbp-产物对比)；清单缺失相关的坑见 [坑 #20/#21](#7-常见坑实测)。

### C.11 Preset 与 shared bundles（内置 shader / MonoScript 抽取）

`DefaultBuildTasks` 的 4 个 Preset：

| Preset | shaderTask | monoScriptTask | 用途 |
|---|---|---|---|
| `PlayerScriptsOnly` | - | - | 只编译玩家脚本，不产 bundle |
| `AssetBundleCompatible` | ❌ | ❌ | **默认**，行为最接近旧管线 |
| `AssetBundleBuiltInShaderExtraction` | ✅ | ❌ | 把内置资源抽到 `UnityBuiltIn.bundle` |
| `AssetBundleShaderAndScriptExtraction` | ✅ | ✅ | 额外把 MonoScript 抽到 `UnityMonoScripts.bundle` |

**“内置 shader bundle”实际做了什么**（`CreateBuiltInShadersBundle` + `UpdateBundleObjectLayout`）：

1. 扫描 `IDependencyData` 中所有 `referencedObject.guid == 0000000000000000f000000000000000`
   的对象（即 `resources/unity_builtin_extra` 里被引用的对象：Standard shader、默认材质、内置贴图等）；
2. 把这些对象放进 `IBundleExplictObjectLayout`；
3. `UpdateBundleObjectLayout` 把它们从原 bundle 移出，放入独立的 `UnityBuiltIn.bundle`
   （这个 bundle 的 `BundleLayout` 里 **没有 Asset**，只装 Object，不能 `LoadAsset`）。

**实测对比**（Ex15，Unity 2022.3 / StandaloneWindows64 / LZ4）：

| Preset | bundle 数 | 明细 |
|---|---|---|
| `AssetBundleCompatible`（默认） | 3 | props_a 3.2 KB (deps: shared)、props_b 3.1 KB、**shared 45.3 KB** |
| `AssetBundleBuiltInShaderExtraction` | 4 | props_a 3.2 KB (deps: shared, UnityBuiltIn.bundle)、props_b 3.1 KB、**shared 2.5 KB**、**UnityBuiltIn.bundle 43.9 KB** |
| `AssetBundleShaderAndScriptExtraction` | 4 | 同上（本 lab 的 prefab 没挂脚本，所以 MonoScript 抽取无差异） |

结论：

- **不抽取**：内置 shader 等对象被打进每个引用它的 bundle → 包体膨胀、运行时多份副本；
  但每个 bundle 自包含、少一个运行时依赖。
- **抽取**：所有引用内置资源的 bundle 都依赖 `UnityBuiltIn.bundle`，**必须先加载它**；
  包体去重，引用它的 bundle 越多收益越大（本 lab 只有 1 个 bundle 引用，所以总大小几乎不变）。
- `UnityBuiltIn.bundle` / `UnityMonoScripts.bundle` 属于 **shared bundles**：
  通常跟 Player 一起固定发布，不参与内容热更（Addressables 文档也强调 shared bundles
  默认跟随默认组，使用本地路径时只能随新 Player 更新）。
- 默认 Preset 不抽取，是因为抽取会改变运行时依赖契约（多一个必须加载的依赖），
  所以做成 opt-in；生产项目一般建议开启。

> 参考：[Addressables · Shared AssetBundles](https://docs.unity3d.com/Packages/com.unity.addressables@2.7/manual/build-shared-assetbundles.html)。

### C.12 重内容实验：体积 / 压缩 / 缓存（Ex08 + Ex16）

> 轻量内容（几 KB）测不出压缩/缓存的真实差异，所以 lab 准备了一份**重内容**。
> 资源仅用于本地学习实验，不要随仓库分发。

**内容构成**（`Assets/Bundles/Heavy/`，源资源约 179 MB）：

| 子目录 | 来源 | 规模 | 打进 bundle 的特点 |
|---|---|---|---|
| `Audio/` | ARPGTutorial 拷贝（55 个 wav） | 26.3 MB | 音频导入后序列化体积较小（LZMA 后约 2.6 MB） |
| `Art/` | ARPGTutorial 动画 FBX 60 个 | 152.9 MB | 动画 clip 打包后约 5–8 MB（源文件大 ≠ bundle 大） |
| `Textures/` | 程序生成 8 张 2048² 贴图 | PNG 源很小 | 导入设为 **Uncompressed** → packed 约 **96 MB** |

> 这组贴图刻意做成高度可压缩的渐变图案，所以 Ex16 里 LZMA 的压缩率会非常夸张；
> 换成真实照片/UI 贴图，压缩率会低一些，但体积趋势一致。

**Ex16 实测（2026-11-08，StandaloneWindows64；每种压缩都 PurgeCache 后跑“冷 + 热”两次）**：

| 压缩方式 | 冷构建 | 热构建 | 缓存加速 | 总大小 | 明细（audio / textures / art_a+art_b） |
|---|---|---|---|---|---|
| LZMA | 8738 ms | 1349 ms | 6.5x | 8.49 MB | 2.60 MB / 271.7 KB / 5.62 MB |
| LZ4 | 2653 ms | 1306 ms | 2.0x | 14.44 MB | 2.60 MB / 3.89 MB / 7.95 MB |
| Uncompressed | 2520 ms | 1277 ms | 2.0x | **110.24 MB** | 2.62 MB / **96.01 MB** / 11.62 MB |

> 为什么每种要跑两次：只跑一次的话，先跑的那组会额外承担冷缓存成本，对比不公平；
> 冷构建比压缩成本，热构建比缓存收益。

汇总对比（脚本会打印）：

- **LZMA vs Uncompressed（冷）**：体积 7.7%，耗时 3.5x
- **LZ4 vs Uncompressed（冷）**：体积 13.1%，耗时 1.1x
- **LZMA 比 LZ4（冷）**：体积小 41.2%，耗时多 3.3x
- 机器可读报告：`Artifacts/SBP/ex16-summary.json`（含每种压缩的冷/热耗时与 bundle 体积明细）

**Ex08 实测（冷/热缓存，LZMA）**：冷 6024 ms → 热 1428 ms，约 **4.2x**。

怎么用：

- 想测包体/压缩：跑 `Ex16`（或菜单 `SBP Lab/Examples/16`）；
- 想测增量缓存：跑 `Ex08`；
- 换真实资源：把 `Assets/Bundles/Heavy/` 下的内容替换掉，或改 `SbpLabCommon.HeavyLayout()` 里的目录/扩展名过滤。

> 提示：`SBP Lab/0b - Setup Heavy Textures` 会重新生成缺失的合成贴图；
> `Ex08`/`Ex16` 运行前会自动确保它们存在。

### C.13 确定性构建与缓存：SBP 到底保证什么

> 对应案例：`SbpLabEx17_Determinism.cs`。结论先行：
> **同机器、同 Unity 版本、同构建参数下，SBP 产物通常字节级一致；内容 hash 稳定。
> 但它不承诺跨 Unity 版本/平台/压缩设置/机器字节一致。**
>
> 源码级完整拆解见子 Wiki：[SBP-Determinism.md](Docs/SBP-Determinism.md)、[SBP-BuildCache.md](Docs/SBP-BuildCache.md)。

#### 确定性机制（源码位置）

| 机制 | 位置 | 作用 |
|---|---|---|
| `IDeterministicIdentifiers` | `Editor/Interfaces/IDeterministicIdentifiers.cs` | 生成确定性的 localID（默认 `Unity5PackedIdentifiers`；`ContiguousBundles=true` 时用 `PrefabPackedIdentifiers`） |
| 序列化对象排序 | `GenerateBundleCommands.cs` | 对 `serializeObjects` 排序，保证对象写入顺序稳定 |
| 前向/反向依赖映射 | `GenerateBundleMaps.cs` | `BuildReferenceMap` 只包含真正引用的对象；无关对象变化不引起重建 |
| 内容 hash 排除 header | `WriteSerializedFiles.cs` + `SerializedFileMetaData` | `ContentHash` 跳过 SerializedFile header，Unity 版本升级不改变内容 hash |
| Hash 算法 | `HashingMethods`（SpookyHash/MD5/MD4 可扩展） | 稳定、可替换 |
| Bundle hash | `ArchiveAndCompressBundles.CalculateHashVersion` | `Hash = hash(ContentHash, Dependencies)`，**不含压缩方式** |
| DeterministicAssetBundle | 官方 UpgradeGuide | SBP 始终开启、不可关闭 |

#### 缓存机制（两层）

1. **artifact 缓存**：`BuildCache`（`Library/BuildCache`），内容寻址，可 `PurgeCache/PruneCache`；
2. **任务级缓存**：`TaskCachingUtility.RunCachedOperation` + `CacheEntry/CachedInfo`。
   - `CacheEntry.Guid`：任务/文件标识；
   - `CacheEntry.Hash`：`HashingMethods.Calculate(任务 Version, 输入 hash, 设置 hash, 依赖 hash...)`；
   - 任务 `Version` 变了 → 缓存失效（改逻辑要 +1）；
   - 依赖变化由 `BuildCache.HasAssetOrDependencyChanged` 判定；
   - `IProcessScene/IPreprocessShaders` 回调带 `VersionedCallbackAttribute` 时，回调版本 hash 也进缓存 key。

#### 实测（Ex17，2026-11-08，StandaloneWindows64，轻量内容）

| 对比 | 文件字节 | 内容 hash | 说明 |
|---|---|---|---|
| A vs B（同输入、`UseCache=false` 两次） | ✅ 完全一致 | ✅ 一致 | 确定性成立 |
| C vs D（冷缓存 vs 热缓存） | ✅ 完全一致 | ✅ 一致 | 缓存命中产物 = 冷构建产物 |
| B vs D（无缓存 vs 缓存产物） | ✅ 完全一致 | ✅ 一致 | 缓存不改变产物 |
| A vs E（仅 `ContiguousBundles` 不同） | ❌ 不同 | ❌ 不同 | 布局参数属于构建配置；换配置不保证一致 |

实测 bundle hash（A/B/C/D 完全相同）：

```
props_a 61222da9f2feecb6d312bd46dc4929c9
props_b fa61d49e4ee0f13eff0bb32488100470
shared  699f53d56c5e640866f990bb3c97b6ab
```

> 注意：上表是 **StandaloneWindows64** 实测。若在 **Android** 目标下跑 Ex17：
> - `props_a` / `props_b` 的内容 hash 与 Standalone 相同（预制体序列化数据平台无关）；
> - `shared` 的 hash 不同（它包含内置 Standard shader，按平台编译/序列化）；
> - A/B/C/D 仍应一致，A/E 仍应不同（换 `ContiguousBundles` 必改布局）。
> 所以**跨平台比较 hash/大小没有意义**，构建指纹必须包含平台。

#### 不保证的部分（工程上要自己兜底）

- **跨 Unity 版本 / 跨平台**：序列化格式、TypeTree、shader 编译、纹理压缩都可能不同；
- **跨压缩设置**：`BundleDetails.Hash` 不变，但文件字节/CRC 必变；
- **自定义任务**：不在 key 里的输入不会被自动识别；要么实现 `IBuildCache` 缓存，要么接受每次重跑；
- **自定义参数/ID 生成**：`ContiguousBundles`、`DisableWriteTypeTree`、`StripUnityVersion`、自定义 `IDeterministicIdentifiers` 都会改变产物，应纳入“构建配置版本”；
- **跨机器**：默认不保证；要验证就在 CI 的固定 runner 上跑两次比对（Ex17 就是模板）。

#### 给「全量构建 + 历史仓库 diff」流程的建议

1. **逻辑变更判定**用 `BundleDetails.Hash`（内容 hash）+ 压缩/配置版本，而不是文件 MD5；
2. **文件 MD5** 只用于上传校验/去重，不要当内容身份；
3. CI 加一道**确定性门禁**：同一 runner 上构建两次（可一次走缓存、一次不走），逐文件比对 MD5，不一致就报警；
4. 把 Editor 版本、SBP 版本、`ContiguousBundles`、压缩方式、TypeTree/strip 开关写进构建配置 hash，配置变化时接受全量；
5. 自定义任务如果参与产物，补一个稳定的 cache key（任务 Version + 所有输入 hash）。

---

## 附录 D. 参考资料与延伸阅读

### 核心参考

- **Unity 中文社区**：[《【Unity】SBP - Scriptable Build Pipeline》](https://developer.unity.cn/projects/62551c68edbc2a4e5071d760)
  —— 按任务拆解 SBP 源码流程（本附录 C 的主要外部参考）。
- 官方手册（1.21 / 2.3）：
  - [Getting Started](https://docs.unity3d.com/Packages/com.unity.scriptablebuildpipeline@1.21/manual/GettingStarted.html)
  - [Usage Examples](https://docs.unity3d.com/Packages/com.unity.scriptablebuildpipeline@1.21/manual/UsageExamples.html)
  - [Upgrade Guide](https://docs.unity3d.com/Packages/com.unity.scriptablebuildpipeline@1.21/manual/UpgradeGuide.html)
  - [Build Logging](https://docs.unity3d.com/Packages/com.unity.scriptablebuildpipeline@2.3/manual/BuildLogger.html)
  - [2.3 手册（Unity 2023.1+/6）](https://docs.unity3d.com/Packages/com.unity.scriptablebuildpipeline@2.3/manual/index.html)
- 源码镜像：[needle-mirror/com.unity.scriptablebuildpipeline](https://github.com/needle-mirror/com.unity.scriptablebuildpipeline)
  （本地对应 `Library/PackageCache/com.unity.scriptablebuildpipeline@1.23.4/`）。
- 官方 C# 参考：[UnityCsReference](https://github.com/Unity-Technologies/UnityCsReference)
  （如 `UnityEditor.U2D.SpriteAtlasUtility`、`BuildPipeline` 的绑定层）。

### 延伸

- Addressables 构建扩展：[Create a custom build script](https://docs.unity3d.com/Packages/com.unity.addressables@2.9/manual/build-scripting-custom.html)、[BuildLayoutReport](https://docs.unity3d.com/Packages/com.unity.addressables@2.9/manual/BuildLayoutReport.html)。
- YooAsset 中文文档（对比产品层设计）：[资源构建](https://www.yooasset.com/docs/guide-editor/AssetBundleBuilder)。
- 性能分析工具：[Perfetto UI](https://ui.perfetto.dev)（打开 `buildlogtep.json`）。
- **子 Wiki（源码级）**：
  - [SBP-Determinism.md](Docs/SBP-Determinism.md) —— 确定性构建：ID/排序/hash/回调版本/构建指纹
  - [SBP-BuildCache.md](Docs/SBP-BuildCache.md) —— 构建缓存：CacheEntry/存储/Load/Save/任务 key/自定义模板
- [SBP-Cache-Invalidation.md](Docs/SBP-Cache-Invalidation.md) —— 缓存失效图谱：哪些改动会让 hash / CacheEntry 失效
- 本 lab 全部案例：见 [第 3 节案例总览](#3-案例总览)。

---

## 附录 E. 产物清单（实测）

> 数据来源：lab 工程 `Artifacts/SBP/`，2026-11-08 实跑（目标平台 **Android**）。
> 大小按 1024 进制换算；“产物”不含构建 trace `buildlogtep.json`（trace 单列）。
> SBP/旧管线产物**没有扩展名**，文件名 = bundle 名（见 Ex14 产物对比与 §7 常见坑）。

### E.1 目录总览

| 目录 | 对应案例 | 产物文件数 | 产物合计 | trace |
|---|---|---|---|---|
| `cmp-legacy-Android` | Ex14 旧管线 | 8 | 127.1 KB | — |
| `cmp-sbp-Android` | Ex14 SBP 兼容层 | 4 | 140.6 KB | 12.0 KB |
| `ex01-compat-Android` | Ex01 Compatibility | 4 | 140.6 KB | 10.8 KB |
| `ex02-explicit-Android` | Ex02 显式布局 | 3 | 140.1 KB | 9.5 KB |
| `ex03-LZMA-Android` | Ex03 LZMA | 3 | 104.9 KB | 11.5 KB |
| `ex03-LZ4-Android` | Ex03 LZ4 | 3 | 140.1 KB | 10.9 KB |
| `ex03-Uncompressed-Android` | Ex03 不压缩 | 3 | 253.5 KB | 11.5 KB |
| `ex04-per-bundle-Android` | Ex04 独立压缩 | 3 | 107.0 KB | 9.5 KB |
| `ex07-delivery/<时间戳>/` | Ex07 交付清单 | 4/次 | 140.8 KB/次 | — |
| `ex08-cold-Android` | Ex08 冷缓存 | 7 | 8925.1 KB | 32.0 KB |
| `ex08-warm-Android` | Ex08 热缓存 | 7 | 8925.1 KB | 32.4 KB |
| `ex09-profiling-Android` | Ex09 自定义 logger | 3 | 140.1 KB | **无**（自定义 logger 不写 trace） |
| `ex10-content-file-Android` | Ex10 Content File | 5 | 140.6 KB | 10.0 KB |
| `ex11-encrypted-delivery` | Ex11 加密交付 | 4 | 52.4 KB | — |
| `ex12-merge-Android` | Ex12 合并布局 | 2 | 137.2 KB | 10.2 KB |
| `ex13-A-per-bundle-LZ4-Android` | Ex13 A 配置 | 3 | 140.1 KB | 10.9 KB |
| `ex13-B-merged-LZ4-Android` | Ex13 B 配置 | 2 | 137.2 KB | 9.5 KB |
| `ex13-C-per-bundle-LZMA-Android` | Ex13 C 配置 | 3 | 104.9 KB | 11.5 KB |
| `ex15-AssetBundleCompatible-Android` | Ex15 默认 Preset | 3 | 140.1 KB | 10.9 KB |
| `ex15-AssetBundleBuiltInShaderExtraction-Android` | Ex15 抽内置 shader | 4 | 141.3 KB | 14.3 KB |
| `ex15-AssetBundleShaderAndScriptExtraction-Android` | Ex15 再抽 MonoScript | 4 | 141.3 KB | 13.4 KB |
| `ex16-LZMA-Android` | Ex16 重内容 LZMA | 4 | 8784.9 KB | 30.5 KB |
| `ex16-LZ4-Android` | Ex16 重内容 LZ4 | 4 | 14873.9 KB | 30.5 KB |
| `ex16-Uncompressed-Android` | Ex16 重内容不压缩 | 4 | 112981.2 KB | 30.5 KB |

> 注意：`ex11` 的交付产物是按 **Editor 平台（Standalone）** 构建的（为了能在 Editor 里加载验证），
> 所以 `shared.enc` 只有 45.3 KB；Android 的同一 bundle 是 133.8 KB（内置 shader 按平台不同）。
> **跨平台比较体积没有意义。**

### E.2 典型目录明细

**① 清单差异：Ex01（兼容层）vs Ex02（ContentPipeline）**

| 文件 | ex01-compat | ex02-explicit |
|---|---|---|
| `props_a` | 3.2 KB | 3.2 KB |
| `props_b` | 3.1 KB | 3.1 KB |
| `shared` | 133.8 KB | 133.8 KB |
| `ex01-compat-Android.manifest` | 0.5 KB | **无** |
| 产物合计 | 140.6 KB | 140.1 KB |

**② 压缩对比：Ex03（同一内容，Android）**

| 文件 | LZMA | LZ4 | Uncompressed |
|---|---|---|---|
| `props_a` | 2.2 KB | 3.2 KB | 9.0 KB |
| `props_b` | 2.1 KB | 3.1 KB | 8.3 KB |
| `shared` | 100.7 KB | 133.8 KB | 236.2 KB |
| **合计** | **104.9 KB** | **140.1 KB** | **253.5 KB** |

**③ 每 bundle 独立压缩：Ex04**

| 文件 | 配置 | 大小 |
|---|---|---|
| `props_a` | LZ4 | 3.2 KB |
| `props_b` | LZ4 | 3.1 KB |
| `shared` | LZMA | 100.7 KB |
| **合计** | — | **107.0 KB**（对比 Ex03 LZ4 的 140.1 KB） |

**④ Content File 产物：Ex10（cluster hash 命名，无 bundle 名）**

| 文件 | 大小 | 说明 |
|---|---|---|
| `cae7b92f6c8170ebff679d4d4a65a4be` | 2.3 KB | cluster archive |
| `fe4de0c036f5f680c897aef09a1edd0a` | 1.9 KB | cluster archive |
| `26d6c5f63d2572ca51657b0ad9e84599` | 2.3 KB | cluster archive |
| `d0483dacc73607d9ed4aa216f931768c` | 2.3 KB | cluster archive |
| `6a0110f9445406e5abb2d09864129da8` | 131.7 KB | cluster archive（含内置 shader 等） |
| **合计** | **140.6 KB** | 无 `.manifest`、无依赖字段（`deps=0`） |

**⑤ 交付与加密：Ex07 / Ex11**

| 目录 | 文件 | 大小 |
|---|---|---|
| `ex07-delivery/<时间戳>/` | `delivery-manifest.json` / `props_a` / `props_b` / `shared` | 0.7 / 3.2 / 3.1 / 133.8 KB |
| `ex11-encrypted-delivery/` | `manifest.json` / `props_a.enc` / `props_b.enc` / `shared.enc` | 0.7 / 3.2 / 3.1 / 45.3 KB |

**⑥ 合并策略：Ex12 / Ex13-B**

| 文件 | Ex12 合并后 | Ex13-A 默认布局 |
|---|---|---|
| `props_all`（合并产物） | 3.4 KB | — |
| `props_a` / `props_b` | — | 3.2 KB / 3.1 KB |
| `shared` | 133.8 KB | 133.8 KB |
| **合计** | **137.2 KB** | **140.1 KB** |

**⑦ Preset 对比：Ex15（Android）**

| 文件 | 默认 | 抽内置 shader | + 抽 MonoScript |
|---|---|---|---|
| `props_a` | 3.2 KB | 3.2 KB | 3.2 KB |
| `props_b` | 3.1 KB | 3.1 KB | 3.1 KB |
| `shared` | 133.8 KB | **2.5 KB** | **2.5 KB** |
| `UnityBuiltIn.bundle` | — | 132.4 KB | 132.4 KB |
| **合计** | **140.1 KB** | **141.3 KB** | **141.3 KB** |

**⑧ 重内容：Ex16（Android，产物不含 trace）**

| 文件 | LZMA | LZ4 | Uncompressed |
|---|---|---|---|
| `heavy_audio` | 2664.8 KB | 2663.6 KB | 2678.2 KB |
| `heavy_textures` | 271.7 KB | 3984.2 KB | 98311.0 KB |
| `heavy_art_a` | 3258.5 KB | 4899.2 KB | 7545.8 KB |
| `heavy_art_b` | 2590.0 KB | 3326.9 KB | 4446.3 KB |
| **合计** | **8784.9 KB** | **14873.9 KB** | **112981.2 KB** |

**⑨ 缓存：Ex08 冷 vs 热**

两目录产物完全一致（7 个文件、合计 8925.1 KB），差异只在构建耗时：
冷 6024 ms → 热 1428 ms（见 C.12）。

### E.3 说明

- 同一份内容在不同平台/不同压缩下大小不同，属正常；比较体积要固定平台与配置；
- `buildlogtep.json` 是构建 trace，不是交付物，上传 CDN 时应排除；
- 文件名没有扩展名；如果团队约定 `.bundle`/`.assetbundle`，要把后缀**写进 bundle 名**
  （Unity 不会自动加后缀），产物文件名就会跟着带后缀；
- 以上产物可直接在 `Artifacts/SBP/` 对应目录核对，重跑案例会被覆盖。

---

*最后更新：2026-11-08 · 对应 SBP 1.23.4 · 工程 UnityGradleDemo*
