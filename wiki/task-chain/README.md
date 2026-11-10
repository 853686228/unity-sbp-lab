# SBP 任务链逐阶段学习 Wiki

> 面向：需要**吃透 SBP 每个 Task 的作用、输入/输出、数据结构**，并且团队对 SBP 做了**高度魔改**（不用 Unity AssetBundle，写入阶段换成自定义 DB）的工程师。
>
> 本 Wiki 用一份**真实跑出来的完整例子**（lab 工程 `SbpLabEx18_StageProbe`）带你走完 `AssetBundleCompatible` 预设的 19 个任务，并在每个任务后抓取 `BuildContext` 的真实数据。
>
> 配套阅读：
> - 实验工程主 Wiki：`_e2e/UnityGradleDemo/SBP-Lab-Wiki.md`
> - 源码级专题：`_e2e/UnityGradleDemo/Docs/SBP-Determinism.md`、`Docs/SBP-BuildCache.md`
> - 捕获数据：本文同目录 `captured/`（JSON、summary、运行日志）
> - 探针源码：`assets/SbpLabEx18_StageProbe.cs`

---

## 0. 这份 Wiki 怎么用

三条线，按需选：

1. **先建立全局图**：§1（19 任务 = 6 阶段 + 执行模型 + 上下文对象 + 数据形态演化）
2. **跟着真实例子走一遍**：§2（Ex18 的输入、20 个边界快照、总表）+ §3（逐任务详解）
3. **对照你们魔改管线**：§4（数据结构手册）+ §5（保留/替换/删除的判断框架 + 对照清单）

> ⚠️ 术语约定：本文的 “Task / 步骤 / 阶段” 都指 `IBuildTask` 实例；`contextObjects` 指 `BuildContext` 里按接口类型注册的对象。

### 0.1 最小阅读清单（如果只有 1~2 小时）

| 顺序 | 文件 | 行数级 | 为什么读 |
|---|---|---|---|
| 1 | `Editor/Shared/DefaultBuildTasks.cs` | 160 | 一眼看清整个流程和预设差异 |
| 2 | `Editor/ContentPipeline.cs` | 250 | 入口、上下文注册、缓存/日志/回调装配 |
| 3 | `Editor/Shared/BuildTasksRunner.cs` | 130 | 执行模型：Inject → Run → Extract |
| 4 | `Editor/Shared/BuildContext.cs` + `Editor/Injector/ContextInjector.cs` | 250 | 注入机制原貌 |
| 5 | `Editor/Shared/BuildWriteData.cs` | 150 | “装箱单”的字段原型 |
| 6 | `Editor/Tasks/WriteSerializedFiles.cs` | 800+ | 唯一复杂且被完整保留的 Task，理解它才能理解缓存和写入 |

> 这 6 个文件读完，再回来看 §3 的逐任务细节，会快很多。

---

## 1. 先建立整体心智模型

### 1.1 19 个任务 = 6 个阶段

`DefaultBuildTasks.Create(Preset.AssetBundleCompatible)` 返回的任务顺序（Unity 2022.3 / SBP 1.23.4）：

```text
① Setup
   SwitchToBuildPlatform
   RebuildSpriteAtlasCache
② Player Scripts
   BuildPlayerScripts
   PostScriptsCallback
③ Dependency
   CalculateSceneDependencyData
   CalculateCustomDependencyData        (2019.3+)
   CalculateAssetDependencyData
   StripUnusedSpriteSources
   PostDependencyCallback
④ Packing
   GenerateBundlePacking
   GenerateBundleCommands
   GenerateSubAssetPathMaps
   GenerateBundleMaps
   PostPackingCallback
⑤ Writing
   WriteSerializedFiles
   ArchiveAndCompressBundles
   AppendBundleHash                     (AppendHash=false 时 SuccessNotRun)
   GenerateLinkXml                      (WriteLinkXML=false 时 SuccessNotRun)
   PostWritingCallback
```

> 另一条产物形态是 `ContentFileCompatible()`（2022.2+，用 `ClusterBuildLayout` 取代 Packing 阶段），见主 Wiki 案例 10。

### 1.2 任务执行模型

`BuildTasksRunner.Run(taskList, context)` 对每个任务：

```csharp
ContextInjector.Inject(context, task);   // 把 [InjectContext] 私有字段填上
var code = task.Run();                   // 任务逻辑
ContextInjector.Extract(context, task);  // InOut/Out 字段写回上下文
```

`ContextInjector` 的规则（源码）：

- 只扫描 **`BindingFlags.Instance | BindingFlags.NonPublic`** 字段 → 注入字段必须是 `private`；
- `ContextUsage.In`：只拿不写；
- `ContextUsage.Out`：只写不拿；
- `ContextUsage.InOut`：拿 → 改 → 写回（默认）；
- `Optional = true`：上下文里没有就注入 `null`，不抛异常；
- `IBuildContext` 只能配 `ContextUsage.In`，注入的就是 `BuildContext` 本身。

**这条模型决定了：任务之间不直接调用，只通过 context 传数据。** 你们魔改时最容易踩的也是这里（改了数据类但没注册/注入类型不对）。

### 1.3 BuildContext 里有什么

Ex18 在 `start` 边界抓到的 32 个注册对象（按类型名排序）：

```
BuildCache, BuildCallbacks, BuildDependencyData, BuildInterfacesWrapper, BuildLog,
BuildParameters, BundleBuildContent, BundleBuildParameters, BundleBuildResults, BundleWriteData,
IBuildCache, IBuildContent, IBuildLogger, IBuildParameters, IBuildResults, IBundleBuildContent,
IBundleBuildParameters, IBundleBuildResults, IBundleWriteData, IDependencyCallback,
IDependencyData, IDeterministicIdentifiers, IEditorBuildCallbacks, IObjectDependencyData,
IPackingCallback, IProgressTracker, IScriptsCallback, IWriteData, IWritingCallback,
ObjectDependencyData, PrefabPackedIdentifiers, ProgressTracker
```

按用途分类：

