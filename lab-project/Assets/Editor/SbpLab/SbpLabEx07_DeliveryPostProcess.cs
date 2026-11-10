// ---------------------------------------------------------------------------
// Example 07 —— 构建后处理：把产物整理成“可交付”的目录 + 清单
//
// 教学目标：
//   1. 学会写“后处理型” BuildTask：读取 IBundleBuildResults，对已落盘的文件做事；
//   2. 认识 SBP 与内置管线的一个重要差异：
//        SBP 的 ContentPipeline **不会**自动生成 .manifest 文件；
//        清单信息都在 IBundleBuildResults 里，需要自己导出。
//   3. 理解“交付目录”的典型结构（版本目录 + 清单 + bundle 文件），
//      这是接 CDN / 热更系统前的最后一步。
//
// 关键 API：
//   IBundleBuildResults.BundleInfos  -> 每个 bundle 的 BundleDetails
//   BundleDetails.FileName / Hash / Crc / Dependencies
//
// 预期现象：
//   Artifacts/SBP/ex07-delivery/<时间戳>/ 下出现 3 个 bundle + delivery-manifest.json。
//
// 思考题：
//   - 如果要做“差异更新”，除了 hash，还需要哪些信息？（提示：依赖关系、bundle 内资源）
//   - 为什么后处理任务通常放在 AppendBundleHash 之后？如果放在之前会怎样？
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;

/// <summary>清单里的单条 bundle 记录。</summary>
[Serializable]
public class DeliveryEntry
{
    public string name;
    public string hash;
    public uint crc;
    public long size;
    public string[] dependencies;
}

/// <summary>交付清单：版本、平台、时间、bundle 列表。</summary>
[Serializable]
public class DeliveryManifest
{
    public string builtAt;
    public string target;
    public List<DeliveryEntry> bundles = new List<DeliveryEntry>();
}

/// <summary>
/// 后处理任务：把构建好的 bundle 复制到交付目录，并生成 JSON 清单。
/// 构造时传入交付目录，任务本身不需要知道其它业务配置。
/// </summary>
public class DeliveryTask : IBuildTask
{
    public int Version { get { return 1; } }

    [InjectContext(ContextUsage.In)]
    IBundleBuildResults m_Results;

    readonly string m_DeliveryFolder;

    public DeliveryTask(string deliveryFolder)
    {
        m_DeliveryFolder = deliveryFolder;
    }

    public ReturnCode Run()
    {
        if (m_Results == null)
        {
            Debug.LogError("[Ex07] 没有拿到 IBundleBuildResults");
            return ReturnCode.Success;
        }

        Directory.CreateDirectory(m_DeliveryFolder);
        var manifest = new DeliveryManifest
        {
            builtAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            target = EditorUserBuildSettings.activeBuildTarget.ToString(),
        };

        foreach (var kv in m_Results.BundleInfos)
        {
            var details = kv.Value;
            var source = SbpLabCommon.ResolveBundleFile(details);
            if (!File.Exists(source))
            {
                Debug.LogWarning("[Ex07] 找不到产物文件：" + source);
                continue;
            }

            var dest = Path.Combine(m_DeliveryFolder, kv.Key);
            File.Copy(source, dest, true);

            manifest.bundles.Add(new DeliveryEntry
            {
                name = kv.Key,
                hash = details.Hash.ToString(),
                crc = details.Crc,
                size = new FileInfo(dest).Length,
                dependencies = details.Dependencies ?? new string[0],
            });

            Debug.Log($"[Ex07] 已交付 {kv.Key,-10} -> {dest}");
        }

        var manifestPath = Path.Combine(m_DeliveryFolder, "delivery-manifest.json");
        File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));
        Debug.Log("[Ex07] 清单已生成：" + manifestPath);
        Debug.Log("[Ex07] 提示：SBP 不像内置管线那样自动写 .manifest，这份清单就是替代品");

        return ReturnCode.Success;
    }
}

public static class SbpLabEx07_DeliveryPostProcess
{
    [MenuItem("SBP Lab/Examples/07 - 后处理：生成交付目录与清单", priority = 107)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        var outPath = SbpLabCommon.OutputFolder("ex07-build");
        var deliveryFolder = Path.Combine(
            SbpLabCommon.ArtifactsRoot,
            "ex07-delivery",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(deliveryFolder);

        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, outPath)
        {
            BundleCompression = UnityEngine.BuildCompression.LZ4,
            UseCache = true,
        };
        var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());

        // 默认任务链 + 末尾追加交付任务
        var tasks = DefaultBuildTasks.Create(DefaultBuildTasks.Preset.AssetBundleCompatible);
        tasks.Add(new DeliveryTask(deliveryFolder));

        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults results, tasks);
        if (code != ReturnCode.Success)
        {
            Debug.LogError("[Ex07] 构建失败，ReturnCode=" + code);
            return;
        }

        Debug.Log("[Ex07] 交付目录：" + deliveryFolder);
    }
}
#endif
