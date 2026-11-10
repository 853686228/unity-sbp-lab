# 子 Wiki：SBP 构建缓存（Build Cache）

> 对应主 Wiki：[SBP-Lab-Wiki.md](../SBP-Lab-Wiki.md) · 实验案例：`SbpLabEx08_CacheExperiment.cs`（Ex08）、`SbpLabEx16_HeavyCompression.cs`（Ex16）、`SbpLabEx17_Determinism.cs`（Ex17）
> 源码版本：`com.unity.scriptablebuildpipeline` 1.23.4（Unity 2022.3）
> 源码路径前缀：`Library/PackageCache/com.unity.scriptablebuildpipeline@1.23.4/`

---

## 0. 先给结论

| 问题 | 结论 |
|---|---|
| 缓存什么 | **任务产物（artifact）+ 任务的结构化结果（`CachedInfo.Data`）** |
| 存在哪 | `Library/BuildCache/`，按 `Entry.Guid` + `hash(GlobalHash, Entry.Hash)` 分目录 |
| 怎么判断命中 | `CacheEntry` 的 `Guid/Version/Hash` 完全一致，且所有依赖的 entry 也没变化 |
| 命中后做什么 | 跳过任务计算：直接取缓存 Data（如 WriteResult）或拷贝缓存 artifact（如 .bundle 文件） |
| 什么时候失效 | 任务 `Version` 变、任一输入 hash 变、依赖变、设置变、Unity 版本变、手动 Purge/Prune |
| 能跨机器共享吗 | 不能：Cache Server 已废弃（只打 warning），缓存是本地 `Library/` 目录 |

> 完整失效 case 图谱（全局 / Guid / Version / Hash / 依赖校验）见子文档：**[SBP-Cache-Invalidation.md](SBP-Cache-Invalidation.md)**。

---

## 1. 源码地图

| 主题 | 文件 | 关键符号 |
|---|---|---|
| 缓存接口 | `Editor/Interfaces/IBuildCache.cs` | `GetCacheEntry` / `LoadCachedData` / `SaveCachedData` |
| 缓存主键 | `Editor/Utilities/CacheEntry.cs` | `CacheEntry`、`CachedInfo` |
| 缓存存储 | `Editor/Utilities/BuildCache.cs` | `k_CachePath`、`GetCachedInfoFile`、`GetCachedArtifactsDirectory`、`LoadCachedData`、`SaveCachedData`、`PurgeCache`、`PruneCache` |
| 资产 hash 规则 | `Editor/Utilities/BuildCacheUtility.cs` | `GetCacheEntry(GUID/path/Type)`、`AutoBuildCacheUtility` |
| 任务缓存框架 | `Editor/Utilities/TaskCachingUtility.cs` | `RunCachedOperation`、`IRunCachedCallbacks<T>` |
| 依赖缓存 | `Editor/Tasks/CalculateAssetDependencyData.cs` | `GetCacheEntry`、`GetCachedInfo`、`ExtractCommonCacheData` |
| 场景依赖缓存 | `Editor/Tasks/CalculateSceneDependencyData.cs` | prefab entries、`prefabDependency` hash |
| 写盘缓存 | `Editor/Tasks/WriteSerializedFiles.cs` | `GetCacheEntry`、`ProcessUncached/ProcessCached`、`slimWriteResults` |
| 归档压缩缓存 | `Editor/Tasks/ArchiveAndCompressBundles.cs` | `GetCacheEntry`、`CachedArtifactPath`、`CopyFileWithTimestampIfDifferent` |
| 线程队列 | `Editor/Utilities/ThreadingManager.cs` | `SaveQueue` / `UploadQueue` / `PruneQueue` |
| 设置 | `Editor/Utilities/Preferences.cs` | `logCacheMiss`、`maximumCacheSize`、`useV2Hasher`、`slimWriteResults` |

---

## 2. 两层缓存结构

SBP 缓存分两层来看最清楚：

```
┌─────────────────────────────────────────────────────────┐
│ 任务层（每个 Task 自己决定）                             │
│   CacheEntry = { Guid, Version, Hash }                   │
│   CachedInfo = { Asset, Dependencies[], Data[] }          │
├─────────────────────────────────────────────────────────┤
│ 存储层（BuildCache）                                     │
│   Library/BuildCache/<guid前2位>/<guid>/<finalHash>/     │
│      ├─ <guid>.info      ← 序列化的 CachedInfo           │
│      └─ (artifact 文件)  ← 任务产物                      │
└─────────────────────────────────────────────────────────┘
```

- **任务层**决定“key 怎么算、未命中算什么、命中怎么还原”；
- **存储层**决定“放哪、怎么序列化、怎么校验、怎么清理”。

---

## 3. 核心数据结构

`Editor/Utilities/CacheEntry.cs`：

```csharp
[Serializable]
public class CachedInfo
{
    public CacheEntry Asset { get; set; }       // 自己的 entry
    public CacheEntry[] Dependencies { get; set; } // 依赖的 entry
    public object[] Data { get; set; }          // 任务自定义的结构化结果
}

public struct CacheEntry
{
    public Hash128 Hash { get; internal set; }   // 所有输入的 hash
    public GUID Guid { get; internal set; }      // 任务/文件标识
    public int Version { get; internal set; }    // 任务 Version
    public EntryType Type { get; internal set; } // Asset / File / Data / ScriptType
    public string File { get; internal set; }
    public string ScriptType { get; internal set; }

    public bool IsValid() => Hash.isValid && !Guid.Empty();
}
```

`IBuildCache`（`Editor/Interfaces/IBuildCache.cs`）对外暴露：

```csharp
CacheEntry GetCacheEntry(GUID asset, int version = 1);
CacheEntry GetCacheEntry(string path, int version = 1);
CacheEntry GetCacheEntry(ObjectIdentifier objectID, int version = 1);
CacheEntry GetCacheEntry(Type type, int version = 1);
bool HasAssetOrDependencyChanged(CachedInfo info);
string GetCachedInfoFile(CacheEntry entry);
string GetCachedArtifactsDirectory(CacheEntry entry);
void LoadCachedData(IList<CacheEntry> entries, out IList<CachedInfo> cachedInfos);
void SaveCachedData(IList<CachedInfo> infos);
```

> `CachedInfo` 在项目里的真实展开（`Data[6]`、`Dependencies[9]`、`ObjectTypes`、`ObjectDependencyInfo`）见 **§17**。

---

## 4. 存储布局与全局隔离

`BuildCache.cs`：

```csharp
const string k_CachePath = "Library/BuildCache";
const int k_Version = 5;

static Hash128 CalculateGlobalArtifactVersionHash()
{
    return HashingMethods.Calculate(Application.unityVersion, k_Version).ToHash128();
}
```

路径计算：

```csharp
// CachedInfo 文件
string finalHash = HashingMethods.Calculate(m_GlobalHash, entry.Hash).ToString();
return string.Format("{0}/{1}/{2}/{3}/{2}.info", k_CachePath, guid.Substring(0, 2), guid, finalHash);

// artifact 目录
public string GetCachedArtifactsDirectory(CacheEntry entry)
{
    var guid = entry.Guid.ToString();
    string finalHash = HashingMethods.Calculate(m_GlobalHash, entry.Hash).ToString();
    return string.Format("{0}/{1}/{2}/{3}", k_CachePath, guid.Substring(0, 2), guid, finalHash);
}
```

要点：

1. `m_GlobalHash` 含 **Unity 版本 + cache 格式版本** → 换 Unity 版本自动隔离，不会读到旧格式缓存；
2. `entry.Hash` 变化 → 目录变化 → 旧缓存自然变成“孤儿”（由 Prune 清理，不会误命中）；
3. `.info` 用 SBP 自带的 **USerialize**（`Editor/Utilities/USerialize/`）二进制序列化，不是 JSON；
4. 一个 hash 目录可以放多个 artifact 文件（任务自己命名）。

