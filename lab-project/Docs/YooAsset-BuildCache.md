# YooAsset 3.0.7 上层构建缓存与加速详解

> 版本：YooAsset **3.0.7**（embedded package）
> 安装位置：`_e2e/UnityGradleDemo/Packages/com.tuyoogame.yooasset`
> 对照：SBP 1.23.4 · Unity 2022.3.17f1c1
>
> 相关文档：
> - [SBP-BuildCache.md](SBP-BuildCache.md) —— SBP 自身 BuildCache 原理
> - [YooAsset-vs-SBP.md](YooAsset-vs-SBP.md) —— YooAsset 与 SBP 的管线对比
> - [SBP-Cache-Invalidation.md](SBP-Cache-Invalidation.md) —— SBP 缓存失效图谱

---

## 0. 结论摘要

YooAsset 的“构建加速”不是一个单独的 bundle 缓存，而是**两层互补**：

| 层 | 负责 | 缓存对象 | 是否 YooAsset 自研 |
|---|---|---|---|
| **SBP BuildCache** | SBP 内部任务（依赖计算、写 SerializedFile、归档压缩） | `CacheEntry` / `CachedInfo` / artifact 文件 | ❌ 直接复用 SBP |
| **AssetDependencyDatabase** | YooAsset collector 的依赖收集 | `assetPath → 依赖 hash + 直接依赖 GUID[]` | ✅ YooAsset 自研 |
| 外层任务（manifest/package/encryption/catalog） | YooAsset 管线收尾 | **不缓存**，每次重建 | — |

一句话：

> **SBP BuildCache 缓存“打包任务产物”；YooAsset 的 `AssetDependencyDB` 缓存“资源依赖图”。两者覆盖的阶段不同，不是重复实现。**

---

## 1. YooAsset 的构建管线分层

`ScriptableBuildPipeline.Run()` → `BundleBuilder.Run()` 执行 11 个 YooAsset 任务：

```text
TaskPrepare_SBP              ← 清 SBP BuildCache（可选）、准备输出目录
TaskGetBuildMap_SBP          ← collector + 依赖收集 + build map
TaskBuilding_SBP             ← 进入 SBP：ContentPipeline.BuildAssetBundles
TaskVerifyBuildResult_SBP
TaskEncryption_SBP           ← 加密 bundle
TaskUpdateBundleInfo_SBP     ← 读取 SBP BundleDetails.Hash/Crc，算文件 hash
TaskCreateManifest_SBP
TaskCreateReport_SBP
TaskCreatePackage_SBP        ← 生成补丁包目录
TaskCopyBundledFiles_SBP
TaskCreateCatalog_SBP
```

缓存相关的点只有两个：

1. `TaskPrepare_SBP`：清 SBP `BuildCache`；
2. `TaskGetBuildMap_SBP` → collector：用 `AssetDependencyCache`。

其余任务（manifest/package/encryption/catalog）**没有缓存**，每次构建都会跑。

---

## 2. 第一层：SBP BuildCache（YooAsset 直接复用）

### 2.1 YooAsset 只做两件事

`ScriptableBuildParameters.GetBundleBuildParameters()`：

```csharp
buildParams.UseCache = true;              // 打开 SBP BuildCache
buildParams.CacheServerHost = CacheServerHost;
buildParams.CacheServerPort = CacheServerPort;
```

`TaskPrepare_SBP`：

```csharp
if (buildParameters.ClearBuildCacheFiles)
{
    UnityEditor.Build.Pipeline.Utilities.BuildCache.PurgeCache(false);
    DeletePackageRootDirectory(buildParameters);
}
```

- 不勾 Clear Build Cache → SBP cache 正常命中；
- 勾选 → `PurgeCache(false)` 清掉 `Library/BuildCache`，并删 YooAsset 输出目录。

YooAsset **没有**自己的 `CacheEntry` / bundle cache / cache server；SBP 之外也没有再包一层 bundle 缓存。

### 2.2 SBP CacheEntry / CachedInfo（被复用的数据结构）

