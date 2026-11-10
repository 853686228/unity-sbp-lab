// ---------------------------------------------------------------------------
// Example 22 —— CachedInfo 实例 dump
//
// 目的：
//   把 CalculateAssetDependencyData 的 CachedInfo.Data 完整打印出来，包括源码里这三个强转：
//     var assetInfos        = info.Data[0] as AssetLoadInfo;
//     var objectTypes       = info.Data[4] as List<ObjectTypes>;
//     var objectDependencyInfo = info.Data[5] as List<ObjectDependencyInfo>;
//
//   同时打印：
//     - CachedInfo.Asset / Dependencies 的 CacheEntry（Guid/Version/Hash/Type）
//     - Data[1] BuildUsageTagSet 的 hash
//     - Data[2]/[3] 是否为 null
//
// 做法：
//   1. 先 Purge，冷构建一次把 .info 写出来；
//   2. 第二次构建时，用 DumpingBuildCache 包住 IBuildCache，
//      在 LoadCachedData 返回后 dump 每个 CachedInfo；
//   3. 报告写到 Artifacts/SBP/ex22-cachedinfo-dump.txt。
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEngine;

public class CacheDumpHookTask : IBuildTask
{
    public int Version { get { return 1; } }

    [InjectContext(ContextUsage.In)] IBuildContext m_Context;
    [InjectContext(ContextUsage.In)] IBuildCache m_Cache;

    public ReturnCode Run()
    {
        SbpLabEx22_CachedInfoDump.Install(m_Context, m_Cache);
        return ReturnCode.Success;
    }
}

/// <summary>只记录 LoadCachedData 返回的 CachedInfo，不改变行为。</summary>
public class DumpingBuildCache : IBuildCache
{
    readonly IBuildCache m_Inner;
    readonly List<string> m_Log;

    public DumpingBuildCache(IBuildCache inner, List<string> log)
    {
        m_Inner = inner;
        m_Log = log;
    }

    public CacheEntry GetCacheEntry(GUID asset, int version = 1) { return m_Inner.GetCacheEntry(asset, version); }
    public CacheEntry GetCacheEntry(string path, int version = 1) { return m_Inner.GetCacheEntry(path, version); }
    public CacheEntry GetCacheEntry(ObjectIdentifier objectID, int version = 1) { return m_Inner.GetCacheEntry(objectID, version); }
    public CacheEntry GetCacheEntry(Type type, int version = 1) { return m_Inner.GetCacheEntry(type, version); }
    public bool HasAssetOrDependencyChanged(CachedInfo info) { return m_Inner.HasAssetOrDependencyChanged(info); }
    public string GetCachedInfoFile(CacheEntry entry) { return m_Inner.GetCachedInfoFile(entry); }
    public string GetCachedArtifactsDirectory(CacheEntry entry) { return m_Inner.GetCachedArtifactsDirectory(entry); }
    public void SaveCachedData(IList<CachedInfo> infos) { m_Inner.SaveCachedData(infos); }

    public void LoadCachedData(IList<CacheEntry> entries, out IList<CachedInfo> cachedInfos)
    {
        m_Inner.LoadCachedData(entries, out cachedInfos);

        if (cachedInfos == null)
            return;

        for (int i = 0; i < cachedInfos.Count; i++)
        {
            var info = cachedInfos[i];
            if (info == null || info.Data == null || info.Data.Length != 6 || !(info.Data[0] is AssetLoadInfo))
                continue;

            SbpLabEx22_CachedInfoDump.Dump(info, i, m_Log, m_Inner);
        }
    }
}

public static class SbpLabEx22_CachedInfoDump
{
    static List<string> s_Log;

    [MenuItem("SBP Lab/Examples/22 - CachedInfo 实例 dump", priority = 122)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();
        BuildCache.PurgeCache(false);

        var log = new List<string>();
        s_Log = log;
        log.Add("CachedInfo dump — CalculateAssetDependencyData");
        log.Add("");

        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, SbpLabCommon.OutputFolder("ex22-cachedinfo"))
        {
            BundleCompression = UnityEngine.BuildCompression.LZ4,
            UseCache = true,
        };

        // 第一次：冷构建，把 .info 写到磁盘
        Build(parameters, "cold", log, false);
        // 第二次：命中缓存，dump Data[0] / Data[4] / Data[5]
        Build(parameters, "dump", log, true);

