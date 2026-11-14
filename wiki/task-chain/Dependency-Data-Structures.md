# 依赖数据结构详解（ObjectIdentifier / FileType / prefabA）

> 本文是任务链 Wiki 的子文档，专门拆解依赖阶段的数据结构（`ObjectIdentifier`、FileType、included/referenced、prefabA 案例）。
> 装箱/写盘阶段（`IBundleWriteData`）见姊妹文档：[Bundle-Write-Data.md](Bundle-Write-Data.md)。
>
> 主文档：[任务链 Wiki README](README.md) · 数据来源：`captured/ex19-filetypes.txt`、`captured/ex18-stage-probe.json`

---

## 1. GUID、localIdentifierInFile、filePath：三个身份概念

看 `AssetInfo` 时最容易卡住的两个现象是：

1. **同一个 GUID 下有很多不同的 fileID**；
2. **对象有 GUID，但 filePath 是空的**。

这两件事都不是 bug，而是 Unity 对象身份模型决定的：

| 字段 | 回答的问题 | 作用域 | 备注 |
|---|---|---|---|
| `guid` | 对象属于哪个**资产文件** | 整个项目唯一 | 内置资源是 Unity 固定伪 GUID |
| `localIdentifierInFile`（fileID） | 对象是文件里的**哪个对象** | **仅在该文件内唯一** | 64 位不透明值，不要解析 |
| `filePath` | 对象所在文件路径 | 全局 | **只有 NonAssetType 一定有意义**（官方注释见下） |
| `fileType` | 对象数据物理上在哪类文件里 | — | 决定身份用 GUID 还是 filePath |

> 官方对 `ObjectIdentifier.filePath` 的注释原文：
>
> *“The file path on disk that contains this object. **(Only used for objects not known by the AssetDatabase)**”*
>
> 这句话正好解释了上面的现象：只有 **`NonAssetType`**（AssetDatabase 不认识的对象，如 `Library/unity default resources`、`Resources/unity_builtin_extra`）才靠 `filePath` 定位；`MetaAssetType` / `SerializedAssetType` 是 AssetDatabase 资产，用 `guid` 定位，所以它们的 `filePath` 经常是空字符串。

> 对象身份公式：**对象 =（文件身份, fileID）**
> - Asset 类（`MetaAssetType` / `SerializedAssetType`）：文件身份 = `guid`；
> - NonAsset 类：文件身份 = `filePath`。

**真实数据（lab：`Assets/Bundles/Props/CubeA.prefab`，GUID `b1665a6f...`）**

`includedObjects`（5 个，全部属于同一个 prefab 资产）：

| fileID | fileType | 实际对象（`GetTypeForObjects` 实测） |
|---|---|---|
| `1266598513701572995` | MetaAssetType | GameObject |
| `36684013351910960` | MetaAssetType | MeshFilter |
| `1529215442718738174` | MetaAssetType | MeshRenderer |
| `3459590857198133346` | MetaAssetType | Transform |
| `6277825710962230088` | MetaAssetType | BoxCollider |

`referencedObjects`（2 个，别人拥有的对象）：

| guid | fileID | fileType | filePath | 含义 |
|---|---|---|---|---|
| `0000000000000000e000000000000000` | `10202` | NonAssetType | `Library/unity default resources` | Unity 内置 Mesh（Cube 网格） |
| `0d70bf7a4a50f0e459a53aef98ebd4c6` | `2100000` | SerializedAssetType | `""` | `Mat.mat` 里的 Material 对象 |

### 为什么同一个 GUID 有不同 fileID

**prefab 是一个资产文件，但里面有很多对象。** `guid` 只能定位到 `.prefab` 这个文件；要定位到“prefab 里具体哪个 GameObject/组件”，必须再加 `localIdentifierInFile`。

- 5 个 included 对象 GUID 相同是必然的（同属一个 prefab）；
- 打包/DB 如果只用 GUID 当 key，这 5 个对象会被折叠成 1 个 → 丢对象；
- 只有 `(guid, localId)` 才能区分同一资产内的不同对象。

fileID 的数值：

- prefab 里的 ID 是 64 位、看起来像随机大数（如 `1266598513701572995`），当成**不透明值**，不要解析；
- `.mat` / `.asset` 主对象常见规律是 `类ID × 100000`（Material classID=21 → `2100000`），但不要依赖这个规律。

### 为什么有 GUID 却没有 filePath

因为 **AssetDatabase 里的资产靠 GUID 就能定位文件**，内容构建返回的 `ObjectIdentifier` 对 `MetaAssetType` / `SerializedAssetType` 经常把 `filePath` 留空：

- `MetaAssetType`：对象数据在 **Library 的导入产物**里（prefab 内部对象、模型/贴图对象等），身份 = `guid + localId`；
- `SerializedAssetType`：对象在 **Assets 下的序列化资产文件**里（`.mat` / `.asset`），身份 = `guid + localId`；
- 想要路径：`AssetDatabase.GUIDToAssetPath(guid.ToString())`，**不需要 filePath 字段**。

> 官方对 `filePath` 的注释写得很直接：
> *“The file path on disk that contains this object. **(Only used for objects not known by the AssetDatabase)**”*
> 所以 AssetDatabase 资产的 `filePath` 为空不是 bug，而是设计如此。

而 `NonAssetType` 相反：对象不在 AssetDatabase 管理下（`Library/unity default resources`、`Resources/unity_builtin_extra`、`VirtualArtifacts/Extra/...`），所以 **filePath 才是身份**，GUID 只是内置固定伪 GUID：

| 伪 GUID | 对应文件 |
|---|---|
| `0000000000000000d000000000000000` | `library/unity editor resources` |
| `0000000000000000e000000000000000` | `library/unity default resources` |
| `0000000000000000f000000000000000` | `resources/unity_builtin_extra` |

这些 GUID 用 `GUIDToAssetPath` 查不到 Assets 路径，**不要当成普通资产**。

### 怎么把 `ObjectIdentifier` 反查成具体对象

调试依赖时非常实用：

