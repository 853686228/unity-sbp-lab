# SBP 缓存失效图谱：哪些情况下 Hash / CacheEntry 会变

> 姊妹文档：[SBP-BuildCache.md](SBP-BuildCache.md)（实现原理与架构）
> 实验证据：[ex20-cache-probe.txt](ex20-cache-probe.txt)、[ex21-asset-hash-probe.txt](ex21-asset-hash-probe.txt)
> 实验脚本：`Assets/Editor/SbpLab/SbpLabEx20_CacheProbe.cs`、`SbpLabEx21_AssetHashProbe.cs`

---

## 0. 先看四层失效模型

SBP 判断一个缓存条目能不能用，要同时过四层：

```text
第 1 层：GlobalHash    hash(unityVersion, k_Version)        → 决定缓存根路径
第 2 层：CacheEntry.Guid  任务/资产的身份                    → 决定 entry 目录
第 3 层：CacheEntry.Version  任务版本                        → 进 Hash，同时参与相等性比较
第 4 层：CacheEntry.Hash  任务输入的 hash                    → 决定 finalHash 路径
         + CachedInfo.Dependencies 逐个 depend 校验          → 依赖变了也判 miss
```

任何一层变化都会导致 miss，只是影响范围不同：

| 层 | 变化影响范围 |
|---|---|
| GlobalHash | **所有任务、所有 entry** |
| Guid | 该 entry（通常某任务/某资产/某文件） |
| Version | 该任务的所有 entry |
| Hash / 依赖校验 | 单个 entry 及其上层依赖者 |

---

## 1. 全局层：所有缓存一起失效

`BuildCache.CalculateGlobalArtifactVersionHash()`：

```csharp
return HashingMethods.Calculate(Application.unityVersion, k_Version).ToHash128();
```

路径：

```text
finalHash = hash(GlobalHash, entry.Hash)
Library/BuildCache/<guid前2位>/<guid>/<finalHash>/
```

| Case | 为什么全失效 |
|---|---|
| 换 Unity 版本（含 patch，如 `2022.3.17f1c1 → 2022.3.18f1c1`） | `Application.unityVersion` 变 |
| SBP 升级改了 `BuildCache.k_Version`（1.23.4 是 5） | `k_Version` 变 |
| 切换 `ScriptableBuildPipeline.useV2Hasher`（SpookyHash ↔ MD5） | `HashingMethods.Calculate` 结果全变；GlobalHash 与 entry.Hash 都变 |
| 手动 `PurgeCache` / 删除 `Library/BuildCache` | 文件不存在 → miss |
| 后台 `PruneCache` 按 20GB 上限 + 访问时间删掉旧目录 | 文件被回收 → miss |
| 换一台机器（同版本） | 本地库不存在；Cache Server 已废弃，不会远端命中 |

> “失效”不等于“删除”：旧版本的目录会留在磁盘上，直到 Purge/Prune。

**实测值**：本机冷构建 `GlobalHash = 16b4790ca43db58c76243dd04115a209`（Unity 2022.3.17f1c1 / SBP 1.23.4）。

---

## 2. Guid 层：entry 的身份变了

`CacheEntry.Guid` 是“这个 entry 是谁”的标识，不同任务来源不同：

| 任务 | Guid 来源 |
|---|---|
| Asset 依赖 | **asset GUID 本身**（`BuildCacheUtility.GetCacheEntry(GUID)`） |
| Scene 依赖 | scene GUID |
| WriteSerializedFiles | `hash("WriteSerializedFiles", operation.Command.fileName).ToGUID()` |
| ArchiveAndCompressBundles | `hash("ArchiveAndCompressBundles", bundleName).ToGUID()` |
| File 依赖 | `hash("FileHash", path).ToGUID()` |
| Type 缓存 | `hash("TypeHash", assemblyQualifiedName).ToGUID()` |

会改变 Guid 的 case：

- **资产 GUID 变化**（删除重建资产、换 GUID）：asset 依赖 entry 全换；
- **内部文件名变化**：`WriteSerializedFiles` 的 `Command.fileName` 来自 `GenerateInternalFileName(bundleName)`，受下面影响：
  - `ContiguousBundles`（`PrefabPackedIdentifiers` ↔ `Unity5PackedIdentifiers`）
  - `useV2Hasher`
  - bundle 名本身（改布局、改 bundle 名）
- **bundle 名变化**：`ArchiveAndCompressBundles` 的 Guid 变；
- **文件路径变化**：非 AssetDatabase 文件（`GetCacheEntry(string path)`）的 Guid 变。

> 注意：`CacheEntry.Hash` 变了**不会**改 Guid，只会改 `finalHash` 目录；两者是独立的。

