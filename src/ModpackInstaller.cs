using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;

namespace PclHook
{
    /// <summary>
    /// 重建被编译期删掉的整合包安装分发逻辑。
    /// 判定顺序与 PCL 源码 ModModpack.vb 保持一致。
    /// </summary>
    internal static class ModpackInstaller
    {
        internal static object Install(string file, string instanceName, string logo)
        {
            string mcFolder;
            if (PatchContext.McFolder != null)
                mcFolder = (string)PatchContext.McFolder.Invoke(null, null);
            else
                mcFolder = (string)Refl.Static(Refl.Pcl("PCL.ModMinecraft"), "McFolderSelected");
            string targetFolder = mcFolder + "versions\\" + instanceName + "\\";
            if (targetFolder.Contains("!") || targetFolder.Contains(";"))
            {
                Refl.Hint("游戏路径中不能含有感叹号或分号：" + targetFolder, true);
                throw new OperationCanceledException();
            }

            ZipArchive archive = null;
            string baseFolder = "";
            try
            {
                int packType;
                try
                {
                    archive = ZipFile.OpenRead(file);
                    packType = Detect(archive, ref baseFolder);
                }
                catch (Exception ex)
                {
                    throw OpenError(file, ex);
                }
                DomainManager.Log("pack type = " + packType + ", base = \"" + baseFolder + "\"");

                string methodName;
                int argCount;
                switch (packType)
                {
                    case 0: methodName = "InstallPackCurseForge"; argCount = 5; break;
                    case 1: methodName = "InstallPackHMCL"; argCount = 3; break;
                    case 2: methodName = "InstallPackMMC"; argCount = 3; break;
                    case 3: methodName = "InstallPackMCBBS"; argCount = 4; break;
                    case 4: methodName = "InstallPackModrinth"; argCount = 5; break;
                    case 9: methodName = "InstallPackLauncherPack"; argCount = 3; break;
                    default: methodName = "InstallPackCompress"; argCount = 2; break;
                }

                MethodInfo installer = null;
                if (PatchContext.Installers != null)
                {
                    if (PatchContext.Installers.ContainsKey(packType))
                        installer = PatchContext.Installers[packType];
                    else if (PatchContext.Installers.ContainsKey(-1))
                        installer = PatchContext.Installers[-1];
                }
                if (installer == null)
                    installer = FindInstaller(Refl.Pcl("PCL.ModModpack"), methodName, argCount);

                object[] args = BuildArgs(argCount, file, archive, baseFolder, instanceName, logo);
                DomainManager.Log("dispatch -> " + installer);

                try
                {
                    return installer.Invoke(null, args);
                }
                catch (TargetInvocationException ex)
                {
                    if (ex.InnerException != null)
                        ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw;
                }
            }
            finally
            {
                if (archive != null) archive.Dispose();
            }
        }

        // 与 PCL 原方法的异常处理保持一致
        private static Exception OpenError(string file, Exception ex)
        {
            string extension = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
            if (extension == "rar")
                return new Exception("PCL 无法处理 rar 格式的压缩包，请在解压后重新压缩为 zip 格式再试", ex);
            if (ex is IOException || ex is InvalidDataException)
                return new Exception("打开整合包文件失败，文件可能损坏或为不支持的压缩包格式", ex);
            return new Exception("打开整合包文件失败", ex);
        }

        private static MethodInfo FindInstaller(Type type, string name, int argCount)
        {
            foreach (MethodInfo method in type.GetMethods(Refl.All))
                if (method.Name == name && method.GetParameters().Length == argCount) return method;
            throw new Exception("installer not found: " + name + "/" + argCount);
        }

        private static object[] BuildArgs(int argCount, string file, ZipArchive archive,
                                          string baseFolder, string instanceName, string logo)
        {
            switch (argCount)
            {
                case 2: return new object[] { file, archive };
                case 3: return new object[] { file, archive, baseFolder };
                case 4: return new object[] { file, archive, baseFolder, instanceName };
                case 5: return new object[] { file, archive, baseFolder, instanceName, logo };
                default: throw new Exception("unexpected installer arg count: " + argCount);
            }
        }

        private static int Detect(ZipArchive archive, ref string baseFolder)
        {
            if (archive.GetEntry("mcbbs.packmeta") != null) return 3;
            if (archive.GetEntry("mmc-pack.json") != null) return 2;
            if (archive.GetEntry("modrinth.index.json") != null) return 4;

            ZipArchiveEntry manifest = archive.GetEntry("manifest.json");
            if (manifest != null) return HasAddons(manifest) ? 3 : 0;

            if (archive.GetEntry("modpack.json") != null) return 1;
            if (archive.GetEntry("modpack.zip") != null || archive.GetEntry("modpack.mrpack") != null) return 9;

            // 一级目录下的判定
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string[] parts = entry.FullName.Split('/');
                baseFolder = parts[0] + "/";
                if (parts.Length != 2) continue;

                if (parts[1] == "mcbbs.packmeta") return 3;
                if (parts[1] == "mmc-pack.json") return 2;
                if (parts[1] == "modrinth.index.json") return 4;
                if (parts[1] == "manifest.json")
                {
                    if (HasAddons(entry))
                    {
                        // 与 PCL 源码一致：一级目录下的 MCBBS 包，overrides 目录固定在 overrides/
                        baseFolder = "overrides/";
                        return 3;
                    }
                    return 0;
                }
                if (parts[1] == "modpack.json") return 1;
                if (parts[1] == "modpack.zip" || parts[1] == "modpack.mrpack") return 9;
            }
            return -1;
        }

        /// <summary>manifest.json 带 addons 时是 MCBBS，否则是 CurseForge。</summary>
        private static bool HasAddons(ZipArchiveEntry entry)
        {
            using (Stream stream = entry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                // manifest.json 通常很小，限制读取长度，避免异常压缩包消耗内存
                var buffer = new char[1 << 20];
                int read = reader.ReadBlock(buffer, 0, buffer.Length);
                string text = new string(buffer, 0, read);
                return System.Text.RegularExpressions.Regex.IsMatch(text, "\"addons\"\\s*:");
            }
        }
    }
}