```csharp
var id = /* ObjectIdentifier */;
var path = AssetDatabase.GUIDToAssetPath(id.guid.ToString());
var types = ContentBuildInterface.GetTypeForObjects(new[] { id }); // 对象类型
foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
{
    // 注意：签名是 (Object, out string guid, out long localId)
    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out var g, out var localId);
    if (localId == id.localIdentifierInFile)
    {
        // obj 就是该 ObjectIdentifier 对应的对象
    }
}
```

> `LoadAllAssetsAtPath` 对 prefab 会加载内部所有对象，正好能和 `includedObjects` 对上。

### 对依赖图 / 自定义 DB 的意义

1. **两级依赖**：
   - 资产级：`AssetInfo[guid]`、`AssetLoadInfo.asset`；
   - 对象级：`includedObjects` / `referencedObjects` 里的 `ObjectIdentifier`。
   从资产映射到 pack unit 用 GUID 归并；真正写进 bundle/DB 时要保留对象级的 `(guid, localId)`（NonAsset 用 `(path, localId)`）。
2. **included vs referenced**：
   - included：属于本资产的对象，打包时写进本资产的 file；
   - referenced：别人拥有的对象（Material、内置 Mesh/Shader），打包时通常转成**文件级依赖**；如果引用对象所在资产也被打包，就只留 file 依赖。
3. **Packing 会过滤部分引用**：`GenerateBundlePacking.FilterReferencesForAsset` 只过滤 `library/unity default resources`（源码常量是小写，匹配用 `OrdinalIgnoreCase`，所以 `Library/...` 也能匹配）。因此内置 Mesh 在依赖阶段存在、Packing 后会消失；`Resources/unity_builtin_extra` 的 Shader 不被过滤，默认会复制进 `shared` bundle，除非用 extraction preset 抽取。

## 2. includedObjects vs referencedObjects

一句话：**included = 本资产“拥有”的对象（序列化在它自己文件里的）；referenced = 指向别人文件里的对象（只有引用/指针）。**

一个简单的判断方法：**把这个资产文件单独拷走时，哪些对象的数据会跟着走 → included；只剩 GUID/fileID 指针、数据在别处的 → referenced。**
所以“挂载的组件”对 prefab 来说基本等于 included，但更准确的说法是“数据序列化在这个资产文件里”（Material 资产没有“挂载”概念，它的 Material 对象本身也在 included 里）。

所以 **prefab 上挂的组件属于 `includedObjects`，不是 `referencedObjects`**——它们的数据就序列化在这个 prefab 文件里。

以 lab 的 `Assets/Bundles/Props/CubeA.prefab`（GUID `b1665a6f...`）为例：

| 类别 | 数量 | 内容 |
|---|---|---|
| `includedObjects` | 5 | GameObject / Transform / MeshFilter / MeshRenderer / BoxCollider（都在该 prefab 文件里） |
| `referencedObjects` | 2 | 内置 Mesh `10202`（`Library/unity default resources`）；Material `2100000`（`Assets/Bundles/Shared/Mat.mat`，GUID `0d70bf...`） |

为什么 Mesh/Material 是“引用”而不是“包含”：`MeshFilter.m_Mesh`、`MeshRenderer.m_Materials` 只是指向**别的文件**里的对象，这些对象的数据不在 prefab 里。

再往下看 `Mat.mat`：

| 类别 | 数量 | 内容 |
|---|---|---|
| `includedObjects` | 1 | Material（fileID `2100000`） |
| `referencedObjects` | 2 | 内置 Shader（fileID `46` / `6`，`Resources/unity_builtin_extra`） |

打包时（Ex18 实测数字正好对得上）：

| bundle | 对象数（Ex18） | 组成 |
|---|---|---|
| `props_a` | 10 | CubeA(5) + CubeB(5) 的 included 对象；内置 Mesh 被过滤；Mat 引用转成对 `shared` 的文件依赖 |
| `props_b` | 5 | CubeC(5) 的 included 对象 |
| `shared` | 3 | Mat 的 included(1) + 它引用的 2 个内置 Shader（`builtin_extra` 不过滤） |

> 校验点：`GenerateBundlePacking.FilterReferencesForAsset` 只过滤 `library/unity default resources`；`Resources/unity_builtin_extra` 的 Shader 会被打进 bundle，所以 `shared` 才会是 3 个对象、约 46KB。

> 调试器摘要里的 `type:` 是 **`FileType`（文件类型）**，不是组件/对象类型。对象类型（GameObject / Mesh / Material / Shader）要用 `ContentBuildInterface.GetTypeForObjects(new[]{ objectId })` 查——Ex19 打印出来的那一列才是对象类型。

## 3. FileType 的四个取值

字段含义见上一节；`FileType` 回答的是“对象数据物理上在哪类文件里”，它直接决定了身份用 `guid` 还是 `filePath`：

| FileType | 含义 | 典型来源 | 身份字段 |
|---|---|---|---|
| `SerializedAssetType` | `Assets/` 下标准序列化资产文件里的对象 | `.mat` / `.asset` 等直接序列化的资产对象 | **guid + localId**（filePath 常为空） |
| `MetaAssetType` | 导入产物（Library 中的 imported asset meta data）里的对象 | prefab 内部对象、模型/贴图等导入产物对象 | **guid + localId**（filePath 常为空） |
| `NonAssetType` | 不在 AssetDatabase 管理下的文件里的对象 | `Library/unity default resources`、`Resources/unity_builtin_extra`、`VirtualArtifacts/Extra/...` | **filePath + localId**（guid 可能是内置固定 GUID） |
| `DeprecatedCachedAssetType` | 旧缓存格式遗留，当前未使用 | — | 忽略 |

**Ex19 实测**（`SbpLabEx19_FileTypeProbe`，捕获见 `captured/ex19-filetypes.txt`）：

```text
CubeA/B/C.prefab  includedObjects:
  MetaAssetType  GameObject/Transform/...  path=''  guid=<prefab guid>  localId=...
  （prefab 的导入产物在 Library，路径为空）

prefab referencedObjects:
  NonAssetType      Mesh     path='Library/unity default resources'  guid=0000000000000000e000000000000000
  SerializedAssetType Material path=''  guid=<Mat guid>  localId=2100000

Mat.mat:
  SerializedAssetType Material（included）
  NonAssetType        Shader  path='Resources/unity_builtin_extra'  guid=0000000000000000f000000000000000
```

