#if SELFTEST
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace PclHook
{
    /// <summary>
    /// 自检：不经过鼠标，直接调用 PCL 的页面切换进入下载页，
    /// 触发 PageDownloadInstall 初始化，用于验证第 2 处补丁。
    /// 在 exe 同目录放 selftest.flag 启用。
    /// </summary>
    internal static class SelfCheck
    {
        internal static void Run()
        {
            try
            {
                Thread.Sleep(1500);

                Type appType = null;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name != "PresentationFramework") continue;
                    appType = assembly.GetType("System.Windows.Application");
                    break;
                }
                if (appType == null) { DomainManager.Log("selftest: PresentationFramework not found"); return; }

                object app = appType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                if (app == null) { DomainManager.Log("selftest: Application.Current is null"); return; }

                object dispatcher = appType.GetProperty("Dispatcher").GetValue(app, null);
                MethodInfo invoke = dispatcher.GetType().GetMethod("Invoke", new Type[] { typeof(Delegate), typeof(object[]) });
                if (invoke == null) { DomainManager.Log("selftest: Dispatcher.Invoke not found"); return; }

                invoke.Invoke(dispatcher, new object[] { new Action(NavigateToDownload), new object[0] });
            }
            catch (Exception ex)
            {
                DomainManager.Log("selftest error: " + ex);
            }
        }

        private static void NavigateToDownload()
        {
            try
            {
                Type modMain = Type.GetType("PCL.ModMain, Plain Craft Launcher 2", true);
                FieldInfo frmField = modMain.GetField("FrmMain", Refl.All);
                object form = frmField.GetValue(null);
                Type formMain = form.GetType();

                Type stackType = formMain.GetNestedType("PageStackData", Refl.All);
                object stack = Activator.CreateInstance(stackType);
                Type pageType = formMain.GetNestedType("PageType", Refl.All);
                stackType.GetField("Page").SetValue(stack, Enum.ToObject(pageType, 1));

                MethodInfo pageChange = null;
                foreach (MethodInfo method in formMain.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (method.Name == "PageChange" && method.GetParameters().Length == 2) { pageChange = method; break; }
                if (pageChange == null) throw new Exception("PageChange not found");

                pageChange.Invoke(form, BindingFlags.OptionalParamBinding | BindingFlags.InvokeMethod, null,
                    new object[] { stack, Type.Missing }, null);
                DomainManager.Log("selftest: switched to download page");

                Thread.Sleep(2000);
                LogDownloadLeft();

                Thread.Sleep(8000);
                Type modDownload = Type.GetType("PCL.ModDownload, Plain Craft Launcher 2", true);
                FieldInfo loaderField = modDownload.GetField("DlClientListLoader", Refl.All);
                object loader = loaderField.GetValue(null);
                object state = loader.GetType().GetProperty("State").GetValue(loader, null);
                object output = loader.GetType().GetField("Output", Refl.All).GetValue(loader);

                int count = -1;
                if (output != null)
                {
                    MethodInfo item = null;
                    foreach (MethodInfo method in output.GetType().GetMethods())
                        if (method.Name == "get_Item" && method.GetParameters().Length == 1) { item = method; break; }

                    object versions = item == null ? null : item.Invoke(output, new object[] { "versions" });
                    if (versions != null)
                    {
                        PropertyInfo countProperty = versions.GetType().GetProperty("Count");
                        count = countProperty == null ? -2 : Convert.ToInt32(countProperty.GetValue(versions, null));
                    }
                }
                DomainManager.Log("selftest: loader state=" + state + " versions=" + count);
                CheckSelectLeft(form, formMain, pageType, stackType);
            }
            catch (Exception ex)
            {
                DomainManager.Log("selftest navigate error: " + ex);
            }
        }

        private static void CheckSelectLeft(object form, Type formMain, Type pageType, Type stackType)
        {
            try
            {
                object stack = Activator.CreateInstance(stackType);
                stackType.GetField("Page").SetValue(stack, Enum.Parse(pageType, "InstanceSelect"));

                MethodInfo pageChange = null;
                foreach (MethodInfo method in formMain.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (method.Name == "PageChange" && method.GetParameters().Length == 2) { pageChange = method; break; }
                if (pageChange == null) throw new Exception("PageChange not found");

                pageChange.Invoke(form, BindingFlags.OptionalParamBinding | BindingFlags.InvokeMethod, null,
                    new object[] { stack, Type.Missing }, null);
                Thread.Sleep(2000);

                Type modMain = Type.GetType("PCL.ModMain, Plain Craft Launcher 2", true);
                object left = modMain.GetField("FrmSelectLeft", Refl.All).GetValue(null);
                if (left == null)
                {
                    DomainManager.Log("selftest: FrmSelectLeft is null");
                    return;
                }

                FieldInfo last = left.GetType().GetField("McFolderListLast", Refl.All);
                if (last != null) last.SetValue(left, null);

                MethodInfo refresh = left.GetType().GetMethod("McFolderListUI", Refl.All);
                if (refresh != null) refresh.Invoke(left, null);
                Thread.Sleep(500);

                object pan = GetMember(left, "PanList");
                object children = GetMember(pan, "Children");

                var names = new List<string>();
                foreach (object child in (System.Collections.IEnumerable)children)
                {
                    object title = GetMember(child, "Title");
                    object text = GetMember(child, "Text");
                    names.Add(child.GetType().Name + ":" + (title ?? text ?? "?").ToString());
                }
                DomainManager.Log("selftest: select-left items=" + string.Join(" | ", names.ToArray()));

                object right = modMain.GetField("FrmSelectRight", Refl.All).GetValue(null);
                if (right != null)
                {
                    object download = GetMember(right, "BtnEmptyDownload");
                    DomainManager.Log("selftest: BtnEmptyDownload=" + VisibilityOf(download));
                }

                object launchStack = Activator.CreateInstance(stackType);
                stackType.GetField("Page").SetValue(launchStack, Enum.Parse(pageType, "Launch"));
                pageChange.Invoke(form, BindingFlags.OptionalParamBinding | BindingFlags.InvokeMethod, null,
                    new object[] { launchStack, Type.Missing }, null);
                Thread.Sleep(1000);

                object launchLeft = modMain.GetField("FrmLaunchLeft", Refl.All).GetValue(null);
                if (launchLeft != null)
                {
                    object btn = GetMember(launchLeft, "BtnLaunch");
                    object text = GetMember(btn, "Text");
                    DomainManager.Log("selftest: BtnLaunch.Text=" + text);
                }

                Type resourceType = Type.GetType("PCL.PageResource, Plain Craft Launcher 2");
                if (resourceType != null)
                {
                    object resource = Activator.CreateInstance(resourceType);
                    PropertyInfo pageTypeProperty = resourceType.GetProperty("PageType", Refl.All);
                    pageTypeProperty.SetValue(resource, Enum.Parse(pageTypeProperty.PropertyType, "ModPack"), null);
                    DomainManager.Log("selftest: resource install btn=" +
                        VisibilityOf(GetMember(resource, "BtnSearchInstallModPack")));
                }

                Type modDisabledType = Type.GetType("PCL.PageInstanceModDisabled, Plain Craft Launcher 2");
                if (modDisabledType != null)
                {
                    object modDisabled = Activator.CreateInstance(modDisabledType);
                    MethodInfo loaded = modDisabledType.GetMethod("BtnDownload_Loaded", Refl.All);
                    if (loaded != null) loaded.Invoke(modDisabled, null);
                    DomainManager.Log("selftest: moddisabled download btn=" +
                        VisibilityOf(GetMember(modDisabled, "BtnDownload")));
                }
            }
            catch (Exception ex)
            {
                DomainManager.Log("selftest select-left error: " + ex);
            }
        }

        private static void LogDownloadLeft()
        {
            try
            {
                Type modMain = Type.GetType("PCL.ModMain, Plain Craft Launcher 2", true);
                object left = modMain.GetField("FrmDownloadLeft", Refl.All).GetValue(null);
                if (left == null)
                {
                    DomainManager.Log("selftest: FrmDownloadLeft is null");
                    return;
                }

                object item = GetMember(left, "ItemInstall");
                object mod = GetMember(left, "ItemMod");
                object lab = GetMember(left, "LabResource");
                object pageId = left.GetType().GetField("PageID", Refl.All).GetValue(left);
                DomainManager.Log("selftest: ItemInstall=" + VisibilityOf(item) +
                                  " LabResource=" + VisibilityOf(lab) +
                                  " PageID=" + pageId +
                                  " checked=" + CheckedOf(item) + "/" + CheckedOf(mod));
            }
            catch (Exception ex)
            {
                DomainManager.Log("selftest left error: " + ex);
            }
        }

        private static object GetMember(object instance, string name)
        {
            Type type = instance.GetType();
            PropertyInfo property = type.GetProperty(name, Refl.All);
            if (property != null) return property.GetValue(instance, null);
            FieldInfo field = type.GetField(name, Refl.All);
            return field != null ? field.GetValue(instance) : null;
        }

        private static string CheckedOf(object element)
        {
            if (element == null) return "null";
            PropertyInfo property = element.GetType().GetProperty("Checked", Refl.All);
            return property == null ? "n/a" : property.GetValue(element, null).ToString();
        }

        private static string VisibilityOf(object element)
        {
            if (element == null) return "null";
            PropertyInfo property = element.GetType().GetProperty("Visibility", Refl.All);
            if (property == null) return "n/a";
            return property.GetValue(element, null).ToString();
        }
    }
}

#endif
