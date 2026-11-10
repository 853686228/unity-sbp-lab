// ---------------------------------------------------------------------------
// Example 17 —— 确定性构建验证：同输入多次构建，产物是否一致？
//
// 难度：★★★★
//
// 目标：回答两个问题
//   1. SBP/ContentPipeline 的“确定性构建”到底确定到什么程度？
//      —— bundle 的内容 hash 是否稳定？文件字节是否逐字节一致？
//   2. 构建缓存（BuildCache）命中时，产物是否和冷构建完全一致？
//
// 实验设计（全部用轻量内容 ExplicitLayout，快速可重复）：
//   A. UseCache=false 构建一次           → 快照（文件 MD5 + BundleDetails.Hash）
//   B. UseCache=false 再构建一次（同输入） → 和 A 比：字节 / hash
//   C. Purge 后 UseCache=true 构建（冷缓存）→ 快照
//   D. UseCache=true 再构建（热缓存命中） → 和 C 比：字节 / hash
//   E. 换一个参数（ContiguousBundles=false）构建 → 和 A 比
//      说明“确定性”只对同一套参数成立，换布局参数产物必然不同。
//
// 结论预期（源码 + 实测）：
//   - 同机器、同 Unity 版本、同参数：SBP 产物通常是**字节级一致**的
//     （确定性 localID + 序列化对象排序 + 内容 hash 不含 header）；
//   - 内容 hash（BundleDetails.Hash）是稳定的逻辑标识，和压缩方式无关；
//   - 缓存命中产物 = 冷构建产物（缓存存的是 artifact 本身）；
//   - 但“确定性”不承诺跨 Unity 版本 / 跨平台 / 跨压缩设置 / 跨机器字节一致，
//     所以 CDN diff 应该以“逻辑内容 hash + 压缩配置”为准，文件 MD5 只做上传校验。
//
// 注意：本示例不依赖 Heavy 资源，可在裁剪掉 Heavy 的工程里运行。
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEngine;

[Serializable]
public class Ex17Report
{
    public string builtAt;
    public string platform;
    public bool sameAB;
    public bool sameCD;
    public bool sameBD;
    public bool sameAE;
    public List<string> bundleHashes = new List<string>();   // "bundle|hash"
}

public static class SbpLabEx17_Determinism
{
    class Snapshot
    {
        public string Label;
        public readonly Dictionary<string, string> FileMd5 = new Dictionary<string, string>();
        public readonly Dictionary<string, long> FileSize = new Dictionary<string, long>();
        public readonly Dictionary<string, string> BundleHash = new Dictionary<string, string>();
    }

    [MenuItem("SBP Lab/Examples/17 - 确定性构建验证", priority = 117)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        Debug.Log("[Ex17] ===== 目的：验证“同样的输入 / 缓存 / 配置，产物能不能被信任” =====");
        Debug.Log("[Ex17] A/B 验证同输入可复现；C/D 验证缓存不改变产物；E 是对照组（换配置应该不同）");

        Debug.Log("[Ex17] ===== A: UseCache=false（第一次，基准）=====");
        var a = Build("ex17-a", false, true);

        Debug.Log("[Ex17] ===== B: UseCache=false（第二次，同输入）=====");
        var b = Build("ex17-b", false, true);
        var sameAB = Compare("A vs B（同输入、无缓存）", a, b);

        Debug.Log("[Ex17] ===== C: 清空缓存后 UseCache=true（冷缓存）=====");
        BuildCache.PurgeCache(false);
        var c = Build("ex17-c", true, true);

        Debug.Log("[Ex17] ===== D: UseCache=true（热缓存命中）=====");
        var d = Build("ex17-d", true, true);
        var sameCD = Compare("C vs D（冷缓存 vs 热缓存）", c, d);
        var sameBD = Compare("B vs D（无缓存产物 vs 缓存产物）", b, d);

        Debug.Log("[Ex17] ===== E: ContiguousBundles=false（对照组：换构建配置）=====");
        var e = Build("ex17-e", false, false);
        var sameAE = Compare("A vs E（仅换 ContiguousBundles）", a, e);

        Debug.Log("[Ex17] ================= 结论 =================");
        Debug.Log($"[Ex17] 1) 同输入可复现（A vs B）      : {(sameAB ? "✅ 一致" : "❌ 不一致（构建不稳定）")}");
        Debug.Log($"[Ex17] 2) 缓存不改变产物（C/D、B/D）  : {(sameCD && sameBD ? "✅ 一致" : "❌ 不一致（缓存 key 有漏洞）")}");
        Debug.Log($"[Ex17] 3) 换配置产物不同（A vs E）     : {(sameAE ? "❌ 居然一致（反而异常）" : "✅ 不同（符合预期，配置属于构建指纹）")}");
        Debug.Log("[Ex17] 含义：同环境+同配置可复现；缓存可信；跨平台/跨配置/跨版本必须视为不同指纹");

        // 落一份 JSON，避免 Console 被清后找不到结果
        var report = new Ex17Report
        {
            builtAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            platform = SbpLabCommon.Target.ToString(),
            sameAB = sameAB,
            sameCD = sameCD,
            sameBD = sameBD,
            sameAE = sameAE,
        };
        if (a != null)
            foreach (var kv in a.BundleHash)
                report.bundleHashes.Add(kv.Key + "|" + kv.Value);
        var reportPath = Path.Combine(SbpLabCommon.ArtifactsRoot, "ex17-summary.json");
        Directory.CreateDirectory(SbpLabCommon.ArtifactsRoot);
        File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
        Debug.Log("[Ex17] 报告 -> " + reportPath);
    }