读依赖图的三条规则：

1. **不要用 `filePath` 判断“是不是项目资产”**：`MetaAssetType` / `SerializedAssetType` 的 `filePath` 经常是空字符串，资产身份用 `guid`；`NonAssetType` 才主要靠 `filePath` 识别（内置资源路径）。
2. **默认资源 vs 内置扩展**：`GenerateBundlePacking.FilterReferencesForAsset` 只过滤 `Library/unity default resources`（Player 内置，不进 bundle）；`Resources/unity_builtin_extra` **不过滤**，会随引用复制进 bundle，除非用 `CreateBuiltInShadersBundle` 抽取到独立 bundle。
3. **自定义 DB 分类建议**：`guid` 非空 + Meta/Serialized → 可映射回项目资产（pack unit）；`NonAssetType` + default resources → 跳过；`NonAssetType` + builtin_extra → 决定“复制/抽取/运行时引用”；`Deprecated` → 忽略。

SBP 内部对 `FileType` 的使用：

- `Unity5PackedIdentifiers` / `PrefabPackedIdentifiers`：Meta/Serialized 类型用 `guid` 参与 ID hash，其它用 `filePath`（`VirtualArtifacts/Extra/` 有内容 hash 特判）；
- `BuildCache.GetCacheEntry(ObjectIdentifier)`：`guid` 非空走资产缓存，否则走文件缓存——`fileType` 不直接进 cache key，但决定了 `guid` 是否可用；
- 测试代码里 `FileType.MetaAssetType` + 空 path 是常态（`ObjectExtractionTaskTests` 等），印证“路径不可靠、guid+localId 才是身份”。

> 反查技巧：`AssetDatabase.GUIDToAssetPath(guid.ToString())` 得到资产路径；
> `AssetDatabase.LoadAllAssetsAtPath(path)` 后用 `AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out var g, out var localId)` 按 `localId` 匹配，
> 就能把 `ObjectIdentifier` 还原成具体对象；对象类型可用 `ContentBuildInterface.GetTypeForObjects(new[]{ objectId })` 查。
> `localIdentifierInFile` 常是 64 位不透明值；`.mat`/`.asset` 主对象常见 `类ID * 100000`（如 Material = 2100000），但不要依赖这个规律。


---

## 4. 完整案例：prefabA（`CubeA.prefab` 的依赖全链路）

> 本案例用 lab 里的 `Assets/Bundles/Props/CubeA.prefab` 作为 **prefabA**，数据来自 Ex19 实测（`captured/ex19-filetypes.txt`）与 Ex18 打包结果。
> 目的：把 GUID / fileID / FileType / included / referenced / 打包过滤串成一条完整链路。

### 先记三句话（后面所有数据都在验证它们）

1. **`MetaAssetType` 不是 `.meta` 文件**：它指“导入资产的元数据/产物（Library 里的 imported artifact）”，不是工程里的 `*.meta`。所以 prefab 里的 GameObject、Transform、BoxCollider 都是 `MetaAssetType`，`path` 为空是正常现象。
2. **组件不是独立资产**：BoxCollider 没有自己的 GUID——它的 `guid` 是所属 prefab 资产的 GUID，`fileID`（`localIdentifierInFile`）才是它在 prefab 里的对象 ID。只有 `(prefab guid, BoxCollider fileID)` 才能唯一定位这个 BoxCollider 实例。
3. **只有“数据在别的文件里”的对象才算 referenced**：prefab 上挂的 5 个对象（GameObject/组件实例）全部是 `includedObjects`；组件序列化引用的外部对象（MeshFilter → Mesh、MeshRenderer → Material、MonoBehaviour → Script 等）才是 `referencedObjects`。打包时 5 个 included 对象一起写进 `props_a` 的 SerializedFile，外部引用则转成文件级依赖或被内置资源过滤。

### 资产结构

```text
prefabA = Assets/Bundles/Props/CubeA.prefab
GUID = b1665a6fc133d8d4b87ac1b4e4a645a8

prefabA（文件/导入产物）
└─ GameObject        fileID 1266598513701572995  MetaAssetType
   ├─ Transform      fileID 3459590857198133346  MetaAssetType
   ├─ MeshFilter     fileID 36684013351910960    MetaAssetType  ──引用──▶ 内置 Mesh 10202
   ├─ MeshRenderer   fileID 1529215442718738174  MetaAssetType  ──引用──▶ Material 2100000（Mat.mat）
   └─ BoxCollider    fileID 6277825710962230088  MetaAssetType
```

### 第 1 层：prefabA 的 `AssetInfo`

`IDependencyData.AssetInfo[prefabA 的 GUID]`：

| 类别 | 数量 | 明细 |
|---|---|---|
| `includedObjects` | 5 | GameObject、Transform、MeshFilter、MeshRenderer、BoxCollider |
| `referencedObjects` | 2 | 内置 Mesh（NonAssetType）、Material（SerializedAssetType） |

**includedObjects（对象数据在 prefab 文件/导入产物里）：**

| fileID | FileType | 对象类型 | 为什么 included |
|---|---|---|---|
| `1266598513701572995` | MetaAssetType | GameObject | prefab 的根对象，序列化在 prefab 里 |
| `3459590857198133346` | MetaAssetType | Transform | 组件实例，序列化在 prefab 里 |
| `36684013351910960` | MetaAssetType | MeshFilter | 组件实例，序列化在 prefab 里 |
| `1529215442718738174` | MetaAssetType | MeshRenderer | 组件实例，序列化在 prefab 里 |
| `6277825710962230088` | MetaAssetType | BoxCollider | 组件实例，序列化在 prefab 里 |

> 5 个对象的 `guid` 全是 prefabA 的 GUID；`fileID` 才区分它们。`FileType` 全部是 `MetaAssetType`，因为 prefab 是导入型资产，对象数据属于 Library 的导入产物——**BoxCollider 也不例外，FileType 跟“所在文件”走，不跟组件类型走**。

