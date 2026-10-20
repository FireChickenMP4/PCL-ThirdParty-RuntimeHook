using System;
using PclHook;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

// 离线兼容检查：只解析锚点，不修改任何文件
//   pclhook-check <PCL exe>
// 退出码 0 = 全部命中，1 = 有缺失，2 = 参数错误
internal static class CheckProgram
{
    private static void DumpType(string exe, string name)
    {
        using (var mod = ModuleDefMD.Load(exe))
        {
            bool found = false;
            foreach (TypeDef type in mod.GetTypes())
            {
                if (!type.FullName.Contains(name)) continue;
                found = true;
                Console.WriteLine("TYPE " + type.FullName);

                foreach (FieldDef field in type.Fields)
                    Console.WriteLine("  field  " + field.FieldType + " " + field.Name);

                foreach (MethodDef method in type.Methods)
                    Console.WriteLine("  method " + method.Name + " " + method.MethodSig);
            }
            if (!found) Console.WriteLine("not found: " + name);
        }
    }

    private static void DumpInstallers(string exe)
    {
        using (var mod = ModuleDefMD.Load(exe))
        {
            TypeDef modpack = mod.Find("PCL.ModModpack", false);
            if (modpack == null)
            {
                Console.WriteLine("PCL.ModModpack not found");
                return;
            }

            foreach (MethodDef method in modpack.Methods)
            {
                if (!method.IsStatic || !method.HasBody) continue;
                if (!method.ReturnType.TypeName.Contains("LoaderCombo")) continue;

                Console.WriteLine(method.Name + " (" + method.MethodSig.Params.Count + "):");
                foreach (Instruction ins in method.Body.Instructions)
                {
                    if (ins.OpCode.Code != Code.Ldstr) continue;
                    string text = ins.Operand as string;
                    if (text == null) continue;
                    if (text.Contains("pack") || text.Contains("manifest"))
                        Console.WriteLine("    \"" + text + "\"");
                }
            }
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--dump-type")
        {
            try
            {
                DumpType(args[2], args[1]);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        if (args.Length == 2 && args[0] == "--dump-installers")
        {
            try
            {
                DumpInstallers(args[1]);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        if (args.Length != 1)
        {
            Console.WriteLine("usage: pclhook-check <Plain Craft Launcher 2.exe>");
            Console.WriteLine("       pclhook-check --dump-installers <Plain Craft Launcher 2.exe>");
            Console.WriteLine("       pclhook-check --dump-type <type> <Plain Craft Launcher 2.exe>");
            return 2;
        }

        try
        {
            Console.WriteLine("version: " + System.Reflection.AssemblyName.GetAssemblyName(args[0]).Version);
            Version version = System.Reflection.AssemblyName.GetAssemblyName(args[0]).Version;
            if (version.Major != 2 || version.Minor != 99)
                Console.WriteLine("NOTE: 非 2.99，运行时补丁会被版本闸门禁用");

            HookPlan plan = HookPlan.Build(args[0]);
            Console.WriteLine(plan.Report);

            bool[] anchors =
            {
                plan.HasLoaderInit,
                plan.HasVisibility,
                plan.HasModpack3,
                plan.HasModpack0,
                plan.HasDlLoader,
                plan.ControlInitToken != 0,
                plan.GetLoadMinecraftToken != 0,
                plan.GetPanLoadToken != 0,
                plan.GetPanAllBackToken != 0,
                plan.OnFinishToken != 0,
                plan.PageLoaderInitToken != 0,
                plan.McFolderToken != 0,
                plan.HintToken != 0
            };

            string[] names =
            {
                "LoaderInit",
                "CardMinecraft visibility",
                "ModpackInstall(3)",
                "ModpackInstall()",
                "DlClientListLoader",
                "  ControlInit",
                "  get_LoadMinecraft",
                "  get_PanLoad",
                "  get_PanAllBack",
                "  on-finish callback",
                "  PageLoaderInit",
                "  McFolderSelected",
                "  Hint"
            };

            int missing = 0;
            for (int i = 0; i < anchors.Length; i++)
            {
                if (anchors[i]) continue;
                missing++;
                Console.WriteLine("MISS: " + names[i]);
            }

            Console.WriteLine("installers resolved: " + plan.InstallerTokens.Count + " (expected 7: 6 types + compress)");
            var keys = new System.Collections.Generic.List<int>(plan.InstallerTokens.Keys);
            keys.Sort();
            Console.WriteLine("installer keys: " + string.Join(",", keys.ConvertAll(k => k.ToString()).ToArray()));
            if (plan.InstallerTokens.Count < 7)
                Console.WriteLine("WARN: 部分整合包安装方法没有按格式标记识别出来");

            if (version > new Version(2, 99, 0, 0))
            {
                if (!plan.LaunchPattern) { Console.WriteLine("MISS:   LaunchLeft transpiler pattern"); missing++; }
                if (!plan.SelectLeftPattern) { Console.WriteLine("MISS:   SelectLeft transpiler pattern"); missing++; }
            }

            if (missing == 0)
            {
                Console.WriteLine("RESULT: compatible");
                return 0;
            }

            Console.WriteLine("RESULT: needs update (" + missing + " anchors missing)");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }
}