| 分类 | 对象 | 来源 |
|---|---|---|
| 参数 | `IBuildParameters` / `IBundleBuildParameters`（运行时是 `BundleBuildParameters`） | 调用方传入 |
| 内容/布局 | `IBundleBuildContent`（`BundleBuildContent`） | 调用方传入 |
| 结果 | `IBuildResults` / `IBundleBuildResults`（`BundleBuildResults`） | `ContentPipeline` new |
| 依赖 | `IDependencyData`（`BuildDependencyData`）、`IObjectDependencyData` | `ContentPipeline` new |
| 装箱单 | `IWriteData` / `IBundleWriteData`（`BundleWriteData`） | `ContentPipeline` new |
| ID 生成 | `IDeterministicIdentifiers`（`PrefabPackedIdentifiers`） | `ContentPipeline` 按 `ContiguousBundles` 选择 |
| 缓存 | `IBuildCache`（`BuildCache`） | `ContentPipeline` new |
| 日志 | `IBuildLogger`（默认 `BuildLog`；传了自定义就用自定义） | 上下文或 `ContentPipeline` new |
| 回调 | `IScriptsCallback` / `IDependencyCallback` / `IPackingCallback` / `IWritingCallback`（默认同一个 `BuildCallbacks`） | `ContentPipeline.BuildCallbacks` |
| 平台回调 | `IEditorBuildCallbacks`（`BuildInterfacesWrapper`） | `ContentPipeline` new |
| 进度 | `IProgressTracker`（`ProgressTracker`） | `ContentPipeline` new |

任务运行中新增的 context object 只有一种（本例）：`CalculateCustomDependencyData` 加了 `ICustomAssets`（即使没有 custom asset 也会注册），所以 `ctx=32 → 33`。

### 1.4 数据形态的 5 次演化

这是理解“每个任务输入/输出”的最短路径：

```text
[1] 内容根
    IBundleBuildContent: bundle -> [GUID...]，Assets/Scenes/Addresses

[2] 依赖数据（Dependency）
    IDependencyData:
      AssetInfo[GUID] -> AssetLoadInfo { includedObjects[], referencedObjects[], address }
      SceneInfo[GUID] -> SceneDependencyInfo { referencedObjects[], includedTypes, ... }
      AssetUsage/SceneUsage[GUID] -> BuildUsageTagSet
    IBuildExtendedAssetData: GUID -> ExtendedAssetData { Representations[] }   （SubAsset）
    IBuildSpriteData: GUID -> SpriteImporterData { PackedSprite, SourceTexture }（图集）
    IObjectDependencyData: ObjectIdentifier -> 对象级依赖

[3] 装箱单（Packing）
    IBundleWriteData:
      AssetToFiles[GUID] -> List<file>
      FileToObjects[file] -> List<ObjectIdentifier>
      FileToBundle[file] -> bundle 名
      WriteOperations[] -> IWriteOperation（命令 + usage + reference map）

[4] 写盘结果（Writing）
    IBuildResults:
      WriteResults[internalName] -> WriteResult { resourceFiles[], serializedObjects[], ... }
      WriteResultsMetaData[internalName] -> SerializedFileMetaData { RawFileHash, ContentHash }

[5] 最终产物（Archive）
    IBundleBuildResults.BundleInfos[bundle] -> BundleDetails { FileName, Hash, Crc, Dependencies[] }
```

把这张图记住，§3 里每个任务就是在某一层之间搬数据。

---

## 2. 完整例子：Ex18 全流程探针

### 2.1 输入

| 项 | 值 |
|---|---|
| 工程 | `_e2e/UnityGradleDemo`（Unity 2022.3.17f1c1 / SBP 1.23.4） |
| 目标平台 | `StandaloneWindows64`（构建机 Editor 平台，便于加载验证） |
| 内容 | `SbpLabCommon.ExplicitLayout()`：4 个资源 → 3 个 bundle |
| bundle 布局 | `props_a` ← CubeA.prefab + CubeB.prefab；`props_b` ← CubeC.prefab；`shared` ← Mat.mat |
| 参数 | `BundleCompression=LZ4`、`AppendHash=false`、`ContiguousBundles=true`、`UseCache=true` |
| 任务链 | `DefaultBuildTasks.Create(Preset.AssetBundleCompatible)`（19 个） |

### 2.2 探针怎么做的

Ex18 在 19 个真实任务之间插入 20 个 `StageProbeTask`：

```
[probe "start"] → T1 → [probe after T1] → T2 → [probe after T2] → ... → T19 → [probe after T19]
```

探针本身也是 `IBuildTask`，字段同样是 `[InjectContext]`：

```csharp
[InjectContext(ContextUsage.In)]                IBuildContext m_Context;
[InjectContext(ContextUsage.In, true)]          IBundleBuildParameters m_Params;
[InjectContext(ContextUsage.In, true)]          IBundleBuildContent m_Content;
[InjectContext(ContextUsage.In, true)]          IDependencyData m_Dependency;
[InjectContext(ContextUsage.In, true)]          IBundleWriteData m_WriteData;
[InjectContext(ContextUsage.In, true)]          IBuildResults m_Results;
[InjectContext(ContextUsage.In, true)]          IBundleBuildResults m_BundleResults;
[InjectContext(ContextUsage.In, true)]          IBuildExtendedAssetData m_ExtendedData;
[InjectContext(ContextUsage.In, true)]          IBuildSpriteData m_SpriteData;
[InjectContext(ContextUsage.In, true)]          IBundleExplictObjectLayout m_ObjectLayout;
```

然后反射读 `BuildContext.m_ContextObjects` 枚举已注册对象，把每个边界的数据格式化成一行字符串。

构造探针时把**紧邻的前一个任务**传进去，`Run()` 就能打印“上一步骤”的名字：

```csharp
// 组装任务链
taskList.Add(task);
taskList.Add(new StageProbeTask("after " + task.GetType().Name, task));

// StageProbeTask.Run()
Debug.Log(string.Format(
    "[StageProbe] 上一步骤 = {0}（v{1}） | 当前边界 = {2}",
    m_PreviousTaskName, m_PreviousTaskVersion, m_Label));
```

> `BuildTasksRunner` 不会告诉任务“上一个是谁”，任务列表的顺序就是唯一信息来源；所以要在构造时把前一个任务（或它的名字/Version）传进来。

> 探针会出现在 `buildlogtep.json` 里，属于学习工具，**不要放进生产管线**。