---

## 5. 读路径：LoadCachedData

`BuildCache.LoadCachedData`（19.4+ 版本）：

```csharp
using (m_Logger.ScopedStep(LogLevel.Info, "LoadCachedData"))
{
    // 1) 并行读 .info（最多 4 线程：注释说超过 4 线程反而更慢，疑似 GC 竞争）
    int workerThreadCount = Math.Min(Environment.ProcessorCount, 4);
    Parallel.For(0, entries.Count, parallelOptions, index =>
    {
        string file = GetCachedInfoFile(entries[index]);
        byte[] bytes = File.ReadAllBytes(file);
        if (bytes.Length > 0)
            cachedInfoArray[index] = deserializer.DeSerialize<CachedInfo>(memoryStream);
        else
            LogCacheMiss($"[Cache Miss]: Missing cache entry. Entry: {entries[index]}");
    });
}
using (m_Logger.ScopedStep(LogLevel.Info, "Check for changed dependencies"))
{
    for (int i = 0; i < cachedInfos.Count; i++)
    {
        if (HasAssetOrDependencyChanged(cachedInfos[i]))
            cachedInfos[i] = null;   // 置空 = miss
        else
            unchangedCount++;
    }
}
```

校验逻辑（`HasAssetOrDependencyChanged`）：

```csharp
var updatedEntry = GetUpdatedCacheEntry(info.Asset);
if (info.Asset != updatedEntry) { /* miss: 源资产变了 */ }

foreach (var dependency in info.Dependencies)
{
    if (!dependency.IsValid()) { /* miss */ }
    updatedEntry = GetUpdatedCacheEntry(dependency);
    if (dependency != GetUpdatedCacheEntry(updatedEntry)) { /* miss: 依赖变了 */ }
}
```

> 注意：这段校验是**非递归**的——只看 `info.Asset` 和直接 `Dependencies`，不会继续读依赖自己的 `CachedInfo.Dependencies`。详情见 [SBP-Cache-Invalidation.md](SBP-Cache-Invalidation.md) 的 §5.1。

### 5.1 三份 CacheEntry：当前 key / 磁盘历史 key / 现场重算 key

看这段代码时最容易混的是三个“entry”：

| 来源 | 是什么 | 用途 |
|---|---|---|
| `entries[i]` | 调用方**现在**构造的 `CacheEntry`（`CalculateAssetDependencyData` 的 `GetCacheEntry(asset, -Version)`、`TaskCachingUtility.CreateCacheEntry` 等） | **只用来算 `.info` 路径**，定位磁盘文件 |
| `CachedInfo.Asset` / `CachedInfo.Dependencies` | 上次写盘时保存在 `.info` 里的 entry（**历史 key**） | 和现场重算结果比较，判断是否过期 |
| `GetUpdatedCacheEntry(...)` | 现场按**当前** AssetDatabase/TypeDB/文件重新算出的 entry | 当前 key |

流程：

```text
1) entries[i]（当前 key）
      │ GetCachedInfoFile(entries[i])
      ▼
   Library/BuildCache/<guid前2位>/<guid>/<hash(GlobalHash, entries[i].Hash)>.info
      │ 读文件 + USerialize
      ▼
2) CachedInfo { Asset = 历史 entry, Dependencies = 历史 entry[], Data }
      │ HasAssetOrDependencyChanged
      ▼
3) info.Asset         vs GetUpdatedCacheEntry(info.Asset)      // 历史 vs 当前
   info.Dependencies  vs GetUpdatedCacheEntry(dependency)      // 历史 vs 当前
```

注意：

- **`entries[i]` 并不直接和 `info.Asset` 比较**。如果当前 key 和历史 key 不同，路径就会不同 → `.info` 找不到 → 直接 miss；能读到文件，说明 `Guid + Hash` 已经对上了。
- 那为什么还要比较 `info.Asset`？因为 `.info` 可能被拷贝/损坏，或者构建过程中 AssetDatabase 状态发生变化；而且依赖必须逐条校验。
  - **正常情况下 `info.Asset` 应该等于“写盘时的那条 entry”**，所以这项检查通常不会触发；它是防御性检查，不是发现“自己变了”的主要手段（主要靠路径）。
  - 它比较的是 `info.Asset`（磁盘历史）与 `GetUpdatedCacheEntry(info.Asset)`（按磁盘里的 Guid/Version 现场重算），**并不校验 `entries[i]` 与 `info.Asset` 是否一致**；如果自定义 entry 的 Hash 里没包含 Version，两个 Version 可能落在同一路径，这项检查也发现不了。
- **首次构建**：`.info` 不存在 → `bytes` 为空/读失败 → `cachedInfoArray[i]` 保持 `null` → `HasAssetOrDependencyChanged(null)` 第一行就返回 `true` → miss；任务跑完后 `SaveCachedData` 才把 `.info` 写出来。
- 日志里的 `Local Cache hit count: N` 是**反序列化成功**的数量，不是最终可用数量；最终可用看后面的 `Unchanged dependencies count: M`。
- `Version` 参与 entry 相等性比较；很多任务的 `Hash` 里也混了 `Version`（所以 Version 变通常路径也变）。
- `Guid` 决定目录（`<guid前2位>/<guid>/`），`Hash` 决定 `finalHash` 子目录，两者缺一不可。

**用 Ex21 的数字走一遍**（CubeA 的依赖缓存）：

| 构建 | `entries[i].Hash`（现场查询 key） | 磁盘 `.info` 里的 `Asset` | 现场重算 `info.Asset` | 磁盘 `Dependencies` 里的 Mat | 现场重算 Mat | 结果 |
|---|---|---|---|---|---|---|
| A：before（冷） | `2ac0aacc` → 路径不存在 | — | — | — | — | miss，任务跑完写入 `.info`（Asset=2ac0） |
| B：改 CubeA transform 后 | `cf07eed2`（新路径） | 旧 `.info` 在 `2ac0` 路径，**根本不会被读** | — | — | — | miss，写入新 `.info`（Asset=cf07，deps 里 Mat=5e6f） |
| C：改 Mat color 后 | `cf07eed2`（路径存在） | `cf07eed2` | `cf07eed2` ✅ | `5e6f11c0` | `909ddf4e` ❌ | **miss（依赖校验）** |

看构建 C：

- 查询 key 和磁盘 `Asset` 都是 `cf07eed2`——如果只比较“当前 entries vs 磁盘 key”，会通过；
- 真正发现变化的是 **`info.Dependencies` 里的历史 Mat（5e6f）vs 现场重算的 Mat（909d）**；
- 所以校验对象是“磁盘 CachedInfo 里的历史 entry vs 现场重算的 entry”，`entries[i]` 只是用来找文件的查询 key。

> 换个说法：`entries[i]` 是“取件码”，`CachedInfo.Asset/Dependencies` 是“柜子里的历史登记单”；校验是拿登记单去和今天的实际情况重新核对，而不是拿取件码和登记单对比。



```csharp
if (entry.Type == CacheEntry.EntryType.File)       return GetCacheEntry(entry.File, entry.Version);
if (entry.Type == CacheEntry.EntryType.Asset)      return GetCacheEntry(entry.Guid, entry.Version);
if (entry.Type == CacheEntry.EntryType.ScriptType) return GetCacheEntry(Type.GetType(entry.ScriptType), entry.Version);
```

> “重新算”用的是 `BuildCacheUtility` 的**当前**资产 hash（`AssetDatabase.GetAssetDependencyHash` 等），所以能发现“上次构建后资产被改过”。

trace 里对应的步骤名（`buildlogtep.json`）：

```
LoadCachedData
  Read and deserialize cache info
  Check for changed dependencies
Local Cache hit count: N
Unchanged dependencies count: M
```

