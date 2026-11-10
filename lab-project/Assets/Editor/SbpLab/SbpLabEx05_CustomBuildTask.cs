// ---------------------------------------------------------------------------
// Example 05 —— 写第一个自定义 BuildTask（依赖注入最小闭环）
//
// 教学目标：
//   1. 学会实现 IBuildTask（只要 Version + Run 两个成员）；
//   2. 理解 [InjectContext] 的三种用法：
//        In  : 从 BuildContext 取数据（任务 -> 上下文）
//        Out : 把数据写回 BuildContext（上下文 -> 后续任务）
//        InOut: 既读又写（默认值）
//   3. 亲手把一个任务插到默认任务链末尾，观察它能拿到什么数据。
//
// 关键 API：
//   public class MyTask : IBuildTask { public int Version => 1; public ReturnCode Run() {...} }
//   [InjectContext(ContextUsage.In)] 字段;
//   ContentPipeline.BuildAssetBundles(params, content, out results, taskList)
//
// 重要细节：
//   - 注入字段必须声明为**私有**（ContextInjector 只扫描 NonPublic 字段）；
//   - 任务放在链尾，因此能拿到“已经写盘完成”的 IBundleBuildResults；
//     如果插在写盘之前，注入的 results 虽然存在但 BundleInfos 还是空的。
//   - Version 参与缓存 key：当你修改任务逻辑时把它 +1，否则 SBP 可能复用
//     旧缓存，导致你以为新逻辑没生效。
//
// 预期现象：
//   Console 依次打印 BundleLayout 明细和最终构建结果（都是由自定义任务打印）。
//
// 思考题：
//   - 把 LabSummaryTask 插到 BuildPlayerScripts 之前，会发生什么？会不会报错？
//   - 如果两个自定义任务要传数据，应该用 In 还是 Out？
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;

/// <summary>
/// 自定义任务：在构建结束后汇报“内容布局”和“产物结果”。
/// 这是理解 SBP 任务模型最小的可运行例子。
/// </summary>
public class LabSummaryTask : IBuildTask
{
    /// <summary>
    /// 任务版本号。SBP 的缓存以 (任务类型, Version, 输入 hash) 为 key 的一部分。
    /// 修改 Run 的逻辑后记得把它 +1，否则旧的缓存结果可能被复用。
    /// </summary>
    public int Version { get { return 1; } }

    // 注入内容容器：bundle -> 资源 GUID 列表。
    // ContextUsage.In 表示“只从上下文读取，不写回”。
    [InjectContext(ContextUsage.In)]
    IBundleBuildContent m_Content;

    // 注入构建结果：构建完成后每个 bundle 的 hash / crc / 依赖。
    [InjectContext(ContextUsage.In)]
    IBundleBuildResults m_Results;

    public ReturnCode Run()
    {
        Debug.Log("[Ex05] LabSummaryTask 开始执行（任务链末尾）");

        if (m_Content != null)
        {
            foreach (var pair in m_Content.BundleLayout)
                Debug.Log($"[Ex05]   layout: {pair.Key,-10} 资源数={pair.Value.Count}");
        }

        if (m_Results != null)
        {
            foreach (var kv in m_Results.BundleInfos)
                Debug.Log($"[Ex05]   result: {kv.Key,-10} hash={kv.Value.Hash} deps={(kv.Value.Dependencies == null ? 0 : kv.Value.Dependencies.Length)}");
        }

        return ReturnCode.Success;
    }
}

public static class SbpLabEx05_CustomBuildTask
{
    [MenuItem("SBP Lab/Examples/05 - 自定义 BuildTask", priority = 105)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        var outPath = SbpLabCommon.OutputFolder("ex05-custom-task");
        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, outPath)
        {
            BundleCompression = UnityEngine.BuildCompression.LZ4,
            UseCache = true,
        };
        var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());

        // 关键：先取默认任务链，再把自定义任务加进去。
        // DefaultBuildTasks.Create 返回的是 IList<IBuildTask>，可以直接 Add/Insert。
        var tasks = DefaultBuildTasks.Create(DefaultBuildTasks.Preset.AssetBundleCompatible);
        tasks.Add(new LabSummaryTask());

        Debug.Log("[Ex05] 当前任务链共 " + tasks.Count + " 个任务，最后一个是 " + tasks[tasks.Count - 1].GetType().Name);

        // 传入自定义任务链的 4 参重载（第 5 个参数 params contextObjects 可省略）
        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults results, tasks);
        if (code != ReturnCode.Success)
        {
            Debug.LogError("[Ex05] 构建失败，ReturnCode=" + code);
            return;
        }

        Debug.Log("[Ex05] 构建完成，自定义任务已在管线中执行");
    }
}
#endif
