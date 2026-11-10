// ---------------------------------------------------------------------------
// Example 11 —— 加密交付 + 运行时解密加载验证（端到端闭环）
//
// 难度：★★★★★
//
// 教学目标：
//   1. 学会在构建后处理阶段对产物做加密（本示例用 XOR 演示，生产用 AES）；
//   2. 理解“加密会改变文件内容，但不会改变 SBP 记录的 hash”这一事实及后果：
//        - 不能用 hash 做文件完整性校验（要改成校验加密后的文件）；
//        - 增量构建的缓存语义不受影响（缓存发生在打包前），但交付清单要自己记录加密后大小；
//   3. 亲手验证“加密 -> 解密 -> 按依赖顺序加载 -> 实例化 prefab -> 检查材质”全链路。
//
// 关键 API / 知识点：
//   - 自定义 IBuildTask 读 IBundleBuildResults，把产物写进交付目录；
//   - AssetBundle.LoadFromFile：**必须按依赖顺序**先加载 shared，再加载 props_a；
//   - 平台限制：Bundle 只能被“构建目标平台”的运行时加载。
//     所以本示例显式按 **Editor 所在平台**（Windows64）构建，否则在 Editor 里加载 Android bundle 会报平台不匹配。
//
// 产物：
//   Artifacts/SBP/ex11-encrypted-build-<平台>/   SBP 原始产物
//   Artifacts/SBP/ex11-encrypted-delivery/       *.enc + manifest.json
//
// 预期现象：
//   [Ex11] 加密完成：3 个文件
//   [Ex11] 加载顺序：shared -> props_a -> props_b
//   [Ex11] 实例化 CubeA 成功，材质颜色=(0.20, 0.70, 1.00)
//   [Ex11] 端到端验证通过
//
// 思考题：
//   - 如果先加载 props_a 再加载 shared，会怎样？为什么？
//   - 生产环境里，密钥放哪里？如何避免“客户端可解密 = 没有加密”？
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
using Object = UnityEngine.Object;

[Serializable]
public class EncryptedEntry
{
    public string name;
    public string file;
    public string hash;
    public long size;
    public string[] deps;
}

[Serializable]
public class EncryptedManifest
{
    public string builtAt;
    public string platform;
    public List<EncryptedEntry> bundles = new List<EncryptedEntry>();
}

/// <summary>构建后处理任务：把 SBP 产物 XOR 加密后写入交付目录，并生成清单。</summary>
public class EncryptDeliveryTask : IBuildTask
{
    public int Version { get { return 1; } }

    [InjectContext(ContextUsage.In)]
    IBundleBuildResults m_Results;

    readonly string m_DeliveryDir;

    public EncryptDeliveryTask(string deliveryDir)
    {
        m_DeliveryDir = deliveryDir;
    }

    public ReturnCode Run()
    {
        Directory.CreateDirectory(m_DeliveryDir);
        var manifest = new EncryptedManifest
        {
            builtAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            platform = EditorUserBuildSettings.activeBuildTarget.ToString(),
        };

        foreach (var kv in m_Results.BundleInfos)
        {
            var source = SbpLabCommon.ResolveBundleFile(kv.Value);
            if (!File.Exists(source))
            {
                Debug.LogWarning("[Ex11] 缺少产物：" + source);
                continue;
            }

            var bytes = File.ReadAllBytes(source);
            XorInPlace(bytes); // 演示用；生产请用 AES-GCM，并做完整性校验

            var fileName = kv.Key + ".enc";
            var dest = Path.Combine(m_DeliveryDir, fileName);
            File.WriteAllBytes(dest, bytes);

            manifest.bundles.Add(new EncryptedEntry
            {
                name = kv.Key,
                file = fileName,
                hash = kv.Value.Hash.ToString(),
                size = bytes.Length,
                deps = kv.Value.Dependencies ?? new string[0],
            });
        }

        var manifestPath = Path.Combine(m_DeliveryDir, "manifest.json");
        File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));
        Debug.Log($"[Ex11] 加密完成：{manifest.bundles.Count} 个文件 -> {m_DeliveryDir}");
        return ReturnCode.Success;
    }

    public static void XorInPlace(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= 0x5A;
    }
}

