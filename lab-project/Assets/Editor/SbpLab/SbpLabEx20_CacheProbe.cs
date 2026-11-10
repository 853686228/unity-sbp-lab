// ---------------------------------------------------------------------------
// Example 20 —— BuildCache 采样：冷构建 vs 热构建，抓取关键数据结构与运行时值
//
// 目的（配合 Docs/SBP-BuildCache.md 的实测章节）：
//   1. 给 ContentPipeline 内部的 BuildCache 套一层 IBuildCache 包装（不修改 SBP 源码）；
//   2. 记录每次 LoadCachedData / SaveCachedData 的：
//        - 调用方任务（CalculateAssetDependencyData / WriteSerializedFiles / ArchiveAndCompressBundles ...）
//        - CacheEntry：Type / Guid / Version / Hash
//        - 命中数 / 未命中数、耗时
//        - GetCachedInfoFile / GetCachedArtifactsDirectory 的真实路径、.info 大小、artifact 文件数
//   3. 冷构建（PurgeCache 后）与热构建各跑一次，对比结果；
//   4. 输出缓存目录统计：dirs / files / .info 数 / 总字节数。
//
// 原理：
//   ContentPipeline 会 new BuildCache 并注册到 BuildContext（接口类型 IBuildCache）。
//   我们在任务链最前面插入 CacheHookTask：
//     - 注入拿到真实的 IBuildCache；
//     - 用 LoggingBuildCache 包装它；
//     - buildContext.SetContextObject(typeof(IBuildCache), wrapper) 替换接口键。
//   之后所有任务的 [InjectContext] IBuildCache 拿到的就是包装对象，从而可以采样。
//   真实 BuildCache 仍由 ContentPipeline 用于日志/Prune，包装对象只是代理。
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Stopwatch = System.Diagnostics.Stopwatch;
using StackTrace = System.Diagnostics.StackTrace;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEngine;

public class CacheHookTask : IBuildTask
{
    public int Version { get { return 1; } }

    [InjectContext(ContextUsage.In)]
    IBuildContext m_Context;

    [InjectContext(ContextUsage.In)]
    IBuildCache m_Cache;

    public ReturnCode Run()
    {
        SbpLabEx20_CacheProbe.Install(m_Context, m_Cache);
        return ReturnCode.Success;
    }
}

/// <summary>IBuildCache 的日志代理：只记录，不改变行为。</summary>
public class LoggingBuildCache : IBuildCache
{
    readonly IBuildCache m_Inner;
    readonly List<string> m_Log;
    readonly string m_Label;

    public LoggingBuildCache(IBuildCache inner, List<string> log, string label)
    {
        m_Inner = inner;
        m_Log = log;
        m_Label = label;
    }

    public CacheEntry GetCacheEntry(GUID asset, int version = 1) { return m_Inner.GetCacheEntry(asset, version); }
    public CacheEntry GetCacheEntry(string path, int version = 1) { return m_Inner.GetCacheEntry(path, version); }
    public CacheEntry GetCacheEntry(ObjectIdentifier objectID, int version = 1) { return m_Inner.GetCacheEntry(objectID, version); }
    public CacheEntry GetCacheEntry(Type type, int version = 1) { return m_Inner.GetCacheEntry(type, version); }
    public bool HasAssetOrDependencyChanged(CachedInfo info) { return m_Inner.HasAssetOrDependencyChanged(info); }
    public string GetCachedInfoFile(CacheEntry entry) { return m_Inner.GetCachedInfoFile(entry); }
    public string GetCachedArtifactsDirectory(CacheEntry entry) { return m_Inner.GetCachedArtifactsDirectory(entry); }

    public void LoadCachedData(IList<CacheEntry> entries, out IList<CachedInfo> cachedInfos)
    {
        var sw = Stopwatch.StartNew();
        m_Inner.LoadCachedData(entries, out cachedInfos);
        sw.Stop();

        int hits = 0;
        if (cachedInfos != null)
            for (int i = 0; i < cachedInfos.Count; i++)
                if (cachedInfos[i] != null)
                    hits++;

        m_Log.Add(string.Format(
            "[Load] build={0} caller={1} entries={2} hits={3} misses={4} ms={5}",
            m_Label, Caller(), entries.Count, hits, entries.Count - hits, sw.ElapsedMilliseconds));

        for (int i = 0; i < entries.Count; i++)
        {
            bool hit = cachedInfos != null && i < cachedInfos.Count && cachedInfos[i] != null;
            LogEntry("  load", entries[i], hit);
        }
    }

    public void SaveCachedData(IList<CachedInfo> infos)
    {
        var sw = Stopwatch.StartNew();
        m_Inner.SaveCachedData(infos);
        sw.Stop();

        m_Log.Add(string.Format(
            "[Save] build={0} caller={1} count={2} ms={3}",
            m_Label, Caller(), infos.Count, sw.ElapsedMilliseconds));

        for (int i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            LogEntry("  save", info.Asset, null);
            m_Log.Add(string.Format("       data={0} deps={1}",
                info.Data == null ? 0 : info.Data.Length,
                info.Dependencies == null ? 0 : info.Dependencies.Length));
        }
    }