        Directory.CreateDirectory(SbpLabCommon.ArtifactsRoot);
        string report = Path.Combine(SbpLabCommon.ArtifactsRoot, "ex22-cachedinfo-dump.txt");
        File.WriteAllText(report, string.Join("\n", log.ToArray()));
        Debug.Log("[Ex22] 完成 -> " + report);
    }

    static void Build(BundleBuildParameters parameters, string label, List<string> log, bool dump)
    {
        var tasks = new List<IBuildTask>();
        if (dump)
            tasks.Add(new CacheDumpHookTask());
        tasks.AddRange(DefaultBuildTasks.Create(DefaultBuildTasks.Preset.AssetBundleCompatible));

        var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());
        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults _, tasks);
        log.Add("=== build " + label + " code=" + code + " ===");
        log.Add("");
    }

    internal static void Install(IBuildContext context, IBuildCache actual)
    {
        context.SetContextObject(typeof(IBuildCache), new DumpingBuildCache(actual, s_Log));
    }

    internal static void Dump(CachedInfo info, int index, List<string> log, IBuildCache cache)
    {
        var assetInfo = (AssetLoadInfo)info.Data[0];
        string assetPath = AssetDatabase.GUIDToAssetPath(assetInfo.asset.ToString());

        log.Add("############################################################");
        log.Add("CachedInfo[" + index + "] " + assetPath);
        log.Add("  Asset CacheEntry:");
        log.Add("    Type=" + info.Asset.Type + " Guid=" + info.Asset.Guid + " Version=" + info.Asset.Version + " Hash=" + info.Asset.Hash);
        log.Add("    infoFile=" + cache.GetCachedInfoFile(info.Asset));
        log.Add("  Data.Length=" + info.Data.Length);
        log.Add("");
        log.Add("  Data[0] AssetLoadInfo:");
        log.Add("    asset=" + assetInfo.asset);
        log.Add("    address=" + (assetInfo.address ?? "null"));
        log.Add("    includedObjects=" + (assetInfo.includedObjects == null ? 0 : assetInfo.includedObjects.Count));
        DumpObjects(log, "      ", assetInfo.includedObjects);
        log.Add("    referencedObjects=" + (assetInfo.referencedObjects == null ? 0 : assetInfo.referencedObjects.Count));
        DumpObjects(log, "      ", assetInfo.referencedObjects);

        log.Add("");
        log.Add("  Data[1] BuildUsageTagSet:");
        var usage = info.Data[1];
        if (usage == null)
            log.Add("    null");
        else
        {
            var method = usage.GetType().GetMethod("GetHash128", Type.EmptyTypes);
            log.Add("    Type=" + usage.GetType().Name +
                    " Hash128=" + (method == null ? "?" : method.Invoke(usage, null).ToString()));
        }

        log.Add("");
        log.Add("  Data[2] SpriteImporterData: " + (info.Data[2] == null ? "null" : info.Data[2].GetType().Name));
        log.Add("  Data[3] ExtendedAssetData: " + (info.Data[3] == null ? "null" : info.Data[3].GetType().Name));

        log.Add("");
        log.Add("  Data[4] List<ObjectTypes>:");
        DumpObjectTypes(log, info.Data[4]);

        log.Add("");
        log.Add("  Data[5] List<ObjectDependencyInfo>:");
        DumpObjectDependencies(log, info.Data[5]);

        log.Add("");
        log.Add("  Dependencies = CacheEntry[" + (info.Dependencies == null ? 0 : info.Dependencies.Length) + "]:");
        if (info.Dependencies != null)
        {
            for (int i = 0; i < info.Dependencies.Length; i++)
                log.Add("    [" + i + "] " + FormatEntry(info.Dependencies[i]));
        }
        log.Add("");
    }

    static void DumpObjects(List<string> log, string indent, IEnumerable<ObjectIdentifier> objects)
    {
        if (objects == null) return;
        int i = 0;
        foreach (var obj in objects)
        {
            log.Add(indent + "[" + i++ + "] " + FormatObjectIdentifier(obj));
        }
    }

    static void DumpObjectTypes(List<string> log, object data)
    {
        var list = data as IEnumerable;
        if (list == null) { log.Add("    null"); return; }
        int i = 0;
        foreach (var item in list)
        {
            var t = item.GetType();
            var objectId = (ObjectIdentifier)t.GetField("ObjectID").GetValue(item);
            var types = (Type[])t.GetField("Types").GetValue(item);
            log.Add("    [" + i++ + "] " + FormatObjectIdentifier(objectId) +
                    "  Types=[" + string.Join(", ", types.Select(x => x.Name).ToArray()) + "]");
        }
    }

    static void DumpObjectDependencies(List<string> log, object data)
    {
        var list = data as IEnumerable;
        if (list == null) { log.Add("    null"); return; }
        int i = 0;
        foreach (var item in list)
        {
            var t = item.GetType();
            var objectId = (ObjectIdentifier)t.GetField("Object").GetValue(item);
            var deps = (IEnumerable)t.GetField("Dependencies").GetValue(item);
            log.Add("    [" + i++ + "] Object=" + FormatObjectIdentifier(objectId));
            int j = 0;
            if (deps != null)
            {
                foreach (var dep in deps)
                    log.Add("         dep[" + j++ + "]=" + FormatObjectIdentifier((ObjectIdentifier)dep));
            }
        }
    }

    static string FormatObjectIdentifier(ObjectIdentifier o)
    {
        return string.Format("guid={0} fileID={1} type={2} path='{3}'",
            o.guid, o.localIdentifierInFile, o.fileType, o.filePath);
    }

    static string FormatEntry(CacheEntry e)
    {
        string extra = "";
        if (e.Type == CacheEntry.EntryType.Asset)
        {
            var path = AssetDatabase.GUIDToAssetPath(e.Guid.ToString());
            extra = " assetPath='" + path + "'";
        }
        else if (e.Type == CacheEntry.EntryType.ScriptType)
            extra = " scriptType='" + e.ScriptType + "'";
        else if (e.Type == CacheEntry.EntryType.File)
            extra = " file='" + e.File + "'";

        return string.Format("type={0} guid={1} version={2} hash={3}{4}",
            e.Type, e.Guid, e.Version, e.Hash, extra);
    }
}
#endif
