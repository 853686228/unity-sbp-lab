# IBundleWriteData 与装箱数据结构详解

> 本文是任务链 Wiki 的子文档，专门拆解 Packing 阶段的 `IBundleWriteData`（`AssetToFiles` / `FileToBundle` / `FileToObjects` / `WriteOperations`），以及单个对象从依赖到 bundle 的完整走查。
>
> 依赖阶段（`ObjectIdentifier` / FileType / included vs referenced / prefabA）见姊妹文档：[Dependency-Data-Structures.md](Dependency-Data-Structures.md)。
> 主文档：[任务链 Wiki README](README.md) · 数据来源：`captured/ex18-stage-probe.json`、`captured/ex19-filetypes.txt`

---

## 1. IBundleWriteData 是什么

`IBundleWriteData`（实现类 `BundleWriteData`）是 Packing 阶段的输出，也是 Writing 阶段的输入。它把“资产级依赖”翻译成“文件级装箱单”。

### 1.1 先厘清：file / bundle / SerializedFile 的关系

| 概念 | 含义 | 在 `IBundleWriteData` 里 |
|---|---|---|
| **file** | 一个 **SerializedFile**，内部名 `archive:/CAB-xxx/CAB-xxx`；`WriteOperation` 一个 file 一条 | `FileToObjects` / `FileToBundle` / `WriteOperations` 的 key/单位 |
| **bundle** | 磁盘上的产物容器（AssetBundle `.bundle`，或你们自定义的打包容器）；**一个 bundle 可以包含多个 file** | `FileToBundle[file]` 的值 |
| **object** | SerializedFile 里的对象记录；`localIdentifierInFile` 只在**该 file 内**唯一 | `FileToObjects[file]` 列表元素 |

- lab 本例：3 个 bundle、3 个 file，刚好 1:1（`props_a` / `props_b` / `shared` 各一个 CAB）；
- 场景 bundle 常见“1 个 bundle 多个 file”（主场景 CAB + `.sharedAssets`）；`CreateBuiltInShadersBundle` 抽出的内置 shader 也是独立 bundle/file；
- **`AssetToFiles[guid]` 里出现 2 个 CAB，不代表这个资产被拆成两个文件**：它是“加载该资产需要先加载的 file 列表”——`[0]` 是本资产的主文件（自己的对象都在这里），后面的条目是它引用的其他资产所在的 file。CubeA 的 2 个 CAB 就是“props_a 主文件 + shared 依赖文件”。

### 1.2 结构回顾

```csharp
IBundleWriteData : IWriteData
{
    // IWriteData
    Dictionary<GUID, List<string>> AssetToFiles;          // asset -> 文件列表（[0] = 主文件）
    Dictionary<string, List<ObjectIdentifier>> FileToObjects; // 文件 -> 该文件序列化的对象
    List<IWriteOperation> WriteOperations;                // 每个文件一个写命令

    // IBundleWriteData
    Dictionary<string, string> FileToBundle;              // 文件 -> bundle 名
    Dictionary<string, BuildUsageTagSet> FileToUsageSet;  // 文件 -> usage（变体/裁剪）
    Dictionary<string, BuildReferenceMap> FileToReferenceMap; // 文件 -> 跨文件对象引用
}
```

### 1.3 Ex18 实测（raw debugger 数据）

以下就是 `m_WriteData = BundleWriteData` 在 Packing 之后的内容：

