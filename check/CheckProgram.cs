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
            foreach (TypeDef type in mod.GetTypes())
            {
                if (!type.FullName.Contains(name)) continue;
                Console.WriteLine("TYPE " + type.FullName);

                foreach (FieldDef field in type.Fields)
                    if (field.Name.String.Contains("Card") || field.Name.String.Contains("Minecraft"))
                        Console.WriteLine("  field " + field.Name + " : " + field.FieldSig);

                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    bool hit = method.Name.String.Contains("Card") || method.Name.String.Contains("Instance");
                    foreach (Instruction ins in method.Body.Instructions)
                    {
                        string text = ins.Operand == null ? "" : ins.Operand.ToString();
                        if (text.Contains("CardMinecraft") || text.Contains("set_Visibility") || text.Contains("get_Card"))
                        {
                            hit = true;
                            break;
                        }
                    }
                    if (!hit) continue;

                    Console.WriteLine("  method " + method.Name + " " + method.MethodSig);
                    foreach (Instruction ins in method.Body.Instructions)
                    {
                        string text = ins.Operand == null ? "" : ins.Operand.ToString();
                        if (text.Contains("CardMinecraft") || text.Contains("Visibility"))
                            Console.WriteLine("    " + ins.OpCode.Code + " " + text);
                    }
                }
            }
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
            DumpType(args[1], args[2]);
            return 0;
        }

        if (args.Length == 2 && args[0] == "--dump-installers")
        {
            DumpInstallers(args[1]);
            return 0;
        }

        if (args.Length != 1)
        {
            Console.WriteLine("usage: pclhook-check <Plain Craft Launcher 2.exe>");
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
