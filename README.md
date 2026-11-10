# SBP Learning Lab

> 一个可复现的 Unity **Scriptable Build Pipeline (SBP)** 学习仓库：
> **22 个实验 + 逐任务源码拆解 + 缓存/确定性/依赖数据实测**。
>
> Unity 2022.3.17f1c1 · SBP 1.23.4

[![Unity](https://img.shields.io/badge/Unity-2022.3.17f1c1-black)](https://unity.com)
[![SBP](https://img.shields.io/badge/SBP-1.23.4-blue)](https://docs.unity3d.com/Packages/com.unity.scriptablebuildpipeline@1.23/manual/index.html)

---

## 这是什么

不是 API 文档，也不是生产管线，而是一份 **“读源码 + 跑实验 + 留证据”** 的学习包。

适合这样的读者：

- 已经会用 SBP 打 AssetBundle，但想搞清 **任务链、依赖数据、缓存、确定性** 到底怎么运转；
- 准备 **魔改 SBP**（换产物格式、换写入方式、接自己的 DB），需要知道哪些层能改、哪些不能；
- 排查过 “同样代码两次构建结果不同 / 缓存不命中 / 改完没生效 / diff 误报” 之类的问题。

每个结论都尽量对应到本仓库里的脚本和采样文件，而不是只给结论。

---

## 先看结论（TL;DR）

| # | 结论 | 详见 |
|---|---|---|
| 1 | **确定性是“同构建指纹内”的确定性**：同 Editor 版本/平台/参数/资源状态 → 产物字节一致；跨平台/跨版本不保证。 | [SBP-Determinism.md](lab-project/Docs/SBP-Determinism.md) |
| 2 | `BundleDetails.Hash = hash(ContentHash[], dependencies)`，**压缩方式不参与**；`ContentHash` 跳过 SerializedFile header，跨 Unity 版本稳定。 | [SBP-Determinism.md](lab-project/Docs/SBP-Determinism.md) |
| 3 | **SBP BuildCache 完全在 C# 层**：`Library/BuildCache`、`.info`、Load/Save/依赖校验/Purge/Prune 都是 managed；native 只提供 hash 输入和写入/压缩产物。 | [SBP-BuildCache.md](lab-project/Docs/SBP-BuildCache.md) |
| 4 | 缓存失效分五层：**GlobalHash（Unity 版本 + cache 版本）→ Guid → Version → Hash → 直接依赖校验**；依赖校验是**非递归**的。 | [SBP-Cache-Invalidation.md](lab-project/Docs/SBP-Cache-Invalidation.md) |
| 5 | 对象级依赖图由 native `ContentBuildInterface` 算；**C# 负责过滤/分组/ID/hash/缓存/写盘编排**。 | [Dependency-Data-Structures.md](wiki/task-chain/Dependency-Data-Structures.md) §6 |
| 6 | `included` = 数据序列化在本资产文件里；`referenced` = 指向别的文件；**`guid` 定位资产文件，`fileID` 定位文件内对象**，`filePath` 只对 NonAssetType 有意义。 | [Dependency-Data-Structures.md](wiki/task-chain/Dependency-Data-Structures.md) |
| 7 | 一个 asset 的 `AssetToFiles` 可能有多个 CAB，这是**加载依赖列表**（`[0]` 主文件 + 依赖文件），不是资产被拆开；一个 CAB 里可以有很多 objects。 | [Bundle-Write-Data.md](wiki/task-chain/Bundle-Write-Data.md) |
| 8 | `TypeDB` 是构建期 Player 脚本类型库（native 数据 + managed 句柄），**不是** AB 里的 TypeTree；TypeTree 默认随包发布、可 `DisableWriteTypeTree` 关闭。 | [Dependency-Data-Structures.md](wiki/task-chain/Dependency-Data-Structures.md) §7/§8 |

---

## 推荐阅读路线

| 目标 | 路线 | 预计时间 |
|---|---|---|
| **快速建立全局观** | [wiki/task-chain/README.md](wiki/task-chain/README.md) §1 心智模型 → §2 Ex18 数据 → §3 逐任务 | 30–60 min |
| **看懂依赖与装箱** | [Dependency-Data-Structures.md](wiki/task-chain/Dependency-Data-Structures.md) → [Bundle-Write-Data.md](wiki/task-chain/Bundle-Write-Data.md) | 1–2 h |
| **搞懂缓存与确定性** | [SBP-BuildCache.md](lab-project/Docs/SBP-BuildCache.md) → [SBP-Determinism.md](lab-project/Docs/SBP-Determinism.md) → [SBP-Cache-Invalidation.md](lab-project/Docs/SBP-Cache-Invalidation.md) | 2–3 h |
| **动手跑实验** | [lab-project](lab-project) → 先跑 Ex18，再跑 Ex20/21/22 | 1–2 h |
| **准备魔改 SBP / 自定义 DB** | [task-chain README §5](wiki/task-chain/README.md) 的“保留/替换/删除”框架与 12 条检查清单 | 30 min |

---

## 仓库结构

```text
.
├─ lab-project/                        # 可打开的 Unity 工程（SBP 相关部分）
│  ├─ Assets/Editor/SbpLabBuild.cs     # 基础菜单：Setup / Compat build / ContentPipeline / diff
│  ├─ Assets/Editor/SbpLab/            # Ex01 ~ Ex22 实验脚本 + 公共代码
│  ├─ Assets/Bundles/Props|Shared/     # 轻量测试资源（3 prefab + 1 material）
│  ├─ Packages/                        # manifest + lock（SBP 1.23.4）
│  ├─ ProjectSettings/
│  ├─ Docs/                            # 源码级专题 + Ex20/21/22 实测报告
│  └─ SBP-Lab-Wiki.md                  # 主实验 Wiki（案例 01~18）
├─ wiki/
│  └─ task-chain/                      # 任务链逐阶段 Wiki（含 Ex18/Ex19 采样）
└─ data/                               # Ex13/16/17/18 的 summary/report JSON
```

---

## 文档地图

### 任务链与数据结构（建议先读）

| 文档 | 内容 |
|---|---|
| [wiki/task-chain/README.md](wiki/task-chain/README.md) | `AssetBundleCompatible` 的 **19 个任务逐阶段**：作用、输入/输出、数据结构、Ex18 真实数据、与魔改管线的对照 |
| [Dependency-Data-Structures.md](wiki/task-chain/Dependency-Data-Structures.md) | `ObjectIdentifier`、FileType、included vs referenced、prefabA 案例、TypeDB、native/C# 边界、调试反查 |
| [Bundle-Write-Data.md](wiki/task-chain/Bundle-Write-Data.md) | `IBundleWriteData`、file/bundle/SerializedFile、`AssetToFiles`/`FileToBundle`/`FileToObjects`、单个对象实例走查 |

### 专题（缓存 / 确定性）

| 文档 | 内容 |
|---|---|
| [SBP-BuildCache.md](lab-project/Docs/SBP-BuildCache.md) | 缓存架构：CacheEntry/CachedInfo、存储布局、Load/Save、任务 key、Purge/Prune、冷热构建实测（Ex20/21/22） |
| [SBP-Determinism.md](lab-project/Docs/SBP-Determinism.md) | 确定性：ID 生成、排序、ContentHash/RawFileHash、BundleDetails.Hash、回调版本、构建指纹 |
| [SBP-Cache-Invalidation.md](lab-project/Docs/SBP-Cache-Invalidation.md) | 失效图谱：GlobalHash / Guid / Version / Hash / 依赖校验五层，逐 case 说明什么改动会让缓存失效 |

### 实验总览

| 文档 | 内容 |
|---|---|
| [SBP-Lab-Wiki.md](lab-project/SBP-Lab-Wiki.md) | 主实验 Wiki：案例 01~18 的目的、操作、实测产物与坑 |

---

## 实验索引

脚本都在 [lab-project/Assets/Editor/SbpLab](lab-project/Assets/Editor/SbpLab)。

### 基础流程

| # | 主题 | 实验 |
|---|---|---|
| 01 | Compatibility 最小构建 | [SbpLabEx01_CompatibilityMinimal.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx01_CompatibilityMinimal.cs) |
| 02 | ContentPipeline 显式布局 | [SbpLabEx02_ExplicitLayout.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx02_ExplicitLayout.cs) |
| 03 | 压缩矩阵 LZMA/LZ4/Uncompressed | [SbpLabEx03_CompressionMatrix.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx03_CompressionMatrix.cs) |
| 04 | per-bundle 压缩 | [SbpLabEx04_PerBundleCompression.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx04_PerBundleCompression.cs) |

### 扩展点与交付

| # | 主题 | 实验 |
|---|---|---|
| 05 | 自定义 BuildTask | [SbpLabEx05_CustomBuildTask.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx05_CustomBuildTask.cs) |
| 06 | 自定义任务链 + IContextObject | [SbpLabEx06_CustomTaskChain.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx06_CustomTaskChain.cs) |
| 07 | 交付后处理 | [SbpLabEx07_DeliveryPostProcess.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx07_DeliveryPostProcess.cs) |
| 09 | 自定义 IBuildLogger | [SbpLabEx09_CustomLogger.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx09_CustomLogger.cs) |
| 11 | 加密交付 + 运行时加载 | [SbpLabEx11_EncryptedDelivery.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx11_EncryptedDelivery.cs) |

### 产物形态与工程化

| # | 主题 | 实验 |
|---|---|---|
| 10 | ContentFile / Cluster 管线 | [SbpLabEx10_ContentFilePipeline.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx10_ContentFilePipeline.cs) |
| 12 | 在 Packing 前改写布局 | [SbpLabEx12_PackingStrategy.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx12_PackingStrategy.cs) |
| 13 | 多配置矩阵 + 报告聚合 | [SbpLabEx13_BuildMatrix.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx13_BuildMatrix.cs) |
| 14 | 旧 BuildPipeline vs SBP | [SbpLabEx14_LegacyVsSbpCompare.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx14_LegacyVsSbpCompare.cs) |
| 15 | Preset 对比（内置 shader 抽取） | [SbpLabEx15_PresetComparison.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx15_PresetComparison.cs) |

### 重内容与确定性

| # | 主题 | 实验 |
|---|---|---|
| 08 | 增量缓存收益（约 180MB 源资源） | [SbpLabEx08_CacheExperiment.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx08_CacheExperiment.cs) |
| 16 | 重内容压缩矩阵（冷 + 热） | [SbpLabEx16_HeavyCompression.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx16_HeavyCompression.cs) |
| 17 | 确定性 + 缓存一致性验证 | [SbpLabEx17_Determinism.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx17_Determinism.cs) |

### 探针与采样（配套 Wiki）

| # | 主题 | 产出 |
|---|---|---|
| 18 | 全流程阶段探针 | [SbpLabEx18_StageProbe.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx18_StageProbe.cs) → `data/ex18-stage-probe.json` |
| 19 | FileType 探针 | [SbpLabEx19_FileTypeProbe.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx19_FileTypeProbe.cs) → `wiki/task-chain/captured/ex19-filetypes.txt` |
| 20 | 构建缓存采样（冷/热） | [SbpLabEx20_CacheProbe.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx20_CacheProbe.cs) → `lab-project/Docs/ex20-cache-probe.txt` |
| 21 | AssetDependencyHash 探针 | [SbpLabEx21_AssetHashProbe.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx21_AssetHashProbe.cs) → `lab-project/Docs/ex21-asset-hash-probe.txt` |
| 22 | CachedInfo 实例 dump | [SbpLabEx22_CachedInfoDump.cs](lab-project/Assets/Editor/SbpLab/SbpLabEx22_CachedInfoDump.cs) → `lab-project/Docs/ex22-cachedinfo-dump.txt` |

---

## 实测数据

| 数据 | 对应实验 | 能说明什么 |
|---|---|---|
| [data/ex18-stage-probe.json](data/ex18-stage-probe.json) | Ex18 | 19 任务 × 20 个边界：上下文对象、依赖计数、WriteOperation、BundleInfos |
| [data/ex16-summary.json](data/ex16-summary.json) | Ex16 | LZMA/LZ4/Uncompressed 的体积与冷/热构建耗时 |
| [data/ex17-summary.json](data/ex17-summary.json) | Ex17 | A/B/C/D/E 的 md5、内容 hash、配置差异 |
| [data/ex13-summary.json](data/ex13-summary.json) | Ex13 | 多配置矩阵 |
| [lab-project/Docs/ex20-cache-probe.txt](lab-project/Docs/ex20-cache-probe.txt) | Ex20 | 冷/热构建的 CacheEntry、命中数、`.info`/artifact 路径与大小 |
| [lab-project/Docs/ex21-asset-hash-probe.txt](lab-project/Docs/ex21-asset-hash-probe.txt) | Ex21 | depHash vs 文件 MD5 vs 缓存命中 |
| [lab-project/Docs/ex22-cachedinfo-dump.txt](lab-project/Docs/ex22-cachedinfo-dump.txt) | Ex22 | `CachedInfo.Data[0]/[4]/[5]`、Dependencies 的完整实例 |

---

## 快速开始

```bash
git clone https://github.com/853686228/unity-sbp-lab.git
```

1. 用 **Unity 2022.3.17f1c1** 打开 `lab-project/`；
2. 确认 `lab-project/Packages/manifest.json` 含 `"com.unity.scriptablebuildpipeline": "1.23.4"`；
3. 菜单入口：
   - `SBP Lab/0 - Setup Test Assets`
   - `SBP Lab/1 - Compatibility Build`
   - `SBP Lab/2 - ContentPipeline Build`
   - `SBP Lab/Examples/18 - 全流程阶段探针`
   - `SBP Lab/Examples/20 - 构建缓存采样`
   - `SBP Lab/Examples/21 - AssetDependencyHash 探针`
   - `SBP Lab/Examples/22 - CachedInfo 实例 dump`

Batchmode 示例：

```powershell
& 'C:\Program Files\Unity\Hub\Editor\2022.3.17f1c1\Editor\Unity.exe' `
  -batchmode -quit -nographics `
  -projectPath '<repo>/lab-project' `
  -executeMethod SbpLabEx18_StageProbe.Run `
  -logFile -
```

> 实验输出写到 `lab-project/Artifacts/SBP/`（已在 `.gitignore` 中忽略，可随时重跑再生）。

---

## Heavy 资源

Ex08 / Ex16 依赖约 **180MB** 的重资源，仓库刻意没有提交：

- `Assets/Bundles/Heavy/Audio`、`Assets/Bundles/Heavy/Art`：来自本机 `C:\UnityProj\ARPGTutorial`；
- `Assets/Bundles/Heavy/Textures`：由菜单 `SBP Lab/0b - Setup Heavy Textures` 生成。

细节见 [lab-project/Assets/Bundles/Heavy/README.md](lab-project/Assets/Bundles/Heavy/README.md)。

---

## 版本与边界

- 所有实测基于 **Unity 2022.3.17f1c1 + SBP 1.23.4**；不同版本/平台结果可能不同；
- 仓库不包含 SBP 源码（来自 PackageCache），阅读时请对照本地 `Library/PackageCache/com.unity.scriptablebuildpipeline@1.23.4/`；
- 缓存/确定性结论以 **本地 BuildCache + 当前 Editor 版本** 为前提；
- 这是学习工程，不是生产管线；部分实验使用绝对路径 / StandaloneWindows64 / Android 目标。

## 许可

- 本仓库为个人学习产出，欢迎参考、引用（注明来源即可）；
- Unity、Scriptable Build Pipeline 及相关源码版权归 Unity Technologies；
- 仓库内不包含任何商业资源。