### 5.2 未命中之后：`ProcessAsset` 才是真正计算

`LoadCachedData` 只是把不可用的 entry 置 `null`，真正的计算在 `CalculateAssetDependencyData.RunInternal`：

```csharp
if (cachedInfo != null && cachedInfo[i] != null)
{
    // 用缓存数据填充 AssetOutput
    if (useCachedData) { output.AssetResults[i] = assetResult; output.CachedAssetCount++; continue; }
}
...
output.AssetResults[i] = ProcessAsset(asset, assetPath, input, explicitAssets, implicitAssetsOutput, packedSprites, null);
```

触发 `ProcessAsset` 的条件不止“没找到缓存”：

1. `cachedInfo[i] == null`：文件缺失/损坏/依赖校验失败（`LoadCachedData` 置 null）；
2. **缓存命中但 Sprite 资产重校验失败**：如果 `ObjectTypes.Types[0] == typeof(Sprite)`，会重新算一遍 references，和缓存里的 `referencedObjects` 比对，不一致就 `useCachedData=false`，也走 `ProcessAsset`；
3. `UseCache=false` 或 `BuildCache == null`：根本不走缓存，全部 `ProcessAsset`。

`ProcessAsset` 里做的是：

- native `GetPlayerObjectIdentifiersInAsset` → `includedObjects`；
- native `GetPlayerDependenciesForObjects` / `GetPlayerDependenciesForAsset` → `referencedObjects`；
- native `CalculateBuildUsageTags` → `BuildUsageTagSet`；
- Sprite / SubAsset / 对象级依赖处理；
- 返回 `AssetOutput`，由调用方写回 `output.AssetResults[i]`；
- 跑完后只对 `cachedInfo[i] == null` 的资产 `GetCachedInfo(...)` 并 `SaveCachedData(toCache)`；命中的复用磁盘 `info.Asset.Hash` / `Data[4]`。

> 所以 `ProcessAsset` = “缓存不可用时的完整计算路径”；它既包含 native 依赖计算，也负责 implicit asset、sprite atlas 等特殊处理。

---

## 6. 写路径：SaveCachedData

```csharp
public void SaveCachedData(IList<CachedInfo> infos)
{
    using (m_Logger.ScopedStep(LogLevel.Info, "SaveCachedData"))
    {
        // 1) 排到 SaveQueue；等待 save 任务真正开始（确保前面的 prune 完成）
        ThreadingManager.QueueTask(ThreadingManager.ThreadQueues.SaveQueue, SaveCachedDataTask, taskData);
        taskData.m_ReadyLock.WaitOne();

        // 2) 并行序列化（同样最多 4 线程）
        Parallel.For(0, infos.Count, parallelOptions, index =>
        {
            using (var stream = new MemoryStream())
            {
                serializer.Serialize(stream, infos[index], 1);
                using (FileStream fs = new FileStream(GetCachedInfoFile(infos[index].Asset), FileMode.Create))
                    fs.Write(stream.GetBuffer(), 0, (int)stream.Length);
            }
        });

        taskData.m_DoneLock.Release();
    }
}
```

`ThreadingManager`（`Editor/Utilities/ThreadingManager.cs`）：

```csharp
public enum ThreadQueues { SaveQueue, UploadQueue, PruneQueue, TotalQueues }
// PruneQueue 会等所有已有任务完成再执行
// EditorApplication.quitting += WaitForOutstandingTasks  (退出前等所有任务)
```

任务层通过 `TaskCachingUtility.RunCachedOperation` 串起来：

```csharp
public static ReturnCode RunCachedOperation<T>(IBuildCache cache, IBuildLogger log,
    IProgressTracker tracker, List<WorkItem<T>> workItems, IRunCachedCallbacks<T> cbs)
{
    // 1) cbs.CreateCacheEntry
    // 2) cache.LoadCachedData
    // 3) 未命中 → cbs.ProcessUncached；命中 → cbs.ProcessCached
    // 4) cbs.PostProcess
    // 5) cache.SaveCachedData(只保存未命中的)
}
```

关键：**只保存未命中的**，命中项不重写；`UseCache=false` 时传进去的 cache 是 `null`，整个流程退化为同步执行。

---

## 7. 各任务的 key 怎么算

### 7.1 CalculateAssetDependencyData（资产依赖）

```csharp
CacheEntry GetCacheEntry(GUID asset) => m_Cache.GetCacheEntry(asset, Version);
```

`BuildCacheUtility.GetCacheEntry(GUID, version)`（`Editor/Utilities/BuildCacheUtility.cs`）：

```csharp
entry.Hash = AssetDatabase.GetAssetDependencyHash(path);          // Unity 导入 hash
if (!entry.Hash.isValid && File.Exists(path))
    entry.Hash = HashingMethods.CalculateFile(path).ToHash128();  // 非 AssetDatabase 文件
if (path.EndsWith(".unity", ...))
    entry.Hash = HashingMethods.Calculate(entry.Hash,
        BuildInterfacesWrapper.SceneCallbackVersionHash,
        PlayerSettings.stripUnusedMeshComponents).ToHash128();
entry.Hash = HashingMethods.Calculate(entry.Hash, entry.Version).ToHash128();
```

- 内置资源（`unity default resources` / `unity_builtin_extra`）：`hash(Application.unityVersion, path)`；
- 依赖列表来自被引用对象：

```csharp
// ExtensionMethods.ExtractCommonCacheData
foreach (var objectId in referencedObjects)
{
    ...
    dependencies.Add(cache.GetCacheEntry(objectId));
}
info.Dependencies = dependencies.ToArray();
```

命中后的特殊校验：如果资产包含 Sprite，会重新调 `GetPlayerDependenciesForObjects` 对比引用集合，防止 SpriteAtlas 关联变化导致 stale 数据。

### 7.2 CalculateSceneDependencyData（场景依赖）

结构与资产依赖类似，但多了 prefab 依赖：

```csharp
var dependencies = new HashSet<CacheEntry>(prefabEntries);
ExtensionMethods.ExtractCommonCacheData(m_Cache, null, references, uniqueTypes, objectTypes, dependencies);
info.Data = new object[] { sceneInfo, usageTags, prefabDependency, objectTypes };
```

### 7.3 WriteSerializedFiles（写 SerializedFile）

```csharp
entry.Guid = HashingMethods.Calculate("WriteSerializedFiles", operation.Command.fileName).ToGUID();
entry.Hash = HashingMethods.Calculate(
    Version,                                   // 任务版本
    operation.GetHash128(m_Log),               // 写操作内容 hash（含对象/usage/reference/bundle info/shader 回调版本）
    settings.GetHash128(),                     // BuildSettings（target/group/typeDB/flags）
    globalUsage,                               // BuildUsageTagGlobal
    onlySaveFirstSerializedObject,             // slimWriteResults
    GetPlayerSettingsHash128(settings.target)  // stripUnusedMeshComponents / mipStripping / GraphicsAPIs
).ToHash128();
```

- 未命中：写到 `GetCachedArtifactsDirectory(entry)`，然后 `CalculateFileMetadata` 算 `RawFileHash/ContentHash`；
- `slimWriteResults` 为真时 `SlimifySerializedObjects` 裁剪 WriteResult（减少缓存体积）；
- 命中：直接取 `CachedInfo.Data[0]`（WriteResult）和 `Data[1]`（SerializedFileMetaData）。

### 7.4 ArchiveAndCompressBundles（归档压缩）

```csharp
entry.Guid = HashingMethods.Calculate("ArchiveAndCompressBundles", bundleName).ToGUID();
List<object> toHash = new List<object> { kVersion, compression };
foreach (var resource in resources)
{
    toHash.Add(resource.serializedFile);
    toHash.Add(resource.fileAlias);
}
toHash.AddRange(hashes.Select(x => (object)x.RawFileHash));
entry.Hash = HashingMethods.Calculate(toHash).ToHash128();
```

