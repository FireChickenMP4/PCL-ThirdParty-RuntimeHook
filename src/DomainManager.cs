using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PclHook
{
    /// <summary>
    /// CLR 在 PCL 的 Main 之前加载这个 DomainManager。
    /// 注意：InitializeNewDomain 里不能做文件 I/O，也不能调用 base，
    /// 否则会破坏 PCL 的启动环境（表现为"没有对当前文件夹的权限"）。
    /// </summary>
    public class DomainManager : AppDomainManager
    {
        private static int started;

        public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
        {
            InitializationFlags = AppDomainManagerInitializationOptions.RegisterWithHost;
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs e)
        {
            try
            {
                if (e.LoadedAssembly.GetName().Name != "Plain Craft Launcher 2") return;
                if (Interlocked.Exchange(ref started, 1) != 0) return;

                Thread thread = new Thread(Install);
                thread.IsBackground = true;
                thread.Start();
            }
            catch (Exception ex)
            {
                Log("AssemblyLoad handler error: " + ex);
            }
        }

        private static void Install()
        {
            try
            {
                // 等 PCL 主程序集加载稳定后再打补丁
                Thread.Sleep(300);

                if (IsHelperProcess())
                {
                    Log("helper process, skip patches");
                    return;
                }
                if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nopatch.flag")))
                {
                    Log("nopatch.flag found, skip patches");
                    return;
                }

                Log("installing runtime patches");
                Patches.Apply();

#if SELFTEST
                if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.flag")))
                    SelfCheck.Run();
#endif
            }
            catch (Exception ex)
            {
                Log("Install error " + ex);
            }
        }

        private static bool IsHelperProcess()
        {
            foreach (string arg in Environment.GetCommandLineArgs())
                if (arg == "--update" || arg == "--gpu" || arg == "--memory") return true;
            return false;
        }

        public static void Log(string message)
        {
            try
            {
                lock (typeof(DomainManager))
                {
                    string path = LogPath;
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 1024 * 1024) File.Delete(path);

                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + Process.GetCurrentProcess().Id + "] " +
                        message + Environment.NewLine);
                }
            }
            catch
            {
            }
        }

        private static string LogPath
        {
            // 必须是惰性属性：静态字段初始化会在域初始化阶段执行，破坏 PCL 启动
            get { return Path.Combine(Path.GetTempPath(), "pclhook.log"); }
        }
    }
}