    void LogEntry(string prefix, CacheEntry entry, bool? hit)
    {
        string infoFile = m_Inner.GetCachedInfoFile(entry);
        string artifactsDir = m_Inner.GetCachedArtifactsDirectory(entry);
        string infoState = File.Exists(infoFile)
            ? "exists," + new FileInfo(infoFile).Length + "B"
            : "missing";

        int artifactCount = 0;
        long artifactBytes = 0;
        string sample = "";
        if (Directory.Exists(artifactsDir))
        {
            var files = Directory.GetFiles(artifactsDir);
            artifactCount = files.Length;
            artifactBytes = files.Sum(f => new FileInfo(f).Length);
            sample = files.Length > 0 ? Path.GetFileName(files[0]) : "";
        }

        m_Log.Add(string.Format(
            "{0} type={1} guid={2} version={3} hash={4} hit={5} info={6} artifacts={7}files/{8}B sample={9}",
            prefix, entry.Type, entry.Guid, entry.Version, entry.Hash,
            hit.HasValue ? hit.Value.ToString() : "-",
            infoState, artifactCount, artifactBytes, sample));
    }

    static string Caller()
    {
        var st = new StackTrace(2, false);
        string fallback = "";
        var frames = st.GetFrames();
        if (frames == null) return "?";
        foreach (var f in frames)
        {
            var m = f.GetMethod();
            if (m == null) continue;
            var t = m.DeclaringType;
            if (t == null || t.Namespace == null) continue;
            if (t.Namespace.StartsWith("UnityEditor.Build.Pipeline.Tasks"))
                return t.Name + "." + m.Name;
            if (fallback == "" && t.Namespace.StartsWith("UnityEditor.Build.Pipeline"))
                fallback = t.Name + "." + m.Name;
        }
        return fallback == "" ? "?" : fallback;
    }
}

public static class SbpLabEx20_CacheProbe
{
    static List<string> s_Log;
    static string s_Label;
    static string s_GlobalHash;
    static string s_CacheRoot;

    [MenuItem("SBP Lab/Examples/20 - 构建缓存采样", priority = 120)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        s_CacheRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Library/BuildCache"));
        BuildCache.PurgeCache(false);

        var log = new List<string>();
        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, SbpLabCommon.OutputFolder("ex20-cache"))
        {
            BundleCompression = UnityEngine.BuildCompression.LZ4,
            UseCache = true,
        };

        // 冷构建：缓存已被 Purge
        RunOne(parameters, "cold", log);

        // 热构建：复用同一个 parameters（ScriptInfo 已在第一次构建后设置，跳过脚本重编译）
        RunOne(parameters, "warm", log);

        Directory.CreateDirectory(SbpLabCommon.ArtifactsRoot);
        string reportPath = Path.Combine(SbpLabCommon.ArtifactsRoot, "ex20-cache-probe.txt");
        File.WriteAllText(reportPath, string.Join("\n", log.ToArray()));
        Debug.Log("[Ex20] 完成 -> " + reportPath);
    }

    static void RunOne(BundleBuildParameters parameters, string label, List<string> log)
    {
        s_Log = log;
        s_Label = label;

        var tasks = new List<IBuildTask>();
        tasks.Add(new CacheHookTask());
        tasks.AddRange(DefaultBuildTasks.Create(DefaultBuildTasks.Preset.AssetBundleCompatible));

        var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());
        var sw = Stopwatch.StartNew();
        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults results, tasks);
        sw.Stop();

        log.Add("");
        log.Add("=== build " + label + " ===");
        log.Add("code=" + code + " totalMs=" + sw.ElapsedMilliseconds);
        log.Add("globalHash=" + s_GlobalHash);
        log.Add("cacheDir=" + CacheStats());
    }

    internal static void Install(IBuildContext context, IBuildCache actual)
    {
        s_GlobalHash = ReflectGlobalHash(actual);
        context.SetContextObject(typeof(IBuildCache), new LoggingBuildCache(actual, s_Log, s_Label));
        s_Log.Add("--- " + s_Label + ": wrapped " + actual.GetType().Name + ", globalHash=" + s_GlobalHash);
    }

    static string ReflectGlobalHash(IBuildCache cache)
    {
        var field = cache.GetType().GetField("m_GlobalHash", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) return "?";
        var value = field.GetValue(cache);
        return value == null ? "?" : value.ToString();
    }

    static string CacheStats()
    {
        if (!Directory.Exists(s_CacheRoot))
            return "root=" + s_CacheRoot + " missing";

        var files = Directory.GetFiles(s_CacheRoot, "*", SearchOption.AllDirectories);
        var dirs = Directory.GetDirectories(s_CacheRoot, "*", SearchOption.AllDirectories);
        long bytes = 0;
        int infos = 0;
        foreach (var file in files)
        {
            bytes += new FileInfo(file).Length;
            if (file.EndsWith(".info", StringComparison.OrdinalIgnoreCase)) infos++;
        }
        return string.Format("root={0} dirs={1} files={2} infos={3} bytes={4}",
            s_CacheRoot, dirs.Length, files.Length, infos, bytes);
    }
}
#endif