**referencedObjects（对象数据在别的文件里）：**

| guid | fileID | FileType | filePath | 对象类型 | 为什么 referenced |
|---|---|---|---|---|---|
| `0000000000000000e000000000000000` | `10202` | NonAssetType | `Library/unity default resources` | Mesh | MeshFilter 引用了 Unity 内置 Cube 网格，数据不在 prefab 里 |
| `0d70bf7a4a50f0e459a53aef98ebd4c6` | `2100000` | SerializedAssetType | `""` | Material | MeshRenderer 引用了 `Mat.mat` 里的 Material 对象 |

### 第 2 层：被引用的 `Mat.mat`

`Mat.mat` 的 GUID = `0d70bf7a4a50f0e459a53aef98ebd4c6`，它自己的 `AssetInfo`：

| 类别 | 数量 | 明细 |
|---|---|---|
| `includedObjects` | 1 | Material（fileID `2100000`，`SerializedAssetType`） |
| `referencedObjects` | 2 | 内置 Shader（fileID `46` / `6`，`NonAssetType`，`Resources/unity_builtin_extra`） |

所以依赖图是两层：

```text
prefabA ──included──▶ 自己的 5 个对象
   │
   ├──referenced──▶ 内置 Mesh 10202（default resources，NonAssetType）
   └──referenced──▶ Mat.mat 的 Material 2100000
                        │
                        └──referenced──▶ 内置 Shader 46 / 6（builtin_extra，NonAssetType）
```

### 第 3 层：打包后对象去哪了

Packing（`GenerateBundlePacking` + `FilterReferencesForAsset`）的结果（Ex18 实测）：

| bundle | 写进 SerializedFile 的对象数 | 组成 |
|---|---|---|
| `props_a` | **10** | CubeA(5) + CubeB(5) 的 included 对象（prefabA + 另一个 prefab） |
| `props_b` | **5** | CubeC(5) 的 included 对象 |
| `shared` | **3** | Mat 的 included Material(1) + 它引用的 2 个内置 Shader |

对应规则：

1. **内置 Mesh 被过滤**：`library/unity default resources` 在 `FilterReferencesForAsset` 里直接 `continue`，所以它不占 props_a 的对象数（由 Player 内置数据提供）。
2. **Material 引用转成文件依赖**：Material 数据在 `shared` bundle 里，props_a 不复制它，只记录“依赖 shared 文件”；所以 props_a 只有 10 个对象。
3. **内置 Shader 没有被过滤**：`Resources/unity_builtin_extra` 不在过滤名单里，所以 `shared` 把 Material + 2 个 Shader 一起打包（这就是 shared 约 46KB 的主要来源；用 extraction preset 才会抽到独立 bundle）。

### 在调试器里遇到 `ObjectIdentifier` 时的判断流程

```text
拿到一个 ObjectIdentifier
├─ guid 能 GUIDToAssetPath 到 Assets/ 路径？
│   ├─ fileType = MetaAssetType        → 导入产物对象（prefab/模型内部对象），身份 = guid + fileID
│   └─ fileType = SerializedAssetType  → 原生序列化资产对象（.mat/.asset），身份 = guid + fileID
└─ guid 查不到路径 / fileType = NonAssetType
    └─ 看 filePath：
        ├─ Library/unity default resources  → 内置默认资源，Packing 会过滤
        ├─ Resources/unity_builtin_extra    → 内置扩展资源（Shader 等），默认会复制/可抽取
        └─ VirtualArtifacts/Extra/...       → SBP 构建期抽出的内置对象，用 path + fileID 做身份
```

### 本案例 FAQ

**Q1：为什么 BoxCollider 也是 `MetaAssetType`？**
`FileType` 表示“对象数据躺在哪类文件”，不是“组件类型/对象类型”。BoxCollider 实例和 GameObject 一样序列化在 prefab 的导入产物里，所以都是 MetaAssetType。它是内置组件类型这件事只影响 native 类型，不影响 FileType。

**Q2：为什么 `MetaAssetType` 不是 `.meta` 文件？**
`MetaAssetType` 指的是“导入资产的产物/元数据（Library 里的 imported artifact）”，不是工程里的 `*.meta` 文件；`path` 为空是正常现象。

**Q3：为什么同一个 GUID 下 5 个 fileID？**
GUID 定位到 `.prefab` 这个资产文件；fileID 定位到文件内的具体对象。prefab 有 5 个对象，所以 5 个 fileID。打包/DB 里只用 GUID 会丢对象，必须 `(guid, fileID)`。

**Q4：为什么 Material 有 GUID 但 filePath 空？**
它是 `SerializedAssetType`，AssetDatabase 资产靠 GUID 就能定位，构建返回的 `filePath` 常为空；路径用 `AssetDatabase.GUIDToAssetPath(guid)` 反查即可（见「怎么把 `ObjectIdentifier` 反查成具体对象」一节）。

**Q5：BoxCollider 会出现在 `referencedObjects` 里吗？**
不会——BoxCollider 实例本身是 prefab 的 included 对象。但如果给 BoxCollider 设置了 **PhysicMaterial**，这个 PhysicMaterial 资产对象会出现在 prefab 的 `referencedObjects` 里（它属于别的资产文件）；规则始终是“看对象数据存在哪个文件”，而不是看对象是哪个组件。

**Q6：`.mat` 文件的 `includedObjects` 是它自己的 GameObject 吗？**
不是。`.mat` 不是 prefab，它没有 GameObject；`Mat.mat` 的 included 是 **Material 对象本身**（fileID `2100000`）。`included` 的准确含义是“数据序列化在这个资产文件里的对象”，不是“挂在它身上的对象”。同理，`referencedObjects` 里的内置 Shader 是 `NonAssetType`，是因为它们的数据在 `Resources/unity_builtin_extra`（AssetDatabase 不管理的文件），而不是因为“它们是被引用的”。

### 如何复现本案例

