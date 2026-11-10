// ---------------------------------------------------------------------------
// Example 06 —— 操作任务链：在指定阶段插入任务 + 传递自定义上下文
//
// 教学目标：
//   1. 学会在默认任务链的**正确阶段**插入自定义任务，而不是只会 Add 到末尾；
//   2. 学会用自定义 IContextObject 在任务之间、任务与调用方之间传递数据；
//   3. 理解任务链的阶段边界：
//        Setup -> Scripts -> Dependency -> Packing -> Writing -> PostWriting
//      其中 Dependency 阶段的产物是 IDependencyData，Packing 才开始布局。
//
// 关键 API：
//   DefaultBuildTasks.Create(Preset.AssetBundleCompatible)  // IList<IBuildTask>
//   tasks.Insert(index, new MyTask())
//   ContentPipeline.BuildAssetBundles(params, content, out results, tasks, contextObjects)
//
// 注入规则复习（ContextInjector）：
//   - 字段必须是 private（默认就是）；
//   - [InjectContext(ContextUsage.In)] 从上下文取；
//   - 上下文的 key 是**接口/类型**，所以注入时写的类型要与 SetContextObject 注册的类型一致。
//
// 预期现象：
//   Console 先打印“依赖计算完成：N 个资源、M 个场景”，构建结束后打印
//   自定义上下文里记录的值——证明数据确实在任务与调用方之间传递了。
//
// 思考题：
//   - 如果把 DependencyReportTask 插到 PostDependencyCallback **之前**，还能拿到
//     完整的 IDependencyData 吗？为什么？（提示：CalculateAssetDependencyData 的产物）
//   - 自定义上下文为什么要实现 IContextObject？它其实是个空接口。
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;

/// <summary>
/// 自定义上下文对象：用来在“构建任务”和“调用方代码”之间传数据。
/// 必须实现 IContextObject（空标记接口），才能放进 BuildContext。
///
/// 注意：必须**通过接口**注入（见 ILabStats），不能直接用 class 类型注入。
/// 原因：ContextInjector.Extract 会把字段写回上下文，而
/// BuildContext.SetContextObject(Type, object) 要求 Type 是接口，
/// 直接注入 class 会在任务执行结束后抛 “Passed in type 'LabStatsContext' is not an interface”。
/// </summary>
public interface ILabStats : IContextObject
{
    int AssetCount { get; set; }
    int SceneCount { get; set; }
    int BundleCount { get; set; }
}

/// <summary>ILabStats 的默认实现。</summary>
public class LabStatsContext : ILabStats
{
    public int AssetCount { get; set; }
    public int SceneCount { get; set; }
    public int BundleCount { get; set; }
}

/// <summary>
/// 依赖报告任务：插在 PostDependencyCallback 之后，
/// 此时 IDependencyData 已经由 CalculateAssetDependencyData 填好。
/// </summary>
public class DependencyReportTask : IBuildTask
{
    public int Version { get { return 1; } }

    [InjectContext(ContextUsage.In)]
    IDependencyData m_DependencyData;

    // 自定义上下文：InOut 会“注入 -> 修改 -> 写回”，所以字段类型必须是接口
    [InjectContext(ContextUsage.InOut)]
    ILabStats m_Stats;

    public ReturnCode Run()
    {
        if (m_DependencyData == null)
        {
            Debug.LogWarning("[Ex06] 没有拿到 IDependencyData，任务可能插错了阶段");
            return ReturnCode.Success;
        }

        var assetCount = m_DependencyData.AssetInfo.Count;
        var sceneCount = m_DependencyData.SceneInfo.Count;

        Debug.Log($"[Ex06] 依赖计算完成：资源={assetCount}，场景={sceneCount}");

        if (m_Stats != null)
        {
            m_Stats.AssetCount = assetCount;
            m_Stats.SceneCount = sceneCount;
            Debug.Log("[Ex06] 已把统计结果写入自定义上下文 LabStatsContext");
        }

        return ReturnCode.Success;
    }
}

public static class SbpLabEx06_CustomTaskChain
{
    [MenuItem("SBP Lab/Examples/06 - 任务链插入 + 自定义上下文", priority = 106)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        var outPath = SbpLabCommon.OutputFolder("ex06-task-chain");
        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, outPath)
        {
            BundleCompression = UnityEngine.BuildCompression.LZ4,
            UseCache = true,
        };
        var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());

        // 1) 取默认任务链
        var tasks = DefaultBuildTasks.Create(DefaultBuildTasks.Preset.AssetBundleCompatible);

        // 2) 找到 PostDependencyCallback，在它后面插入自己的任务
        var insertAt = tasks.Count;
        for (var i = 0; i < tasks.Count; i++)
        {
            if (tasks[i].GetType().Name == "PostDependencyCallback")
            {
                insertAt = i + 1;
                break;
            }
        }
        tasks.Insert(insertAt, new DependencyReportTask());
        Debug.Log($"[Ex06] 任务链共 {tasks.Count} 个任务，DependencyReportTask 插在下标 {insertAt}");

        // 3) 传入自定义上下文对象。第 5 个参数是 params IContextObject[]，
        //    它会被放进 BuildContext，然后被 [InjectContext] 注入到任务里。
        var stats = new LabStatsContext();
        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults results, tasks, stats);
        if (code != ReturnCode.Success)
        {
            Debug.LogError("[Ex06] 构建失败，ReturnCode=" + code);
            return;
        }

        stats.BundleCount = results.BundleInfos.Count;
        Debug.Log($"[Ex06] 调用方读回上下文：资源={stats.AssetCount}，场景={stats.SceneCount}，bundle={stats.BundleCount}");
    }
}
#endif