```text
AssetToFiles (Count = 4)
[0] key = b1665a6f... (CubeA.prefab)
    value = [
      "archive:/CAB-c297d6b35da6a83c86b4dadbdfee5d91/CAB-c297d6b35da6a83c86b4dadbdfee5d91",   ← 主文件（props_a）
      "archive:/CAB-ef051277ec27de9e7fc97842ca18b97e/CAB-ef051277ec27de9e7fc97842ca18b97e"    ← 依赖文件（shared）
    ]
[1] 99a41d... (CubeB.prefab)  → 同样是 2 个文件（props_a 主文件 + shared 依赖）
[2] e5c153... (CubeC.prefab)  → 2 个文件（props_b 主文件 + shared 依赖）
[3] 0d70bf... (Mat.mat)       → 1 个文件（shared）

FileToBundle (Count = 3)
  archive:/CAB-c297d6b3.../CAB-c297d6b3...  →  props_a
  archive:/CAB-c2692d0c.../CAB-c2692d0c...  →  props_b
  archive:/CAB-ef051277.../CAB-ef051277...  →  shared

FileToObjects (Count = 3)
  archive:/CAB-c297d6b3...  →  Count = 10
    [0..4] CubeA.prefab 的 5 个对象（GameObject/Transform/MeshFilter/MeshRenderer/BoxCollider）
    [5..9] CubeB.prefab 的 5 个对象
  archive:/CAB-c2692d0c...  →  Count = 5（CubeC.prefab 的 5 个对象）
  archive:/CAB-ef051277...  →  Count = 3（Mat 的 Material + 2 个内置 Shader）
```

注意 `FileToObjects[props_a]` 里全是 `MetaAssetType`、GUID 只有两个 prefab——**内置 Mesh 不在里面**（被 `FilterReferencesForAsset` 过滤掉），**Mat 的 Material 也不在里面**（它作为文件依赖，不复制对象）。

### 1.4 三个字典分别在回答什么

| 字典 | 回答的问题 | 谁消费它 |
|---|---|---|
| `AssetToFiles` | “加载这个资产前，需要先把哪些文件加载好？”（`[0]` 是主文件，其余是依赖文件） | `ArchiveAndCompressBundles` 用它算 bundle 依赖（`CalculateBundleDependencies`）；运行时寻址 |
| `FileToBundle` | “这个内部文件属于哪个 bundle？” | `ArchiveAndCompressBundles` 按 bundle 聚合 `WriteResult` |
| `FileToObjects` | “这个内部文件里到底序列化了哪些对象？” | `GenerateBundleCommands` / `GenerateBundleMaps` / `WriteSerializedFiles` |
| `WriteOperations` | “每个文件怎么写成 SerializedFile？”（`WriteCommand` + usage + reference map） | `WriteSerializedFiles` 逐条执行 |
| `FileToUsageSet` / `FileToReferenceMap` | 变体/裁剪数据、跨文件对象引用 | 写盘和增量缓存 |

### 1.5 为什么 CubeA 的 `AssetToFiles` 有 2 个文件

`GenerateBundlePacking.Run()` 的最后一段（源码）：

```csharp
// Calculate Asset file load dependency list
foreach (var bundle in m_BuildContent.BundleLayout)
{
    foreach (var asset in bundle.Value)
    {
        List<string> files = m_WriteData.AssetToFiles[asset];
        List<GUID> references = assetToReferences[asset];
        foreach (var reference in references)
        {
            List<string> referenceFiles = m_WriteData.AssetToFiles[reference];
            if (!files.Contains(referenceFiles[0]))
                files.Add(referenceFiles[0]);
        }
    }
}
```

- 主文件是 Packing 时写入的：`AssetToFiles[asset] = new List<string> { internalName }`；
- 然后遍历它引用的资产，把“被引用资产的主文件”追加进来；
- CubeA 引用了 `Mat.mat`（在 `shared` bundle），所以追加 `shared` 的主文件；
- 内置 Mesh 没有对应资产（`NonAssetType`），不会出现在这里。

所以 `[0] = 主文件` 是约定：写盘时先加载 `[0]`，再按需加载后续依赖文件。

### 1.6 内部文件名是怎么来的

`FileToBundle` / `FileToObjects` 的 key 是“内部文件名”，格式：

```text
archive:/{CAB-hash}/{CAB-hash}
```

- `CAB-hash` 来自 `IDeterministicIdentifiers.GenerateInternalFileName(bundleName)`；
- `ContiguousBundles = true` 时用 `PrefabPackedIdentifiers`：`"CAB-" + HashingMethods.Calculate(bundleName)`；
- `HashingMethods` 默认算法：新项目（2021.1+）是 SpookyHash，升级上来的老项目可能是 MD5（`useV2Hasher`）；
- 所以**同一个 bundle 名 + 同一套设置 → 内部文件名稳定可复现**，不是随机值。

