# 子 Wiki：SBP 确定性构建（Determinism）

> 对应主 Wiki：[SBP-Lab-Wiki.md](../SBP-Lab-Wiki.md) · 实验案例：`SbpLabEx17_Determinism.cs`（Ex17）
> 源码版本：`com.unity.scriptablebuildpipeline` 1.23.4（Unity 2022.3）
> 源码路径前缀：`Library/PackageCache/com.unity.scriptablebuildpipeline@1.23.4/`

---

## 0. 先给结论

| 问题 | 结论 |
|---|---|
| 同一环境、同一平台、同一参数、同一资源状态，构建两次，产物一致吗？ | ✅ 通常**字节级一致**（Ex17 A vs B 实测） |
| 走缓存构建，产物和冷构建一致吗？ | ✅ 一致（缓存存的是 artifact 本身） |
| 换一个构建参数（如 `ContiguousBundles`）还一致吗？ | ❌ 不同，而且**内容 hash 也会变** |
| 跨 Unity 版本 / 跨平台还一致吗？ | ❌ 不保证；文件字节、甚至 `shared` 的内容 hash 都可能不同 |

“确定性”在 SBP 里的准确含义是：**给定同一套构建指纹（Editor 版本 + 平台 + 参数 + 资源导入状态），产物内容可复现**。它保证的是 `IBundleBuildResults` 里的内容 hash 和依赖布局稳定，而不是“任何情况下字节都一样”。

---

## 1. 源码地图

| 主题 | 文件 | 关键符号 |
|---|---|---|
| ID 生成接口 | `Editor/Interfaces/IDeterministicIdentifiers.cs` | `GenerateInternalFileName` / `SerializationIndexFromObjectIdentifier` |
| 默认 ID 算法 | `Editor/Shared/Unity5PackedIdentifiers.cs`、`PrefabPackedIdentifiers.cs`、`LinearPackedIdentifiers.cs` | MD4 / MD5 / 自增 |
| ID 选择 | `Editor/ContentPipeline.cs`（`BuildContext` 注册段） | `ContiguousBundles ? PrefabPacked : Unity5Packed` |
| 序列化顺序 | `Editor/Tasks/GenerateBundleCommands.cs` | `GetSortIndex`、`AssetLoadInfoCompare`、`GetSortedSceneObjectIdentifiers` |
| 打包布局/内部名 | `Editor/Tasks/GenerateBundlePacking.cs` | `PackAssetBundle`、`PackSceneBundle`、`archive:/CAB-*` |
| 依赖图稳定 | `Editor/Tasks/GenerateBundleMaps.cs` | forward `BuildReferenceMap`、reverse `BuildUsageTagSet` |
| 子资源顺序 | `Editor/Tasks/GenerateSubAssetPathMaps.cs` | `Representations` 插入顺序 |
| 内容 hash | `Editor/Tasks/WriteSerializedFiles.cs` | `CalculateFileMetadata`、`SerializedFileMetaData` |
| Bundle hash | `Editor/Tasks/ArchiveAndCompressBundles.cs` | `CalculateHashVersion`、`BundleDetails.Hash` |
| 文件名带 hash | `Editor/Tasks/AppendBundleHash.cs` | `details.Hash` |
| 回调版本 | `Editor/Utilities/BuildInterfacesWrapper.cs`、`VersionedCallbackAttribute.cs` | `SceneCallbackVersionHash` / `ShaderCallbackVersionHash` |
| 写操作 hash | `Editor/WriteTypes/*.cs` | `AssetBundleWriteOperation.GetHash128` 等 |
| 全局偏好 | `Editor/Utilities/Preferences.cs` | `useV2Hasher`、`fileIDHashSeed`、`prefabPackedHeaderSize`、`slimWriteResults` |

---

## 2. 确定性发生在哪几个环节