```csharp
[Serializable]
public class CachedInfo
{
    public CacheEntry Asset;          // 自己的 entry（历史 key）
    public CacheEntry[] Dependencies; // 直接依赖 entry
    public object[] Data;             // 任务自定义数据
}

public struct CacheEntry
{
    public Hash128 Hash;      // 所有输入 hash
    public GUID Guid;         // 任务/文件/资产标识
    public int Version;       // 任务版本
    public EntryType Type;    // Asset / File / Data / ScriptType
    public string File;
    public string ScriptType;
}
```

- 存储：`Library/BuildCache/<guid前2位>/<guid>/<hash(GlobalHash, Entry.Hash)>/`
  - `<guid>.info`：USerialize 序列化的 `CachedInfo`
  - artifact 文件：任务产物
- `GlobalHash = hash(Application.unityVersion, BuildCache.k_Version)` → 跨 Unity 版本自动隔离；
- 校验：`LoadCachedData` + `HasAssetOrDependencyChanged`（非递归，只看自己 + 直接依赖）。

> 细节见 [SBP-BuildCache.md](SBP-BuildCache.md)。

### 2.3 YooAsset 没有改 SBP cache 的结构

- `SBPBuildTasks.Create()` 只是任务列表，不碰 `CacheEntry`；
- `TaskBuilding_SBP` 调 `ContentPipeline.BuildAssetBundles` 时也没有传自定义 `IBuildCache`；
- 所以 SBP cache 的 key/失效完全由 SBP 任务决定，YooAsset 只是消费者。

---

## 3. 第二层：AssetDependencyDatabase（YooAsset 上层加速）

### 3.1 它是干什么的

YooAsset 的 collector 在生成 build map 时，需要知道“每个资源依赖了哪些资源”。默认会频繁调用：

```csharp
AssetDatabase.GetDependencies(assetPath, recursive: true);
```

`AssetDependencyDatabase` 把这部分结果持久化：

```text
Library/AssetDependencyDB
assetPath → { DependHash, 直接依赖 GUID[] }
```

下次构建直接读 DB，只对“变化的 asset”重新查依赖。

### 3.2 开关与生命周期

`BuildParameters`：

```csharp
public bool ClearBuildCacheFiles { get; set; } = false;
public bool UseAssetDependencyDB { get; set; } = false;  // 默认关闭
```

> 代码注释：*“开启此项可以极大提高资源收集速度”*

`AssetDependencyCache`（collector 侧封装）：

```csharp
public class AssetDependencyCache
{
    private readonly AssetDependencyDatabase _database;

    public AssetDependencyCache(bool useAssetDependencyDB)
    {
        string databaseFilePath = "Library/AssetDependencyDB";
        _database = new AssetDependencyDatabase();
        _database.CreateDatabase(useAssetDependencyDB, databaseFilePath);

        if (useAssetDependencyDB)
            _database.SaveDatabase();
    }

    public string[] GetDependencies(string assetPath, bool recursive = true)
    {
        return _database.GetDependencies(assetPath, recursive);
    }
}
```

`CollectCommand` 里懒加载：

```csharp
public AssetDependencyCache AssetDependency
{
    get
    {
        if (_assetDependency == null)
            _assetDependency = new AssetDependencyCache(UseAssetDependencyDB);
        return _assetDependency;
    }
}
```

`BundleCollector.GetAllDependencies()`：

```csharp
string[] depends = command.AssetDependency.GetDependencies(mainAssetPath, true);
```

### 3.3 一个容易误会的点：开关控制的是“持久化”，不是“是否使用 DB”

`AssetDependencyDatabase.CreateDatabase(readCacheDatabaseFile, path)` 的逻辑是：

1. 如果 `readCacheDatabaseFile && File.Exists` → 从磁盘读 DB；
2. **无论读不读，都会遍历 `AssetDatabase.GetAllAssetPaths()` 构建/更新内存 DB**；
3. 之后只有 `useAssetDependencyDB` 为 true 时，`AssetDependencyCache` 才 `SaveDatabase()`。

也就是说：