**compression 在 key 里** → 改某个 bundle 的压缩方式，只会让那个 bundle 重新归档，其它 bundle 不受影响（Ex16 能直观看到）。

命中时：

```csharp
item.ResultDetails = (BundleDetails)cachedInfo[item.Index].Data[0];
...
CopyFileWithTimestampIfDifferent(item.CachedArtifactPath, item.ResultDetails.FileName, input.Log);
```

其中 `CachedArtifactPath = GetCachedArtifactsDirectory(entry) + "/" + hash(bundleName)`。

未命中时：`ContentBuildInterface.ArchiveAndCompress(resourceFiles, writePath, compression, stripUnityVersion)` 写入缓存目录/临时目录，再拷贝到输出目录。

---

## 8. 缓存维护与设置

**维护 API**：

```csharp
BuildCache.PurgeCache(false);   // 等任务结束 → 清 GUID hash 缓存 → 删 Library/BuildCache
BuildCache.PruneCache();        // 超过 ScriptableBuildPipeline.maximumCacheSize(GB) 才清
BuildCache.PruneCache_Background(maximumCacheSize);  // ContentPipeline 每次构建结束自动排队
```

`ContentPipeline` 构建结束后：

```csharp
long maximumCacheSize = ScriptableBuildPipeline.maximumCacheSize * BuildCache.k_BytesToGigaBytes;
BuildCache.PruneCache_Background(maximumCacheSize);
```

**相关设置**（`ProjectSettings/ScriptableBuildPipeline.json`）：

| 设置 | 默认 | 影响 |
|---|---|---|
| `logCacheMiss` | false | 打开后 miss 会输出 `[Cache Miss]: ...`（含资产/依赖对比） |
| `maximumCacheSize` | 20 GB | 后台 prune 阈值 |
| `slimWriteResults` | true | 缓存体积/内容（不影响产物） |
| `useV2Hasher` | 新项目 true | SpookyHash vs MD5，切换会 invalidate 整个缓存 |
| `threadedArchiving` | true | 归档线程化 |
| `useDetailedBuildLog` | false | 更详细的 trace（注意与自定义 logger 的冲突，见主 Wiki 坑） |

> `BuildCache(host, port)` 里对 Cache Server 只打一句 `"Cache Server is no longer supported."`——不要再指望远端共享构建缓存，资源导入缓存请用 Unity Accelerator（那是 importer 层，不是 SBP build cache）。

---

## 9. 调试方法

1. **打开 `Log Cache Miss`**（Preferences → Build Cache）→ Console 会输出：
   - `[Cache Miss]: Missing cache entry. Entry: (...)`
   - `[Cache Miss]: Source asset changed. Old: ... New: ...`
   - `[Cache Miss]: Dependency changed. Asset: ... Dependency: ...`
2. **看 `buildlogtep.json`**：`Gathering Cache Entries to Load`、`LoadCachedData`、`Read and deserialize cache info`、`Check for changed dependencies`、`Local Cache hit count`、`SaveCachedData` 等 step 的耗时和计数。
3. **直接看目录**：`Library/BuildCache/<xx>/<guid>/<hash>/` 下有没有 `.info` 和 artifact；删掉某个目录可定向验证。
4. **冷/热对比**：`PurgeCache(false)` 后跑一次，再跑一次，对比耗时（Ex08）。
5. **注意两次构建必须同一 Unity 版本**：`m_GlobalHash` 不同会直接换目录。

---

## 10. 自定义任务接入缓存（模板）

```csharp
class MyCallbacks : TaskCachingUtility.IRunCachedCallbacks<MyItem>
{
    const int kVersion = 1;

    CacheEntry CreateCacheEntry(WorkItem<MyItem> item)
    {
        return new CacheEntry
        {
            Type = CacheEntry.EntryType.Data,
            Guid = HashingMethods.Calculate("MyTask", item.Context.Key).ToGUID(),
            Hash = HashingMethods.Calculate(
                kVersion,
                item.Context.InputHash,      // 所有影响产物的输入
                settingsHash,                // 影响产物的设置
                dependencyEntries).ToHash128(),
            Version = kVersion,
        };
    }

    void ProcessUncached(WorkItem<MyItem> item)
    {
        // 1) 产物写到 m_UseCache.GetCachedArtifactsDirectory(item.entry)
        // 2) 结果放进 CreateCachedInfo 的 Data
    }

    void ProcessCached(WorkItem<MyItem> item, CachedInfo info)
    {
        item.Result = info.Data[0];   // 或从 artifact 目录拿文件
    }

    CachedInfo CreateCachedInfo(WorkItem<MyItem> item)
    {
        return new CachedInfo
        {
            Asset = item.entry,
            Dependencies = dependencyEntries,   // 让 HasAssetOrDependencyChanged 帮你校验
            Data = new object[] { item.Result },
        };
    }

    void PostProcess(WorkItem<MyItem> item) { /* 汇总到任务输出 */ }
}
```

三条铁律：

1. **key 覆盖所有输入**：包括工具版本、参数、依赖、平台；漏一个就会“错误命中”；
2. **依赖用 `CacheEntry[]` 表达**，不要自己拼字符串；
3. **改逻辑必须 `Version++`**，否则旧缓存继续用。

---

## 11. 常见坑

1. **自定义任务没进缓存，但你以为进了**：只有实现 `IRunCachedCallbacks<T>` / 手动用 `IBuildCache` 才走缓存。
2. **key 漏输入**（例如新增一个影响结果的设置但没加进 hash）→ 错误命中，产物与设置不一致。
3. **任务 `Version` 没加**：改代码后仍复用旧缓存，表现为“改了没生效”。
4. **依赖没声明**：依赖资源变了但缓存命中，产物 stale。
5. **产物写到缓存目录外面**：命中时拿不到 artifact，只能每次重算。
6. **`m_GlobalHash` 换 Unity 版本**：旧缓存不命中是正常现象，不是 bug。
7. **`useV2Hasher` 切换**：所有 hash 变，缓存全量失效（官方说明）。
8. **Purge 时还在构建**：`PurgeCache` 会先等任务结束，但不要手动删 `Library/BuildCache` 目录与构建并发。
9. **把 BuildCache 当资源 CDN 用**：它是本地构建缓存，不是发布产物。
10. **`.info` 和 artifact 混淆**：`.info` 是结构化结果，bundle 文件是 artifact；命中压缩任务时拷贝的是 artifact。

---

## 12. SBP 缓存是 C# 还是 native？

结论：**SBP 的 BuildCache 完全在 C# 层实现和维护**；native 只提供“输入 hash”和“产物”（序列化/压缩），不参与缓存决策。

### 12.1 C# 负责

- 存储：`BuildCache`（`Library/BuildCache`、`.info` 的 USerialize、artifact 目录）
- key：`CacheEntry`（Guid/Version/Hash）由各任务用 `HashingMethods` 组合
- 读取/校验：`LoadCachedData` + `HasAssetOrDependencyChanged`
- 保存：`SaveCachedData`（`ThreadingManager.SaveQueue`）
- 命中/失效判定、Purge/Prune、日志（`logCacheMiss`）
- hash 算法实现：`HashingMethods`（MD5 / SpookyHash / MD4 都在 package 里，纯 C#）
- 文件内容 hash：`SerializedFileMetaData.ContentHash` 由 `WriteSerializedFiles.CalculateFileMetadata` 在 C# 里读文件流计算

### 12.2 native 只提供输入/产物

| native API | 提供什么 |
|---|---|
| `AssetDatabase.GetAssetDependencyHash` | asset 级 hash 输入 |
| `TypeDB.GetHash128` / `CalculatePlayerSerializationHashForType` | 类型 / TypeDB hash 输入 |
| `ContentBuildInterface.WriteSerializedFile` | 产出 SerializedFile（随后 C# 算 hash、缓存） |
| `ContentBuildInterface.ArchiveAndCompress` | 产出 bundle 文件（随后 C# 缓存 / 拷贝） |
| `ContentBuildInterface.GetPlayerDependenciesForObjects` 等 | 依赖数据（影响 cache key 的内容） |