1. 打开 lab 工程，执行菜单 `SBP Lab/Examples/19 - FileType 探针（依赖对象）`；
2. 看 Console 的 `[Ex19]` 输出，或 `Artifacts/SBP/ex19-filetypes.txt`（归档副本：`captured/ex19-filetypes.txt`）；
3. 打包对象数对照：跑 `SBP Lab/Examples/18 - 全流程阶段探针`，看 `after GenerateBundleCommands` 行的 `writeOps=[props_a:...(10), props_b:...(5), shared:...(3)]`。

---

## 5. 装箱与写盘数据（IBundleWriteData）

`IBundleWriteData`（`AssetToFiles` / `FileToBundle` / `FileToObjects` / `WriteOperations`）、内部文件名生成、单个对象从依赖到 bundle 的走查，已拆到独立子文档：

- **[Bundle-Write-Data.md](Bundle-Write-Data.md)**

---

## 6. 依赖关系是 native 还是 C# 算的？

结论：**“谁引用谁”的对象级依赖图由 native（C++，经 `ContentBuildInterface` 暴露）计算；SBP 的 C# 层负责调度、缓存、过滤、分组、hash 和写盘。**

### 6.1 native 提供什么（C# 只是调用）

SBP 里所有 `ContentBuildInterface.xxx` 调用都是 native 入口，依赖阶段主要用到：

| native API | 产出 |
|---|---|
| `ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(asset, target)` | 一个 asset 的 `includedObjects`（对象级，带 guid/fileID/fileType） |
| `ContentBuildInterface.GetPlayerDependenciesForObjects(objects, target, typeDB[, DependencyType])` | `referencedObjects`（对象引用图） |
| `ContentBuildInterface.GetPlayerDependenciesForObject(...)` | 单对象版本 |
| `ContentBuildInterface.CalculatePlayerDependenciesForScene(scenePath, settings, usageTags, cache[, DependencyType])` | `SceneDependencyInfo`（场景引用/usage） |
| `ContentBuildInterface.CalculateBuildUsageTags(allObjects, includedObjects, globalUsage, usageTags, cache)` | `BuildUsageTagSet`（变体/裁剪用） |
| `ContentBuildInterface.GetPlayerAssetRepresentations(asset, target)` | SubAsset（Representations） |
| `ContentBuildInterface.GetTypeForObjects` / `GetTypesForObject` | 对象类型 / 类型列表 |
| `ContentBuildInterface.GetGlobalUsageFromGraphicsSettings()` | 全局 usage |
| `ContentBuildInterface.WriteSerializedFile` / `WriteSceneSerializedFile` | 实际序列化（Writing 阶段） |
| `ContentBuildInterface.ArchiveAndCompress` | 归档压缩（Archive 阶段） |

调用点举例：

- `CalculateAssetDependencyData.cs`：

```csharp
var includedObjects = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(asset, input.Target);
...
referencedObjects = ContentBuildInterface.GetPlayerDependenciesForObjects(includedObjects, input.Target, input.TypeDB);
ContentBuildInterface.CalculateBuildUsageTags(allObjects.ToArray(), includedObjects, input.GlobalUsage, assetResult.usageTags, input.DependencyUsageCache);
```

- `CalculateSceneDependencyData.cs`：

```csharp
sceneInfo = ContentBuildInterface.CalculatePlayerDependenciesForScene(
    scenePath, settings, usageTags, m_DependencyData.DependencyUsageCache, DependencyType.ValidReferences);
```

这些 API 的输入是 Unity 的序列化文件/导入产物（Library）、`TypeDB`、目标平台；**依赖图本身（对象引用了哪些对象）是引擎根据序列化数据算的**，不是 SBP 的 C# 逻辑。

### 6.2 C# 层做什么（fork 后可以改的部分）

| C# 位置 | 做什么 |
|---|---|
| `Calculate*DependencyData` 任务 | 组织调用 native、处理 cache、把结果写进 `IDependencyData`；对 SpriteAtlas 等做额外校验 |
| `ExtensionMethods.FilterReferencedObjectIDs` | 对场景引用做 C# 过滤（排除非显式资产等） |
| `GenerateBundlePacking.FilterReferencesForAsset` | 过滤 `library/unity default resources`、去掉被其他资产重复包含的引用、处理循环引用 |
| `StripUnusedSpriteSources` | 把已打进图集的源贴图从依赖里剔除 |
| `GenerateBundlePacking` | 把对象按 bundle 分组，生成 `AssetToFiles` / `FileToObjects` |
| `GenerateBundleCommands` | 生成 `WriteCommand` / `SerializationInfo`（localID） |
| `GenerateBundleMaps` | 生成 `BuildReferenceMap` / `BuildUsageTagSet` |
| `ArchiveAndCompressBundles.CalculateBundleDependencies` | 从 `AssetToFiles` 算 bundle 级依赖 |
| `WriteSerializedFiles` / `ArchiveAndCompressBundles` | 调用 native 写盘/归档，并在 C# 里算 hash、元数据、缓存 |

### 6.3 一句话边界

> **native 回答“谁引用谁”；C# 回答“保留哪些引用、分到哪个 file/bundle、怎么 hash/缓存/写盘”。**

这也解释了为什么 Ex19 的探针只能“读”依赖数据：`GetPlayerDependenciesForObjects` 返回的对象列表是 native 算好的，SBP 只是在它之上做裁剪和分组。

### 6.4 对你们魔改管线的启示

1. **只换产物格式**（AssetBundle → 自定义 DB）：对象的引用图仍然可以复用 native 依赖数据，改 C# 的 Packing/Write/Archive 层即可；
2. **如果自定义 DB 里的对象不经过 Unity 序列化/导入器**：native 不认识这些对象，依赖图要么自己算，要么构造 `ObjectIdentifier` / `AssetLoadInfo` 喂给 C# 层；
3. 你们 19 个任务里“删除/替换”的那批，很可能就发生在 C# 的 Packing/Write 层——**native 依赖计算通常不需要动，也很难动**（`ContentBuildInterface` 是引擎内部接口）。

---

## 7. TypeDB 是什么、在哪用