### 2.3 实测总表（20 个边界的核心数据）

完整字段见 `captured/ex18-stage-probe.json`，这里只摘关键变化：

| 边界 | 内容布局 | 依赖数据 | 写盘数据 | 结果 |
|---|---|---|---|---|
| start | 3 bundle / 4 asset | 空 | 空 | 空 |
| after SwitchToBuildPlatform | 不变 | 空 | 空 | 空 |
| after RebuildSpriteAtlasCache | 不变 | 空 | 空 | 空 |
| after BuildPlayerScripts | 不变 | 空 | 空 | `scriptTypeDB=true` |
| after PostScriptsCallback | 不变 | 空 | 空 | 不变 |
| after CalculateSceneDependencyData | 不变 | 空（本例无场景） | 空 | 不变 |
| after CalculateCustomDependencyData | 不变 | 空（无 custom asset） | 空 | **ctx 32→33（+ICustomAssets）** |
| after CalculateAssetDependencyData | 不变 | **assets=4，assetObj=16/8，assetUsage=4** | 空 | 不变 |
| after StripUnusedSpriteSources | 不变 | 不变（无 sprite atlas 引用） | 空 | 不变 |
| after PostDependencyCallback | 不变 | 不变 | 空 | 不变 |
| after GenerateBundlePacking | 不变 | 不变 | **assetToFiles=4，files=3，bundles=3** | 不变 |
| after GenerateBundleCommands | 不变 | 不变 | **writeOps=3**（props_a:10 / props_b:5 / shared:3 个对象） | 不变 |
| after GenerateSubAssetPathMaps | 不变 | 不变 | 不变（无 SubAsset） | 不变 |
| after GenerateBundleMaps | 不变 | 不变 | **usageSets=3，referenceMaps=3** | 不变 |
| after PostPackingCallback | 不变 | 不变 | 不变 | 不变 |
| after WriteSerializedFiles | 不变 | 不变 | 不变 | **writeResults=3，writeMeta=3** |
| after ArchiveAndCompressBundles | 不变 | 不变 | 不变 | **bundleInfos=3**（hash/size/deps，见下） |
| after AppendBundleHash | 不变 | 不变 | 不变 | 不变（`AppendHash=false`，任务 no-op） |
| after GenerateLinkXml | 不变 | 不变 | 不变 | 不变（`WriteLinkXML=false`，任务 no-op） |
| after PostWritingCallback | 不变 | 不变 | 不变 | 不变 |

`assetMap`（Packing 之后就能看到）：

```
Mat.mat -> shared
CubeA.prefab -> props_a
CubeB.prefab -> props_a
CubeC.prefab -> props_b
```

Archive 之后的 `BundleInfos`（Standalone，LZ4）：

```
props_a  hash=61222da9f2feecb6d312bd46dc4929c9  size=3323 B  deps=[shared]
props_b  hash=fa61d49e4ee0f13eff0bb32488100470  size=3166 B  deps=[shared]
shared   hash=699f53d56c5e640866f990bb3c97b6ab  size=46399 B deps=[]
```

> 依赖数据里的 `assetObj=16/8` 表示：4 个 asset 共 16 个 includedObjects、8 个 referencedObjects。`sample` 只打了前 3 个：CubeA/CubeB/CubeC 各 5 included + 2 referenced；第 4 个 Mat 是 1 included + 2 referenced（合计 16/8）。

### 2.4 怎么自己复现

GUI：`SBP Lab/Examples/18 - 全流程阶段探针`

batchmode：

```powershell
& 'C:\Program Files\Unity\Hub\Editor\2022.3.17f1c1\Editor\Unity.exe' `
  -batchmode -quit -nographics -projectPath '<工程>' `
  -executeMethod SbpLabEx18_StageProbe.Run -logFile -
```

产物：

| 文件 | 内容 |
|---|---|
| `Artifacts/SBP/ex18-stage-probe.json` | 20 个边界的完整快照（本文 `captured/` 下有拷贝） |
| `Artifacts/SBP/ex18-stages-<平台>/` | 实际构建产物 + `buildlogtep.json` |

---

## 3. 逐任务详解（19 个）

> 每个任务统一按：**作用 / 输入 / 输出（副作用）/ 源码 / Ex18 实测 / 对魔改管线的提示**。
> “输出”指它写回 `BuildContext` 的对象或字段。

### ① Setup 阶段

#### 3.1 SwitchToBuildPlatform

- **作用**：把 Editor 的活动构建平台切到 `IBuildParameters.Target/Group`，并重新初始化 Unity 的构建回调。
- **输入**：`IBuildParameters`、可选 `IEditorBuildCallbacks`。
- **输出**：无 context 数据；副作用是 **`EditorUserBuildSettings.SwitchActiveBuildTarget`**（构建后不切回）+ 初始化 `IPreprocessShaders` / `IProcessScene` 等回调。
- **源码**：`Editor/Tasks/SwitchToBuildPlatform.cs`
- **Ex18**：之后内容/依赖/写盘都为空，`ctx=32`；但它是所有后续“平台相关数据”的前提。
- **提示**：魔改管线如果也做平台相关处理（shader/图集/纹理压缩），这个任务要保留；跨平台构建后记得恢复平台（主 Wiki 坑 #19）。

#### 3.2 RebuildSpriteAtlasCache

- **作用**：调 `SpriteAtlasUtility.PackAllAtlases(target)`，按目标平台重打所有 SpriteAtlas。
- **输入**：`IBuildParameters`。
- **输出**：无 context 数据；副作用是图集资产/缓存就绪（之后依赖计算才能看到正确的图集对象）。
- **源码**：`Editor/Tasks/RebuildSpriteAtlasCache.cs`
- **Ex18**：无变化（本例没有 SpriteAtlas）。
- **提示**：只要依赖计算依赖“生成型资源”（图集、shader 变体等），这一步（或等价步骤）必须在 Dependency 之前。

### ② Player Scripts 阶段

#### 3.3 BuildPlayerScripts