| `UseAssetDependencyDB` | 行为 |
|---|---|
| `false` | 每次构建**在内存里重新建一遍**依赖 DB，不读/不写磁盘 |
| `true` | 从磁盘读 DB → 只更新变化的 asset → 写回磁盘 |

所以开这个开关的意义是 **跨构建复用依赖图**，而不是“从 AssetDatabase 改成 DB”。

### 3.4 数据结构

`AssetDependencyDatabase` 内部：

```csharp
private const string FILE_VERSION = "1.0";

private class DependencyInfo
{
    // AssetDatabase.GetAssetDependencyHash(assetPath).ToString()
    public string DependHash;

    // AssetDatabase.GetDependencies(assetPath, false) 的 GUID
    public List<string> DependGUIDs = new List<string>();
}

private readonly Dictionary<string, DependencyInfo> _database
    = new Dictionary<string, DependencyInfo>(100000);
```

注意：

- **只存直接依赖**（`recursive: false`）；
- 递归关系不落盘，查询时由 DB 里逐层 `CollectDependencies` 还原；
- key 是 **assetPath**，不是 GUID；
- `DependHash` 用来判断该 asset 是否需要重新收集依赖。

### 3.5 磁盘格式（`Library/AssetDependencyDB`）

`SaveDatabase()` 用 `BinaryWriter` 写：

```text
string  FILE_VERSION          // "1.0"
int32   count
repeat count:
    string assetPath
    string DependHash
    int32  dependGUIDCount
    repeat dependGUIDCount:
        string guid
```

- 简单二进制格式，无压缩；
- 文件大小 ≈ 所有 asset 路径 + 直接依赖 GUID 字符串；
- 100k 容量只是 Dictionary 初始 capacity，不代表实际数量。

### 3.6 `CreateDatabase` 伪代码

```csharp
CreateDatabase(readCacheDatabaseFile, databaseFilePath):
    _database.Clear()
    needClearDatabase = false

    if readCacheDatabaseFile and File.Exists(databaseFilePath):
        try:
            read FILE_VERSION
            if FILE_VERSION != "1.0": throw
            read count
            for i in 0..count-1:
                assetPath = ReadString()
                dependHash = ReadString()
                dependGUIDs = ReadStringList()
                _database[assetPath] = DependencyInfo(dependHash, dependGUIDs)

            // 删除已经不存在的 asset
            foreach assetPath in _database:
                if AssetDatabase.AssetPathToGUID(assetPath) == "":
                    removeList.Add(assetPath)
            remove invalid
        catch:
            LogError
            needClearDatabase = true

    if needClearDatabase:
        ClearDatabase(deleteDatabaseFile: true)

    // 关键：无论是否读缓存，都会遍历所有 asset
    foreach assetPath in AssetDatabase.GetAllAssetPaths():
        if _database.TryGetValue(assetPath, out info):
            currentHash = AssetDatabase.GetAssetDependencyHash(assetPath).ToString()
            if currentHash != info.DependHash:
                _database[assetPath] = CreateDependencyInfo(assetPath)
        else:
            _database[assetPath] = CreateDependencyInfo(assetPath)
```

`CreateDependencyInfo`：

```csharp
CreateDependencyInfo(assetPath):
    dependHash = AssetDatabase.GetAssetDependencyHash(assetPath)
    dependAssetPaths = AssetDatabase.GetDependencies(assetPath, false)
    dependGUIDs = []
    foreach dependAssetPath:
        guid = AssetDatabase.AssetPathToGUID(dependAssetPath)
        if guid != "":
            dependGUIDs.Add(guid)
    return DependencyInfo(dependHash, dependGUIDs)
```

### 3.7 `GetDependencies` 伪代码