public static class SbpLabEx11_EncryptedDelivery
{
    // 用 Editor 所在平台构建，保证能在 Editor 里直接加载验证。
    static BuildTarget EditorPlatformTarget
    {
        get
        {
#if UNITY_EDITOR_WIN
            return BuildTarget.StandaloneWindows64;
#elif UNITY_EDITOR_OSX
            return BuildTarget.StandaloneOSX;
#else
            return BuildTarget.StandaloneLinux64;
#endif
        }
    }

    [MenuItem("SBP Lab/Examples/11 - 加密交付 + 运行时加载验证", priority = 111)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        var target = EditorPlatformTarget;
        var group = BuildPipeline.GetBuildTargetGroup(target);
        if (!BuildPipeline.IsBuildTargetSupported(group, target))
        {
            Debug.LogWarning($"[Ex11] 当前 Editor 未安装 {target} 模块，无法在 Editor 内做加载验证，跳过。");
            return;
        }

        // SBP 的 SwitchToBuildPlatform 任务会真的切换 Editor 的活动平台，且构建后不会自动切回。
        // 这里记录原平台，结束后恢复，避免污染后续 Android 出包流程。
        var previousTarget = EditorUserBuildSettings.activeBuildTarget;
        var previousGroup = BuildPipeline.GetBuildTargetGroup(previousTarget);

        var outPath = System.IO.Path.Combine(SbpLabCommon.ArtifactsRoot, "ex11-encrypted-build-" + target);
        System.IO.Directory.CreateDirectory(outPath);
        var deliveryDir = System.IO.Path.Combine(SbpLabCommon.ArtifactsRoot, "ex11-encrypted-delivery");
        if (System.IO.Directory.Exists(deliveryDir)) System.IO.Directory.Delete(deliveryDir, true);
        System.IO.Directory.CreateDirectory(deliveryDir);

        try
        {
            var parameters = new BundleBuildParameters(target, group, outPath)
            {
                BundleCompression = UnityEngine.BuildCompression.LZ4,
                UseCache = true,
            };
            var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());
            var tasks = DefaultBuildTasks.Create(DefaultBuildTasks.Preset.AssetBundleCompatible);
            tasks.Add(new EncryptDeliveryTask(deliveryDir));

            var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults results, tasks);
            if (code != ReturnCode.Success)
            {
                Debug.LogError("[Ex11] 构建失败，ReturnCode=" + code);
                return;
            }

            
            VerifyByLoading(deliveryDir);
        }
        
        finally
        {
            // 恢复 Editor 活动平台（SBP 的 SwitchToBuildPlatform 不会帮你切回来）
            if (EditorUserBuildSettings.activeBuildTarget != previousTarget)
            {
                Debug.Log($"[Ex11] 恢复 Editor 活动平台：{previousTarget}（构建期间被切到 {target}）");
                EditorUserBuildSettings.SwitchActiveBuildTarget(previousGroup, previousTarget);
            }
        }
    }

    /// <summary>解密 -> 按依赖顺序加载 -> 实例化 -> 检查材质 -> 清理。</summary>
    static void VerifyByLoading(string deliveryDir)
    {
        var manifestPath = Path.Combine(deliveryDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Debug.LogError("[Ex11] 找不到 manifest.json");
            return;
        }

        var manifest = JsonUtility.FromJson<EncryptedManifest>(File.ReadAllText(manifestPath));
        var tempDir = Path.Combine("Temp", "Ex11Decrypted");
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        Directory.CreateDirectory(tempDir);

        var loaded = new Dictionary<string, AssetBundle>();
        try
        {
            // 1) 解密到临时目录
            foreach (var entry in manifest.bundles)
            {
                var bytes = File.ReadAllBytes(Path.Combine(deliveryDir, entry.file));
                EncryptDeliveryTask.XorInPlace(bytes);
                File.WriteAllBytes(Path.Combine(tempDir, entry.name), bytes);
            }

            // 2) 按依赖顺序加载（拓扑：依赖先加载）
            foreach (var entry in SortByDependencies(manifest))
            {
                var path = Path.GetFullPath(Path.Combine(tempDir, entry.name));
                var bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null)
                {
                    Debug.LogError("[Ex11] 加载失败：" + entry.name + "（可能是平台不匹配）");
                    return;
                }
                loaded[entry.name] = bundle;
                Debug.Log("[Ex11] 已加载 " + entry.name);
            }

            // 3) 从 props_a 里取出 CubeA 并实例化（依赖 shared 已在内存中，材质才能解析）
            var propsBundle = loaded["props_a"];
            var assetNames = propsBundle.GetAllAssetNames();
            Debug.Log("[Ex11] props_a 内的资源名：" + (assetNames == null ? "(空)" : string.Join(", ", assetNames)));

            var prefab = propsBundle.LoadAsset<GameObject>("CubeA");
            if (prefab == null && assetNames != null && assetNames.Length > 0)
            {
                // 兜底：某些构建方式下内部名是完整资源路径
                prefab = propsBundle.LoadAsset<GameObject>(assetNames[0]);
                Debug.Log("[Ex11] 用完整资源路径兜底加载：" + assetNames[0]);
            }
            if (prefab == null)
            {
                Debug.LogError("[Ex11] 在 props_a 里找不到 CubeA");
                return;
            }

            var instance = Object.Instantiate(prefab);
            var renderer = instance.GetComponent<Renderer>();
            var material = renderer != null ? renderer.sharedMaterial : null;
            var color = material != null ? material.color : Color.clear;

            Debug.Log($"[Ex11] 实例化 CubeA 成功，材质={(material != null ? material.name : "(null)")}，颜色=({color.r:0.00}, {color.g:0.00}, {color.b:0.00})");

            var ok = material != null
                     && Mathf.Abs(color.r - 0.2f) < 0.01f
                     && Mathf.Abs(color.g - 0.7f) < 0.01f
                     && Mathf.Abs(color.b - 1.0f) < 0.01f;
            Debug.Log(ok ? "[Ex11] 端到端验证通过 ✅" : "[Ex11] 材质颜色不符合预期 ❌");

            Object.DestroyImmediate(instance);
        }
        finally
        {
            AssetBundle.UnloadAllAssetBundles(true);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    /// <summary>简单拓扑排序：依赖为空的先加载。</summary>
    static List<EncryptedEntry> SortByDependencies(EncryptedManifest manifest)
    {
        var remaining = new List<EncryptedEntry>(manifest.bundles);
        var ordered = new List<EncryptedEntry>();
        var loadedNames = new HashSet<string>();

        while (remaining.Count > 0)
        {
            var progressed = false;
            for (var i = remaining.Count - 1; i >= 0; i--)
            {
                var entry = remaining[i];
                var ready = true;
                foreach (var dep in entry.deps)
                    if (!loadedNames.Contains(dep)) { ready = false; break; }

                if (!ready) continue;
                ordered.Add(entry);
                loadedNames.Add(entry.name);
                remaining.RemoveAt(i);
                progressed = true;
            }

            if (!progressed)
            {
                Debug.LogError("[Ex11] 依赖图存在环或缺失依赖，无法排序");
                ordered.AddRange(remaining);
                break;
            }
        }

        Debug.Log("[Ex11] 加载顺序：" + string.Join(" -> ", ordered.ConvertAll(e => e.name).ToArray()));
        return ordered;
    }
}
#endif