- **作用**：编译 Player 脚本，得到 `TypeDB`（类型/字段信息，后续序列化用）。
- **输入**：`IBuildParameters`（`ScriptInfo` / `ScriptOutputFolder` / `GetScriptCompilationSettings()`）、`IBuildResults`。
- **输出**：`IBuildResults.ScriptResults`（`ScriptCompilationResult`）、`IBuildParameters.ScriptInfo = ScriptResults.typeDB`；同时 `BuildCacheUtility.SetTypeDB(typeDB)`。
- **源码**：`Editor/Tasks/BuildPlayerScripts.cs`
  - 如果 `ScriptInfo != null` → `SuccessNotRun`（复用外部传入的 TypeDB）；
  - 否则清空 `ScriptOutputFolder` → `PlayerBuildInterface.CompilePlayerScripts(...)`；
  - assemblies 和 typeDB 都为空 → `Error`。
- **Ex18**：`scriptTypeDB=true`，其它数据不变。
- **提示**：TypeDB 是后续所有序列化/缓存 key 的输入。魔改如果替换了序列化格式，但还要序列化 Unity 对象，TypeDB 仍然关键。

#### 3.4 PostScriptsCallback

- **作用**：调用 `IScriptsCallback.PostScripts(parameters, results)`。
- **输入**：`IBuildParameters`、`IBuildResults`、`IScriptsCallback`（默认来自 `ContentPipeline.BuildCallbacks`）。
- **输出**：取决于回调实现（默认无）。
- **源码**：`Editor/Tasks/PostScriptsCallback.cs`
- **提示**：这是“脚本编译后”的官方扩展点；魔改若要在脚本编译后改参数/资源，优先用回调而不是改任务。

### ③ Dependency 阶段

#### 3.5 CalculateSceneDependencyData

- **作用**：计算所有 Scene 的依赖数据（includedTypes/referencedObjects/usage），带缓存。
- **输入**：`IBuildParameters`、`IBuildContent.Scenes`、`IDependencyData`、可选 `IBuildResults`、`IProgressTracker`、`IBuildCache`、`IBuildLogger`。
- **输出**：`IDependencyData.SceneInfo`（`SceneDependencyInfo`）、`SceneUsage`（`BuildUsageTagSet`）；缓存 `CachedInfo`（`Data = { sceneInfo, usageTags, prefabDependency, objectTypes }`）。
- **源码**：`Editor/Tasks/CalculateSceneDependencyData.cs`
- **Ex18**：本例没有 Scene，无变化。
- **提示**：Scene 的依赖与 Prefab 引用相关；缓存 key 会混入 prefab 依赖和 `SceneCallbackVersionHash`（见确定性/缓存子 Wiki）。

#### 3.6 CalculateCustomDependencyData

- **作用**：处理 `IBundleBuildContent.CustomAssets`（AssetDatabase 追踪不到的自定义资源），调用每个 `CustomContent.Processor` 把结果写进依赖数据。
- **输入**：`IBuildParameters`、`IBundleBuildContent`（InOut）、`IDependencyData`（InOut）、可选 `ICustomAssets`（Out）、`IProgressTracker`、`IBuildCache`、`IBuildLogger`。
- **输出**：`IDependencyData.AssetInfo/AssetUsage`（追加自定义资产）；`ICustomAssets`（新 context 对象，记录自定义资产 GUID）。
- **源码**：`Editor/Tasks/CalculateCustomDependencyData.cs`
- **Ex18**：`ctx 32→33`（多了 `ICustomAssets`），其它数据不变（无 custom asset）。
- **提示**：如果你们有“构建期生成、AssetDatabase 看不到”的资源，这是官方入口。

#### 3.7 CalculateAssetDependencyData

- **作用**：对每个 Asset 计算 includedObjects / referencedObjects / representations / sprite 数据 / usage，是整个依赖阶段的核心。
- **输入**：`IBundleBuildParameters`、`IBuildContent`、`IDependencyData`（InOut）、`IObjectDependencyData`、可选 `IBuildResults`、`IBuildSpriteData`、`IBuildExtendedAssetData`、`IProgressTracker`、`IBuildCache`、`IBuildLogger`。
- **输出**：
  - `IDependencyData.AssetInfo[GUID]` = `{ includedObjects, referencedObjects, address }`
  - `IDependencyData.AssetUsage[GUID]`、`GlobalUsage`
  - `IBuildExtendedAssetData.ExtendedData[GUID].Representations`
  - `IBuildSpriteData.ImporterData[GUID]`
  - `IObjectDependencyData.ObjectDependencyMap`
- **源码**：`Editor/Tasks/CalculateAssetDependencyData.cs`（`kVersion = 6`）
- **Ex18**：`assets=4`、`included/referenced = 16/8`、`assetUsage=4`；sample 显示每个 prefab 5/2。
- **提示**：这是“AssetBundle pack unit → 你们的 pack ID unit”要保留的核心任务。缓存 key = `BuildCacheUtility.GetCacheEntry(asset, Version)` + 依赖 entries。

#### 3.8 StripUnusedSpriteSources

- **作用**：把“已经打进 SpriteAtlas 的源贴图”从依赖里剔除（仍被显式引用的保留），避免源贴图重复进包。
- **输入**：`IDependencyData`（InOut）、可选 `IBuildSpriteData`、`IBuildExtendedAssetData`。
- **输出**：修改 `AssetInfo/SceneInfo.referencedObjects`。
- **源码**：`Editor/Tasks/StripUnusedSpriteSources.cs`
- **Ex18**：无变化（无 sprite atlas）。
- **提示**：如果你们的包体规则不处理 sprite atlas，这个任务可以删；但要注意源贴图重复问题。

#### 3.9 PostDependencyCallback

- **作用**：调用 `IDependencyCallback.PostDependency(parameters, dependencyData)`。
- **输入**：`IBuildParameters`、`IDependencyData`、`IDependencyCallback`。
- **输出**：取决于回调实现；此时 `IDependencyData` 完整，适合做依赖分析/校验（Ex06）。
- **源码**：`Editor/Tasks/PostDependencyCallback.cs`

### ④ Packing 阶段

#### 3.10 GenerateBundlePacking