### 12.3 调用链

```text
task (C#) 
  → GetCacheEntry (C#)                      // 组合 key
  → LoadCachedData (C#, USerialize 读 .info)
  → HasAssetOrDependencyChanged (C#, 调 native hash 校验输入)
      ├─ miss → task.Run() → native WriteSerializedFile / ArchiveAndCompress
      │        → SaveCachedData (C#)        // 只保存未命中的
      └─ hit  → 直接用 CachedInfo.Data / 拷贝缓存 artifact
```

### 12.4 和 Unity 自己的缓存区分

- `Library/BuildCache`：**SBP 的 build cache**（C# 管理）
- `Library/ArtifactDB` / 导入缓存：**Unity 的 importer cache**（native）
- Cache Server：SBP 旧远端缓存，已 deprecated（`BuildCache(host, port)` 只打 warning）

### 12.5 对自定义 DB 的实际影响

- 缓存完全可以在 C# 层替换/扩展：任务实现 `IRunCachedCallbacks`，把影响产物的输入全部放进 `CacheEntry.Hash`，就能复用这套机制；
- 如果 DB 写入不再经过 native `ContentBuildInterface`，需要自己生成 artifact 并放进 `GetCachedArtifactsDirectory`；
- native 的 `WriteSerializedFile` / `ArchiveAndCompress` 耗时不会被缓存省掉，除非缓存它们的输出——SBP 默认就是这么做的（缓存 artifact 文件本身）。

---

## 13. 练习题

1. 给 Ex08 打开 `Log Cache Miss`，Purge 后构建，观察哪些资产 miss、第二次为什么全 hit。
2. 在 Ex16 里只改某个 bundle 的压缩方式（Ex04 的 per-bundle 参数类），观察 ArchiveAndCompress 的缓存是否只失效一个 bundle。
3. 写一个自定义任务，故意漏一个输入 hash，验证“错误命中”会产出错误结果。
4. 修改 `Assets/Bundles/Props/CubeA.prefab` 后构建，观察 `CalculateAssetDependencyData` 与 `WriteSerializedFiles` 的缓存命中/失效范围（配合 trace）。
5. 切换 `useV2Hasher` 再构建，观察缓存目录变化和命中率。

---

## 14. 源码索引（按阅读顺序）

1. `Editor/Interfaces/IBuildCache.cs`
2. `Editor/Utilities/CacheEntry.cs`
3. `Editor/Utilities/BuildCache.cs`（`GetCachedInfoFile`、`GetCachedArtifactsDirectory`、`LoadCachedData`、`SaveCachedData`、`HasAssetOrDependencyChanged`、`PurgeCache`、`PruneCache`）
4. `Editor/Utilities/BuildCacheUtility.cs`（`GetCacheEntry` 各重载、`AutoBuildCacheUtility`）
5. `Editor/Utilities/TaskCachingUtility.cs`
6. `Editor/Tasks/CalculateAssetDependencyData.cs`
7. `Editor/Tasks/CalculateSceneDependencyData.cs`
8. `Editor/Tasks/WriteSerializedFiles.cs`
9. `Editor/Tasks/ArchiveAndCompressBundles.cs`
10. `Editor/Utilities/ThreadingManager.cs`
11. `Editor/Utilities/Preferences.cs`
12. `Editor/Utilities/USerialize/`（`.info` 的序列化器）

> 确定性部分见子 Wiki：[SBP-Determinism.md](SBP-Determinism.md)。

---

## 15. 项目实测：冷/热构建的缓存采样（Ex20）

> 实验脚本：`Assets/Editor/SbpLab/SbpLabEx20_CacheProbe.cs`（菜单 `SBP Lab/Examples/20 - 构建缓存采样`）
> 原始报告：[ex20-cache-probe.txt](ex20-cache-probe.txt)（本目录下）

### 15.1 实验怎么做的

SBP 的 `BuildCache` 是 C# 实现，但 `ContentPipeline` 内部会自己创建它。为了抓真实数据，Ex20 用了一个不侵入 SBP 源码的做法：

1. 在任务链最前面插入 `CacheHookTask`；
2. 它注入拿到真实的 `IBuildCache`（即 `BuildCache` 实例）；
3. 用 `LoggingBuildCache` 包装它（实现 `IBuildCache`，只记录、不改变行为）；
4. `buildContext.SetContextObject(typeof(IBuildCache), wrapper)` 替换接口键。

之后所有任务的 `[InjectContext] IBuildCache` 拿到的都是包装对象，于是可以记录：

- `LoadCachedData`：entries 数、命中/未命中数、耗时；
- `SaveCachedData`：保存的 entry 数、耗时；
- 每个 `CacheEntry` 的 `Type / Guid / Version / Hash`；
- `GetCachedInfoFile` / `GetCachedArtifactsDirectory` 的真实路径、`.info` 大小、artifact 文件数/总大小。

然后：

- 先 `BuildCache.PurgeCache(false)`，跑一次**冷构建**；
- 复用同一个 `parameters`（`ScriptInfo` 已在第一次构建后生成）再跑一次**热构建**。

### 15.2 构建结果

| 构建 | 耗时 | 结果 |
|---|---|---|
| 冷构建（Purge 后） | **3773 ms** | 全部 miss + 写缓存 |
| 热构建 | **49 ms** | 全部 hit，无新写入 |

> 注意：热构建的 49ms 同时包含“跳过脚本重编译”（`ScriptInfo` 复用）和“缓存全命中”，不是纯缓存收益。

`GlobalHash`（`hash(Application.unityVersion, cache 格式版本)`）：

```text
16b4790ca43db58c76243dd04115a209
```

缓存目录统计（冷构建后）：

```text
Library/BuildCache  dirs=30  files=16  infos=10  bytes=250991
```

- 10 个 entry → 10 个 `.info` + 6 个 artifact 文件；
- 30 个目录 = 10 个 entry × 3 层（`<guid前2位>/<guid>/<finalHash>`）。

热构建后目录统计完全不变（没有新文件写入）。

### 15.3 冷构建：Load / Save 事件

| 调用方 | entries | hits | misses | load ms | save count | save ms |
|---|---|---|---|---|---|---|
| `CalculateAssetDependencyData.RunInternal` | 4 | 0 | 4 | 6 | 4 | 37 |
| `WriteSerializedFiles.Run` | 3 | 0 | 3 | 0 | 3 | 6 |
| `ArchiveAndCompressBundles.Run` | 3 | 0 | 3 | 0 | 3 | 3 |

### 15.4 热构建：全部命中

| 调用方 | entries | hits | misses | load ms | save count |
|---|---|---|---|---|---|
| `CalculateAssetDependencyData.RunInternal` | 4 | **4** | 0 | 19 | 0 |
| `WriteSerializedFiles.Run` | 3 | **3** | 0 | 8 | 0 |
| `ArchiveAndCompressBundles.Run` | 3 | **3** | 0 | 3 | 0 |

> 热构建时依赖缓存 load 反而略慢（19ms vs 6ms）：冷构建 miss 很快，热构建要读 `.info`、USerialize、再做依赖校验。缓存省的是**后续任务的计算/写盘**，不是“读缓存一定比 miss 快”。

### 15.5 关键 CacheEntry（冷构建时写入的真实值）

**① CalculateAssetDependencyData（Asset 类型，依赖列表非空）**