---

## 3. Version 层：任务版本 / 模式变了

`CacheEntry.Version` 直接参与：

- entry 相等性比较；
- 多数任务的 `Hash` 组合（例如 `BuildCacheUtility.GetCacheEntry` 会把 Version 混进 Hash）。

| Case | 例子 |
|---|---|
| 任务代码更新后 `Version++` | SBP 升级、你们改自定义任务逻辑 |
| SBP 的 `kVersion` 常量变化 | `ArchiveAndCompressBundles.kVersion = 2`、`CalculateAssetDependencyData.kVersion = 6` |
| `NonRecursiveDependencies` 开关 | `CalculateAssetDependencyData` 用 `-Version` 表示非递归模式，实测 entry 显示 `version=-6` |
| 自定义任务的算法/布局变了但忘了 bump Version | **不会失效**，会错误命中旧结果（最常见的坑） |

---

## 4. Hash 层：各任务 key 里到底装了什么

### 4.1 CalculateAssetDependencyData（Asset 依赖）

```csharp
entry.Hash = AssetDatabase.GetAssetDependencyHash(path);
// invalid 且文件存在时：Hash = CalculateFile(path)
// .unity 额外混入：SceneCallbackVersionHash + PlayerSettings.stripUnusedMeshComponents
entry.Hash = hash(entry.Hash, entry.Version);
```

| Case | 效果 |
|---|---|
| 修改资产自身序列化属性 | `GetAssetDependencyHash` 变 → miss（实测：CubeA prefab 改 transform） |
| 修改导入设置 / 重新导入 | 变 → miss |
| 资产依赖的其他资产变化 | **该资产的 depHash 可能不变**，但依赖校验（§5）会让它 miss |
| Unity 版本变化 | GlobalHash 已先失效；依赖 hash 也可能变 |
| 文件 hash fallback（非 AssetDatabase 文件） | 文件内容变 → miss |

**实测**（Ex21，改 CubeA.prefab transform）：

```text
depHash: 127b22b8... → 0bb38b75...
entry.Hash: 2ac0aacc... → cf07eed2...
CalculateAssetDependencyData: hits=3 misses=1
```

### 4.2 CalculateSceneDependencyData（Scene 依赖）

- scene 自身依赖 hash（同上）；
- `.unity` 额外混入 `BuildInterfacesWrapper.SceneCallbackVersionHash`、`PlayerSettings.stripUnusedMeshComponents`；
- 依赖：prefab 的 CacheEntry（`prefabEntries`）+ `prefabDependency` hash。

会变的 case：

- 改场景/改引用的 prefab；
- `IProcessScene` 回调的 `VersionedCallbackAttribute.version` 变化；
- `stripUnusedMeshComponents` 变化。

### 4.3 WriteSerializedFiles

```csharp
entry.Hash = hash(
    Version,
    operation.GetHash128(log),          // Command、对象、usage、reference map、bundle info、shader 回调版本
    settings.GetHash128(),              // BuildSettings：target/group/typeDB/buildFlags 等
    globalUsage,                        // GraphicsSettings 推导的全局 usage
    slimWriteResults,                   // ScriptableBuildPipeline.slimWriteResults
    GetPlayerSettingsHash128(target)    // stripUnusedMeshComponents / mipStripping / GraphicsAPIs 等
);
```

| Case | 效果 |
|---|---|
| 改 Asset 内容/布局/地址 → 对象或 bundleInfo 变 | Write entry 变（实测：CubeA prefab 改后 props_a entry `1c2cdf94 → 09eab543`） |
| 改压缩方式 | **不影响 Write**（压缩只影响 Archive） |
| 改 `target` / `group` / `buildFlags` / TypeDB | settings hash 变 |
| 改 `slimWriteResults` | 进 key，变 |
| `IProcessShaders` 回调 version 变 | `ShaderCallbackVersionHash` 进 `operation.GetHash128`，变 |
| `fileIDHashSeed` / `prefabPackedHeaderSize` / `ContiguousBundles` | localID/内部名变 → operation hash 变 |
| PlayerSettings（stripUnusedMeshComponents/mipStripping/GraphicsAPIs） | 变 |

### 4.4 ArchiveAndCompressBundles

```csharp
entry.Hash = hash(kVersion, compression, resources(serializedFile/fileAlias), RawFileHash[]);
```