- **作用**：把“Asset 级依赖”展开成“bundle/file 级装箱单”：决定每个 bundle 内有哪些 Object、每个 asset 的主文件。
- **输入**：`IBundleBuildContent`（bundle 布局）、`IDependencyData`、`IBundleWriteData`（InOut）、`IDeterministicIdentifiers`、可选 `ICustomAssets`。
- **输出**（写 `IBundleWriteData`）：
  - `AssetToFiles[GUID] -> List<file>`（第一个是主文件）
  - `FileToObjects[file] -> List<ObjectIdentifier>`
  - `FileToBundle[file] -> bundle 名`
  - 场景另有 `SceneBundleNameFormat`（`archive:/<firstScene>/<scene>.sharedAssets`）
- **源码**：`Editor/Tasks/GenerateBundlePacking.cs`
- **Ex18**：`assetToFiles=4`、`files=3`、`bundles=3`；`assetMap=[Mat.mat→shared, CubeA/B→props_a, CubeC→props_b]`。
- **提示**：**这是“打包单位”语义最重的一步**。AssetBundle 版以 bundle 为单位；你们 QTS 以 pack ID 为单位——如果替换，通常要重写这个任务（或替换它的布局/命名策略）。

#### 3.11 GenerateBundleCommands

- **作用**：为每个 file/bundle 生成 `IWriteOperation`（真正写盘的“命令”），包括序列化对象列表 + localID + 内部文件名。
- **输入**：`IBuildParameters`、`IBundleBuildContent`、`IDependencyData`、`IBundleWriteData`（InOut）、`IDeterministicIdentifiers`、可选 `ICustomAssets`。
- **输出**：
  - `IBundleWriteData.WriteOperations[]`（`AssetBundleWriteOperation` / `SceneBundleWriteOperation` / `SceneDataCommand`）
  - 每个 `WriteCommand.serializeObjects[]` 的 `SerializationInfo { serializationObject, serializationIndex }`
  - 每个 file 的 `FileToUsageSet` / `FileToReferenceMap`
- **源码**：`Editor/Tasks/GenerateBundleCommands.cs`
  - `CreateWriteCommand` 里做 ID 碰撞检测；`AssetLoadInfoCompare` / `GetSortedSceneObjectIdentifiers` 做确定性排序。
- **Ex18**：`writeOps=3`，分别 `props_a(10)`、`props_b(5)`、`shared(3)` 个序列化对象；`usageSets=3`、`referenceMaps=3`。
- **提示**：**魔改产物的关键替换点之一**：AssetBundle 用 `AssetBundleWriteOperation`（内部还会写 AssetBundleInfo）；你们如果写自定义 DB，最自然的是替换 `IWriteOperation` 的实现（保留 `WriteSerializedFiles` 的调度/缓存），或者重写这个任务生成你们的 write op。

#### 3.12 GenerateSubAssetPathMaps

- **作用**：把 SubAsset（`ExtendedAssetData.Representations`）插入到 bundle 的 asset 列表里，保证 SubAsset 可被正确加载/寻址。
- **输入**：`IBundleWriteData`（InOut）、可选 `IBuildExtendedAssetData`。
- **输出**：修改 `WriteOperation.Info.bundleAssets`（按 `assetInfoIndex + offset` 插入 SubAsset 的 `AssetLoadInfo`，首对象 `Swap(0, index)`）。
- **源码**：`Editor/Tasks/GenerateSubAssetPathMaps.cs`
- **Ex18**：无变化（无 SubAsset）。
- **提示**：如果自定义 DB 不需要“bundleAssets 列表”这种概念，可删；但要自己处理 SubAsset 的寻址。

#### 3.13 GenerateBundleMaps

- **作用**：计算前向依赖（`BuildReferenceMap`）和反向依赖（`BuildUsageTagSet`），保证依赖/变体数据正确且缓存友好。
- **输入**：`IDependencyData`、`IBundleWriteData`（InOut）、可选 `IBuildLogger`。
- **输出**：填充 `FileToReferenceMap`、`FileToUsageSet`；只把“真正被引用”的对象加进 reference map。
- **源码**：`Editor/Tasks/GenerateBundleMaps.cs`
- **Ex18**：`usageSets=3`、`referenceMaps=3`。
- **提示**：这是增量/缓存正确性的关键任务，一般不建议删；即使换产物格式，也要保留“对象级引用/usage”语义。

#### 3.14 PostPackingCallback

- **作用**：调用 `IPackingCallback.PostPacking(parameters, dependencyData, writeData)`。
- **输入**：`IBuildParameters`、`IDependencyData`、`IWriteData`、`IPackingCallback`。
- **输出**：取决于回调；Ex12 的“在 Packing 前改布局”就是插在这个任务之前。

### ⑤ Writing 阶段

#### 3.15 WriteSerializedFiles

- **作用**：对每个 `IWriteOperation` 执行 `Write()`，把 SerializedFile 写到磁盘（或缓存 artifact 目录），并计算文件 hash 元数据。
- **输入**：`IBuildParameters`、`IDependencyData`、`IWriteData`、`IBuildResults`（InOut）、可选 `IProgressTracker`、`IBuildCache`、`IBuildLogger`。
- **输出**：`IBuildResults.WriteResults[internalName] = WriteResult`、`WriteResultsMetaData[internalName] = SerializedFileMetaData`。
- **源码**：`Editor/Tasks/WriteSerializedFiles.cs`
  - `SetupTaskContext` 取 `BuildSettings` / `m_UseCache`；
  - `RunCachedOperation`：`CreateCacheEntry` → `ProcessUncached`（`op.Write(targetDir, settings, globalUsage)` + `CalculateFileMetadata` + `SlimifySerializedObjects`）或 `ProcessCached`（从 `CachedInfo.Data` 取 `WriteResult`/`SerializedFileMetaData`）；
  - 缓存 key = `Version + operation.GetHash128 + settings.GetHash128 + globalUsage + slimWriteResults + player settings hash`。
- **Ex18**：`writeResults=3`、`writeMeta=3`；注意在 Archive 之前 `BundleInfos` 还是空的。
- **提示**：这是**唯一复杂且通常必须保留**的任务（如果你们只换写入实现，不应重写它）。你们自定义 DB 的写入逻辑，最干净的接入点是自定义 `IWriteOperation`（尤其是 `Write` 和 `GetHash128`），而不是改这个任务。

#### 3.16 ArchiveAndCompressBundles

