# YooAsset 3.0.7 vs SBP：构建管线对比与“构建加速”拆解

> 详细的上层缓存数据结构与代码/伪代码见独立文档：**[YooAsset-BuildCache.md](YooAsset-BuildCache.md)**（SBP BuildCache + AssetDependencyDatabase 两层缓存）。
>
> 运行时加密/解密（Offset / Memory / Stream）见 **[YooAsset-Encryption.md](YooAsset-Encryption.md)**。
>
> 本文基于本机安装的 **YooAsset 3.0.7**（embedded package）源码阅读，对照 **SBP 1.23.4**。
>
> 安装位置：`_e2e/UnityGradleDemo/Packages/com.tuyoogame.yooasset`
> 安装方式：嵌入包（embedded），`Packages/manifest.json` 添加 `"com.tuyoogame.yooasset": "file:com.tuyoogame.yooasset"`
> 验证：Unity 2022.3.17f1c1 batchmode 导入，`YooAsset.dll` / `YooAsset.Editor.dll` 编译通过；SBP 仍为 1.23.4。

---

## 1. 结论先看

1. **YooAsset 没有 fork / 修改 SBP 源码**，也没有自定义 `IDeterministicIdentifiers`、`IWriteOperation`、`IBuildCache`。
2. 它做的是：**在 SBP 外面套一层自己的构建管线（BundleBuilder）**，用 `TaskBuilding_SBP` 调 `ContentPipeline.BuildAssetBundles`，并传入自己的 SBP 任务列表与参数。
3. **构建加速没有另造一套 bundle cache**：
   - 真正的 SBP 任务缓存仍然是 `UnityEditor.Build.Pipeline.Utilities.BuildCache`；
   - YooAsset 只是 `UseCache = true`，并在勾选 Clear Build Cache 时调 `BuildCache.PurgeCache(false)`；
   - 额外的“上层加速”是 **`AssetDependencyDatabase`**（`Library/AssetDependencyDB`），用来加速 **资源收集（build map 生成）**，不是加速 SBP 的序列化/归档。
4. 所以：**Unity BuildCache 负责 SBP 内部任务；YooAsset 的 dependency DB 负责 collector 阶段**，两者是互补，不是重复实现。

---

## 2. YooAsset 的构建管线结构

`ScriptableBuildPipeline.GetDefaultBuildPipeline()` 返回 11 个 YooAsset 自己的任务：

```text
TaskPrepare_SBP
TaskGetBuildMap_SBP
TaskBuilding_SBP          ← 这里才进入 SBP
TaskVerifyBuildResult_SBP
TaskEncryption_SBP
TaskUpdateBundleInfo_SBP
TaskCreateManifest_SBP
TaskCreateReport_SBP
TaskCreatePackage_SBP
TaskCopyBundledFiles_SBP
TaskCreateCatalog_SBP
```

`TaskBuilding_SBP` 是 SBP 的边界：

```csharp
var bundleBuilds = buildMapContext.GetPipelineBuilds(scriptableBuildParameters.ReplaceAssetPathWithAddress);
var buildContent = new BundleBuildContent(bundleBuilds);

var buildParameters = scriptableBuildParameters.GetBundleBuildParameters();
var taskList = SBPBuildTasks.Create(builtinShadersBundleName, monoScriptsBundleName);
ContentPipeline.BuildAssetBundles(buildParameters, buildContent, out buildResults, taskList);
```

也就是说：

- **YooAsset 自己**负责 collector、build map、manifest、加密、打包、catalog、首包拷贝；
- **SBP 只负责**“把 build map 变成 AssetBundle 文件”这一段；
- 两边各有自己的 `BuildContext` / `IBuildTask`（YooAsset 的 BuildContext 不是 SBP 的）。

---

## 3. YooAsset 对 SBP 的“自定义”到底改了什么

### 3.1 没有改 SBP 内部

- 没有自定义 `IDeterministicIdentifiers`：用 SBP 默认 `PrefabPackedIdentifiers` / `Unity5PackedIdentifiers`（取决于 `ContiguousBundles`，YooAsset 没显式改）；
- 没有自定义 `IWriteOperation`：仍用 SBP 的 `AssetBundleWriteOperation` 等；
- 没有自定义 `IBuildCache`：仍用 SBP `BuildCache`；
- 没有自定义 `IBuildLogger` / `BuildContext`：仍用 SBP 默认。

### 3.2 改的是“调用方式”

`SBPBuildTasks.Create()` 是 YooAsset 复制的 SBP 任务列表，与 SBP `DefaultBuildTasks.AssetBundleCompatible` 的差异：