| Case | 效果 |
|---|---|
| Write 产物（RawFileHash）变化 | Archive entry 变（实测：改 CubeA 后 props_a `5590c49a → d7b93e7e`） |
| **压缩方式/blockSize 变化** | 只有对应 bundle 的 Archive entry 变，Write entry 不变 |
| 改 bundle 名 | Guid 变 |
| 改 `ContentBuildFlags.DisableWriteTypeTree` | 影响 Write 的 settings hash → RawFileHash 变 → Archive 也跟着变 |
| 改 `ContentBuildFlags.StripUnityVersion` | ⚠️ Archive key 里没有这个 flag（源码 `GetCacheEntry` 未包含），切换时建议 Purge |

**实测**（Ex20，冷构建）：

```text
Archive props_a  entry version=2  hash=5590c49a...
Archive shared   entry version=2  hash=3deca829...
```

---

## 5. 依赖校验层：自己的 key 没变，也会 miss

`CachedInfo.Dependencies` 里存了依赖对象的 `CacheEntry`。`LoadCachedData` 会对每个依赖重新计算 entry 并比较：

```csharp
var updatedEntry = GetUpdatedCacheEntry(info.Asset);
if (info.Asset != updatedEntry) → miss
foreach (var dependency in info.Dependencies)
    if (dependency != GetUpdatedCacheEntry(dependency)) → miss
```

**实测**（Ex21，改 Mat.mat 颜色）：

```text
Mat depHash: f9b31f15... → ce9946df...
CubeA/B/C 的 depHash 完全没变
CubeA/B/C 的 entry.Hash 也完全没变

但 CalculateAssetDependencyData: hits=0 misses=4
原因：它们的 CachedInfo.Dependencies 包含 Mat 的 CacheEntry（hash 5e6f11c0 → 909ddf4e）
```

这解释了为什么：

- `GetAssetDependencyHash` 不需要跟踪“谁引用了我”；
- 被引用资产变化也能正确让上层缓存失效；
- 设计自定义任务时，**依赖必须写进 `CachedInfo.Dependencies`**，否则会 stale。

> 与 Data 类型 entry 的差异：Write/Archive 的 `Dependencies` 为空（`deps=0`），因为它们的 key 已经把 `RawFileHash`、settings、compression 全算进去了，不需要额外依赖列表。

### 5.1 这是非递归校验：只看“自己 + 直接依赖”

代码只遍历 `info.Dependencies` **一层**：

- `GetUpdatedCacheEntry` 只是把**这一条** entry 按类型重新算一遍：
  - `Asset` → `GetCacheEntry(GUID, Version)`
  - `File` → `GetCacheEntry(path, Version)`
  - `ScriptType` → `GetCacheEntry(Type, Version)`
  - 其它（含 `Data`）→ **原样返回**
- 不会读“依赖自己的 `CachedInfo.Dependencies`”，也不会递归调用 `HasAssetOrDependencyChanged`；
- `LoadCachedData` 对每个 entry 独立校验、独立置 null，不会“依赖 miss 所以父级一定 miss”。

那传递性失效从哪来？

1. **直接依赖自己的 entry key 变了** → 直接比较就能发现（Ex21 的 Mat 就是这种）；
2. **影响父级缓存数据的输入，必须体现在直接依赖 entry 的 key 里**（Asset 的导入 hash、ScriptType 的序列化 layout hash、File 的 hash）——这是任务设计的契约；
3. “依赖的依赖”如果只改变直接依赖的内部数据、不改变它的身份，父级缓存数据通常也不依赖它。例如 shader 源码变了，prefab 的直接依赖仍然只是 Material 对象；shader 的变化在 Mat 自己的缓存层处理。

边界（自定义任务要注意）：

- 如果自定义任务把 **`Data` 类型 entry 放进 `Dependencies`**，`GetUpdatedCacheEntry` 会原样返回它 → 永远判不出变化；依赖列表应该用 `Asset/File/ScriptType` entry。
- 依赖列表必须覆盖所有影响本期缓存数据的输入；漏掉任何一层就会 stale。
- `VALIDATE_CACHED_DEPENDENCIES` 条件编译分支默认未定义（SBP 1.23.4 源码里没有定义该宏）。
- `logCacheMiss` 打开时不会早退，会把所有依赖检查完再返回；关闭时遇到第一个变化就 `return true`。
- `dependency != GetUpdatedCacheEntry(updatedEntry)` 里实际调用了两次 `GetUpdatedCacheEntry`（第二次作用于已更新的 entry），语义等价，只是写法冗余。

> 结论：`HasAssetOrDependencyChanged` **不是递归**，它校验的是“自己 + 直接依赖的当前 key”；传递性由“直接依赖的 key 必须覆盖其影响”这个契约保证，而不是靠递归遍历。

---

## 6. 不改变 key 的 case / 容易误判的边界