- **作用**：把每个 bundle 的 SerializedFile 归档/压缩成最终产物文件，计算 CRC 和 `BundleDetails`。
- **输入**：`IBuildParameters`、`IBundleWriteData`、`IBundleBuildContent`、`IBundleBuildResults`（InOut）、可选 `IProgressTracker`、`IBuildCache`、`IBuildLogger`。
- **输出**：`IBundleBuildResults.BundleInfos[bundle] = BundleDetails { FileName, Hash, Crc, Dependencies }`；磁盘上的 bundle 文件。
- **源码**：`Editor/Tasks/ArchiveAndCompressBundles.cs`
  - 缓存 key 含 `kVersion + compression + resource serializedFile/fileAlias + RawFileHash[]`；
  - 命中：`CopyFileWithTimestampIfDifferent(CachedArtifactPath, outputFile)`；
  - 未命中：`ContentBuildInterface.ArchiveAndCompress(resourceFiles, writePath, compression, stripUnityVersion)`；
  - `Hash` 由 `CalculateHashVersion`（ContentHash + dependencies）计算，**不含压缩**。
- **Ex18**：Archive 后才出现 `bundleInfos=3`（hash/size/deps 见 §2.3）。
- **提示**：**换产物的第二个关键替换点**。如果你们写自定义 DB，这一步要把“归档压缩”换成“DB 写入/索引生成”，并自己定义 `BundleDetails`/清单语义（hash、CRC、依赖）。

#### 3.17 AppendBundleHash

- **作用**：`AppendHash=true` 时，把 bundle 文件名改成 `<name>_<hash>`。
- **输入**：`IBundleBuildParameters`、`IBundleBuildResults`（InOut）。
- **输出**：改写 `BundleDetails.FileName` 并移动磁盘文件。
- **源码**：`Editor/Tasks/AppendBundleHash.cs`
- **Ex18**：`AppendHash=false` → 任务 `SuccessNotRun`，无变化。
- **提示**：如果自定义 DB 的文件命名/寻址规则不同，可能替换或删除；但注意运行时加载/清单都要同步。

#### 3.18 GenerateLinkXml

- **作用**：`WriteLinkXML=true` 时，生成 `link.xml`（Player 构建防裁剪），替代旧管线的 manifest 裁剪信息。
- **输入**：`IBuildParameters`、`IBuildResults`。
- **输出**：磁盘 `link.xml`；无 context 数据。
- **源码**：`Editor/Tasks/GenerateLinkXml.cs`
- **Ex18**：`WriteLinkXML=false` → 无变化。
- **提示**：如果你们的出包流程不用 Unity 的代码裁剪，可删；否则保留/替换。

#### 3.19 PostWritingCallback

- **作用**：调用 `IWritingCallback.PostWriting(parameters, dependencyData, writeData, results)`。
- **输入**：`IBuildParameters`、`IDependencyData`、`IWriteData`、`IBuildResults`、`IWritingCallback`。
- **输出**：取决于回调；此时文件已落盘，适合做加密/交付/清单（Ex07/Ex11）。
- **源码**：`Editor/Tasks/PostWritingCallback.cs`
- **提示**：自定义 DB 的“收尾生成清单”可以放这里，而不是塞进任务链中间。

---

## 4. 数据结构手册（读源码时对照）

> 只想先看一个“从 prefab 到 bundle”的完整走通示例？看子文档 **[Dependency-Data-Structures.md](Dependency-Data-Structures.md)**（§4 完整案例：prefabA）。

### 4.1 内容与布局

`IBundleBuildContent`（`BundleBuildContent`）：

```csharp
List<GUID> Assets;                      // 参与构建的 asset
List<GUID> Scenes;                      // 参与构建的 scene
List<CustomContent> CustomAssets;       // 自定义依赖资源
Dictionary<string, List<GUID>> BundleLayout;   // bundle -> asset 列表
Dictionary<GUID, string> Addresses;     // asset -> 内部加载 id
Dictionary<string, List<ResourceFile>> AdditionalFiles;
```

### 4.2 依赖数据

```csharp
IDependencyData:
  AssetInfo  : Dictionary<GUID, AssetLoadInfo>
  AssetUsage : Dictionary<GUID, BuildUsageTagSet>
  SceneInfo  : Dictionary<GUID, SceneDependencyInfo>
  SceneUsage : Dictionary<GUID, BuildUsageTagSet>
  DependencyHash : Dictionary<GUID, Hash128>
  GlobalUsage : BuildUsageTagGlobal
  DependencyUsageCache : ...

AssetLoadInfo:
  GUID asset; string address;
  List<ObjectIdentifier> includedObjects;   // 该 asset 自己的对象
  List<ObjectIdentifier> referencedObjects; // 它引用但不包含的对象

SceneDependencyInfo:
  GUID scene; scene 路径/信息;
  List<ObjectIdentifier> referencedObjects;
  HashSet<Type> includedTypes;             // 2020.1+
  BuildUsageTagGlobal globalUsage;
```

> 依赖数据结构（`ObjectIdentifier` 的 GUID/fileID/filePath/FileType、included vs referenced、prefabA 完整案例、调试反查）已拆到子文档：**[Dependency-Data-Structures.md](Dependency-Data-Structures.md)**。
>
> 速记：
> - `includedObjects` = 数据序列化在本资产文件里的对象（prefab 的根对象 + 组件实例）；
> - `referencedObjects` = 只持有指针、数据在别的文件的对象（Mesh / Material / Shader / Script...）；
> - `guid` 定位资产文件，`localIdentifierInFile` 定位文件内对象，`filePath` 只对 AssetDatabase 不认识的对象（NonAssetType）有意义。

### 4.3 装箱单

```csharp
IWriteData:
  Dictionary<GUID, List<string>> AssetToFiles;     // 第一个是主文件
  Dictionary<string, List<ObjectIdentifier>> FileToObjects;
  List<IWriteOperation> WriteOperations;

IBundleWriteData : IWriteData
  Dictionary<string, string> FileToBundle;
  Dictionary<string, BuildUsageTagSet> FileToUsageSet;
  Dictionary<string, BuildReferenceMap> FileToReferenceMap;
```

> `AssetToFiles` / `FileToBundle` / `FileToObjects` 的实测注解（file/bundle/SerializedFile 概念、内部文件名生成、CubeA 为什么有 2 个文件、各阶段怎么消费）见独立子文档 **[Bundle-Write-Data.md](Bundle-Write-Data.md)**。

