# SBP Learning Lab & Wiki

Unity 2022.3.17f1c1 · Scriptable Build Pipeline 1.23.4

这个仓库把 **SBP（Scriptable Build Pipeline）学习工程、任务链/依赖/缓存/确定性 Wiki、以及各实验的采样数据** 从原始 E2E 工程中提取出来，方便独立阅读、复现和分享。

> 原始工程：`_e2e/UnityGradleDemo`。本仓库只保留 SBP 相关脚本、轻量资源、文档和数据；`Library/`、`Artifacts/` 二进制产物、`Assets/Bundles/Heavy/`（约 180MB 重资源）已排除。

---

## 目录

```text
.
├─ lab-project/                      # 可打开的 Unity 工程（SBP 相关部分）
│  ├─ Assets/Editor/SbpLabBuild.cs   # 基础菜单：Setup / Compat build / ContentPipeline build / diff
│  ├─ Assets/Editor/SbpLab/          # Ex01 ~ Ex22 实验脚本 + 公共代码
│  ├─ Assets/Bundles/Props|Shared/   # 轻量测试资源（3 prefab + 1 material）
│  ├─ Packages/                      # manifest + lock（含 SBP 1.23.4）
│  ├─ ProjectSettings/               # Unity 工程设置
│  ├─ Docs/                          # 源码级专题 Wiki + 实测报告
│  └─ SBP-Lab-Wiki.md                # 主实验 Wiki（案例 01~18）
├─ wiki/
│  └─ task-chain/                    # 任务链逐阶段 Wiki（19 任务 + 依赖/装箱子文档 + 采样数据）
└─ data/                             # 各实验的 summary/report JSON
```

---

## 快速开始

1. 用 **Unity 2022.3.17f1c1** 打开 `lab-project/`；
2. 确认 `lab-project/Packages/manifest.json` 已包含 `"com.unity.scriptablebuildpipeline": "1.23.4"`；
3. 菜单里选择：
   - `SBP Lab/0 - Setup Test Assets`
   - `SBP Lab/1 - Compatibility Build`
   - `SBP Lab/2 - ContentPipeline Build`
   - `SBP Lab/Examples/18 - 全流程阶段探针`（任务链数据）
   - `SBP Lab/Examples/20 - 构建缓存采样`
   - `SBP Lab/Examples/21 - AssetDependencyHash 探针`
   - `SBP Lab/Examples/22 - CachedInfo 实例 dump`

> 实验脚本默认输出到 `lab-project/Artifacts/SBP/`（已被 `.gitignore` 忽略，跑完可再生）。

---

## 文档

### 主实验 Wiki

- [SBP-Lab-Wiki.md](lab-project/SBP-Lab-Wiki.md) —— 案例 01~18：Compatibility、ContentPipeline、压缩、缓存、自定义任务、交付、Preset、确定性等

### 源码级专题

- [SBP-BuildCache.md](lab-project/Docs/SBP-BuildCache.md) —— 缓存实现原理与架构（CacheEntry/CachedInfo、Load/Save、任务 key、Ex20/21/22 实测）
- [SBP-Determinism.md](lab-project/Docs/SBP-Determinism.md) —— 确定性构建（ID 生成、排序、ContentHash/RawFileHash、BundleDetails.Hash、构建指纹）
- [SBP-Cache-Invalidation.md](lab-project/Docs/SBP-Cache-Invalidation.md) —— 缓存失效图谱（GlobalHash / Guid / Version / Hash / 依赖校验）

### 任务链逐阶段

- [README.md](wiki/task-chain/README.md) —— 19 个任务的职责/输入输出/数据结构 + Ex18 阶段数据
- [Dependency-Data-Structures.md](wiki/task-chain/Dependency-Data-Structures.md) —— `ObjectIdentifier`、FileType、included vs referenced、prefabA 案例、TypeDB、native/C# 边界
- [Bundle-Write-Data.md](wiki/task-chain/Bundle-Write-Data.md) —— `IBundleWriteData`、file/bundle/SerializedFile、单对象实例走查

---

## 数据

`data/` 与 `lab-project/Docs/` 中包含实验采样：

| 文件 | 内容 |
|---|---|
| `data/ex18-stage-probe.json` | 19 任务 × 20 个边界的 BuildContext 快照 |
| `data/ex16-summary.json` | 重内容压缩矩阵（冷/热构建） |
| `data/ex17-summary.json` | 确定性 A/B/C/D/E 结果 |
| `data/ex13-summary.json` | 多配置构建矩阵 |
| `lab-project/Docs/ex20-cache-probe.txt` | 冷/热构建缓存命中、CacheEntry、路径、大小 |
| `lab-project/Docs/ex21-asset-hash-probe.txt` | GetAssetDependencyHash / MD5 / 缓存命中对比 |
| `lab-project/Docs/ex22-cachedinfo-dump.txt` | CachedInfo.Data[0]/[4]/[5]/Dependencies 完整实例 |
| `wiki/task-chain/captured/` | Ex18 stage probe 原始 JSON、摘要、日志、Ex19 FileType dump |

---

## Heavy 资源说明

Ex08 / Ex16 依赖约 180MB 的重资源（音频/动画 FBX/大贴图），**没有提交**：

- `Assets/Bundles/Heavy/Audio`、`Assets/Bundles/Heavy/Art` 来自本机 `C:\UnityProj\ARPGTutorial`；
- `Assets/Bundles/Heavy/Textures` 可由菜单 `SBP Lab/0b - Setup Heavy Textures` 生成。

见 [lab-project/Assets/Bundles/Heavy/README.md](lab-project/Assets/Bundles/Heavy/README.md)。

---

## 说明

- 代码中的绝对路径、平台目标（StandaloneWindows64 / Android）以原始实验环境为准；
- 所有 Wiki 与实测数据均来自 Unity 2022.3.17f1c1 + SBP 1.23.4；
- 这些工程/文档是学习与验证用途，不是生产管线。