```
Asset GUID / 路径
  │ ① ID 生成（IDeterministicIdentifiers）
  ▼
localID + 内部文件名 CAB-xxx
  │ ② 排序（对象/asset/子资源）
  ▼
WriteCommand.serializeObjects（有序）
  │ ③ 依赖图裁剪（GenerateBundleMaps：只保留真正引用的对象）
  ▼
ContentBuildInterface.WriteSerializedFile（native）
  │ ④ 内容 hash（跳过 header）
  ▼
SerializedFileMetaData.ContentHash / RawFileHash
  │ ⑤ Bundle hash = hash(ContentHash[], dependencies)
  ▼
BundleDetails.Hash（→ 缓存 key / 文件名 / 更新判断）
```

下面按环节拆。

---

## 3. ID 生成：确定性的第一层

### 3.1 接口

`Editor/Interfaces/IDeterministicIdentifiers.cs`：

```csharp
public interface IDeterministicIdentifiers : IContextObject
{
    string GenerateInternalFileName(string name);
    long SerializationIndexFromObjectIdentifier(ObjectIdentifier objectID);
}
```

- `GenerateInternalFileName`：给 bundle/场景生成**内部文件名**（产物里的 `CAB-xxx`）；
- `SerializationIndexFromObjectIdentifier`：给每个 Object 生成 **SerializedFile 内的 localID**。

### 3.2 默认选择逻辑

`Editor/ContentPipeline.cs`：

```csharp
// If IDeterministicIdentifiers was passed in with contextObjects, don't add the default
if (!buildContext.ContainsContextObject(typeof(IDeterministicIdentifiers)))
    buildContext.SetContextObject(parameters.ContiguousBundles
        ? new PrefabPackedIdentifiers()
        : (IDeterministicIdentifiers)new Unity5PackedIdentifiers());
```

**这是 Ex17 里 A vs E 不同的根源**：`ContiguousBundles` 不只是布局开关，而是切换了整套 ID 算法。想自定义 ID（比如固定种子、迁移兼容），把 `IDeterministicIdentifiers` 作为 contextObject 传进去即可覆盖。

### 3.3 三个实现

**`Unity5PackedIdentifiers`（MD4，`ContiguousBundles=false` 时用）**

```csharp
public virtual string GenerateInternalFileName(string name)
    => "CAB-" + HashingMethods.Calculate<MD4>(name);

public virtual long SerializationIndexFromObjectIdentifier(ObjectIdentifier objectID)
{
    ...
    if (objectID.fileType == FileType.MetaAssetType || objectID.fileType == FileType.SerializedAssetType)
        hash = HashingMethods.Calculate<MD4>(objectID.guid.ToString(), objectID.fileType, objectID.localIdentifierInFile);
    else
        hash = HashingMethods.Calculate<MD4>(objectID.filePath, objectID.localIdentifierInFile);
    return BitConverter.ToInt64(hash.ToBytes(), 0);
}
```

注释写明：**与内置 `BuildPipeline.BuildAssetBundles` 的内部算法一致**，所以旧管线迁移能对齐 ID。

**`PrefabPackedIdentifiers`（MD5，默认）**

```csharp
assetHash  = HashingMethods.Calculate(objectID.guid, objectID.filePath);
objectHash = HashingMethods.Calculate(objectID);
// 取 assetHash 高 32 位 + (objectHash ^ assetHash) 低 32 位
return (long)((0xFFFFFFFF00000000 & assetVal) | (0x00000000FFFFFFFF & (objectVal ^ assetVal)));
```

目的：**同一资源的对象在文件里聚在一起**（提高加载性能）；`prefabPackedHeaderSize` 控制用多少字节的 assetHash 作“头”，用于避免 ID 碰撞。

**`LinearPackedIdentifiers`**：`Index++`，只有对象顺序确定才确定——测试/特殊场景用。

### 3.4 两个 ID 相关偏好（会进入构建指纹）

`Editor/Utilities/Preferences.cs`：

```csharp
public int fileIDHashSeed = 0;          // 额外种子，避免 fileID 碰撞
public int prefabPackedHeaderSize = 2;  // PrefabPacked 头大小（1..4）
```

官方描述：
- *FileID Generator Seed: ... changes the layout of all objects in all bundles and we suggest not changing this value after release.*
- *Prefab Packed Header Size: ... changes the layout of all objects in all bundles and we suggest not changing this value after release.*