| Case | 会失效吗 | 说明 |
|---|---|---|
| `logCacheMiss`、`logAssetWarnings`、`threadedArchiving`、`maximumCacheSize`、`useDetailedBuildLog` | 否 | 只影响日志/调度/清理，不进 key |
| `AppendHash` | 否 | 归档后重命名文件，不在 cache key 里 |
| `threadedArchiving` | 否 | 归档线程化，不影响产物 |
| 改回调实现但**没加** `VersionedCallbackAttribute.version` | 否 | 回调版本 hash 不变 → 可能错误命中 |
| `StripUnityVersion` 切换 | SBP 1.23.4 的 Archive key 未包含该 flag | 建议切换时 Purge |
| 脚本序列化 layout 变化（MonoBehaviour 字段增删）但资产文件没变 | **本实验未覆盖** | 依赖计算依赖 TypeDB；保守做法：清缓存或 bump 自定义任务 Version |
| 仅重命名资产（GUID 不变） | asset 依赖 key 可能不变 | 但地址/路径进入 `BundleInfo`/`Addresses` 时会影响 Write key；以实际为准 |
| 修改 `.meta` 导入设置 | 通常变 | 视导入器是否产生新 hash |
| 切换 target/platform | Write key 变（target 进 BuildSettings） | asset 依赖数据通常也会因平台导入变化而变化；依赖 key 未显式含 target，注意验证 |

---

## 7. 快速对照表

| Case | 变化层 | 影响范围 |
|---|---|---|
| 升级 Unity | GlobalHash | 全局 |
| SBP 改 `k_Version` | GlobalHash | 全局 |
| 切 `useV2Hasher` | GlobalHash + 所有 hash | 全局 |
| 改 bundle 名 / 布局 | Guid（Archive/Write）+ Hash | 相关 bundle |
| 改 `ContiguousBundles` / `fileIDHashSeed` / `prefabPackedHeaderSize` | Guid + Hash（ID/内部名） | 相关 bundle |
| 改资产属性 | depHash → Hash | 该资产 entry + 下游依赖者 |
| 改资产引用的另一个资产 | 自身 depHash 可能不变，但依赖校验 | 依赖它的 entry 全部 miss |
| 改压缩方式 | Archive Hash | 该 bundle |
| 改 shader 回调 version | Write Hash | 相关文件 |
| 改 `slimWriteResults` | Write Hash | 所有 Write entry |
| 手动 Purge / Prune 回收 | 文件不存在 | 被删的 entry |
| 自定义任务改逻辑但不 bump Version | 不变（错误命中） | 该任务全部 entry |

---

## 8. 怎么验证一个 case 会不会失效

1. **打开 `Log Cache Miss`**：输出 `[Cache Miss]: Source asset changed / Dependency changed`；
2. **看 `buildlogtep.json`**：`LoadCachedData` 的 hit count、`SaveCachedData` 的 count；
3. **用 Ex20 的代理**：`LoggingBuildCache` 记录每次 Load/Save 的 entry（Guid/Version/Hash/hit）；
4. **用 Ex21 直接看 hash**：`AssetDatabase.GetAssetDependencyHash` + 文件 MD5 + entry.Hash 三列对照；
5. 对比两次构建的 entry hash，而不是猜。

---

## 9. 自定义任务 / 自定义 DB 的失效设计清单

- [ ] 任务逻辑/算法变化 → `Version++`；
- [ ] DB schema / writer 版本 → 算进 `CacheEntry.Hash`；
- [ ] 所有影响产物的输入（设置、平台、压缩、回调版本、schema）都进 Hash；
- [ ] 依赖关系写进 `CachedInfo.Dependencies`（不要只靠自己的 Hash）；
- [ ] 回调实现变化 → `VersionedCallbackAttribute.version++`；
- [ ] 内部文件名/标识稳定，不要在 key 里用绝对路径或时间戳；
- [ ] 不要依赖 `AssetDatabase.GetAssetDependencyHash` 覆盖 DB 自定义对象——自己定义内容指纹；
- [ ] 切换 `StripUnityVersion` 等未进 key 的开关时，Purge 一次。

---

## 10. 证据与复现

- 冷/热构建采样（Ex20）：[ex20-cache-probe.txt](ex20-cache-probe.txt)
- 资产属性/依赖变化采样（Ex21）：[ex21-asset-hash-probe.txt](ex21-asset-hash-probe.txt)
- 脚本：`Assets/Editor/SbpLab/SbpLabEx20_CacheProbe.cs`、`SbpLabEx21_AssetHashProbe.cs`
- 菜单：`SBP Lab/Examples/20`、`SBP Lab/Examples/21`