| 资产 | entry Type | Version | Hash（前 8 位） | CacheEntry.Dependencies | `.info` 大小 |
|---|---|---|---|---|---|
| CubeA.prefab | Asset | **-6** | `2ac0aacc` | 9 | 6079 B |
| CubeB.prefab | Asset | -6 | `9aefb3d5` | 9 | 6079 B |
| CubeC.prefab | Asset | -6 | `e6e08565` | 9 | 6079 B |
| Mat.mat | Asset | -6 | `5e6f11c0` | 3 | 3717 B |

> `Version = -6` 是因为 `NonRecursiveDependencies = true`（2021.1+ 默认），缓存 key 用 `-Version` 区分非递归依赖模式。

**② WriteSerializedFiles（Data 类型，无依赖列表；key 已包含所有输入）**

| 内部文件 | entry Type | Version | Hash（前 8 位） | `.info` 大小 | artifact |
|---|---|---|---|---|---|
| props_a `CAB-c297...` | Data | **4** | `1c2cdf94` | 3087 B | `CAB-c297d6b35da6a83c86b4dadbdfee5d91`（9031 B） |
| props_b `CAB-c269...` | Data | 4 | `43d08eb7` | 3087 B | `CAB-c2692dc06dec32d93f43b7c2e1768b1a`（8376 B） |
| shared `CAB-ef05...` | Data | 4 | `3d43da2d` | 2580 B | `CAB-ef051277ec27de9e7fc97842ca18b97e`（146828 B） |

**③ ArchiveAndCompressBundles（Data 类型）**

| bundle | entry Type | Version | Hash（前 8 位） | `.info` 大小 | artifact |
|---|---|---|---|---|---|
| props_a | Data | **2** | `5590c49a` | 1057 B | `c297d6b35da6a83c86b4dadbdfee5d91`（3323 B） |
| props_b | Data | 2 | `d8fbde0c` | 1057 B | `c2692dc06dec32d93f43b7c2e1768b1a`（3166 B） |
| shared | Data | 2 | `3deca829` | 1046 B | `ef051277ec27de9e7fc97842ca18b97e`（46399 B） |

### 15.6 真实缓存目录长什么样

CacheEntry 的路径由 `hash(GlobalHash, Entry.Hash)` 决定：

```text
Library/BuildCache/<guid前2位>/<guid>/<finalHash>/<guid>.info
Library/BuildCache/<guid前2位>/<guid>/<finalHash>/<artifact文件>
```

实测示例：

```text
Library/BuildCache/b1/b1665a6fc133d8d4b87ac1b4e4a645a8/1ee54b17d48ca065a9ce520224b1a2e4/
    b1665a6fc133d8d4b87ac1b4e4a645a8.info                                   6079 B

Library/BuildCache/07/070aa2b259104d7f34d98e674e4ab7d6/74c31292c4267ea1819eab747eeb6cb5/
    070aa2b259104d7f34d98e674e4ab7d6.info                                   3087 B
    CAB-c297d6b35da6a83c86b4dadbdfee5d91                                   9031 B   ← SerializedFile artifact

Library/BuildCache/4f/4f5773e9b494b710d8f7d331a483a0e2/27ae49adbd2137203e690bdad6ee82c8/
    4f5773e9b494b710d8f7d331a483a0e2.info                                   1057 B
    c297d6b35da6a83c86b4dadbdfee5d91                                       3323 B   ← props_a.bundle artifact
```

注意：

- `.info` 和 artifact 在**同一个目录**，所以报告里 “artifacts=2 files” 实际是 `.info` + 真实产物；
- 依赖缓存（Asset 类型）目录里只有 `.info`，没有 artifact（它只存依赖数据，不存文件产物）；
- 32 位 hash 看起来随机，但同一个 `GlobalHash + Entry.Hash` 一定落到同一路径。

### 15.7 从数据能确认的原理

1. **CacheEntry 是内容寻址的**：同一个 `Guid + Version + Hash` → 同一个 `.info` 路径；`Hash` 变了路径就变，旧目录成为孤儿（交给 Prune 清）。
2. **Data entry 的 dependencies 为空**：Write/Archive 的 key 已经把 `RawFileHash`、settings、compression 等全部纳入 `Hash`，所以不需要再列 `Dependencies`。
3. **Asset entry 有依赖列表**：依赖任务的 `.info` 里 `dependencies` 存了 9 个（prefab 引用的对象）和 3 个（Mat 的 3 个对象），热构建加载时会逐个校验。
4. **`.info` 不是小索引**：prefab 的依赖 `.info` 有 6KB，比不少 artifact 还大；`slimWriteResults` 只裁剪 `WriteResult`，不会裁依赖数据。
5. **热构建完全命中后没有任何写盘**：`SaveCachedData` count=0，目录统计不变——缓存只读不写。

### 15.8 怎么复现

```text
菜单：SBP Lab/Examples/20 - 构建缓存采样
产物：Artifacts/SBP/ex20-cache-probe.txt
```

脚本里两个关键点：

- `CacheHookTask` 必须在任务链第一个；
- 冷构建前 `BuildCache.PurgeCache(false)`；热构建复用同一个 `parameters`，这样 `ScriptInfo` 不为空、跳过脚本重编译。

---

## 16. 为什么用 AssetDatabase.GetAssetDependencyHash？实测“改属性 / 改依赖”的影响

> 实验脚本：`Assets/Editor/SbpLab/SbpLabEx21_AssetHashProbe.cs`（菜单 `SBP Lab/Examples/21 - AssetDependencyHash 探针`）
> 原始报告：[ex21-asset-hash-probe.txt](ex21-asset-hash-probe.txt)

### 16.1 它是什么、为什么用它

`BuildCacheUtility.GetCacheEntry(GUID asset)` 里：

```csharp
entry.Hash = AssetDatabase.GetAssetDependencyHash(path);
if (!entry.Hash.isValid && File.Exists(path))
    entry.Hash = HashingMethods.CalculateFile(path).ToHash128();
// ...
entry.Hash = HashingMethods.Calculate(entry.Hash, entry.Version).ToHash128();
```

`AssetDatabase.GetAssetDependencyHash` 是 **Unity AssetDatabase/导入管线维护的 Hash128**（native 侧），SBP 只是拿它当“资产是否被导入器认为变了”的廉价指纹：

- 不是文件 MD5，也不直接暴露算法；
- 资产内容/导入设置/导入依赖变化导致重新导入时，它会变；
- 比每次自己算文件 hash 便宜，而且与 Unity 的增量导入体系一致；
- SBP 还会把 `entry.Version` 混进去（所以 `depHash ≠ entry.Hash`）。

### 16.2 实测：改 prefab 自己的属性

修改 `CubeA.prefab` 的 `transform.localPosition`，前后对比：

| 指标 | before | after-prefab-change |
|---|---|---|
| CubeA `GetAssetDependencyHash` | `127b22b8...` | **`0bb38b75...`**（变了） |
| CubeA 文件 MD5 | `1e4486d8...` | **`7caf28e0...`**（也变了） |
| CubeB/C/Mat 的 depHash | — | 不变 |

缓存结果（`CalculateAssetDependencyData` Load）：

```text
entries=4 hits=3 misses=1
  CubeA entry hash: 2ac0aacc... → cf07eed2...   miss
  CubeB/C/Mat:      hit
Save count=1（只重新保存 CubeA）
```

下游：

- `WriteSerializedFiles`：props_a 的 entry hash `1c2cdf94... → 09eab543...`，miss；props_b/shared hit；
- `ArchiveAndCompressBundles`：props_a 的 entry hash `5590c49a... → d7b93e7e...`，miss；其余 hit。

**结论：只改资产自己的序列化属性 → depHash 变、缓存正确 miss。**

### 16.3 实测：改它引用的资产（Mat.mat 颜色）

修改 `Mat.mat` 的 color，前后对比：