`TypeDB`（`UnityEditor.Build.Player.TypeDB`）= **Player 脚本编译产物里的“序列化类型数据库”**：描述每个类型有哪些序列化字段、字段类型、序列化布局。它和 assemblies 一起由 `PlayerBuildInterface.CompilePlayerScripts` 生成。

### 7.1 在 SBP 里的位置

| 环节 | 代码 | 作用 |
|---|---|---|
| 生成 / 复用 | `BuildPlayerScripts.Run()` | `CompilePlayerScripts` 得到 `ScriptResults.typeDB`；若 `ScriptInfo` 已存在则 `SuccessNotRun`（复用外部 TypeDB） |
| 参数 | `BuildParameters.GetContentBuildSettings()` | `typeDB = ScriptInfo` 写进 `BuildSettings`，传给 native 写盘 |
| 依赖 | `CalculateAssetDependencyData` | `GetPlayerDependenciesForObjects(..., input.TypeDB)` |
| 场景 | `CalculateSceneDependencyData` / `ExtensionMethods.FilterReferencedObjectIDs` | 把 `m_Parameters.ScriptInfo` 传给 native 依赖计算 |
| 全局 | `BuildCacheUtility.SetTypeDB()` | 一个构建内 TypeDB 固定；中途修改会抛 `Changing Player TypeDB mid build is not supported` |
| 类型缓存 | `BuildCacheUtility.GetCacheEntry(Type)` | `CalculatePlayerSerializationHashForType(type, m_TypeDB)`（或 `TypeDB.GetHash128()`）作为 ScriptType entry 的 hash |

### 7.2 举例

**例 1：MonoBehaviour 字段的依赖解析**

```csharp
public class EnemyBehaviour : MonoBehaviour
{
    public Material hitMat;          // 需要被算进依赖
    public GameObject targetPrefab;  // 需要被算进依赖
}
```

`Enemy.prefab` 挂了这个脚本。content build 在算 `referencedObjects` 时，必须知道 `EnemyBehaviour` 有哪些**序列化字段**、字段类型是什么——这些信息就来自 TypeDB。没有 TypeDB（或 TypeDB 是旧的），这些引用可能算不出来/算错，bundle 就会缺依赖。

**例 2：改脚本导致缓存失效**

给 `EnemyBehaviour` 新增一个字段：

```csharp
[SerializeField] Texture2D icon;
```

- Player 脚本重新编译 → TypeDB 变化；
- `ScriptType` 的 cache entry hash（`CalculatePlayerSerializationHashForType`）变化 → 依赖缓存失效；
- `Enemy.prefab` 的依赖会重新计算，新字段引用的 Texture 被纳入依赖。

**例 3：ScriptableObject 资产**

`.asset` 文件里的 ScriptableObject 同样靠 TypeDB 解释序列化字段；字段引用其他资产时，依赖关系也由 native + TypeDB 解析。

**例 4：复用 Player 构建的脚本编译结果**

`IBuildParameters.ScriptInfo` 是公开可写的。如果外部（比如 Player 构建流程）已经编译过脚本，把 TypeDB 传进去，`BuildPlayerScripts` 会直接跳过编译（`SuccessNotRun`），AssetBundle 构建不必重复编译脚本。

### 7.3 TypeDB / assemblies / link.xml 的区别

| 产物 | 内容 | 用途 |
|---|---|---|
| assemblies | 编译后的 DLL | 运行时逻辑 |
| **TypeDB** | 类型与序列化字段描述 | **依赖计算、写 SerializedFile、类型缓存** |
| link.xml | 裁剪保留清单 | Player 代码裁剪 |

### 7.4 对自定义 DB 的含义

- 如果你们仍然让 Unity 序列化对象（哪怕只是依赖计算），**MonoBehaviour/ScriptableObject 的依赖就离不开 TypeDB**；
- 如果自定义 DB 的对象完全不经过 Unity 序列化，TypeDB 对 DB 写入没意义，但 `CalculateAssetDependencyData` 这类任务如果还在用，仍需要它；
- 不要在自己的任务里改 `BuildCacheUtility.SetTypeDB`——它是 per-build 单例，改了会抛异常；需要预编译脚本时，走 `IBuildParameters.ScriptInfo` 传入。

### 7.5 TypeDB 的覆盖范围与 hash

- **覆盖范围**：本次 Player 脚本编译涉及的所有 assemblies 里的类型（MonoBehaviour、ScriptableObject、`[Serializable]` 类型等），生成依据是 `GetScriptCompilationSettings()`（target/group、defines、脚本编译选项）。
- **不包含**：引擎内置类型（GameObject、Transform、Mesh、Material 等，它们有引擎自己的类型信息）；也不包含只存在于 Editor 程序集、没进 Player 编译的类型。
- **一个构建一份**：`BuildCacheUtility.SetTypeDB` 是 per-build 单例（中途改会抛异常）；如果外部传入 `IBuildParameters.ScriptInfo`，则复用那一份。
- **两种 hash**：
  - `TypeDB.GetHash128()`：整个类型数据库的 hash；
  - `ContentBuildInterface.CalculatePlayerSerializationHashForType(type, typeDB)`：单个类型的序列化 layout hash（`BuildCacheUtility.GetCacheEntry(Type)` 用它做 ScriptType 缓存 key）。
- **因此**：任意脚本的序列化字段变化 → 整个 TypeDB 的 hash 变；但缓存是否失效取决于用的是全库 hash 还是单类型 hash。

---

## 8. TypeDB 是 native 指针吗？和 AB 的 TypeTree 什么关系？

### 8.1 TypeDB 的形态

从 C# API 看，`UnityEditor.Build.Player.TypeDB` 是一个 **managed 类**；但它的实例是 native 编译流程产出的：

```csharp
// 由 native 编译返回
ScriptCompilationResult.ScriptResults.typeDB
```

然后又被传回 native API 使用：

```csharp
ContentBuildInterface.GetPlayerDependenciesForObjects(objects, target, typeDB);
ContentBuildInterface.CalculatePlayerSerializationHashForType(type, typeDB);
ContentBuildInterface.WriteSerializedFile(...);   // BuildSettings.typeDB
```

