// ---------------------------------------------------------------------------
// SBP Lab 批量入口（供命令行 / CI 使用）
//
// 用法：
//   Unity.exe -batchmode -quit -nographics -projectPath <工程> ^
//     -executeMethod SbpLabExamplesRunner.RunAllExamples -logFile -
//
// 说明：
//   1. 一次跑完 01~13 所有示例，任何一个抛异常都会让 Unity 以退出码 1 结束；
//   2. 也可以单独执行某个示例，例如：
//        -executeMethod SbpLabEx03_CompressionMatrix.Run
//   3. 所有示例的产物都在 Artifacts/SBP/ 下，跑完可以整体对比。
//
// 注意：
//   Ex08 会清空 Library/BuildCache（本地构建缓存），这是实验的一部分。
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using UnityEditor;
using UnityEngine;

public static class SbpLabExamplesRunner
{
    /// <summary>
    /// 把 Editor 活动平台切回 Android。
    /// 用途：Ex11 为了验证加载会按 Editor 平台构建，SBP 的 SwitchToBuildPlatform
    /// 会真的切换活动平台且不会切回；如果中途异常导致没恢复，可以用这个方法修复。
    /// </summary>
    public static void RestoreAndroidTarget()
    {
        SbpLabCommon.Run(() =>
        {
            Debug.Log("[SBP Lab] 当前活动平台 = " + EditorUserBuildSettings.activeBuildTarget);
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            Debug.Log("[SBP Lab] 恢复后活动平台 = " + EditorUserBuildSettings.activeBuildTarget);
        }, "RestoreAndroidTarget");
    }
    /// <summary>依次运行所有教学示例。</summary>
    public static void RunAllExamples()
    {
        SbpLabCommon.Run(() =>
        {
            SbpLabCommon.EnsureLabAssets();

            Debug.Log("[SBP Lab] 当前活动平台 = " + EditorUserBuildSettings.activeBuildTarget);

            Debug.Log("========== [Ex01] Compatibility 最小平替 ==========");
            SbpLabEx01_CompatibilityMinimal.Run();

            Debug.Log("========== [Ex02] ContentPipeline 显式布局 ==========");
            SbpLabEx02_ExplicitLayout.Run();

            Debug.Log("========== [Ex03] 压缩方式对比矩阵 ==========");
            SbpLabEx03_CompressionMatrix.Run();

            Debug.Log("========== [Ex04] 每个 bundle 独立压缩 ==========");
            SbpLabEx04_PerBundleCompression.Run();

            Debug.Log("========== [Ex05] 自定义 BuildTask ==========");
            SbpLabEx05_CustomBuildTask.Run();

            Debug.Log("========== [Ex06] 任务链插入 + 自定义上下文 ==========");
            SbpLabEx06_CustomTaskChain.Run();

            Debug.Log("========== [Ex07] 后处理：交付目录与清单 ==========");
            SbpLabEx07_DeliveryPostProcess.Run();

            Debug.Log("========== [Ex08] 增量缓存实验 ==========");
            SbpLabEx08_CacheExperiment.Run();

            Debug.Log("========== [Ex09] 自定义 BuildLogger（性能剖析） ==========");
            SbpLabEx09_CustomLogger.Run();

            Debug.Log("========== [Ex10] Content File 管线 ==========");
            SbpLabEx10_ContentFilePipeline.Run();

            Debug.Log("========== [Ex11] 加密交付 + 运行时加载验证 ==========");
            SbpLabEx11_EncryptedDelivery.Run();

            Debug.Log("========== [Ex12] 自定义打包策略（合并 bundle） ==========");
            SbpLabEx12_PackingStrategy.Run();

            Debug.Log("========== [Ex13] 多配置构建矩阵 ==========");
            SbpLabEx13_BuildMatrix.Run();
        }, "RunAllExamples");
    }
}
#endif
