using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace PclHook
{
    /// <summary>
    /// 安装运行期补丁。
    /// 目标由 HookPlan 按语义锚点定位（读 IL），再用 MetadataToken 映射到运行中的方法；
    /// 任何一处失败都只记录日志，不影响 PCL 运行。
    /// </summary>
    internal static class Patches
    {
        internal static void Apply()
        {
            Harmony harmony = new Harmony("pcl-runtime-patch");
            string dir = AppDomain.CurrentDomain.BaseDirectory;

            Module module;
            HookPlan plan;
            try
            {
                Assembly entry = Assembly.GetEntryAssembly();
                if (entry == null) throw new Exception("entry assembly not found");

                Version version = AssemblyName.GetAssemblyName(entry.Location).Version;
                DomainManager.Log("PclHook " + typeof(Patches).Assembly.GetName().Version +
                                  " / PCL " + version);

                if (version.Major != 2 || version.Minor != 99)
                {
                    DomainManager.Log("unsupported PCL version, patches disabled");
                    return;
                }

                module = entry.ManifestModule;
                plan = HookPlan.Build(entry.Location);
                DomainManager.Log(plan.Report);
            }
            catch (Exception ex)
            {
                DomainManager.Log("semantic plan failed: " + ex);
                return;
            }

            if (plan.HasLoaderInit && !File.Exists(Path.Combine(dir, "skip2.flag")))
                Try("patch2-loader", delegate { PatchLoaderInit(harmony, module, plan); });

            if (plan.HasVisibility && !File.Exists(Path.Combine(dir, "skip3.flag")))
                Try("patch3-visibility", delegate { PatchVisibility(harmony, module, plan); });

            if (plan.HasModpack3 && !File.Exists(Path.Combine(dir, "skip4.flag")))
                Try("patch4-modpack-install", delegate { PatchModpackInstall(module, plan); });

            if (plan.HasModpack0 && !File.Exists(Path.Combine(dir, "skip5.flag")))
                Try("patch5-modpack-dialog", delegate { PatchModpackDialog(harmony, module, plan); });
        }

        private static void Try(string name, Action patch)
        {
            try
            {
                patch();
                DomainManager.Log(name + " OK");
            }
            catch (Exception ex)
            {
                DomainManager.Log(name + " FAILED: " + ex);
            }
        }

        // 2) PageDownloadInstall.LoaderInit：跳过假 Loader，绑定真正的 DlClientListLoader
        private static void PatchLoaderInit(Harmony harmony, Module module, HookPlan plan)
        {
            MethodInfo target = ResolveMethod(module, plan.LoaderInitToken);
            PatchContext.DlLoader = ResolveField(module, plan.DlLoaderToken);

            PatchContext.ControlInit = ResolveOptional(module, plan.ControlInitToken);
            PatchContext.PageLoaderInit = ResolveOptional(module, plan.PageLoaderInitToken);
            PatchContext.GetLoadMinecraft = ResolveOptional(module, plan.GetLoadMinecraftToken);
            PatchContext.GetPanLoad = ResolveOptional(module, plan.GetPanLoadToken);
            PatchContext.GetPanAllBack = ResolveOptional(module, plan.GetPanAllBackToken);
            PatchContext.OnFinish = ResolveOptional(module, plan.OnFinishToken);

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(PatchMethods).GetMethod("LoaderInitPrefix")));
            DomainManager.Log("  target: " + target);
        }

        // 3) PageDownloadModify.LoadInstanceFinished：CardMinecraft 设为 Visible
        private static void PatchVisibility(Harmony harmony, Module module, HookPlan plan)
        {
            MethodInfo target = ResolveMethod(module, plan.VisibilityToken);
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(PatchMethods).GetMethod("VisibilityPostfix")));
            DomainManager.Log("  target: " + target);
        }

        // 4) ModModpack.ModpackInstall(File, InstanceName, Logo)：重建整合包分发
        //    Harmony 复制这个方法体时会生成无效 IL（finally 延伸到方法末尾），改用原生入口跳转
        private static void PatchModpackInstall(Module module, HookPlan plan)
        {
            MethodInfo target = ResolveMethod(module, plan.Modpack3Token);
            MethodInfo replacement = typeof(PatchMethods).GetMethod("ModpackInstallDetour");

            PatchContext.Installers = new Dictionary<int, MethodInfo>();
            foreach (KeyValuePair<int, int> pair in plan.InstallerTokens)
                PatchContext.Installers[pair.Key] = ResolveMethod(module, pair.Value);

            PatchContext.McFolder = ResolveOptional(module, plan.McFolderToken);

            NativeDetour.Install(target, replacement);
            DomainManager.Log("  target: " + target);
            DomainManager.Log("  installers resolved: " + PatchContext.Installers.Count);
        }

        // 5) ModModpack.ModpackInstall()：自己弹文件选择框
        private static void PatchModpackDialog(Harmony harmony, Module module, HookPlan plan)
        {
            MethodInfo target = ResolveMethod(module, plan.Modpack0Token);
            PatchContext.Hint = ResolveOptional(module, plan.HintToken);

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(PatchMethods).GetMethod("ModpackDialogPrefix")));
            DomainManager.Log("  target: " + target);
        }

        private static MethodInfo ResolveMethod(Module module, int token)
        {
            MethodBase method = module.ResolveMethod(token);
            if (method == null) throw new Exception("resolve method failed: 0x" + token.ToString("X8"));
            return (MethodInfo)method;
        }

        private static FieldInfo ResolveField(Module module, int token)
        {
            FieldInfo field = module.ResolveField(token);
            if (field == null) throw new Exception("resolve field failed: 0x" + token.ToString("X8"));
            return field;
        }

        private static MethodInfo ResolveOptional(Module module, int token)
        {
            if (token == 0) return null;
            return ResolveMethod(module, token);
        }
    }

    /// <summary>补丁执行期共享的反射对象。</summary>
    internal static class PatchContext
    {
        internal static FieldInfo DlLoader;
        internal static MethodInfo ControlInit;
        internal static MethodInfo PageLoaderInit;
        internal static MethodInfo GetLoadMinecraft;
        internal static MethodInfo GetPanLoad;
        internal static MethodInfo GetPanAllBack;
        internal static MethodInfo OnFinish;
        internal static MethodInfo McFolder;
        internal static MethodInfo Hint;
        internal static Dictionary<int, MethodInfo> Installers;
    }
}
