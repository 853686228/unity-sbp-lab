// ---------------------------------------------------------------------------
// Example 21 —— AssetDatabase.GetAssetDependencyHash 探针
//
// 目的：
//   验证 CalculateAssetDependencyData 的缓存 key（entry.Hash = GetAssetDependencyHash + Version）
//   在下面三种情况下会不会变：
//     1. 什么都不改（基准）；
//     2. 只改 CubeA.prefab 自己的属性（transform 位置）；
//     3. 改 CubeA 依赖的 Mat.mat（material 颜色）。
//
// 采样：
//   - 每个阶段直接打印 AssetDatabase.GetAssetDependencyHash(path) 和文件 MD5（对比“导入 hash” vs “文件字节”）；
//   - 复用 Ex20 的 LoggingBuildCache 抓 CalculateAssetDependencyData 的 Load 事件：
//       entry.Guid / entry.Hash / hit/miss
//   - 每次修改后重新构建，对比缓存命中情况。
//
// 结论（跑完看报告）：
//   - 改资产自身序列化属性 → 文件 MD5 变、GetAssetDependencyHash 变 → 该资产缓存 miss；
//   - 改它依赖的资产 → prefab 文件 MD5 不变，但 GetAssetDependencyHash 可能变（取决于依赖是否被跟踪）
//     → 这正是“依赖 hash”和“文件 hash”的区别。
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.Tasks;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEngine;

public class CacheHookTask21 : IBuildTask
{
    public int Version { get { return 1; } }

    [InjectContext(ContextUsage.In)] IBuildContext m_Context;
    [InjectContext(ContextUsage.In)] IBuildCache m_Cache;

    public ReturnCode Run()
    {
        SbpLabEx21_AssetHashProbe.Install(m_Context, m_Cache);
        return ReturnCode.Success;
    }
}

public static class SbpLabEx21_AssetHashProbe
{
    static readonly string[] kAssets =
    {
        "Assets/Bundles/Props/CubeA.prefab",
        "Assets/Bundles/Props/CubeB.prefab",
        "Assets/Bundles/Props/CubeC.prefab",
        "Assets/Bundles/Shared/Mat.mat",
    };

    static List<string> s_Log;
    static string s_Label;

    [MenuItem("SBP Lab/Examples/21 - AssetDependencyHash 探针", priority = 121)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();
        BuildCache.PurgeCache(false);

        var log = new List<string>();
        s_Log = log;

        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, SbpLabCommon.OutputFolder("ex21-asset-hash"))
        {
            BundleCompression = UnityEngine.BuildCompression.LZ4,
            UseCache = true,
        };

        Snapshot(log, "before");
        Build(parameters, "cold", log);

        ModifyPrefab();
        Snapshot(log, "after-prefab-change");
        Build(parameters, "after-prefab-change", log);

        ModifyMaterial();
        Snapshot(log, "after-material-change");
        Build(parameters, "after-material-change", log);

        Directory.CreateDirectory(SbpLabCommon.ArtifactsRoot);
        string report = Path.Combine(SbpLabCommon.ArtifactsRoot, "ex21-asset-hash-probe.txt");
        File.WriteAllText(report, string.Join("\n", log.ToArray()));
        Debug.Log("[Ex21] 完成 -> " + report);
    }

    static void Build(BundleBuildParameters parameters, string label, List<string> log)
    {
        s_Label = label;

        var tasks = new List<IBuildTask>();
        tasks.Add(new CacheHookTask21());
        tasks.AddRange(DefaultBuildTasks.Create(DefaultBuildTasks.Preset.AssetBundleCompatible));

        var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());
        var sw = Stopwatch.StartNew();
        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults _, tasks);
        sw.Stop();

        log.Add("");
        log.Add("=== build " + label + " ===");
        log.Add("code=" + code + " totalMs=" + sw.ElapsedMilliseconds);
    }

    internal static void Install(IBuildContext context, IBuildCache actual)
    {
        context.SetContextObject(typeof(IBuildCache), new LoggingBuildCache(actual, s_Log, s_Label));
    }

    static void Snapshot(List<string> log, string stage)
    {
        log.Add("--- snapshot: " + stage + " ---");
        foreach (var path in kAssets)
        {
            var depHash = AssetDatabase.GetAssetDependencyHash(path);
            log.Add(string.Format("[hash] {0} {1} depHash={2} md5={3}",
                stage, path, depHash, Md5(path)));
        }
    }

    static void ModifyPrefab()
    {
        const string path = "Assets/Bundles/Props/CubeA.prefab";
        var root = PrefabUtility.LoadPrefabContents(path);
        root.transform.localPosition += new Vector3(0.25f, 0.5f, 0f);
        PrefabUtility.SaveAsPrefabAsset(root, path);
        PrefabUtility.UnloadPrefabContents(root);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[Ex21] 修改 CubeA.prefab transform.localPosition");
    }

    static void ModifyMaterial()
    {
        const string path = "Assets/Bundles/Shared/Mat.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        material.color = new Color(0.9f, 0.2f, 0.15f, 1f);
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        AssetDatabase.Refresh();
        Debug.Log("[Ex21] 修改 Mat.mat color");
    }

    static string Md5(string assetPath)
    {
        if (!File.Exists(assetPath)) return "missing";
        using (var md5 = MD5.Create())
        using (var stream = File.OpenRead(assetPath))
        {
            var hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
#endif
