using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Windows.Forms;
using HarmonyLib;

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

        // ---------- 2.99.0.1 UI 限制 ----------

        // PageDownloadLeft..ctor：恢复“原版游戏”标签和默认选中项
        public static void DownloadLeftPostfix(object __instance)
        {
            try
            {
                if (IsDownloadHidden()) return;
                object item = GetMember(__instance, "ItemInstall");
                SetVisibility(item, true);
                SetVisibility(GetMember(__instance, "LabResource"), true);

                FieldInfo pageId = __instance.GetType().GetField("PageID", Refl.All);
                if (pageId != null)
                    pageId.SetValue(__instance, Enum.Parse(pageId.FieldType, "DownloadInstall"));

                SetChecked(item, true);
                SetChecked(GetMember(__instance, "ItemMod"), false);
            }
            catch (Exception ex)
            {
                DomainManager.Log("DownloadLeftPostfix: " + ex);
            }
        }

        // PageSelectRight.McInstanceListUI：无版本时恢复“下载游戏”按钮
        public static void SelectRightPostfix(object __instance)
        {
            try
            {
                if (IsDownloadHidden()) return;
                object showHidden = GetMember(__instance, "ShowHidden");
                bool hidden = showHidden is bool && (bool)showHidden;
                if (hidden) return;

                SetVisibility(GetMember(__instance, "BtnEmptyDownload"), true);
            }
            catch (Exception ex)
            {
                DomainManager.Log("SelectRightPostfix: " + ex);
            }
        }

        // PageLaunchLeft.RefreshButtonsUI：无版本时把 CurrentState 从 1 改成 2
        public static IEnumerable<CodeInstruction> LaunchLeftTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            for (int i = 0; i + 3 < list.Count; i++)
            {
                MethodInfo call = list[i].operand as MethodInfo;
                if (call == null || call.Name != "get_McInstanceSelected") continue;
                if (list[i + 1].opcode != OpCodes.Brtrue_S) continue;
                if (list[i + 2].opcode != OpCodes.Ldc_I4_1) continue;
                if (list[i + 3].opcode != OpCodes.Stloc_0) continue;

                list[i + 2].opcode = OpCodes.Call;
                list[i + 2].operand = typeof(PatchMethods).GetMethod("LaunchStateForNoInstance");
                break;
            }
            return list;
        }

        // PageResource.set_PageType：ModPack 时恢复安装按钮
        public static void ResourcePagePostfix(object __instance, object Value)
        {
            try
            {
                bool isModPack = Value != null && Value.ToString() == "ModPack";
                SetVisibility(GetMember(__instance, "BtnSearchInstallModPack"), isModPack);
            }
            catch (Exception ex)
            {
                DomainManager.Log("ResourcePagePostfix: " + ex);
            }
        }

        // PageInstanceModDisabled.BtnDownload_Loaded：恢复下载按钮
        public static void ModDisabledPostfix(object __instance)
        {
            try
            {
                if (IsDownloadHidden()) return;

                Type modMain = Type.GetType("PCL.ModMain, Plain Craft Launcher 2");
                if (modMain != null)
                {
                    FieldInfo rightField = modMain.GetField("FrmSelectRight", Refl.All);
                    if (rightField != null)
                    {
                        object right = rightField.GetValue(null);
                        if (right != null)
                        {
                            object showHidden = GetMember(right, "ShowHidden");
                            if (showHidden is bool && (bool)showHidden) return;
                        }
                    }
                }

                SetVisibility(GetMember(__instance, "BtnDownload"), true);
            }
            catch (Exception ex)
            {
                DomainManager.Log("ModDisabledPostfix: " + ex);
            }
        }

        // PageSelectLeft.McFolderListUI：把被编译掉的“导入整合包”项加回列表
        public static IEnumerable<CodeInstruction> SelectLeftTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);

            for (int i = 0; i + 3 < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Stloc_1) continue;
                if (list[i + 1].opcode != OpCodes.Ldloc_1) continue;
                if (list[i + 2].opcode != OpCodes.Ldsfld) continue;

                FieldInfo field = list[i + 2].operand as FieldInfo;
                if (field == null || !field.Name.Contains("IR4-8")) continue;

                if (HasItemInstallAdd(list))
                {
                    DomainManager.Log("SelectLeftTranspiler: 导入整合包已存在，跳过注入");
                    break;
                }

                List<CodeInstruction> sequence = BuildAddItemSequence();
                DomainManager.Log("SelectLeftTranspiler: pattern at " + i + ", seq=" + sequence.Count);
                if (sequence.Count > 0) list.InsertRange(i + 1, sequence);
                break;
            }

            return list;
        }

        private static bool HasItemInstallAdd(List<CodeInstruction> list)
        {
            for (int i = 1; i < list.Count; i++)
            {
                MethodInfo call = list[i].operand as MethodInfo;
                if (call == null || call.Name != "Add") continue;
                if (call.DeclaringType == null || call.DeclaringType.Name != "UIElementCollection") continue;
                if (list[i - 1].opcode != OpCodes.Ldloc_1) continue;

                for (int j = i - 2; j >= 0 && j >= i - 5; j--)
                {
                    MethodInfo previous = list[j].operand as MethodInfo;
                    if (previous != null && previous.Name == "get_Children") return true;
                }
            }
            return false;
        }

        private static List<CodeInstruction> BuildAddItemSequence()
        {
            var sequence = new List<CodeInstruction>();
            try
            {
                Type modMain = FindType("PCL.ModMain", "Plain Craft Launcher 2");
                Type page = FindType("PCL.PageSelectLeft", "Plain Craft Launcher 2");
                Type panel = FindType("System.Windows.Controls.Panel", "PresentationFramework");
                Type collection = FindType("System.Windows.Controls.UIElementCollection", "PresentationFramework");
                Type uiElement = FindType("System.Windows.UIElement", "PresentationCore");

                if (modMain == null || page == null || panel == null || collection == null || uiElement == null)
                {
                    DomainManager.Log("BuildAddItemSequence: type missing " +
                        (modMain == null) + (page == null) + (panel == null) + (collection == null) + (uiElement == null));
                    return sequence;
                }

                FieldInfo formField = modMain.GetField("FrmSelectLeft", Refl.All);

                MethodInfo getPanList = null;
                Type current = page;
                while (current != null && getPanList == null)
                {
                    foreach (MethodInfo candidate in current.GetMethods(Refl.All))
                    {
                        if (candidate.Name != "get_PanList") continue;
                        if (candidate.GetParameters().Length != 0) continue;
                        getPanList = candidate;
                        break;
                    }
                    current = current.BaseType;
                }

                MethodInfo getChildren = panel.GetProperty("Children").GetGetMethod();
                MethodInfo add = collection.GetMethod("Add", new Type[] { uiElement });

                if (formField == null || getPanList == null || getChildren == null || add == null)
                {
                    DomainManager.Log("BuildAddItemSequence: member missing form=" + (formField == null) +
                        " pan=" + (getPanList == null) + " children=" + (getChildren == null) + " add=" + (add == null));
                    return sequence;
                }

                sequence.Add(new CodeInstruction(OpCodes.Ldsfld, formField));
                sequence.Add(new CodeInstruction(OpCodes.Callvirt, getPanList));
                sequence.Add(new CodeInstruction(OpCodes.Callvirt, getChildren));
                sequence.Add(new CodeInstruction(OpCodes.Ldloc_1));
                sequence.Add(new CodeInstruction(OpCodes.Callvirt, add));
                sequence.Add(new CodeInstruction(OpCodes.Pop));
            }
            catch (Exception ex)
            {
                DomainManager.Log("BuildAddItemSequence: " + ex);
                sequence.Clear();
            }
            return sequence;
        }

        private static Type FindType(string name, string assemblyName)
        {
            Type type = Type.GetType(name + ", " + assemblyName);
            if (type != null) return type;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != assemblyName) continue;
                type = assembly.GetType(name);
                if (type != null) return type;
            }
            return null;
        }

        public static int LaunchStateForNoInstance()
        {
            return IsDownloadHidden() ? 1 : 2;
        }

        private static bool IsDownloadHidden()
        {
            try
            {
                bool hidden = false;
                Type settings = FindType("PCL.Settings", "Plain Craft Launcher 2");
                if (settings != null)
                {
                    MethodInfo get = null;
                    object[] arguments = null;

                    foreach (MethodInfo method in settings.GetMethods(Refl.All))
                    {
                        if (method.Name != "Get" || !method.IsGenericMethodDefinition) continue;

                        int count = method.GetParameters().Length;
                        if (count == 2) { get = method; arguments = new object[] { "UiHiddenPageDownload", null }; break; }
                        if (count == 1 && get == null) { get = method; arguments = new object[] { "UiHiddenPageDownload" }; }
                    }

                    if (get != null)
                        hidden = (bool)get.MakeGenericMethod(typeof(bool)).Invoke(null, arguments);
                }

                bool force = false;
                Type setup = FindType("PCL.PageSetupUI", "Plain Craft Launcher 2");
                if (setup != null)
                {
                    PropertyInfo property = setup.GetProperty("HiddenForceShow", Refl.All);
                    if (property != null) force = (bool)property.GetValue(null, null);
                    else
                    {
                        FieldInfo field = setup.GetField("HiddenForceShow", Refl.All);
                        if (field != null) force = (bool)field.GetValue(null);
                    }
                }

                return hidden && !force;
            }
            catch
            {
                return false;
            }
        }

        private static object GetMember(object instance, string name)
        {
            Type type = instance.GetType();

            PropertyInfo property = type.GetProperty(name, Refl.All);
            if (property != null) return property.GetValue(instance, null);

            FieldInfo field = type.GetField(name, Refl.All);
            if (field != null) return field.GetValue(instance);

            return null;
        }

        private static void SetVisibility(object element, bool visible)
        {
            if (element == null) return;

            PropertyInfo property = element.GetType().GetProperty("Visibility", Refl.All);
            if (property == null) return;

            property.SetValue(element, Enum.Parse(property.PropertyType, visible ? "Visible" : "Collapsed"), null);
        }

        private static void SetChecked(object element, bool value)
        {
            if (element == null) return;

            PropertyInfo property = element.GetType().GetProperty("Checked", Refl.All);
            if (property != null) property.SetValue(element, value, null);
        }
    }
}