| 任务 | SBP 默认 preset | YooAsset |
|---|---|---|
| SwitchToBuildPlatform / RebuildSpriteAtlasCache | ✅ | ✅ |
| BuildPlayerScripts / PostScriptsCallback | ✅ | ✅ |
| CalculateSceneDependencyData / Custom / Asset | ✅ | ✅ |
| StripUnusedSpriteSources | ✅ | ✅ |
| **CreateBuiltInShadersBundle** | 仅 extraction preset | ✅ 当配置了内置 shader bundle 名 |
| **CreateMonoScriptBundle** | 仅 extraction preset | ✅ 当配置了 MonoScript bundle 名 |
| **UpdateBundleObjectLayout** | 仅 extraction preset | ✅ 总是加 |
| GenerateBundlePacking / Commands / SubAsset / Maps | ✅ | ✅ |
| WriteSerializedFiles / ArchiveAndCompress | ✅ | ✅ |
| AppendBundleHash | ✅（默认 no-op） | ✅（默认 no-op） |
| GenerateLinkXml | ✅ | ✅ |
| PostWritingCallback | ✅ | ✅ |

也就是说：**YooAsset 默认走的是“SBP AssetBundleCompatible + 内置 shader/MonoScript 抽取”的组合**。

> 注意：**没有删除任何默认任务**——SBP `AssetBundleCompatible` 的 19 个任务逐项都在，顺序不变；新增的 3 个里只有 `UpdateBundleObjectLayout` 是无条件加入（layout 为空时内部直接 `SuccessNotRun`），另两个由 bundle 名是否为空决定。

### 3.3 参数映射

`ScriptableBuildParameters.GetBundleBuildParameters()` 把 YooAsset 参数翻译成 SBP 参数：

| YooAsset | SBP |
|---|---|
| `CompressOption` | `BundleBuildParameters.BundleCompression`（Uncompressed / LZMA / LZ4） |
| `StripUnityVersion` | `ContentBuildFlags.StripUnityVersion` |
| `DisableWriteTypeTree` | `ContentBuildFlags.DisableWriteTypeTree` |
| `CacheServerHost/Port` | `BuildParameters.CacheServerHost/Port` |
| `WriteLinkXML` | `BundleBuildParameters.WriteLinkXML` |
| 固定 | `UseCache = true` |

### 3.4 内容与结果映射

- **内容**：`BuildBundleInfo.CreatePipelineBuild()` 生成 `AssetBundleBuild`，可选 `addressableNames`（`ReplaceAssetPathWithAddress`）；
- **结果**：`TaskUpdateBundleInfo_SBP` 从 SBP `BundleDetails` 取 `Hash` / `Crc`，同时自己算文件 MD5 / CRC32 / size；
- **manifest 依赖**：`TaskCreateManifest_SBP.GetBundleDepends()` 直接用 `BundleDetails.Dependencies`；
- **验证**：`TaskVerifyBuildResult_SBP` 对比“计划 bundle 列表”和 SBP 实际 `BundleInfos`。

---

## 4. 所谓“上层构建加速”是什么

### 4.1 第一层：SBP BuildCache（YooAsset 直接复用）

`TaskPrepare_SBP`：

```csharp
if (buildParameters.ClearBuildCacheFiles)
{
    UnityEditor.Build.Pipeline.Utilities.BuildCache.PurgeCache(false);
    DeletePackageRootDirectory(buildParameters);
}
```

`ScriptableBuildParameters.GetBundleBuildParameters()` 固定 `buildParams.UseCache = true`。

所以：

- 未勾选 Clear Build Cache → SBP `BuildCache` 正常命中/未命中；
- 勾选 Clear Build Cache → 清空 SBP `Library/BuildCache` + 删输出目录 → 全量重建。

**YooAsset 没有自己的 bundle 级缓存**（源码中 `BuildCache` 只出现在这里和 `ClearBuildCacheWindow`）。

### 4.2 第二层：`AssetDependencyDatabase`（YooAsset 自己的 collector 加速）

当 `UseAssetDependencyDB = true` 时：

- 数据库文件：`Library/AssetDependencyDB`；
- 结构：`assetPath → { AssetDatabase.GetAssetDependencyHash() 快照, 直接依赖 GUID[] }`；
- 下次构建：
  - 读取数据库；
  - 用 `AssetDatabase.GetAssetDependencyHash(assetPath)` 与快照比较；
  - 只有变化的资产才重新 `AssetDatabase.GetDependencies(assetPath, false)`；
  - 未变化的资产直接用缓存的依赖 GUID 列表。

它加速的是 **collector 生成 build map** 这一阶段：