> 注意：这两项保存在 `ProjectSettings/ScriptableBuildPipeline.json`，**应该提交到版本库**；`CompareAndSet` 只负责保存设置，不会主动清缓存，但改了它们等于改了 ID → 产物/缓存 key 全变。

---

## 4. 排序：确定性的第二层

### 4.1 场景对象排序

`Editor/Tasks/GenerateBundleCommands.cs`：

```csharp
static int GetSortIndex(System.Type type)
{
    // Closely matches CalculateSortIndex in C++, not 100% as the same info is not available to C#, doesn't need to be 100%
    if (type == null) return int.MaxValue - 5;
    if (type == typeof(MonoScript)) return int.MinValue;
    if (typeof(ScriptableObject).IsAssignableFrom(type)) return int.MaxValue - 4;
    if (typeof(MonoBehaviour).IsAssignableFrom(type)) return int.MaxValue - 3;
    if (typeof(TerrainData).IsAssignableFrom(type)) return int.MaxValue - 2;
    return System.BitConverter.ToInt32(HashingMethods.Calculate(type.Name).ToBytes(), 0);
}

internal static List<ObjectIdentifier> GetSortedSceneObjectIdentifiers(List<ObjectIdentifier> objects)
{
    ...
    return sortedObjects.OrderBy(x => x.sortIndex).Select(x => x.objectId).ToList();
}
```

上下文注释很关键：

> *`ContentBuildInterface.PrepareScene` was not returning stable sorted references, causing a indeterminism and loading errors in some cases. Add correct sorting here until patch lands to fix the API.*

也就是说：**native 侧不保证稳定顺序，SBP 在 C# 层补排序**。自定义任务如果绕过这个排序直接写 scene 对象，可能引入非确定性。

### 4.2 bundle 内 asset 列表排序

```csharp
internal static int AssetLoadInfoCompare(AssetLoadInfo x, AssetLoadInfo y)
{
    if (x.asset != y.asset) return x.asset.CompareTo(y.asset);
    ...
}
...
bundleAssets.Sort(AssetLoadInfoCompare);
```

### 4.3 子资源（SubAsset）

`GenerateSubAssetPathMaps` 按 `ExtendedData.Representations` 的顺序插入 `bundleAssets`，并把目标 subAsset 的 includedObjects `Swap(0, index)` 放到首位——顺序来自依赖计算，稳定。

---

## 5. 内部文件名与写命令

`GenerateBundlePacking.PackAssetBundle`：

```csharp
var internalName = string.Format(CommonStrings.AssetBundleNameFormat,
                                 m_PackingMethod.GenerateInternalFileName(bundleName));
// CommonStrings.AssetBundleNameFormat = "archive:/{0}/{0}"
```

`GenerateBundleCommands` 构造 `SerializationInfo`：

```csharp
var serializationInfo = new SerializationInfo
{
    serializationObject = obj,
    serializationIndex = packingMethod.SerializationIndexFromObjectIdentifier(obj)
};
if (consumedIds.TryGetValue(serializationInfo.serializationIndex, out var priorObject))
{
    string msg = string.Format(@"File '{0}' contains a file identifier collision between {1} ... Objects will be missing from the bundle!", ...);
    // 冲突会报错（除非是同一个 object）
}
command.serializeObjects.Add(serializationInfo);
```

所以：**内部文件名 + localID 都由 `IDeterministicIdentifiers` 决定**，换实现/换种子 = 换产物。

---

## 6. 依赖图稳定：避免“无关变化”传染

`GenerateBundleMaps` 的关键注释：

```csharp
// BuildReferenceMap details what objects exist in other bundles that objects in a source bundle depend upon (forward dependencies)
// BuildUsageTagSet details the conditional data needed to be written by objects in a source bundle that is in used by objects in other bundles (reverse dependencies)
```

填充 reference map 时：

```csharp
foreach (var serializedObject in dependentCommand.serializeObjects)
{
    // Only add objects we are referencing. This ensures that new/removed objects to files we depend upon
    // will not cause a rebuild of this file, unless are referencing the new/removed objects.
    if (!objectDependencies.Contains(serializedObject.serializationObject))
        continue;
    referenceMap.AddMapping(file, serializedObject.serializationIndex, serializedObject.serializationObject);
}
```