```csharp
GetDependencies(assetPath, recursive):
    if assetPath not in _database:
        throw InvalidOperationException

    result = HashSet<string>()
    if recursive:
        result.Add(assetPath)   // 注意：递归时结果包含主资源

    CollectDependencies(parent=assetPath, assetPath, result, recursive)
    return result.ToArray()

CollectDependencies(parent, assetPath, result, recursive):
    if assetPath not in _database:
        guid = AssetDatabase.AssetPathToGUID(assetPath)
        if guid == "":
            LogWarning("Asset is missing")
            return
        else:
            throw InvalidOperationException

    info = _database[assetPath]
    foreach guid in info.DependGUIDs:
        dependAssetPath = AssetDatabase.GUIDToAssetPath(guid)
        if dependAssetPath == "": continue
        if AssetDatabase.IsValidFolder(dependAssetPath): continue
        if result.Contains(dependAssetPath): continue

        result.Add(dependAssetPath)
        if recursive:
            CollectDependencies(assetPath, dependAssetPath, result, recursive)
```

### 3.8 成本模型

设资产数 = N，平均直接依赖数 = D。

| 阶段 | `UseAssetDependencyDB=false` | `UseAssetDependencyDB=true` |
|---|---|---|
| 首次构建 | O(N) hash + O(N) `GetDependencies` | O(N) hash + O(N) `GetDependencies` + 读/写 DB |
| 后续构建 | O(N) hash + O(N) `GetDependencies` | O(N) hash + O(变化资产) `GetDependencies` + 读/写 DB |
| 单次查询 | O(递归展开) | O(递归展开)，但依赖来自内存/磁盘 DB |
| 内存 | N 个 asset 的依赖信息 | 同左 |
| 磁盘 | 无 | `Library/AssetDependencyDB` |

关键点：

- **O(N) 的 `GetAllAssetPaths` + `GetAssetDependencyHash` 仍然每构建都跑**；省掉的是“未变化 asset 的 `GetDependencies` 递归查询”；
- 资产越多、依赖越深，收益越明显；小项目可能感知不到。

---

## 4. 一次构建中的缓存时序

```text
BundleBuilder.Build
│
├─ TaskPrepare_SBP
│    ├─ if ClearBuildCacheFiles:
│    │     BuildCache.PurgeCache(false)      ← 清 SBP cache
│    │     DeletePackageRootDirectory()
│    └─ PrepareOutputDirectory()
│
├─ TaskGetBuildMap_SBP
│    └─ BundleCollector
│         └─ AssetDependencyCache
│              ├─ CreateDatabase()
│              │    ├─ load Library/AssetDependencyDB (if enabled)
│              │    └─ update changed assets / invalid assets
│              ├─ GetDependencies(asset, recursive)
│              └─ SaveDatabase() (if enabled)
│
├─ TaskBuilding_SBP
│    └─ ContentPipeline.BuildAssetBundles
│         ├─ LoadCachedData(entries)        ← SBP cache 查询
│         ├─ miss: ProcessAsset / Write / Archive
│         └─ SaveCachedData(infos)         ← 只存 miss 的
│
└─ 外层任务
     ├─ Verify
     ├─ Encryption
     ├─ UpdateBundleInfo（读 SBP BundleDetails.Hash/Crc）
     ├─ CreateManifest
     ├─ CreateReport
     ├─ CreatePackage
     ├─ CopyBundledFiles
     └─ CreateCatalog
```

---

## 5. 两层缓存对比

| 维度 | SBP BuildCache | YooAsset AssetDependencyDB |
|---|---|---|
| 缓存对象 | 任务产物 + `CachedInfo` | 资源依赖图（assetPath → hash + GUID[]） |
| 存储 | `Library/BuildCache` | `Library/AssetDependencyDB` |
| key | `CacheEntry.Guid/Version/Hash` | assetPath |
| 失效判断 | key + 直接依赖校验 | `AssetDatabase.GetAssetDependencyHash` 快照比较 |
| 覆盖阶段 | SBP 内部任务 | YooAsset collector |
| 是否跨 Unity 版本 | 自动隔离（GlobalHash） | 不隔离，但 hash 会驱动更新 |
| 谁维护 | SBP（C#） | YooAsset（C#） |
| Clear Build Cache | `BuildCache.PurgeCache(false)` | **不会删** DB（需要手动删文件） |

> 特别注意：YooAsset 的 Clear Build Cache **不清 `Library/AssetDependencyDB`**；如果依赖 DB 本身脏了，需要手动删这个文件让它重建。

---

## 6. 有必要吗？什么情况下值得开