### 4.4 写命令

```csharp
IWriteOperation:
  WriteCommand Command;            // internalName/fileName + serializeObjects[]
  BuildUsageTagSet UsageSet;
  BuildReferenceMap ReferenceMap;
  Hash128 DependencyHash;
  WriteResult Write(outputFolder, settings, globalUsage);
  Hash128 GetHash128(IBuildLogger log);

WriteCommand:
  string internalName;             // "archive:/CAB-xxx/CAB-xxx"
  string fileName;                 // Path.GetFileName(internalName)
  List<SerializationInfo> serializeObjects;

SerializationInfo:
  ObjectIdentifier serializationObject;
  long serializationIndex;         // 由 IDeterministicIdentifiers 生成
```

### 4.5 结果

```csharp
IBuildResults:
  ScriptCompilationResult ScriptResults;
  Dictionary<string, WriteResult> WriteResults;
  Dictionary<string, SerializedFileMetaData> WriteResultsMetaData;
  Dictionary<GUID, AssetResultData> AssetResults;

WriteResult:
  List<ResourceFile> resourceFiles;
  List<ObjectSerializedInfo> serializedObjects;
  HashSet<Type> includedTypes;

SerializedFileMetaData:
  Hash128 RawFileHash;    // 整文件（含 header）
  Hash128 ContentHash;    // 跳过 header（跨 Unity 版本稳定）

IBundleBuildResults : IBuildResults
  Dictionary<string, BundleDetails> BundleInfos;

BundleDetails:
  string FileName; uint Crc; Hash128 Hash; string[] Dependencies;
```

---

## 5. 对照你们“高度魔改 SBP + 自定义 DB”的阅读方法

> 背景：你们把 SBP 魔改成 SSBP（`QtsBuildPipeline.cs` + `QtsPackedIdentifiers`），产物单位从 **AssetBundle（bundle）** 变成 **pack ID**，写入阶段走自定义 DB。
> 下面是一个“怎么读、怎么替换”的框架，具体以你们代码为准。

### 5.1 先确认你们改的是哪一层

SBP 的耦合点不是任务类，而是 **context object 的接口契约**。按改造代价从低到高：

| 层级 | 替换对象 | 影响 |
|---|---|---|
| A. 参数/内容 | `IBundleBuildParameters`、`IBundleBuildContent` 自定义实现 | 影响全局，慎用 |
| B. ID 生成 | 自定义 `IDeterministicIdentifiers`（如 `QtsPackedIdentifiers`） | 影响产物布局/缓存 key |
| C. 写命令 | 自定义 `IWriteOperation`（`Write` / `GetHash128`） | **最自然的 DB 接入点**，保留 WriteSerializedFiles 的调度/缓存 |
| D. 装箱/归档 | 替换 `GenerateBundlePacking` / `GenerateBundleCommands` / `ArchiveAndCompressBundles` | 产物形态大变，任务链需要重排 |
| E. 骨架 | `BuildContext` / `ContextInjector` / `BuildTasksRunner` | 一般不动 |

你们“19 个任务：10 保留、6 删除、3 替换 + 7 新增”的画像，大概率是 **B + C + D** 的组合：

- Setup / Scripts / Dependency / Callback 类任务基本保留（数据源仍是 Unity 对象）；
- Packing/Writing 中与 AssetBundle 强绑定的任务被替换/删除；
- 新增 7 个任务处理 pack ID、DB 写入、索引/清单。

### 5.2 一份对照检查清单（读你们 `QtsBuildPipeline.cs` 时逐条确认）

1. **pack unit 定义在哪一层？** 是替换了 `IBundleBuildContent.BundleLayout`（bundle 名→pack ID），还是替换了 `GenerateBundlePacking` 的布局算法？
2. **`QtsPackedIdentifiers` 实现了什么？** 是 `IDeterministicIdentifiers` 吗？它生成的是 localID、pack ID 还是文件 ID？输入是 GUID/路径/对象？
3. **自定义 DB 的写入发生在哪个任务？** 是自定义 `IWriteOperation.Write`（保留 WriteSerializedFiles），还是新增 Task 替换 `WriteSerializedFiles`/`ArchiveAndCompressBundles`？
4. **序列化数据从哪来？** 仍是 `ContentBuildInterface.WriteSerializedFile`，还是完全自定义序列化？前者 TypeDB 关键，后者 TypeDB 可弱化。
5. **产物 hash 的语义是什么？** 对齐 `BundleDetails.Hash`（ContentHash + deps）还是自定义 DB 的 hash？缓存 key 是否覆盖 DB 格式版本？
6. **缓存 key 是否包含 DB writer 版本？** 自定义 `IWriteOperation.GetHash128` 是否把 DB schema/writer 版本算进去？
7. **依赖关系怎么表达？** 保留 `BundleDetails.Dependencies` 还是 DB 内部索引？
8. **SubAsset / SpriteAtlas 语义保留了吗？** `GenerateSubAssetPathMaps`/`StripUnusedSpriteSources` 被删后，这些规则移到哪？
9. **回调接口是否保留？** 如果删掉 `PostXxxCallback` 任务，团队是否还有等价的扩展点？
10. **BuildContext 里新增了哪些对象？** 7 个新任务是否注册了新的 `IContextObject`？注入类型是否稳定？
11. **构建指纹**：`QtsPackedIdentifiers` 的 seed/version、DB schema 版本、writer 版本是否进入缓存 key 与产物 hash？
12. **平台/回调版本**：`SwitchToBuildPlatform`、shader/scene 回调版本 hash、sprite atlas 重打是否保留？这些影响“产物一致性”。

### 5.3 建议的阅读顺序（结合你们的画像）

```text
DefaultBuildTasks.cs            ← 看 19 个任务与预设
ContentPipeline.cs              ← 看上下文注册、缓存、日志、ID 选择
BuildTasksRunner.cs             ← Inject → Run → Extract
BuildContext.cs + ContextInjector.cs
BuildWriteData.cs               ← 装箱单字段原型
WriteSerializedFiles.cs         ← 唯一复杂保留任务（调度 + 缓存 + hash）
-------------------------------- 对照分界 ---------------------------------
你们的 QtsBuildPipeline.cs      ← 找：任务列表差异、替换点、新增 context 对象
QtsPackedIdentifiers            ← 对照 IDeterministicIdentifiers
自定义 IWriteOperation / DB writer ← 对照 AssetBundleWriteOperation
ArchiveAndCompress 等价物        ← 对照 BundleDetails / hash / 清单
```

