using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace PclHook
{
    /// <summary>Harmony 调用的补丁方法，以及原生跳转的替换方法。</summary>
    internal static class PatchMethods
    {
        // 2) 重新实现 LoaderInit：
        //    ControlInit(); PageLoaderInit(LoadMinecraft, PanLoad, PanAllBack, Nothing,
        //                                   DlClientListLoader, AddressOf LoadMinecraft_OnFinish)
        public static bool LoaderInitPrefix(object __instance)
        {
            try
            {
                Type page = __instance.GetType();

                MethodInfo controlInit = PatchContext.ControlInit;
                if (controlInit == null) controlInit = Refl.Method(page, "ControlInit", 0);
                controlInit.Invoke(__instance, null);

                object load = GetValue(PatchContext.GetLoadMinecraft, page, "LoadMinecraft", __instance);
                object panLoad = GetValue(PatchContext.GetPanLoad, page, "PanLoad", __instance);
                object panAllBack = GetValue(PatchContext.GetPanAllBack, page, "PanAllBack", __instance);

                FieldInfo dlField = PatchContext.DlLoader;
                if (dlField == null)
                    dlField = Refl.Pcl("PCL.ModDownload").GetField("DlClientListLoader", Refl.All);
                if (dlField == null) throw new Exception("DlClientListLoader not found");
                object loader = dlField.GetValue(null);

                // 原方法传给 PageLoaderInit 的是编译器生成的 _Lambda$__R3-4(LoaderTask)，
                // 不是无参的 LoadMinecraft_OnFinish，所以按签名查找
                Type loaderType = dlField.FieldType;
                MethodInfo onFinish = PatchContext.OnFinish;
                if (onFinish == null)
                {
                    foreach (MethodInfo candidate in page.GetMethods(Refl.All))
                    {
                        if (candidate.IsStatic || candidate.ReturnType != typeof(void)) continue;
                        ParameterInfo[] ps = candidate.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType == loaderType) { onFinish = candidate; break; }
                    }
                }
                if (onFinish == null) throw new Exception("on-finish delegate target not found");

                Type resultType = loaderType.GetGenericArguments()[1];
                Type actionType = typeof(Action<>).MakeGenericType(loaderType);
                Delegate callback = Delegate.CreateDelegate(actionType, __instance, onFinish);

                MethodInfo pageLoaderInit = PatchContext.PageLoaderInit;
                if (pageLoaderInit == null) pageLoaderInit = FindPageLoaderInit(page);
                pageLoaderInit = pageLoaderInit.MakeGenericMethod(typeof(string), resultType);
                pageLoaderInit.Invoke(__instance, new object[]
                {
                    load, panLoad, panAllBack, null, loader, callback, null, true
                });

                DomainManager.Log("LoaderInit reimplemented");
                return false;
            }
            catch (Exception ex)
            {
                DomainManager.Log("LoaderInitPrefix failed, fallback to original: " + ex);
                return true;
            }
        }

        private static object GetValue(MethodInfo getter, Type page, string fallbackName, object instance)
        {
            if (getter != null) return getter.Invoke(instance, null);
            return page.GetProperty(fallbackName, Refl.All).GetValue(instance, null);
        }

        private static MethodInfo FindPageLoaderInit(Type type)
        {
            while (type != null)
            {
                foreach (MethodInfo method in type.GetMethods(Refl.All))
                {
                    if (method.Name != "PageLoaderInit") continue;
                    if (!method.IsGenericMethodDefinition || method.GetGenericArguments().Length != 2) continue;

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 8) continue;
                    if (!parameters[4].ParameterType.Name.Contains("LoaderTask")) continue;

                    return method;
                }
                type = type.BaseType;
            }
            throw new Exception("PageLoaderInit not found");
        }

        // 3) 把 CardMinecraft 重新设为可见
        public static void VisibilityPostfix(object __instance)
        {
            try
            {
                object card = null;
                Type page = __instance.GetType();

                PropertyInfo property = page.GetProperty("CardMinecraft", Refl.All);
                if (property != null)
                {
                    card = property.GetValue(__instance, null);
                }
                else
                {
                    FieldInfo field = page.GetField("CardMinecraft", Refl.All);
                    if (field != null) card = field.GetValue(__instance);
                }
                if (card == null) return;

                PropertyInfo visibility = card.GetType().GetProperty("Visibility", Refl.All);
                visibility.SetValue(card, Enum.Parse(visibility.PropertyType, "Visible"), null);
            }
            catch (Exception ex)
            {
                DomainManager.Log("VisibilityPostfix: " + ex.Message);
            }
        }

        // 4) 原生跳转的替换方法，签名与原方法一致（返回类型按引用兼容）
        public static object ModpackInstallDetour(string file, string instanceName, string logo)
        {
            try
            {
                return ModpackInstaller.Install(file, instanceName, logo);
            }
            catch (Exception ex)
            {
                DomainManager.Log("ModpackInstall detour error: " + ex);
                throw;
            }
        }

        // 5) 弹文件选择框，然后在线程池里安装
        public static bool ModpackDialogPrefix()
        {
            try
            {
                string file;
                using (var dialog = new OpenFileDialog())
                {
                    dialog.Title = "选择整合包压缩文件";
                    dialog.Filter = "整合包文件|*.zip;*.mrpack;*.rar|所有文件|*.*";
                    dialog.CheckFileExists = true;
                    if (dialog.ShowDialog() != DialogResult.OK) return false;
                    file = dialog.FileName;
                }

                string picked = file;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        ModpackInstaller.Install(picked, null, null);
                    }
                    catch (Exception ex)
                    {
                        DomainManager.Log("manual install failed: " + ex);
                        Refl.Hint("手动安装整合包失败：" + ex.Message, true);
                    }
                });
                return false;
            }
            catch (Exception ex)
            {
                DomainManager.Log("ModpackDialogPrefix failed, fallback to original: " + ex);
                return true;
            }
        }
    }
}