所以可以把它理解成 **“native 类型数据库的托管句柄”**：字段布局/序列化信息实际在引擎侧，C# 侧不要依赖它的内部结构，只当 opaque handle 用。公开可用的只有 `GetHash128()`（以及传给 native API 的能力）。

### 8.2 TypeDB ≠ TypeTree

| | TypeDB | TypeTree |
|---|---|---|
| 存在阶段 | **构建期**（Editor） | **序列化文件里**（AB 内） |
| 归属 | Player 脚本编译产物（类型 + 序列化字段布局） | 每个 SerializedFile 的类型布局元数据 |
| 是否随包发布 | ❌ 不会进入 AB/Player | ✅ 默认写进 SerializedFile（可剥离） |
| 谁生成 | native `CompilePlayerScripts` | 引擎按类型信息生成 |
| 谁使用 | 内容构建：依赖计算、写 SerializedFile、类型 hash | 运行时/AssetStudio/UnityPy：反序列化、版本兼容 |
| 开关 | 无（必须提供，或传 null） | `ContentBuildFlags.DisableWriteTypeTree` |

`CompatibilityBuildPipeline` 里就能看到这个开关的映射：

```csharp
if ((options & BuildAssetBundleOptions.DisableWriteTypeTree) != 0)
    parameters.ContentBuildFlags |= ContentBuildFlags.DisableWriteTypeTree;
```

### 8.3 两者的关系

- **TypeDB 是构建期“类型布局的源头”，TypeTree 是写进文件的“类型布局快照”**；
- 对 **MonoBehaviour / ScriptableObject** 这类 managed 类型，构建时需要 TypeDB 提供的字段信息来生成正确的序列化布局（以及 TypeTree 内容）；
- 对 **内置类型**（GameObject/Transform/Mesh/Material…），布局来自引擎自身的类型信息，不依赖 TypeDB；
- 所以：**TypeDB 不是 TypeTree，但 TypeDB 是 managed 类型 TypeTree 的构建输入之一**。

### 8.4 和 AssetBundle 体积/兼容性的关系

- 默认写入 TypeTree → AB 更大，但跨 Unity 版本/脚本变化更容易兼容，AssetStudio 也能直接读字段；
- `DisableWriteTypeTree` → AB 更小，但：
  - 运行时/工具需要靠程序集/自定义 schema 还原类型；
  - 跨版本加载更容易出问题；
  - 对你们自定义 DB：如果 DB 自带 schema，TypeTree 不是必须；如果要兼容 Unity 生态工具（AssetStudio/UnityPy），就要按 TypeTree 格式写。

### 8.5 一句话

> **TypeDB 是构建期 Editor 侧的类型数据库（native 提供、managed 句柄）；TypeTree 是写进 SerializedFile 的类型布局快照。TypeDB 不随包发布，TypeTree 默认随包发布。**

---

## 9. MonoScript 是什么？为什么会被单独抽成 bundle？

### 9.1 一句话

**`MonoScript` 是脚本资产的“序列化元数据对象”，不是 C# 代码。** 它记录类名、命名空间、程序集等信息；真正的代码在 Player 程序集里。

### 9.2 三样东西不要混

| 东西 | 在哪里 | 能否进 AB |
|---|---|---|
| C# 源码 / 编译后的 DLL / IL2CPP 代码 | Player 程序集（`Assembly-CSharp.dll`、`libil2cpp.so` 等） | ❌ 不能靠 AB 更新 |
| `MonoScript` 对象 | Unity 资产数据库里的脚本资产对象 | ✅ 可以序列化进 AB |
| `MonoBehaviour` 实例数据 | prefab / scene 里的组件对象 | ✅ 可以进 AB |

所以“AB 不能打包 C# 脚本”是对的：**AB 里没有可执行代码**；但 AB 里可以有 `MonoScript` 这个“脚本身份/元数据”。

### 9.3 什么脚本能挂到 GameObject 上

| 类型 | 能否挂 GameObject | 用途 |
|---|---|---|
| `MonoBehaviour` 派生类 | ✅ | 组件，挂在 GameObject 上 |
| `ScriptableObject` 派生类 | ❌ | 资源（`.asset`），通过字段引用 |
| `[Serializable]` 普通 class/struct | ❌ | 作为 MonoBehaviour 的字段被序列化 |
| 纯 C# class | ❌ | 普通逻辑对象 |
| native 组件（Transform、MeshRenderer…） | ✅（不是 C# 写的） | 引擎自带 Component |

继承链：

```text
UnityEngine.Object
 └─ Component
     └─ Behaviour
         └─ MonoBehaviour   ← 用户 C# 脚本挂 GameObject 的基类
```

接口（如 `ISerializationCallbackReceiver`）只是附加能力，不能替代 `MonoBehaviour` 基类。

### 9.4 依赖图里的 MonoScript

假设 prefab 上挂了自定义脚本：

```csharp
public class EnemyBehaviour : MonoBehaviour
{
    public Material hitMat;
}
```

依赖数据大致是：

```text
Enemy.prefab
  includedObjects:
    GameObject
    EnemyBehaviour (MonoBehaviour)
  referencedObjects:
    Material            ← hitMat 字段
    MonoScript          ← EnemyBehaviour 对应的脚本元数据对象
```

- `MonoBehaviour` 对象序列化时有一个 `m_Script` 引用，指向 `MonoScript`；
- 运行时 Unity 靠这个引用知道“这个组件是哪个类”；
- 真正的类实现仍然来自 Player 程序集。

> 本 lab 的 `CubeA.prefab` 只用了内置组件（GameObject/Transform/MeshFilter/MeshRenderer/BoxCollider），没有自定义 MonoBehaviour，所以 Ex19 的依赖里看不到 MonoScript。挂一个自定义脚本后，`referencedObjects` 里就会多出它。

### 9.5 和 MonoScript 抽取相关的 3 个 SBP 任务

