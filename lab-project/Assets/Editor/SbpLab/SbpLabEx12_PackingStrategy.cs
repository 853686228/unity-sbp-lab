// ---------------------------------------------------------------------------
// Example 12 —— 自定义打包策略：在 Packing 之前合并小 bundle
//
// 难度：★★★★
//
// 背景：
//   “哪些资源进哪个 bundle”在 SBP 里是 **可编程** 的：布局就是
//   IBundleBuildContent.BundleLayout（bundle 名 -> List<GUID>）。
//   只要在 Packing 阶段之前改写它，最终产物就会按新布局生成。
//
// 教学目标：
//   1. 学会在任务链的 Dependency 与 Packing 之间插入“布局改写”任务；
//   2. 理解 InOut 注入：任务修改的是上下文里的同一个对象，后续任务立即看到；
//   3. 建立“bundle 数量 vs 加载开销”的工程直觉：
//      小 bundle 太多会增加下载请求数与运行时管理成本，太少则失去按需加载粒度。
//
// 关键 API：
//   [InjectContext(ContextUsage.InOut)] IBundleBuildContent m_Content;
//   m_Content.BundleLayout  // Dictionary<string, List<GUID>>，可以直接增删
//
// 本示例策略：
//   把 props_a / props_b 合并成 props_all，shared 保持独立。
//   预期产物从 3 个 bundle 变成 2 个，且 props_all 依赖 shared。
//
// 预期现象：
//   [Ex12] 合并前：props_a(2), props_b(1), shared(1)
//   [Ex12] 合并后：props_all(3), shared(1)
//   [Ex12] 构建结果：2 个 bundle
//
// 思考题：
//   - 合并后 props_b 的资源也被 props_a 的加载者一并下载，代价是什么？
//   - 如果要“按大小阈值自动合并”，应该看资源字节数还是序列化后的 Object 数？
//   - 布局改写任务放在 PostDependencyCallback 之后、GenerateBundlePacking 之前，
//     如果放错位置（例如放到 PostPackingCallback 之后）会怎样？
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;

/// <summary>
/// 布局改写任务：把若干源 bundle 合并进目标 bundle。
/// 注意：需要 InOut，因为要修改上下文里的 IBundleBuildContent。
/// </summary>
public class MergeBundlesTask : IBuildTask
{
    public int Version { get { return 1; } }

    [InjectContext(ContextUsage.InOut)]
    IBundleBuildContent m_Content;

    readonly string m_TargetBundle;
    readonly string[] m_SourceBundles;

    public MergeBundlesTask(string targetBundle, params string[] sourceBundles)
    {
        m_TargetBundle = targetBundle;
        m_SourceBundles = sourceBundles;
    }

    public ReturnCode Run()
    {
        if (m_Content == null)
        {
            Debug.LogError("[Ex12] 没有拿到 IBundleBuildContent");
            return ReturnCode.Success;
        }

        var layout = m_Content.BundleLayout;
        Debug.Log("[Ex12] 合并前：" + Describe(layout));

        var merged = new List<UnityEditor.GUID>();
        foreach (var source in m_SourceBundles)
        {
            if (!layout.TryGetValue(source, out var guids)) continue;
            merged.AddRange(guids);      // 保留原顺序，Object 排序会影响包内布局
            layout.Remove(source);
        }

        if (merged.Count > 0)
            layout[m_TargetBundle] = merged; // 这里的写入会被后续 Packing 任务读到

        Debug.Log("[Ex12] 合并后：" + Describe(layout));
        return ReturnCode.Success;
    }

    static string Describe(Dictionary<string, List<UnityEditor.GUID>> layout)
    {
        var parts = new List<string>();
        foreach (var pair in layout) parts.Add(pair.Key + "(" + pair.Value.Count + ")");
        return string.Join(", ", parts.ToArray());
    }
}

public static class SbpLabEx12_PackingStrategy
{
    [MenuItem("SBP Lab/Examples/12 - 自定义打包策略（合并小 bundle）", priority = 112)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        var outPath = SbpLabCommon.OutputFolder("ex12-merge");
        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, outPath)
        {
            BundleCompression = UnityEngine.BuildCompression.LZ4,
            UseCache = true,
        };
        var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());

        // 1) 默认任务链
        var tasks = DefaultBuildTasks.Create(DefaultBuildTasks.Preset.AssetBundleCompatible);

        // 2) 找到 PostDependencyCallback，在它之后（Packing 之前）插入布局改写任务
        var insertAt = tasks.Count;
        for (var i = 0; i < tasks.Count; i++)
        {
            if (tasks[i].GetType().Name == "PostDependencyCallback")
            {
                insertAt = i + 1;
                break;
            }
        }

        tasks.Insert(insertAt, new MergeBundlesTask("props_all", "props_a", "props_b"));
        Debug.Log($"[Ex12] MergeBundlesTask 插入下标 {insertAt}（PostDependencyCallback 之后、Packing 之前）");

        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults results, tasks);
        if (code != ReturnCode.Success)
        {
            Debug.LogError("[Ex12] 构建失败，ReturnCode=" + code);
            return;
        }

        Debug.Log($"[Ex12] 构建结果：{results.BundleInfos.Count} 个 bundle");
        foreach (var kv in results.BundleInfos)
        {
            var file = SbpLabCommon.ResolveBundleFile(kv.Value);
            var size = System.IO.File.Exists(file) ? new System.IO.FileInfo(file).Length : 0;
            var deps = kv.Value.Dependencies == null ? 0 : kv.Value.Dependencies.Length;
            Debug.Log($"[Ex12]   {kv.Key,-10} hash={kv.Value.Hash} size={SbpLabCommon.Human(size),9} deps={deps}");
        }
    }
}
#endif