| 指标 | before | after-material-change |
|---|---|---|
| Mat `GetAssetDependencyHash` | `f9b31f15...` | **`ce9946df...`**（变了） |
| Mat 文件 MD5 | `c1a787ce...` | **`d797e26b...`**（变了） |
| **CubeA/B/C 的 depHash** | `...` | **完全不变** |
| CubeA/B/C 文件 MD5 | — | 完全不变 |

但缓存结果很关键：

```text
[Load] caller=CalculateAssetDependencyData entries=4 hits=0 misses=4
  CubeA/B/C 的 entry.Hash 都没变（cf07eed2 / 9aefb3d5 / e6e08565）
  但 hit=False —— 因为它们的 CachedInfo.Dependencies 里包含 Mat 的 CacheEntry，
  Mat 的 entry.Hash 变了（5e6f11c0... → 909ddf4e...），依赖校验把 4 个都判为 miss
```

下游：

- `WriteSerializedFiles`：shared 文件 miss（`3d43da2d... → 876d801b...`），props_a/props_b hit；
- `ArchiveAndCompressBundles`：shared miss（`3deca829... → 8bab4287...`），其余 hit。

**结论：**

1. `GetAssetDependencyHash` 只反映**资产自身的导入输入**，不会因为“被别的资产引用”而变化（Mat 变了，prefab 的 depHash 不变）；
2. 但 SBP 的缓存校验不只看自己的 key：`CachedInfo.Dependencies` 里记录了依赖资产/对象，**依赖 entry 变了就会让上层缓存失效**——这是“依赖缓存仍然正确”的关键机制；
3. 所以“改依赖资产”不会产生 stale 依赖数据，即使 prefab 的 depHash 没变。

### 16.4 `depHash` 和 `entry.Hash` 的对应关系（实测）

| 资产 | 阶段 | depHash | entry.Hash（Version=-6） |
|---|---|---|---|
| CubeA.prefab | before | `127b22b8...` | `2ac0aacc...` |
| CubeA.prefab | after-prefab | `0bb38b75...` | `cf07eed2...` |
| Mat.mat | before | `f9b31f15...` | `5e6f11c0...` |
| Mat.mat | after-material | `ce9946df...` | `909ddf4e...` |

`entry.Hash = hash(depHash, Version)`，所以两者不相等；Version 变了（比如 SBP 升级把 `kVersion` 从 6 改成 7）也会让所有 entry 失效。

### 16.5 对你们自定义管线的建议

1. **不要自己实现 `GetAssetDependencyHash` 的等价物**：它是 AssetDatabase 内部维护的导入指纹，自己算很难覆盖导入设置/依赖；
2. 自定义资产/DB 对象如果不在 AssetDatabase 里，需要自己定义“内容指纹”并放进 `CacheEntry.Hash`（或依赖列表）；
3. **脚本序列化布局变化不在本实验覆盖范围**：如果改了 MonoBehaviour 的序列化字段，而资产文件本身没变，`GetAssetDependencyHash` 不一定会变；这种场景建议在 CI 里清缓存或显式 bump 任务 Version，避免依赖缓存 stale；
4. 依赖资产变化时，不必自己手动失效上层缓存——只要依赖关系正确写进 `CachedInfo.Dependencies` / `CacheEntry.Dependencies`，SBP 的校验会处理。

### 16.6 复现

```text
菜单：SBP Lab/Examples/21 - AssetDependencyHash 探针
产物：Artifacts/SBP/ex21-asset-hash-probe.txt
```

脚本会依次做：冷构建 → 改 CubeA.prefab transform → 再构建 → 改 Mat.mat color → 再构建，并打印每个阶段的 `depHash` / 文件 MD5 / 缓存命中。

---

## 17. 缓存里的真实数据结构：`CachedInfo` 的 `Data[6]` 与 `Dependencies[9]`

> 本节对应 `BuildCache.LoadCachedData` 返回的 `CachedInfo[]`（`CalculateAssetDependencyData` 的依赖缓存）。
> 数据来自 lab 工程冷构建后的真实对象（CubeA.prefab，Unity 2022.3 / SBP 1.23.4）。

### 17.1 debugger 里的真实结构

```text
cachedInfoArray = CachedInfo[4]                      // 4 个 asset → 4 个 CachedInfo

[0] CachedInfo
    Asset = (CacheEntry) (b1665a6f..., 2ac0aacc075c01eff68a5033f2c19cad)
        // Guid = CubeA.prefab 的 GUID；Hash = 2ac0aacc...
    Data = object[6]
        [0] AssetLoadInfo
              address = null
              asset = b1665a6f...
              includedObjects  = Count 5
              referencedObjects = Count 2
        [1] BuildUsageTagSet
        [2] null
        [3] null
        [4] List<ObjectTypes> Count = 7
              [4] ObjectTypes
                    ObjectID = { guid: b1665a6f..., fileID: 6277825710962230088,
                                 type: MetaAssetType, path: }
                    Types = Type[1]
                        [0] {RuntimeType} UnityEngine.BoxCollider
        [5] List<ObjectDependencyInfo> Count = 5
    Dependencies = CacheEntry[9]

[1] CachedInfo  (CubeB.prefab)
[2] CachedInfo  (CubeC.prefab)
[3] CachedInfo  (Mat.mat)
```

### 17.2 `Data[6]` 每个槽位是什么

来源 `CalculateAssetDependencyData.GetCachedInfo`：

```csharp
info.Data = new object[] { assetInfo, usageTags, importerData, assetData, objectTypes, objectDependencies };
```

| 下标 | 类型 | 含义 | 本例 |
|---|---|---|---|
| `[0]` | `AssetLoadInfo` | 资产级依赖：asset/address + included/referenced 对象 | CubeA：5 included + 2 referenced |
| `[1]` | `BuildUsageTagSet` | 该资产的 usage tags（变体/裁剪） | 非空 |
| `[2]` | `SpriteImporterData` | Sprite 导入数据；非 Sprite 为 `null` | `null` |
| `[3]` | `ExtendedAssetData` | SubAsset（Representations）；没有为 `null` | `null` |
| `[4]` | `List<ObjectTypes>` | **每个 included/referenced 对象**的 ObjectID + 排序后的运行时类型 | 7 条（5 included + 2 referenced）；其中 BoxCollider 的 `Types[0] = UnityEngine.BoxCollider` |
| `[5]` | `List<ObjectDependencyInfo>` | **对象级依赖**（`NONRECURSIVE_DEPENDENCY_DATA` 模式下生成）；每条是 `Object + Dependencies` | 5 条（对应 5 个 included 对象） |

注意：

- `Data[4]` 的条目数 = included + referenced 对象数（不是“类型数”）；
- `Data[5]` 在 2021.1+ 的非递归依赖模式下才有内容；递归模式下为空列表；
- `Data` 是 `object[]`，**每个任务有自己的 schema**，反序列化靠 `BuildCache` 构造时注入的 `CustomSerializers` / `ObjectFactories`。

### 17.3 `Dependencies = CacheEntry[9]` 怎么来的

`CalculateAssetDependencyData.GetCachedInfo` 里：

```csharp
var dependencies = new HashSet<CacheEntry>();
ExtensionMethods.ExtractCommonCacheData(
    input.BuildCache, assetInfo.includedObjects, assetInfo.referencedObjects,
    uniqueTypes, objectTypes, dependencies);
info.Dependencies = dependencies.ToArray();
```

`ExtractCommonCacheData` 做了两件事：

1. 对每个 **referencedObject** 调 `cache.GetCacheEntry(objectId)`；
2. 对所有 **uniqueTypes** 调 `cache.GetCacheEntry(type)`，并 Union 进同一个 HashSet。

所以 CubeA 的 9 个依赖是：

```text
9 = 2（referencedObjects：内置 Mesh + Mat 的 Material）
  + 7（uniqueTypes：GameObject、Transform、MeshFilter、MeshRenderer、BoxCollider、Mesh、Material）
```