这是**增量正确性**的核心：依赖 bundle 新增/删除一个对象，只要不是本 bundle 引用的那个，就不会影响本 bundle 的写盘 hash。

---

## 7. 内容 hash：跳过 SerializedFile header

`WriteSerializedFiles.CalculateFileMetadata`：

```csharp
foreach (ResourceFile file in result.resourceFiles)
{
    RawHash fileHash = HashingMethods.CalculateFile(file.fileName);
    RawHash contentHash = fileHash;
    fullHashObjects.Add(fileHash);
    if (file.serializedFile && result.serializedObjects.Count > 0)
    {
        ObjectSerializedInfo firstObj = result.serializedObjects.First(x => x.header.fileName == file.fileAlias);
        using (var stream = new FileStream(file.fileName, FileMode.Open, FileAccess.Read))
        {
            stream.Position = (long)firstObj.header.offset;   // 跳过 header
            contentHash = HashingMethods.CalculateStream(stream);
        }
    }
    contentHashObjects.Add(contentHash);
}
data.RawFileHash  = HashingMethods.Calculate(fullHashObjects).ToHash128();
data.ContentHash  = HashingMethods.Calculate(contentHashObjects).ToHash128();
```

`SerializedFileMetaData` 的注释：

> *serialized files exclude the header of their content which allows this hash not to change with new Unity versions.*

两个 hash 的用途不同：

| hash | 计算范围 | 用途 |
|---|---|---|
| `RawFileHash` | 整个 SerializedFile（含 header） | artifact 缓存 key（ArchiveAndCompress） |
| `ContentHash` | 跳过 header 的内容 | **Bundle hash / 更新判断**，跨 Unity 版本稳定 |

> 注意：`ContentHash` 跨版本稳定 ≠ 缓存跨版本共享。缓存另外用 `m_GlobalHash`（含 Unity 版本）隔离，见缓存子 Wiki。

---

## 8. Bundle hash：内容 + 依赖，不含压缩

`ArchiveAndCompressBundles.CalculateHashVersion`：

```csharp
internal static Hash128 CalculateHashVersion(ArchiveWorkItem item, string[] dependencies)
{
    List<Hash128> hashes = new List<Hash128>();
    hashes.AddRange(item.SeriliazedFileMetaDatas.Select(x => x.ContentHash));
    return HashingMethods.Calculate(hashes, dependencies).ToHash128();
}
```

结果写进 `BundleDetails.Hash`，然后：

- `AppendBundleHash` 用它改文件名：

```csharp
var newFileName = string.Format("{0}_{1}{2}", filenameWithoutExtension, details.Hash, Path.GetExtension(oldFileName));
```

- `IBundleBuildResults.BundleInfos` 里对外暴露；
- 你们的 CDN/更新系统通常拿它做“逻辑内容变化”判断。

**压缩方式不在公式里**：换 LZMA/LZ4/Uncompressed，`BundleDetails.Hash` 不变，但文件字节、大小、`Crc` 都会变（CRC 来自 `ContentBuildInterface.ArchiveAndCompress` 的返回值）。

---

## 9. 回调版本 hash：改回调不会误用旧结果

`VersionedCallbackAttribute`（`Editor/Utilities/VersionedCallbackAttribute.cs`）：

```csharp
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public class VersionedCallbackAttribute : Attribute
{
    public readonly float version;
    public VersionedCallbackAttribute(float version) { this.version = version; }
}
```

`BuildInterfacesWrapper.GatherCallbackVersions` 扫描实现 `IPreprocessShaders` / `IPreprocessComputeShaders` / `IProcessScene(WithReport)` 且带该特性的类型，算出：

```csharp
SceneCallbackVersionHash = HashingMethods.Calculate(sceneInputs).ToHash128();
ShaderCallbackVersionHash = HashingMethods.Calculate(shaderInputs).ToHash128();
```

使用位置：