> 这也解释了为什么 Ex17 里换 `ContiguousBundles` / `useV2Hasher` 后产物 hash 全变：内部文件名和对象 ID 都变了。

### 1.7 各阶段怎么消费这些字典

```text
GenerateBundlePacking
   └─ 填 AssetToFiles / FileToObjects（主文件 + 依赖文件）
GenerateBundleCommands
   └─ 按 file 生成 WriteOperations（Command.serializeObjects = 该 file 的对象 + localID）
GenerateBundleMaps
   └─ 填 FileToUsageSet / FileToReferenceMap（前向引用 + 反向 usage）
WriteSerializedFiles
   └─ 遍历 WriteOperations → WriteResult / SerializedFileMetaData
ArchiveAndCompressBundles
   └─ 用 FileToBundle 把文件聚合成 bundle；用 AssetToFiles 算 bundle 依赖
```

### 1.8 调试时怎么读

1. 展开 `WriteOperations`，每个元素的 `Command.internalName` 应该和 `FileToObjects` 的 key 对上；
2. `Command.serializeObjects.Count` 应该等于 `FileToObjects[fileName].Count`；
3. `FileToUsageSet` / `FileToReferenceMap` 的 key 也是同一批内部文件名；
4. 如果发现某个 file 的对象数不对：先看 `AssetToFiles` 里主文件/依赖文件是否分对了，再看 `FilterReferencesForAsset` 是否过滤了内置资源。

### 1.9 对自定义 DB 的意义

`IBundleWriteData` 是 AssetBundle 语义最重的一层，如果你们把产物单位换成 pack ID，这一层通常要改写：

- `FileToBundle` → `FileToPackId`（或你们的 bundle/DB 分组）；
- `FileToObjects` → DB 里的对象列表（对象身份仍是 `(guid, fileID)` / `(path, fileID)`）；
- `AssetToFiles[0]` 的“主文件 + 依赖文件”语义，可以保留为运行时的加载依赖列表；
- `WriteOperations` 可以保留（替换 `IWriteOperation.Write` 写自定义 DB），这样 `WriteSerializedFiles` 的缓存/调度/元数据计算仍然可用。

---

## 2. 单个对象实例走一遍：`Mat.mat` 的 Material


> 本节用到的 included/referenced 定义见姊妹文档 [Dependency-Data-Structures.md](Dependency-Data-Structures.md) 的 §2。
选一个具体对象实例：`Assets/Bundles/Shared/Mat.mat` 里的 **Material**。

它的源 `ObjectIdentifier`：

```text
guid     = 0d70bf7a4a50f0e459a53aef98ebd4c6   （Mat.mat 的资产 GUID）
fileID   = 2100000                            （文件内对象 ID）
fileType = SerializedAssetType                （数据在 Assets 的序列化资产文件里）
path     = ""
```

| 阶段 | 数据结构 | 它出现的位置 | 说明 |
|---|---|---|---|
| 依赖（自有） | `IDependencyData.AssetInfo[0d70...].includedObjects[0]` | Material 对象 | 数据序列化在 `Mat.mat` 里，所以是 Mat 资产的 included |
| 依赖（被引用） | `AssetInfo[CubeA/B/C].referencedObjects[1]` | 同一个 `ObjectIdentifier` | 3 个 prefab 的 MeshRenderer 都只是“指针”，指向这个 Material |
| 装箱 | `IBundleWriteData.FileToObjects[CAB-ef05...]` | shared 文件的 3 个对象之一（Material + 2 Shader） | `AssetToFiles[Mat]` 只有 1 个文件（shared 主文件） |
| 写命令 | `WriteOperations[shared].Command.serializeObjects` | 其中一个 `SerializationInfo` | 构建时 localID 由 `IDeterministicIdentifiers` 重新生成（见下） |
| 写盘 | `IBuildResults.WriteResults[CAB-ef05...]` | SerializedFile 里的对象记录 | 连同 2 个 Shader 一起写成 shared 的 CAB |
| 归档 | `IBundleBuildResults.BundleInfos[shared]` | `shared.bundle` | `shared` 的 deps=[]；`props_a` / `props_b` 的 deps=[shared] |

