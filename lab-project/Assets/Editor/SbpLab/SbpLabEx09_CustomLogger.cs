// ---------------------------------------------------------------------------
// Example 09 —— 自定义 IBuildLogger：把构建过程变成可分析的性能数据
//
// 难度：★★★★
//
// 教学目标：
//   1. 搞清 ContentPipeline 是怎么挑选 logger 的：
//        BuildContext 里如果已经有 IBuildLogger，就直接用它；
//        否则内部 new 一个 BuildLog() 并导出 buildlogtep.json。
//      这意味着：把自定义 logger 作为 contextObject 传进去，就能接管整个构建的埋点。
//   2. 学会实现 IBuildLogger 的三个方法，并用线程栈正确处理嵌套/多线程的 step；
//   3. 把 SBP 的“步骤耗时”变成可排序、可聚合的数据（真实项目里可以上报到监控）。
//
// 关键 API：
//   interface IBuildLogger : IContextObject
//     void AddEntry(LogLevel level, string msg);
//     void BeginBuildStep(LogLevel level, string stepName, bool subStepsCanBeThreaded);
//     void EndBuildStep();
//   ContentPipeline.BuildAssetBundles(params, content, out results, taskList, logger)
//
// 为什么步骤/结束要配对：
//   SBP 内部大量使用 `using (logger.ScopedStep(...))`，Dispose 时调用 EndBuildStep。
//   所以 Begin/End 一定是成对的；但缓存线程可能并发调用，必须用 [ThreadStatic] 栈
//   + 锁保护结果列表，否则会串台或抛栈空异常。
//
// 预期现象：
//   Console 打印“共采集 N 个 step”，以及耗时 Top 10 与按名称聚合的 Top 10。
//   和 buildlogtep.json（Perfetto 打开）对照，数据应基本一致。
//
// 思考题：
//   - 为什么 AddEntry 的 message 不应该直接拼进 stepName？（提示：聚合统计会碎片化）
//   - 如果要做“超过阈值就报警”，阈值应该基于绝对耗时还是占比？
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

/// <summary>
/// 自定义构建日志器：记录每个 build step 的起止时间，构建结束后做聚合分析。
/// </summary>
public class ProfilingLogger : IBuildLogger
{
    /// <summary>单个 step 的耗时记录（public 因为 Snapshot 会把它暴露给调用方）。</summary>
    public class StepRecord
    {
        public string Name;
        public double StartMs;
        public double EndMs;
        public int ThreadId;

        public double DurationMs { get { return EndMs - StartMs; } }
    }

    // 每个线程一份“未结束的 step 栈”。SBP 的缓存/序列化步骤可能跑在 worker 线程上，
    // 用单个共享栈会张冠李戴，所以必须是 ThreadStatic。
    [System.ThreadStatic]
    static Stack<StepRecord> s_ThreadStack;

    readonly object m_Lock = new object();
    readonly List<StepRecord> m_Records = new List<StepRecord>();

    public int Count { get { lock (m_Lock) return m_Records.Count; } }

    public List<StepRecord> Snapshot()
    {
        lock (m_Lock) return new List<StepRecord>(m_Records);
    }

    static double NowMs
    {
        get { return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency; }
    }

    /// <summary>
    /// 步骤内的附加信息。示例里直接忽略；真实项目可采集到列表里做上下文分析。
    /// 注意：不要在 AddEntry 里做耗时统计，它没有配对的 End。
    /// </summary>
    public void AddEntry(LogLevel level, string msg) { }

    public void BeginBuildStep(LogLevel level, string stepName, bool subStepsCanBeThreaded)
    {
        if (s_ThreadStack == null) s_ThreadStack = new Stack<StepRecord>();
        s_ThreadStack.Push(new StepRecord
        {
            Name = stepName,
            StartMs = NowMs,
            ThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId,
        });
    }

    public void EndBuildStep()
    {
        if (s_ThreadStack == null || s_ThreadStack.Count == 0) return;

        var record = s_ThreadStack.Pop();
        record.EndMs = NowMs;
        lock (m_Lock) m_Records.Add(record);
    }

    /// <summary>按 step 名称聚合（同名嵌套会累加），返回总耗时降序。</summary>
    public List<KeyValuePair<string, double>> AggregateByName()
    {
        var totals = new Dictionary<string, double>();
        foreach (var record in Snapshot())
        {
            if (!totals.ContainsKey(record.Name)) totals[record.Name] = 0;
            totals[record.Name] += record.DurationMs;
        }
        return totals
            .OrderByDescending(pair => pair.Value)
            .ToList();
    }
}

public static class SbpLabEx09_CustomLogger
{
    [MenuItem("SBP Lab/Examples/09 - 自定义 BuildLogger（性能剖析）", priority = 109)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        var outPath = SbpLabCommon.OutputFolder("ex09-profiling");
        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, outPath)
        {
            BundleCompression = UnityEngine.BuildCompression.LZ4,
            UseCache = true,
        };
        var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());
        var tasks = DefaultBuildTasks.Create(DefaultBuildTasks.Preset.AssetBundleCompatible);

        // 把自定义 logger 作为 contextObject 传入。
        // ContentPipeline 会先查 BuildContext 里有没有 IBuildLogger：有就用我们的。
        var logger = new ProfilingLogger();
        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults results, tasks, logger);
        if (code != ReturnCode.Success)
        {
            Debug.LogError("[Ex09] 构建失败，ReturnCode=" + code);
            return;
        }

        Debug.Log($"[Ex09] 构建完成，共采集 {logger.Count} 个 step");

        Debug.Log("[Ex09] ===== 单独耗时 Top 10 =====");
        foreach (var record in logger.Snapshot().OrderByDescending(r => r.DurationMs).Take(10))
        {
            Debug.Log($"[Ex09]   {record.DurationMs,8:0.0} ms  {record.Name}  (thread={record.ThreadId})");
        }

        Debug.Log("[Ex09] ===== 按名称聚合 Top 10 =====");
        foreach (var pair in logger.AggregateByName().Take(10))
        {
            Debug.Log($"[Ex09]   {pair.Value,8:0.0} ms  {pair.Key}");
        }

        Debug.Log("[Ex09] 对照：Artifacts/SBP/ex09-profiling-<平台>/buildlogtep.json（用 Perfetto 打开）");
    }
}
#endif