Mat 的 3 个依赖：

```text
3 = 1（referencedObjects 的两个 Shader 共享同一个 builtin_extra GUID，去重为 1 个 CacheEntry）
  + 2（uniqueTypes：Material、Shader）
```

> 这解释了 Ex20 报告里 `deps=9`（prefab）和 `deps=3`（Mat）的来源。

### 17.4 完整实例：CubeA 的 Data[0] / Data[4] / Data[5] / Dependencies

> 脚本：`Assets/Editor/SbpLab/SbpLabEx22_CachedInfoDump.cs`（菜单 `SBP Lab/Examples/22 - CachedInfo 实例 dump`）
> 完整报告：[ex22-cachedinfo-dump.txt](ex22-cachedinfo-dump.txt)（包含 4 个 asset 的完整数据）

#### Data[0] AssetLoadInfo

```text
asset    = b1665a6f...（CubeA.prefab）
address  = null
includedObjects   = 5
referencedObjects = 2
```

| 类别 | # | fileID | guid | FileType | 运行时类型（来自 Data[4]） |
|---|---|---|---|---|---|
| included | 0 | `1266598513701572995` | b1665a6f... | MetaAssetType | GameObject |
| included | 1 | `36684013351910960` | b1665a6f... | MetaAssetType | MeshFilter |
| included | 2 | `1529215442718738174` | b1665a6f... | MetaAssetType | MeshRenderer |
| included | 3 | `3459590857198133346` | b1665a6f... | MetaAssetType | Transform |
| included | 4 | `6277825710962230088` | b1665a6f... | MetaAssetType | BoxCollider |
| referenced | 0 | `10202` | 0000...e000... | NonAssetType | Mesh（`Library/unity default resources`） |
| referenced | 1 | `2100000` | 0d70bf... | SerializedAssetType | Material（Mat.mat） |

#### Data[1] / Data[2] / Data[3]

```text
Data[1] BuildUsageTagSet:
    Type=BuildUsageTagSet
    Hash128=00000000000000000000000000000000   // 这个 asset 没有 usage tags

Data[2] SpriteImporterData: null
Data[3] ExtendedAssetData:  null
```

#### Data[4] List<ObjectTypes>（7 条）

格式是 `ObjectID + Types[]`：

| # | ObjectID（guid / fileID / FileType） | Types |
|---|---|---|
| 0 | b1665a6f... / `1266598513701572995` / MetaAssetType | `[GameObject]` |
| 1 | b1665a6f... / `36684013351910960` / MetaAssetType | `[MeshFilter]` |
| 2 | b1665a6f... / `1529215442718738174` / MetaAssetType | `[MeshRenderer]` |
| 3 | b1665a6f... / `3459590857198133346` / MetaAssetType | `[Transform]` |
| 4 | b1665a6f... / `6277825710962230088` / MetaAssetType | `[BoxCollider]` |
| 5 | 0000...e000... / `10202` / NonAssetType | `[Mesh]` |
| 6 | 0d70bf... / `2100000` / SerializedAssetType | `[Material]` |

#### Data[5] List<ObjectDependencyInfo>（5 条）

每条是“对象 → 它的对象级依赖”：

| # | Object | Dependencies |
|---|---|---|
| 0 | GameObject（`1266598513701572995`） | MeshFilter、MeshRenderer、Transform、BoxCollider |
| 1 | MeshFilter（`36684013351910960`） | Mesh（default resources）、GameObject |
| 2 | MeshRenderer（`1529215442718738174`） | Material（Mat.mat）、GameObject |
| 3 | Transform（`3459590857198133346`） | GameObject |
| 4 | BoxCollider（`6277825710962230088`） | GameObject |

> 这解释了为什么 `Data[5].Count = 5`：它按 **includedObjects** 逐个记录对象级依赖，不是按 referencedObjects。

#### Dependencies = CacheEntry[9]

| # | Type | guid | Version | Hash | 说明 |
|---|---|---|---|---|---|
| 0 | Asset | `0000...e000...` | 1 | `6ea91044...` | 内置 default resources（Mesh） |
| 1 | Asset | `0d70bf...` | 1 | `671ad71a...` | Mat.mat |
| 2 | ScriptType | `dab847ae...` | **0** | `1909f56b...` | `UnityEngine.GameObject` |
| 3 | ScriptType | `e977a307...` | **1** | `3c319971...` | `UnityEngine.MeshFilter` |
| 4 | ScriptType | `e1ce7c99...` | **2** | `ead26004...` | `UnityEngine.MeshRenderer` |
| 5 | ScriptType | `b4110be9...` | **3** | `3c319971...` | `UnityEngine.Transform` |
| 6 | ScriptType | `07b8dea7...` | **4** | `ead26004...` | `UnityEngine.BoxCollider` |
| 7 | ScriptType | `e613b6ac...` | **5** | `1909f56b...` | `UnityEngine.Mesh` |
| 8 | ScriptType | `ac46b893...` | **6** | `1909f56b...` | `UnityEngine.Material` |

两个值得注意的点：

1. **Asset 依赖（0/1）的 Version=1**；**ScriptType 依赖的 Version 是 0..6**，不是默认 1。原因是源码里 `uniqueTypes.Select(cache.GetCacheEntry)` 绑定到了 `Enumerable.Select` 的**带索引重载** `Func<T, int, TResult>`，索引被当作 `version` 参数传进去了。
2. ScriptType 的 `Hash` 是**按类型序列化 layout** 算的（`CalculatePlayerSerializationHashForType`），所以相同 layout 的类型 hash 相同：`GameObject` / `Mesh` / `Material` 都是 `1909f56b...`，`MeshFilter` / `Transform` 都是 `3c319971...`，`MeshRenderer` / `BoxCollider` 都是 `ead26004...`。
   这也意味着“类型依赖”对脚本序列化布局变化很敏感——这是依赖缓存能感知 layout 变化的原因之一。

### 17.5 其他任务的 `Data` schema

| 任务 | `CachedInfo.Data` |
|---|---|
| `CalculateSceneDependencyData` | `{ SceneDependencyInfo, BuildUsageTagSet, Hash128 prefabDependency, List<ObjectTypes> }`（4 项） |
| `WriteSerializedFiles` | `{ WriteResult, SerializedFileMetaData }`（2 项） |
| `ArchiveAndCompressBundles` | `{ BundleDetails }`（1 项） |

所以 `.info` 不是统一格式，而是“通用壳（CacheEntry + Dependencies）+ 任务自定义 Data”。

### 17.6 怎么在 debugger 里看到它

两个位置最方便：

1. `BuildCache.LoadCachedData` 内部：`DeSerialize<CachedInfo>` 之后、`HasAssetOrDependencyChanged` 之前；
2. `CalculateAssetDependencyData.RunInternal` 的缓存命中分支：

```csharp
if (cachedInfo != null && cachedInfo[i] != null)
{
    var objectTypes = cachedInfo[i].Data[4] as List<ObjectTypes>;
    var assetInfos  = cachedInfo[i].Data[0] as AssetLoadInfo;
    var objectDependencyInfo = cachedInfo[i].Data[5] as List<ObjectDependencyInfo>;
    ...
}
```

也可以直接用 Ex20 的 `LoggingBuildCache` 拿到每次 Load/Save 的 entry；`CachedInfo.Data` 需要断点或写自定义 dump。

### 17.7 和前面实测数据的对应

- `CachedInfo[0].Asset.Hash = 2ac0aacc...` 对应 §16 的 depHash `127b22b8...` + Version `-6`；
- `Dependencies[9]` 对应 Ex20 报告的 `deps=9`；
- `Data[4].Count=7` 与 §4.2 的 `included 5 + referenced 2` 对得上；
- `Data[0].includedObjects[4]` 就是 §4.6 prefabA 里的 BoxCollider（fileID `6277825710962230088`）。

---