### 2.1 同一个对象，为什么在两个 AssetInfo 里角色不同

- 在 `Mat.mat` 自己的 `AssetInfo` 里：它是 **included**（数据归 Mat.mat 拥有）；
- 在 `CubeA.prefab` 的 `AssetInfo` 里：它是 **referenced**（prefab 只持有指向它的指针）。

> “included / referenced” 不是对象的固有属性，而是**某个资产与某个对象的关系**。

### 2.2 源 fileID 不等于构建后的 localID

这是读依赖数据时最容易误会的点之一：

- 源 `fileID = 2100000` 是 **Assets 里 `Mat.mat` 文件内**的对象 ID；
- 构建时 `SerializationInfo.serializationIndex = PrefabPackedIdentifiers.SerializationIndexFromObjectIdentifier(obj)`，是**重新生成的 64 位 ID**（input 含 guid/fileType/localId，经 hash 混合）；
- 所以 bundle 里的 SerializedFile 中，这个对象的 localID **通常不是 2100000**，而是新算出来的值。

这对你们自定义 DB 很关键：**对象身份要保留“源 ObjectIdentifier”，但写入 DB/序列化文件时 localID 是构建期重新分配的**，不能拿源 fileID 当运行时 ID 用。

### 2.3 同一个对象实例的 BoxCollider 版本（included-only）

换成 `CubeA.prefab` 的 **BoxCollider**：

```text
guid     = b1665a6fc133d8d4b87ac1b4e4a645a8   （CubeA.prefab 的 GUID）
fileID   = 6277825710962230088
fileType = MetaAssetType                        （prefab 的导入产物）
path     = ""
```

| 阶段 | 位置 | 说明 |
|---|---|---|
| 依赖 | `AssetInfo[CubeA].includedObjects[4]` | 组件实例，数据在 prefab 里 |
| 装箱 | `FileToObjects[CAB-c297...]` | props_a 主文件的 10 个对象之一（CubeA 5 + CubeB 5） |
| 写命令 | `WriteOperations[props_a].serializeObjects` | 构建期生成新的 localID |
| 归档 | `BundleInfos[props_a]` | 在 `props_a.bundle` 里；props_a 的 deps=[shared] |

它自己没有外部引用（除非给它设置了 PhysicMaterial，那会以 referenced 的形式出现）。

### 2.4 如果对象换成内置 Shader

`Mat.mat` 引用的 2 个 Shader 是：

```text
guid     = 0000000000000000f000000000000000   （builtin_extra 伪 GUID）
fileID   = 46 / 6
fileType = NonAssetType
path     = "Resources/unity_builtin_extra"
```

它们的身份用 **path + fileID**（因为不在 AssetDatabase 里）。默认 preset 下它们会被复制进 `shared` bundle；如果用 `AssetBundleShaderAndScriptExtraction` preset，它们会被抽到 `UnityBuiltIn.bundle`，此时 `shared` 的依赖会变成 `[UnityBuiltIn]`。

### 2.5 从单个对象得到的三个结论

1. **included/referenced 是关系不是属性**：同一个 Material 在 Mat 里 included，在 prefab 里 referenced。
2. **一个资产对应多个 CAB 是加载依赖，不是拆分**：`AssetToFiles[Mat]` 只有 shared 主文件；`AssetToFiles[CubeA]` 才是 props_a 主文件 + shared 依赖文件。
3. **源 fileID ≠ 构建后 localID**：对象身份在构建期会重新分配；自定义 DB 要同时保留“源身份”和“写入 ID”的映射。

---


---

## 返回

- [任务链 Wiki 主文档](README.md)
- [依赖数据结构详解](Dependency-Data-Structures.md)（ObjectIdentifier / FileType / included vs referenced / prefabA）