| Task | 作用 | 条件 |
|---|---|---|
| `CreateBuiltInShadersBundle`（SBP 2.x 叫 `CreateBuiltInBundle`） | 把 `resources/unity_builtin_extra`（GUID `0000...f000...`）的内置 shader 对象登记到 `IBundleExplictObjectLayout` | 配置了 builtin shader bundle 名 |
| `CreateMonoScriptBundle` | 把所有 `referencedObjects` 中主类型为 `MonoScript` 的对象登记到 layout（排除 `library/unity default resources`，GUID `0000...e000...`） | 配置了 MonoScript bundle 名 |
| `UpdateBundleObjectLayout` | 把 layout 里的对象从原 bundle 的 `FileToObjects` 挪到新 bundle/file；为空时 `SuccessNotRun` | 总是加（YooAsset）/ extraction preset 时加（SBP） |

`CreateMonoScriptBundle` 的核心：

```csharp
Type monoScript = typeof(MonoScript);
for (int i = 0; i < usedTypes.Length; i++)
{
    if (usedTypes[i] != monoScript || usedSet[i].guid == k_DefaultGuid)
        continue;
    m_Layout.ExplicitObjectLocation.Add(usedSet[i], MonoScriptBundleName);
}
```

`UpdateBundleObjectLayout` 的核心：

```csharp
foreach (var group in m_Layout.ExplicitObjectLocation.GroupBy(s => s.Value))
{
    string bundleName = group.Key;
    string internalName = string.Format(CommonStrings.AssetBundleNameFormat,
        m_PackingMethod.GenerateInternalFileName(bundleName));

    foreach (var objectID in group.Select(s => s.Key))
    {
        UpdateAssetToFilesMap(internalName, ObjectToAssetReferences[objectID], m_WriteData.AssetToFiles);
        RemoveObjectIDFromFiles(objectID, ObjectToFiles[objectID], m_WriteData.FileToObjects);
    }

    UpdateFileToBundleMap(bundleName, internalName, m_WriteData.FileToBundle, m_Content.BundleLayout);
    UpdateFileToObjectMap(internalName, group.Select(s => s.Key), m_WriteData.FileToObjects);
}
```

执行顺序：

```text
CalculateAssetDependencyData
StripUnusedSpriteSources
CreateBuiltInShadersBundle   ← 只登记 layout
CreateMonoScriptBundle       ← 只登记 layout
PostDependencyCallback
GenerateBundlePacking
UpdateBundleObjectLayout     ← 把对象从原 bundle 拆到新 bundle
GenerateBundleCommands
...
ArchiveAndCompressBundles
```

### 9.6 为什么要抽出来

- 同一个 `MonoScript` 可能被很多 prefab 引用；
- 不抽的话，它作为 referenced object 可能被复制进多个 bundle；
- 抽出来后，所有 MonoBehaviour 数据只保留对共享 `MonoScripts.bundle` 的依赖，避免重复元数据。

### 9.7 和 link.xml / 热更的关系

- `GenerateLinkXml` 负责生成 `link.xml`，防止 IL2CPP/代码裁剪把“被 AB 引用的脚本类”裁掉；
- `MonoScript` 只是元数据：**改 `.cs` 代码必须重新出 Player，只更新 AB 不能热更 C# 逻辑**；
- 所以“MonoScript bundle”不等于“脚本 bundle”，它只是元数据去重。

### 9.8 内置组件（BoxCollider 等）为什么没有 MonoScript？

`BoxCollider`、`Transform`、`MeshFilter`、`MeshRenderer` 这些**不是 C# 脚本，而是引擎的 native C++ 类型**；它们不以 `.cs` 资产形式存在，所以没有 `MonoScript`。

| | 内置组件（BoxCollider 等） | 自定义 MonoBehaviour |
|---|---|---|
| 实现 | 引擎 native C++（PhysicsModule、CoreModule…） | C# 程序集（Assembly-CSharp.dll 等） |
| 资产形式 | 引擎内置类型，没有 `.cs` 资产 | 有 `.cs` 脚本资产 + 对应 `MonoScript` |
| 序列化标识 | **固定 class ID**（Transform=4、MeshFilter=33、MeshRenderer=23、BoxCollider=65…） | class ID **114** + `m_Script` 指向 MonoScript |
| 类型布局 | 引擎自带类型信息 / TypeTree | 需要 TypeDB / 生成的类型布局 |
| 是否引用 MonoScript | ❌ 没有 | ✅ 被引用时进 `referencedObjects` |
| 是否进 MonoScript bundle | ❌ | ✅ 如果启用 `CreateMonoScriptBundle` |

序列化层面的差别：

```yaml
# 内置组件：直接用 native class ID + 自身字段
BoxCollider:
  m_GameObject: {fileID: ...}
  m_Size: {x: 1, y: 1, z: 1}

# 自定义脚本：MonoBehaviour + m_Script 指针
MonoBehaviour:
  m_Script: {fileID: 11500000, guid: <EnemyBehaviour.cs 的 GUID>, type: 3}
  hitMat:   {fileID: 2100000, guid: <Mat.mat 的 GUID>, type: 2}
```

所以：

- **组件实例本身**（不管内置还是自定义 MonoBehaviour）都序列化在 prefab/scene 里 → `includedObjects`；
- **自定义 MonoBehaviour** 多一个 `m_Script` 指针 → `referencedObjects` 里多一个 `MonoScript`；
- **内置组件**没有 `m_Script`，运行时靠 class ID + native 类型信息识别，不需要 MonoScript。

另外两个容易混的点：

- `CreateMonoScriptBundle` 只处理 `MonoScript` 对象；内置组件永远不会出现在里面；
- `CreateBuiltInShadersBundle` 抽取的是 `Resources/unity_builtin_extra` 里的**内置 Shader 对象**，不是“内置组件”。

> 一句话：**内置组件是引擎类型；MonoBehaviour 是托管脚本组件；只有后者才需要 MonoScript 元数据。**

### 9.9 结论

> **`MonoScript` = 脚本的序列化身份/元数据，不是代码；`CreateMonoScriptBundle` = 把这些元数据对象抽到共享 bundle 做去重；真正的 C# 代码仍在 Player 程序集里。**

---

## 返回

回到 [任务链 Wiki 主文档](README.md)（§4 数据结构手册 / §4.2 依赖数据）。
