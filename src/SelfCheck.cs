#if SELFTEST
using System;
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

                Thread.Sleep(15000);
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
            }
            catch (Exception ex)
            {
                DomainManager.Log("selftest navigate error: " + ex);
            }
        }
    }
}

#endif