> 经验：**不要试图把 SBP 的 19 个任务逐个翻译成你们的 19 个**。先按“数据形态”（§1.4）对齐，再看每个阶段谁负责产出那一层数据。这样能快速判断“哪个任务对应哪个”。

---

## 6. 从 Ex18 里能直接观察到的结论

1. **内容布局从 start 就在**：`BundleBuildContent` 是输入，不是任务生成的；后续所有任务只读它。
2. **依赖数据只在 `CalculateAssetDependencyData` 之后出现**：`assets=4/assetObj=16/8`。之前全是空。
3. **装箱单只在 `GenerateBundlePacking` 之后出现**：`assetToFiles=4/files=3/bundles=3`。
4. **写命令只在 `GenerateBundleCommands` 之后出现**：3 个 op，按 bundle 分别 10/5/3 个对象。
5. **`WriteSerializedFiles` 先产出写盘结果（writeResults/writeMeta），`ArchiveAndCompressBundles` 才产出 `BundleInfos`**——两者不要混。
6. **条件任务在本例会 no-op**：`AppendBundleHash`（AppendHash=false）、`GenerateLinkXml`（WriteLinkXML=false）、`StripUnusedSpriteSources`（无图集）、`GenerateSubAssetPathMaps`（无 SubAsset）。
7. **ctx 32→33 的唯一原因**是 `CalculateCustomDependencyData` 注册了 `ICustomAssets`（即使为空）。
8. **`BuildPlayerScripts` 的 `scriptTypeDB=true`** 是后续序列化/缓存的关键前提。

---

## 7. 常见坑

1. **把“任务顺序”当可随意调整**：任务之间靠 context 契约，顺序错了要么 Validate 失败，要么结果错（例如加密必须放 Writing 之后）。
2. **注入字段写成 public**：`ContextInjector` 只扫描 `NonPublic` 字段，public 不会被注入。
3. **自定义 context 用 class 类型注入**：`InOut` 写回时 `BuildContext.SetContextObject(Type, ...)` 要求接口类型（主 Wiki 坑 #11）。
4. **把 `IBundleBuildResults` 当成在写盘前就有数据**：`BundleInfos` 在 `ArchiveAndCompressBundles` 之后才完整。
5. **替换 `WriteSerializedFiles` 而不是 `IWriteOperation`**：会丢掉缓存、hash、slim、回调等一整套机制。
6. **忘了条件任务**：`AppendBundleHash` / `GenerateLinkXml` 默认不执行，验证时需要显式开参数。
7. **探针任务进了生产**：Ex18 只是学习工具，会出现在 trace 里并增加一个空任务。
8. **改任务 Version 没同步缓存**：自定义任务如果参与缓存，逻辑改了要 `Version++`。
9. **用 GUID 当对象唯一键**：GUID 只到资产级；同一 prefab 的 5 个对象 GUID 相同，必须配 `localIdentifierInFile`。
10. **把 filePath 为空当成非法数据**：`MetaAssetType` / `SerializedAssetType` 的 `filePath` 经常为空，这是正常的，路径用 `GUIDToAssetPath` 反查。
11. **把内置伪 GUID 当项目资产**：`e000...` / `f000...` 是内置资源文件，`GUIDToAssetPath` 查不到路径；它们靠 `filePath` 识别。

---

## 8. 延伸阅读

- 主 lab Wiki：`_e2e/UnityGradleDemo/SBP-Lab-Wiki.md`
  - 案例 02（ContentPipeline 显式布局）、05/06（自定义任务/上下文）、12（布局改写）、13（多配置）、15（Preset）、17（确定性）、18（本 Wiki 的探针）
- 依赖数据结构子文档：**[Dependency-Data-Structures.md](Dependency-Data-Structures.md)**
  - `ObjectIdentifier` 的 GUID/fileID/filePath、FileType 四个取值、included vs referenced、prefabA 完整案例、调试反查、FAQ、**native vs C# 依赖计算边界**、**TypeDB 的作用**
- 装箱/写盘数据结构子文档：**[Bundle-Write-Data.md](Bundle-Write-Data.md)**
  - `IBundleWriteData`（AssetToFiles / FileToBundle / FileToObjects / WriteOperations）、file/bundle/SerializedFile 概念、内部文件名、单对象实例走查
- 源码级专题：
  - `_e2e/UnityGradleDemo/Docs/SBP-Determinism.md`（ID/排序/hash/构建指纹）
  - `_e2e/UnityGradleDemo/Docs/SBP-BuildCache.md`（CacheEntry/存储/任务 key）
  - `_e2e/UnityGradleDemo/Docs/SBP-Cache-Invalidation.md`（缓存失效图谱：哪些改动会让 hash / CacheEntry 失效）
- 源码索引：
  - 任务：`Editor/Tasks/*.cs`
  - 上下文：`Editor/Interfaces/*.cs`、`Editor/Shared/BuildContext.cs`
  - 注入：`Editor/Injector/ContextInjector.cs`
  - 写操作：`Editor/WriteTypes/*.cs`
  - 缓存：`Editor/Utilities/BuildCache.cs`、`TaskCachingUtility.cs`
  - 偏好：`Editor/Utilities/Preferences.cs`
- 本 Wiki 捕获数据：
  - `captured/ex18-stage-probe.json`（20 个边界完整快照）
  - `captured/ex18-stage-summary.txt`（人读版）
  - `captured/ex18-run-log.txt`（运行日志）
  - `captured/ex19-filetypes.txt`（依赖对象 FileType 实测：prefab 内部对象、内置 Mesh/Shader）
  - `assets/SbpLabEx18_StageProbe.cs`（探针源码，可移植到你们工程）

---

*最后更新：2026-11-09 · SBP 1.23.4 · Unity 2022.3 · 实验工程：`_e2e/UnityGradleDemo`*