- `BuildCacheUtility.GetCacheEntry`：`.unity` 资源的 entry hash 混入 `SceneCallbackVersionHash` + `PlayerSettings.stripUnusedMeshComponents`；
- `WriteTypes/*.GetHash128`：AssetBundle/Raw 写操作混入 `ShaderCallbackVersionHash`，Scene 写操作混入 Scene 版本 hash。

所以：**改 IProcessScene/IPreprocessShaders 实现后，记得给 `VersionedCallbackAttribute` 的 version 加一**，否则产物可能“改了没生效”。

---

## 10. 关键配置项（`ProjectSettings/ScriptableBuildPipeline.json`）

| 配置 | 默认 | 对确定性的影响 |
|---|---|---|
| `ContiguousBundles`（参数，不在 json） | `true`（2021.1+） | 切换 `PrefabPackedIdentifiers` / `Unity5PackedIdentifiers`，**改变所有对象布局** |
| `useV2Hasher` | 新项目 true；升级项目 false | SpookyHash vs MD5，**所有 hash 变**（官方说明：会 invalidate 现有 build cache） |
| `fileIDHashSeed` | 0 | 参与 ID hash，改变所有 bundle 布局 |
| `prefabPackedHeaderSize` | 2（已有设置文件的项目为 4） | PrefabPacked ID 头大小，改变布局 |
| `slimWriteResults` | true | 缓存 WriteResults 时裁剪数据（只影响缓存体积/内容，不影响产物） |
| `logCacheMiss` | false | 只影响日志 |
| `threadedArchiving` | true | 只影响归档并行 |
| `useDetailedBuildLog` | false | 只影响 trace 细节 |
| `maximumCacheSize` | 20（GB） | 只影响缓存清理 |

> 这些设置通过 `CompareAndSet` 保存，**建议把 json 提交版本库**；改 `ContiguousBundles` / `useV2Hasher` / seed / headerSize 都要按“构建指纹变更”处理（接受一次全量重建）。

---

## 11. 实测（Ex17）

`SbpLabEx17_Determinism.cs` 的 A~E 五组：

| 组 | 配置 | 目的 |
|---|---|---|
| A | `UseCache=false` 第一次 | 基准 |
| B | `UseCache=false` 第二次 | 验证“同输入可复现” |
| C | 清缓存 + `UseCache=true` | 冷缓存基准 |
| D | `UseCache=true` 再跑 | 验证“缓存不改变产物” |
| E | `ContiguousBundles=false` | 对照组：换配置应不同 |

StandaloneWindows64 实测：

| 对比 | 文件字节 | 内容 hash |
|---|---|---|
| A vs B | ✅ 一致 | ✅ 一致 |
| C vs D | ✅ 一致 | ✅ 一致 |
| B vs D | ✅ 一致 | ✅ 一致 |
| A vs E | ❌ 不同 | ❌ 不同（预期） |

A/B/C/D 的 hash（Standalone）：

```
props_a 61222da9f2feecb6d312bd46dc4929c9
props_b fa61d49e4ee0f13eff0bb32488100470
shared  699f53d56c5e640866f990bb3c97b6ab
```

Android 下跑同一实验：

- `props_a` / `props_b` 的内容 hash 与 Standalone **相同**（预制体序列化数据平台无关）；
- `shared` 的内容 hash **不同**（含平台相关的内置 shader 数据）；
- 文件 MD5 普遍不同（SerializedFile header/平台细节）。

> 结论：`ContentHash` 可以跨平台保持稳定，但**文件字节不一定**；跨平台比较 hash/大小没有意义。

---

## 12. 怎么验证 / CI 门禁

**最小门禁（照 Ex17）**：

1. 同一 runner 上构建两次（建议一次 `UseCache=false`、一次 `UseCache=true`）；
2. 逐文件比对 MD5 + 比对 `BundleDetails.Hash`；
3. 任何不一致 → 报警，输出差异文件列表。

**更严格**：

- 跨 runner 抽检：第二台机器同 Editor/同配置构建，对比 `BundleDetails.Hash`；
- 把构建指纹写进报告：Editor 版本、SBP 版本、平台、`ContiguousBundles`、压缩、`useV2Hasher`、seed、headerSize、自定义任务 Version、`ScriptableBuildPipeline.json` 内容；
- 自定义任务参与产物时，单独验证它的输出确定性（同一输入两次运行）。