```text
BundleCollectorSettingData.Setting.BeginCollect(..., useAssetDependencyDB: true, ...)
```

而不是 SBP 的 `CalculateAssetDependencyData` / `WriteSerializedFiles` / `ArchiveAndCompressBundles`。

### 4.3 它没有缓存什么

- 没有缓存 build map / bundle 布局（每次构建重新收集、重新分配 bundle）；
- 没有缓存 manifest / package / encryption / catalog（这些是 YooAsset 外层任务，每次都跑）；
- 没有替代 SBP 的依赖计算（SBP 内部仍会算 `CalculateAssetDependencyData`，只是 SBP BuildCache 会缓存结果）。

---

## 5. 和 Unity BuildCache 的关系：有必要吗？

| 问题 | 答案 |
|---|---|
| YooAsset 是不是重复实现了 BuildCache？ | 不是；它直接用 SBP BuildCache。 |
| Unity BuildCache 能不能覆盖 YooAsset 全部构建耗时？ | 不能；它只覆盖 SBP 内部任务（依赖、写盘、归档），不覆盖 YooAsset 的 collector/build map/manifest/package。 |
| `AssetDependencyDB` 有必要吗？ | **看项目规模**。collector 阶段如果是瓶颈（资产数大、依赖深、共享规则复杂），开启它通常有明显收益；小项目收益有限。 |
| 需要额外维护吗？ | 需要一个正确的失效策略：依赖 `AssetDatabase.GetAssetDependencyHash`；如果发现脏数据，删 `Library/AssetDependencyDB` 重建即可。 |

对你们这种“bundle 切得细 + 全量构建 + 历史仓库 diff”的流程：

- 如果继续走 YooAsset，**SBP BuildCache 已经能省掉大部分重复的序列化/归档**；
- `AssetDependencyDB` 只对“资源收集”阶段有加速，是否开启应以你们工程的 collector profile 为准；
- 如果你们已经自研管线（QTS/自定义 DB），**没必要为了加速再引入 YooAsset**；可以借鉴它“外层缓存只缓存 SBP 覆盖不到的阶段”的思路。

---

## 6. 值得借鉴的设计点

1. **分层**：YooAsset 不碰 SBP 内部，只在它外面做资源管理层；SBP 升级时只需要同步任务列表。
2. **缓存边界清晰**：SBP cache 管序列化/归档，YooAsset 只额外缓存 collector 的依赖图。
3. **结果复用**：manifest 的依赖直接取 SBP `BundleDetails.Dependencies`，不自己重算。
4. **可验证性**：`TaskVerifyBuildResult_SBP` 对比“计划 bundle”和“实际 bundle”，避免 build map 和 SBP 结果漂移。

## 7. 风险 / 坑

- **任务列表是复制品**：YooAsset 的 `SBPBuildTasks.Create()` 复制了 SBP 任务顺序/构造；SBP 升级到 2.x/3.x 时可能编译不过或行为漂移；
- **依赖 SBP 内部类型**：`BundleBuildParameters` / `ContentPipeline` / `BuildCache` 都是 SBP API，SBP 大版本升级要回归；
- **Clear Build Cache 会删输出**：不仅是清 SBP cache，还会删 YooAsset 的输出目录；
- **`AssetDependencyDB` 默认关闭**，开启前建议先在目标工程测 collector 阶段耗时；
- 嵌入包 29MB（含 Samples~），不需要 Samples 时可在打包时剔除。

---

## 8. 源码索引

| 主题 | 文件 |
|---|---|
| YooAsset 外层任务链 | `Editor/BundleBuilder/BuildPipeline/ScriptableBuildPipeline/ScriptableBuildPipeline.cs` |
| SBP 任务列表复制 | `.../ScriptableBuildPipeline/ScriptableBuildTasks.cs` |
| SBP 桥接 | `.../BuildTasks/TaskBuilding_SBP.cs` |
| SBP 参数映射 | `.../ScriptableBuildParameters.cs` |
| Clear Build Cache | `.../BuildTasks/TaskPrepare_SBP.cs` |
| SBP 结果 → YooAsset | `.../BuildTasks/TaskUpdateBundleInfo_SBP.cs` |
| manifest 依赖 | `.../BuildTasks/TaskCreateManifest_SBP.cs` |
| collector 依赖 DB | `Editor/BundleCollector/AssetDependencyCache.cs`、`AssetDependencyDatabase.cs` |
| 依赖 DB 开关 | `Editor/BundleBuilder/BuildParameters.cs`（`UseAssetDependencyDB`） |
| 构建 entry | `Editor/BundleBuilder/BundleBuilder.cs` |