| 项目情况 | 建议 |
|---|---|
| 资产少（几千以内）、依赖简单 | 默认关闭即可；收益有限 |
| 资产多（几万+）、依赖深、collector 耗时明显 | 建议开启 `UseAssetDependencyDB`，先 profile 对比 |
| 每次构建的瓶颈在 SBP 序列化/归档 | 不需要依赖 DB；SBP BuildCache 已经在处理 |
| 构建瓶颈在 collector / build map | 依赖 DB 是对症的 |
| 需要 CI 可复现 / 干净构建 | 默认关闭或每次清 DB；开启会引入跨构建状态 |

建议的验证方式：

1. 记录一次完整构建的 profile；
2. 分别跑 `UseAssetDependencyDB=false/true` 两次，对比 **TaskGetBuildMap_SBP** 阶段耗时；
3. 再对比 SBP `TaskBuilding_SBP` 的耗时（这个受 SBP BuildCache 影响，和依赖 DB 无关）；
4. 检查 `Library/AssetDependencyDB` 大小和内存占用。

---

## 7. 对你们自研管线的参考

YooAsset 的分层值得借鉴：

1. **不要重复造 SBP BuildCache**：它已经覆盖序列化/归档；
2. **只缓存 SBP 覆盖不到的前后阶段**：YooAsset 选择了 collector 依赖图；
3. **缓存 key 用引擎已有的稳定指纹**：YooAsset 用 `AssetDatabase.GetAssetDependencyHash`，不自己算文件 hash；
4. **区分“持久化开关”和“算法使用”**：YooAsset 的 DB 算法始终在用，开关只控制磁盘复用；
5. **明确缓存不覆盖什么**：manifest/package/encryption/catalog 每次重建。

对你们“细粒度 bundle + 全量构建 + 历史仓库 diff”的流程：

- 如果继续用 YooAsset：SBP cache + `UseAssetDependencyDB` 可以组合使用；
- 如果自研管线：可以只借鉴第 2、3 点，不需要引入 YooAsset。

---

## 8. 源码索引

| 主题 | 文件/方法 |
|---|---|
| YooAsset 管线入口 | `Editor/BundleBuilder/BundleBuilder.cs` · `Run()` |
| 11 个外层任务 | `Editor/BundleBuilder/BuildPipeline/ScriptableBuildPipeline/ScriptableBuildPipeline.cs` |
| SBP 任务列表 | `.../ScriptableBuildTasks.cs` · `SBPBuildTasks.Create()` |
| SBP 参数映射 | `.../ScriptableBuildParameters.cs` · `GetBundleBuildParameters()` |
| Clear Build Cache | `.../BuildTasks/TaskPrepare_SBP.cs` |
| build map / collector | `.../BuildTasks/TaskGetBuildMap_SBP.cs` → `TaskGetBuildMap.cs` |
| 依赖 DB 封装 | `Editor/BundleCollector/AssetDependencyCache.cs` |
| 依赖 DB 实现 | `Editor/BundleCollector/AssetDependencyDatabase.cs` |
| 依赖 DB 开关 | `Editor/BundleBuilder/BuildParameters.cs` · `UseAssetDependencyDB` |
| collector 查询 | `Editor/BundleCollector/BundleCollector.cs` · `GetAllDependencies()` |
| SBP CacheEntry | `Library/PackageCache/com.unity.scriptablebuildpipeline@1.23.4/Editor/Utilities/CacheEntry.cs` |
| SBP BuildCache | `.../Editor/Utilities/BuildCache.cs` |
| SBP 任务缓存框架 | `.../Editor/Utilities/TaskCachingUtility.cs` |

---

## 9. 一句话总结

> **SBP BuildCache 缓存“打包任务”；YooAsset 的 `AssetDependencyDatabase` 缓存“依赖图”。YooAsset 没有重复实现 BuildCache，它只是把 collector 阶段单独加速，且这个加速是可选的、默认关闭的。**

---

*最后更新：2026-11-14 · YooAsset 3.0.7 · Unity 2022.3.17f1c1 · SBP 1.23.4*