---

## 13. 常见坑

1. **改了 `ContiguousBundles` 却按“同配置”对比 hash** → 误判为构建不稳定（Ex17 A vs E）。
2. **改回调没加 `VersionedCallbackAttribute.version`** → 依赖/写盘缓存命中旧结果，表现为“改了没生效”。
3. **改 `fileIDHashSeed` / `prefabPackedHeaderSize` 没当构建指纹** → 历史 CDN 全量、diff 全红。
4. **切 `useV2Hasher` 没清缓存** → 官方说明会 invalidate 现有 build cache，实际表现为大量 cache miss / 产物 hash 全变。
5. **跨平台/跨 Unity 版本比较内容 hash**：`shared` 这类含内置 shader 的 bundle 会不同，别误判。
6. **把 `RawFileHash` 和 `ContentHash` 混用**：前者含 header，后者不含；更新判断该用 ContentHash，缓存 key 用 RawFileHash。
7. **把文件 MD5 当内容身份**：压缩/平台/header 都会影响 MD5，但逻辑内容可能没变。
8. **自定义任务绕过排序/ID 生成器**：直接往 `serializeObjects` 塞对象、自己生成文件名 → 破坏确定性。
9. **`ScriptableBuildPipeline.json` 没进版本库** → 不同机器 seed/headerSize 不同，产物不一致。
10. **以为“确定性”等于“跨版本可复现”**：TypeTree、shader 编译、纹理压缩都会随版本/平台变化。

---

## 14. 练习题

1. 把 Ex17 的 E 组改成“同样 `ContiguousBundles`，但换 `fileIDHashSeed`”，观察哪些内容 hash 变了。
2. 写一个自定义 `IDeterministicIdentifiers`（比如用 SHA1 的简化版），对比默认实现产物的差异。
3. 在 Ex17 里加一组“改了 IProcessScene 回调 + version”，验证 `SceneCallbackVersionHash` 进入缓存 key 的效果（配合缓存子 Wiki）。
4. 用一个自定义任务故意打乱 `serializeObjects` 顺序，跑两次，观察 `BundleDetails.Hash` 是否稳定（提示：不会）。

---

## 15. 源码索引（按阅读顺序）

1. `Editor/ContentPipeline.cs` —— 注册 identifiers、cache、callbacks 的位置
2. `Editor/Shared/BuildParameters.cs` —— `ContentBuildFlags`、`GetContentBuildSettings`
3. `Editor/Shared/Unity5PackedIdentifiers.cs` / `PrefabPackedIdentifiers.cs` / `LinearPackedIdentifiers.cs`
4. `Editor/Tasks/GenerateBundlePacking.cs` —— 内部文件名、布局、依赖过滤
5. `Editor/Tasks/GenerateBundleCommands.cs` —— 排序、localID、碰撞检测
6. `Editor/Tasks/GenerateSubAssetPathMaps.cs`
7. `Editor/Tasks/GenerateBundleMaps.cs` —— forward/reverse 依赖
8. `Editor/Tasks/WriteSerializedFiles.cs` —— `CalculateFileMetadata`、`GetCacheEntry`
9. `Editor/Tasks/ArchiveAndCompressBundles.cs` —— `CalculateHashVersion`、缓存
10. `Editor/Tasks/AppendBundleHash.cs`
11. `Editor/WriteTypes/*.cs` —— 各写操作的 `GetHash128`
12. `Editor/Utilities/BuildInterfacesWrapper.cs` + `VersionedCallbackAttribute.cs`
13. `Editor/Utilities/Preferences.cs` —— 全局构建设置
14. `Editor/Utilities/HashingMethods.cs` —— hash 算法（SpookyHash/MD5/MD4）

> 下一层（native）：`ContentBuildInterface.WriteSerializedFile` / `ArchiveAndCompress`。SBP 这一层负责“输入 → 确定调度”，字节级序列化在 C++，因而跨版本/平台不保证一致。