    static Snapshot Build(string tag, bool useCache, bool contiguousBundles)
    {
        var outPath = SbpLabCommon.OutputFolder(tag);
        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, outPath)
        {
            BundleCompression = UnityEngine.BuildCompression.LZ4,
            UseCache = useCache,
            ContiguousBundles = contiguousBundles,
        };
        var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());

        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults results);
        if (code != ReturnCode.Success)
        {
            Debug.LogError($"[Ex17] {tag} 构建失败，ReturnCode={code}");
            return null;
        }

        var snapshot = new Snapshot { Label = tag };
        foreach (var kv in results.BundleInfos)
        {
            var file = SbpLabCommon.ResolveBundleFile(kv.Value);
            if (!File.Exists(file)) continue;
            var name = Path.GetFileName(file);
            snapshot.FileMd5[name] = Md5(file);
            snapshot.FileSize[name] = new FileInfo(file).Length;
            snapshot.BundleHash[kv.Key] = kv.Value.Hash.ToString();
        }

        Debug.Log($"[Ex17] {tag}: {snapshot.FileMd5.Count} 个文件，bundle hash=" +
                  string.Join(", ", snapshot.BundleHash.Select(p => p.Key + ":" + p.Value).ToArray()));
        return snapshot;
    }

    /// <summary>对比两次构建；返回文件字节是否完全一致（内容 hash 是否一致会一起打印）。</summary>
    static bool Compare(string label, Snapshot left, Snapshot right)
    {
        if (left == null || right == null)
        {
            Debug.LogError($"[Ex17] {label}: 有一侧构建失败，跳过对比");
            return false;
        }

        var names = left.FileMd5.Keys.Union(right.FileMd5.Keys).OrderBy(x => x).ToList();
        var bytesSame = true;
        foreach (var name in names)
        {
            left.FileMd5.TryGetValue(name, out var lm);
            right.FileMd5.TryGetValue(name, out var rm);
            var same = lm != null && rm != null && lm == rm;
            if (!same) bytesSame = false;
            Debug.Log($"[Ex17]   {label} | {name,-40} md5 {lm ?? "(无)"} vs {rm ?? "(无)"} {(same ? "一致" : "不同")}");
        }

        var hashesSame = left.BundleHash.Count == right.BundleHash.Count &&
                         left.BundleHash.All(kv => right.BundleHash.TryGetValue(kv.Key, out var other) && other == kv.Value);
        Debug.Log($"[Ex17] {label}: 文件字节{(bytesSame ? "完全一致 ✅" : "存在差异 ❌")}；" +
                  $"内容 hash {(hashesSame ? "一致 ✅" : "不同 ❌")}");
        return bytesSame;
    }

    static string Md5(string path)
    {
        using (var md5 = MD5.Create())
        {
            var bytes = File.ReadAllBytes(path);
            return BitConverter.ToString(md5.ComputeHash(bytes)).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }
    }
}
#endif
