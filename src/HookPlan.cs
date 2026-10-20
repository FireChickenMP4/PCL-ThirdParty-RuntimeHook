using System;
using System.Collections.Generic;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PclHook
{
    /// <summary>
    /// 语义化目标定位：用锚点而不是固定偏移/方法名去读 IL，
    /// 输出各个目标的方法/字段 MetadataToken，运行期再映射回 MethodInfo。
    /// </summary>
    internal sealed class HookPlan
    {
        internal int LoaderInitToken;
        internal int VisibilityToken;
        internal int Modpack3Token;
        internal int Modpack0Token;
        internal int DlLoaderToken;

        // LoaderInit 内部用到的成员（不再按名字）
        internal int ControlInitToken;
        internal int PageLoaderInitToken;
        internal int GetLoadMinecraftToken;
        internal int GetPanLoadToken;
        internal int GetPanAllBackToken;
        internal int OnFinishToken;
        internal int McFolderToken;
        internal int HintToken;

        // 2.99.0.1 新增的 UI 限制
        internal int DownloadLeftCtorToken;
        internal int SelectRightUiToken;
        internal int LaunchLeftRefreshToken;
        internal int ResourcePageTypeToken;
        internal int ModDisabledLoadedToken;
        internal int SelectLeftUiToken;

        internal bool LaunchPattern;
        internal bool SelectLeftPattern;
        internal bool SelectLeftHasAdd;

        // 整合包类型 -> InstallPack* 方法 token；-1 表示"未能识别，按压缩包处理"
        internal readonly Dictionary<int, int> InstallerTokens = new Dictionary<int, int>();

        internal string LoaderInitName;
        internal string VisibilityName;
        internal string Modpack3Name;
        internal string Modpack0Name;
        internal string DlLoaderName;
        internal string McFolderName;
        internal string HintName;

        internal string Report;
        internal string Notes = "";

        internal bool HasLoaderInit { get { return LoaderInitToken != 0; } }
        internal bool HasVisibility { get { return VisibilityToken != 0; } }
        internal bool HasModpack3 { get { return Modpack3Token != 0; } }
        internal bool HasModpack0 { get { return Modpack0Token != 0; } }
        internal bool HasDlLoader { get { return DlLoaderToken != 0; } }

        internal static HookPlan Build(string exePath)
        {
            var plan = new HookPlan();

            using (var mod = ModuleDefMD.Load(exePath))
            {
                FindDlLoader(mod, plan);
                FindLoaderInit(mod, plan);
                FindVisibility(mod, plan);
                FindModpackInstall(mod, plan);
                FindModpackDialog(mod, plan);
                FindUiTargets(mod, plan);
            }

            plan.Report = plan.Describe();
            return plan;
        }

        private static void FindDlLoader(ModuleDefMD mod, HookPlan plan)
        {
            // 不依赖类型名：先收集所有 LoaderTask<string, *DlClientListResult> 静态字段，
            // 再按被引用次数选主 loader（其余一般是局部缓存字段）
            var candidates = new List<FieldDef>();
            foreach (TypeDef type in mod.GetTypes())
            {
                foreach (FieldDef field in type.Fields)
                {
                    if (!field.IsStatic) continue;
                    var signature = field.FieldSig.Type as GenericInstSig;
                    if (signature == null) continue;
                    if (signature.GenericType.TypeName != "LoaderTask`2") continue;
                    if (signature.GenericArguments[0].FullName != "System.String") continue;
                    if (!signature.GenericArguments[1].TypeName.Contains("DlClientListResult")) continue;
                    candidates.Add(field);
                }
            }

            if (candidates.Count == 0) return;

            FieldDef best = null;
            int bestRefs = -1;
            int secondRefs = -1;
            bool tie = false;
            foreach (FieldDef field in candidates)
            {
                int refs = CountFieldRefs(mod, field);
                if (refs > bestRefs)
                {
                    secondRefs = bestRefs;
                    best = field;
                    bestRefs = refs;
                    tie = false;
                }
                else if (refs == bestRefs)
                {
                    tie = true;
                }
                else if (refs > secondRefs)
                {
                    secondRefs = refs;
                }
            }

            // 主 loader 的引用数应明显高于子 loader，否则宁可跳过
            if (best == null || tie || bestRefs < 2 * Math.Max(secondRefs, 1))
            {
                plan.Notes += "DlClientListLoader 候选 " + candidates.Count +
                              " 个且引用次数无法区分（" + bestRefs + " vs " + secondRefs + "），已跳过；";
                return;
            }

            plan.DlLoaderToken = (int)best.MDToken.Raw;
            plan.DlLoaderName = best.FullName + " (refs " + bestRefs + ")";
        }

        private static int CountFieldRefs(ModuleDefMD mod, FieldDef field)
        {
            int count = 0;
            foreach (TypeDef type in mod.GetTypes())
            {
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    foreach (Instruction instruction in method.Body.Instructions)
                    {
                        if (instruction.OpCode.Code != Code.Ldsfld && instruction.OpCode.Code != Code.Ldsflda)
                            continue;
                        if (ReferenceEquals(instruction.Operand, field)) count++;
                    }
                }
            }
            return count;
        }

        private static void FindLoaderInit(ModuleDefMD mod, HookPlan plan)
        {
            // 锚点：字符串 "关闭下载" + 其后出现的 LoaderTask newobj
            foreach (TypeDef type in mod.GetTypes())
            {
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    IList<Instruction> ins = method.Body.Instructions;
                    for (int i = 0; i < ins.Count; i++)
                    {
                        if (ins[i].OpCode.Code != Code.Ldstr) continue;
                        if (!(ins[i].Operand is string) || (string)ins[i].Operand != "关闭下载") continue;
                        if (!HasLoaderCtor(ins, i + 1, 60)) continue;

                        plan.LoaderInitToken = (int)method.MDToken.Raw;
                        plan.LoaderInitName = method.FullName;
                        ResolveLoaderInitDetails(method, i, plan);
                        return;
                    }
                }
            }
        }

        private static bool HasLoaderCtor(IList<Instruction> ins, int start, int limit)
        {
            for (int i = start; i < Math.Min(start + limit, ins.Count); i++)
            {
                if (ins[i].OpCode.Code != Code.Newobj) continue;
                IMethod ctor = ins[i].Operand as IMethod;
                if (ctor != null && ctor.DeclaringType != null && ctor.DeclaringType.Name.String.Contains("LoaderTask"))
                    return true;
            }
            return false;
        }

        // 从 LoaderInit 的方法体里解析：ControlInit、三个属性 getter、完成回调、PageLoaderInit
        private static void ResolveLoaderInitDetails(MethodDef method, int anchorIndex, HookPlan plan)
        {
            IList<Instruction> ins = method.Body.Instructions;

            int ctor = -1;
            for (int i = anchorIndex + 1; i < ins.Count; i++)
            {
                if (ins[i].OpCode.Code != Code.Newobj) continue;
                IMethod created = ins[i].Operand as IMethod;
                if (created != null && created.DeclaringType != null &&
                    created.DeclaringType.Name.String.Contains("LoaderTask"))
                {
                    ctor = i;
                    break;
                }
            }
            if (ctor < 0) return;

            // ControlInit：ctor 之前、同类型、0 参数、返回 void 的第一次 call
            for (int i = 0; i < ctor; i++)
            {
                if (ins[i].OpCode.Code != Code.Call) continue;
                var callee = ins[i].Operand as MethodDef;
                if (callee == null) continue;
                if (callee.MethodSig.Params.Count != 0) continue;
                if (callee.ReturnType.FullName != "System.Void") continue;
                if (callee.DeclaringType != method.DeclaringType) continue;

                plan.ControlInitToken = (int)callee.MDToken.Raw;
                break;
            }

            // 三个属性 getter：按返回类型识别，不依赖出现顺序或名字
            for (int i = 0; i < ctor; i++)
            {
                if (ins[i].OpCode.Code != Code.Call && ins[i].OpCode.Code != Code.Callvirt) continue;
                var callee = ins[i].Operand as MethodDef;
                if (callee == null || callee.MethodSig.Params.Count != 0) continue;

                string returnType = callee.ReturnType.TypeName;
                int token = (int)callee.MDToken.Raw;

                if (returnType == "MyLoading") plan.GetLoadMinecraftToken = token;
                else if (returnType == "MyCard") plan.GetPanLoadToken = token;
                else if (returnType == "Grid") plan.GetPanAllBackToken = token;
            }

            // OnFinish：ctor 之后、PageLoaderInit 之前的 ldftn
            // （ctor 之前那个 ldftn 是假 Loader 自己的 lambda，不能用）
            for (int i = ctor + 1; i < ins.Count; i++)
            {
                if (ins[i].OpCode.Code != Code.Ldftn) continue;
                var target = ins[i].Operand as MethodDef;
                if (target != null) plan.OnFinishToken = (int)target.MDToken.Raw;
                break;
            }

            // PageLoaderInit：ctor 之后、8 参数、泛型方法
            for (int i = ctor + 1; i < ins.Count; i++)
            {
                if (ins[i].OpCode.Code != Code.Call) continue;

                IMethod callee = ins[i].Operand as IMethod;
                MethodDef definition = callee as MethodDef;
                if (definition == null)
                {
                    var spec = callee as MethodSpec;
                    if (spec != null) definition = spec.Method as MethodDef;
                }
                if (definition == null) continue;
                if (definition.MethodSig.Params.Count != 8) continue;
                if (definition.MethodSig.GenParamCount == 0) continue;

                plan.PageLoaderInitToken = (int)definition.MDToken.Raw;
                break;
            }
        }

        private static void FindVisibility(ModuleDefMD mod, HookPlan plan)
        {
            // 锚点：get_CardMinecraft ... set_Visibility
            foreach (TypeDef type in mod.GetTypes())
            {
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    IList<Instruction> ins = method.Body.Instructions;
                    for (int i = 0; i < ins.Count; i++)
                    {
                        bool isCard = false;
                        IMethod get = ins[i].Operand as IMethod;
                        if (get != null && get.Name.String == "get_CardMinecraft") isCard = true;
                        IField field = ins[i].Operand as IField;
                        if (field != null && field.Name.String == "CardMinecraft") isCard = true;
                        if (!isCard) continue;

                        for (int j = i + 1; j < Math.Min(i + 12, ins.Count); j++)
                        {
                            IMethod set = ins[j].Operand as IMethod;
                            if (set == null || set.Name.String != "set_Visibility") continue;

                            plan.VisibilityToken = (int)method.MDToken.Raw;
                            plan.VisibilityName = method.FullName;
                            return;
                        }
                    }
                }
            }
        }

        private static void FindModpackInstall(ModuleDefMD mod, HookPlan plan)
        {
            // 锚点：3 个 string 参数 + 返回 LoaderCombo + 包含 qwq 异常
            foreach (TypeDef type in mod.GetTypes())
            {
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    if (!method.IsStatic) continue;
                    if (method.MethodSig.Params.Count != 3) continue;
                    if (!method.ReturnType.TypeName.Contains("LoaderCombo")) continue;

                    bool allString = true;
                    foreach (TypeSig parameter in method.MethodSig.Params)
                        if (parameter.FullName != "System.String") { allString = false; break; }
                    if (!allString) continue;

                    if (!HasQwqThrow(method.Body.Instructions)) continue;

                    plan.Modpack3Token = (int)method.MDToken.Raw;
                    plan.Modpack3Name = method.FullName;
                    FindMcFolderGetter(method, plan);
                    FindInstallers(type, method, plan);
                    return;
                }
            }
        }

        private static bool HasQwqThrow(IList<Instruction> ins)
        {
            for (int i = 2; i < ins.Count; i++)
            {
                if (ins[i].OpCode.Code != Code.Throw) continue;
                if (ins[i - 1].OpCode.Code != Code.Newobj) continue;
                if (ins[i - 2].OpCode.Code != Code.Ldstr) continue;
                string message = ins[i - 2].Operand as string;
                if (message != null && message.Contains("qwq")) return true;
            }
            return false;
        }

        // 从 ModpackInstall(3) 的 IL 里找 McFolderSelected 的 getter：
        // Format("{0}versions\{1}\", <getter>, InstanceName)
        private static void FindMcFolderGetter(MethodDef method, HookPlan plan)
        {
            IList<Instruction> ins = method.Body.Instructions;
            for (int i = 0; i + 3 < ins.Count; i++)
            {
                if (ins[i].OpCode.Code != Code.Ldstr) continue;
                string text = ins[i].Operand as string;
                if (text == null || !text.Contains("versions")) continue;

                for (int j = i + 1; j <= i + 5 && j < ins.Count; j++)
                {
                    if (ins[j].OpCode.Code != Code.Call) continue;
                    var callee = ins[j].Operand as MethodDef;
                    if (callee == null) continue;
                    if (callee.MethodSig.Params.Count != 0) continue;
                    if (callee.ReturnType.FullName != "System.String") continue;

                    plan.McFolderToken = (int)callee.MDToken.Raw;
                    plan.McFolderName = callee.FullName;
                    return;
                }
            }
        }

        // 从 ModpackInstall() 的 IL 里找 Hint 调用：Hint(qwq, Red, True)
        private static int FindHintToken(MethodDef method, out string hintName)
        {
            hintName = null;
            IList<Instruction> ins = method.Body.Instructions;
            for (int i = 0; i + 2 < ins.Count; i++)
            {
                if (ins[i].OpCode.Code != Code.Ldstr) continue;
                string text = ins[i].Operand as string;
                if (text == null || !text.Contains("qwq")) continue;

                for (int j = i + 1; j <= i + 6 && j < ins.Count; j++)
                {
                    if (ins[j].OpCode.Code != Code.Call) continue;
                    var callee = ins[j].Operand as MethodDef;
                    if (callee == null) continue;
                    if (callee.MethodSig.Params.Count != 3) continue;
                    if (callee.ReturnType.FullName != "System.Void") continue;

                    hintName = callee.FullName;
                    return (int)callee.MDToken.Raw;
                }
            }
            return 0;
        }

        // 按方法体里的格式标记识别各个 InstallPack*，不依赖方法名；
        // 标记缺失的（带启动器压缩包、普通压缩包）用参数个数兜底
        private static void FindInstallers(TypeDef modpack, MethodDef installMethod, HookPlan plan)
        {
            var candidates = new List<MethodDef>();
            foreach (MethodDef method in modpack.Methods)
            {
                if (method == installMethod) continue;
                if (!method.IsStatic || !method.HasBody) continue;
                if (!method.ReturnType.TypeName.Contains("LoaderCombo")) continue;
                if (method.Name.String == "ModpackInstallDispatch") continue;
                if (method.MethodSig.Params.Count > 5) continue;
                candidates.Add(method);
            }

            var assigned = new List<MethodDef>();

            foreach (MethodDef method in candidates)
            {
                bool mcbbs = false, mmc = false, modrinth = false, hmcl = false, manifest = false, launcher = false;
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code != Code.Ldstr) continue;
                    string text = instruction.Operand as string;
                    if (text == null) continue;

                    if (text == "mcbbs.packmeta") mcbbs = true;
                    else if (text == "mmc-pack.json") mmc = true;
                    else if (text == "modrinth.index.json") modrinth = true;
                    else if (text == "modpack.json") hmcl = true;
                    else if (text == "manifest.json") manifest = true;
                    else if (text == "modpack.zip" || text == "modpack.mrpack") launcher = true;
                }

                int? key = null;
                if (mcbbs) key = 3;
                else if (mmc) key = 2;
                else if (modrinth) key = 4;
                else if (hmcl) key = 1;
                else if (manifest) key = 0;
                else if (launcher) key = 9;

                if (key == null) continue;

                plan.InstallerTokens[key.Value] = (int)method.MDToken.Raw;
                assigned.Add(method);
            }

            // 兜底：3 参数的剩下那个是"带启动器的压缩包"，2 参数的是普通压缩包
            foreach (MethodDef method in candidates)
            {
                if (assigned.Contains(method)) continue;

                int argc = method.MethodSig.Params.Count;
                if (argc == 3 && !plan.InstallerTokens.ContainsKey(9))
                {
                    plan.InstallerTokens[9] = (int)method.MDToken.Raw;
                    assigned.Add(method);
                }
                else if (argc == 2 && !plan.InstallerTokens.ContainsKey(-1))
                {
                    plan.InstallerTokens[-1] = (int)method.MDToken.Raw;
                    assigned.Add(method);
                }
            }
        }

        private static void FindModpackDialog(ModuleDefMD mod, HookPlan plan)
        {
            // 锚点：0 参数、返回 void、包含 qwq，并且 qwq 后面确实跟了一个 3 参数的 Hint 调用
            // （PCL 里另有一个 0 参数、含 qwq 的 lambda 会直接 throw，不能选它）
            foreach (TypeDef type in mod.GetTypes())
            {
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    if (method.MethodSig.Params.Count != 0) continue;
                    if (method.MethodSig.RetType.FullName != "System.Void") continue;

                    string hintName;
                    int hintToken = FindHintToken(method, out hintName);
                    if (hintToken == 0) continue;

                    plan.Modpack0Token = (int)method.MDToken.Raw;
                    plan.Modpack0Name = method.FullName;
                    plan.HintToken = hintToken;
                    plan.HintName = hintName;
                    return;
                }
            }
        }

        private static void FindUiTargets(ModuleDefMD mod, HookPlan plan)
        {
            plan.DownloadLeftCtorToken = FindMethodToken(mod, "PCL.PageDownloadLeft", ".ctor", 0);
            plan.SelectRightUiToken = FindMethodToken(mod, "PCL.PageSelectRight", "McInstanceListUI", 1);
            plan.LaunchLeftRefreshToken = FindMethodToken(mod, "PCL.PageLaunchLeft", "RefreshButtonsUI", 0);
            plan.ResourcePageTypeToken = FindMethodToken(mod, "PCL.PageResource", "set_PageType", 1);
            plan.ModDisabledLoadedToken = FindMethodToken(mod, "PCL.PageInstanceModDisabled", "BtnDownload_Loaded", 0);
            plan.SelectLeftUiToken = FindMethodToken(mod, "PCL.PageSelectLeft", "McFolderListUI", 0);

            MethodDef launch = FindMethodByToken(mod, plan.LaunchLeftRefreshToken);
            if (launch != null) plan.LaunchPattern = HasLaunchPattern(launch);

            MethodDef selectLeft = FindMethodByToken(mod, plan.SelectLeftUiToken);
            if (selectLeft != null)
            {
                bool hasAdd;
                plan.SelectLeftPattern = HasSelectLeftPattern(selectLeft, out hasAdd);
                plan.SelectLeftHasAdd = hasAdd;
            }
        }

        private static MethodDef FindMethodByToken(ModuleDefMD mod, int token)
        {
            if (token == 0) return null;
            foreach (TypeDef type in mod.GetTypes())
                foreach (MethodDef method in type.Methods)
                    if ((int)method.MDToken.Raw == token) return method;
            return null;
        }

        private static bool HasLaunchPattern(MethodDef method)
        {
            if (!method.HasBody) return false;
            IList<Instruction> ins = method.Body.Instructions;
            for (int i = 0; i + 3 < ins.Count; i++)
            {
                IMethod call = ins[i].Operand as IMethod;
                if (call == null || call.Name.String != "get_McInstanceSelected") continue;
                if (ins[i + 1].OpCode.Code != Code.Brtrue_S) continue;
                if (ins[i + 2].OpCode.Code != Code.Ldc_I4_1) continue;
                if (ins[i + 3].OpCode.Code != Code.Stloc_0) continue;
                return true;
            }
            return false;
        }

        private static bool HasSelectLeftPattern(MethodDef method, out bool hasAdd)
        {
            hasAdd = false;
            if (!method.HasBody) return false;
            IList<Instruction> ins = method.Body.Instructions;
            bool pattern = false;

            for (int i = 0; i + 2 < ins.Count; i++)
            {
                if (ins[i].OpCode.Code != Code.Stloc_1) continue;
                if (ins[i + 1].OpCode.Code != Code.Ldloc_1) continue;
                if (ins[i + 2].OpCode.Code != Code.Ldsfld) continue;

                IField field = ins[i + 2].Operand as IField;
                if (field == null || !field.Name.String.Contains("IR4-8")) continue;

                pattern = true;
                break;
            }

            for (int i = 1; i < ins.Count; i++)
            {
                if (ins[i].OpCode.Code != Code.Callvirt) continue;
                IMethod call = ins[i].Operand as IMethod;
                if (call == null || call.Name.String != "Add") continue;
                if (call.DeclaringType == null || call.DeclaringType.Name.String != "UIElementCollection") continue;
                if (ins[i - 1].OpCode.Code != Code.Ldloc_1) continue;

                for (int j = i - 2; j >= 0 && j >= i - 5; j--)
                {
                    IMethod previous = ins[j].Operand as IMethod;
                    if (previous != null && previous.Name.String == "get_Children") { hasAdd = true; break; }
                }
                if (hasAdd) break;
            }

            return pattern;
        }

        private static int FindMethodToken(ModuleDefMD mod, string typeName, string methodName, int paramCount)
        {
            TypeDef type = mod.Find(typeName, false);
            if (type == null) return 0;

            foreach (MethodDef method in type.Methods)
            {
                if (method.Name.String != methodName) continue;
                if (method.MethodSig.Params.Count != paramCount) continue;
                return (int)method.MDToken.Raw;
            }
            return 0;
        }

        private int UiCount()
        {
            int count = 0;
            if (DownloadLeftCtorToken != 0) count++;
            if (SelectRightUiToken != 0) count++;
            if (LaunchLeftRefreshToken != 0) count++;
            if (ResourcePageTypeToken != 0) count++;
            if (ModDisabledLoadedToken != 0) count++;
            if (SelectLeftUiToken != 0) count++;
            return count;
        }

        private string Describe()
        {
            var builder = new StringBuilder();
            builder.AppendLine("plan:");
            builder.AppendLine("  loader-init:  " + Describe(LoaderInitName, LoaderInitToken));
            builder.AppendLine("  visibility:   " + Describe(VisibilityName, VisibilityToken));
            builder.AppendLine("  modpack(3):   " + Describe(Modpack3Name, Modpack3Token));
            builder.AppendLine("  modpack(0):   " + Describe(Modpack0Name, Modpack0Token));
            builder.AppendLine("  dl-loader:    " + Describe(DlLoaderName, DlLoaderToken));
            builder.AppendLine("  mc-folder:    " + Describe(McFolderName, McFolderToken));
            builder.AppendLine("  hint:         " + Describe(HintName, HintToken));
            builder.AppendLine("  ui-targets:   " + UiCount() + "/6");
            builder.AppendLine("  ui-transpilers: launch=" + (LaunchPattern ? "pattern" : "MISS") +
                               " select-left=" + (SelectLeftPattern ? (SelectLeftHasAdd ? "skip(existing)" : "inject") : "MISS"));
            if (Notes.Length > 0) builder.AppendLine("  notes:        " + Notes);
            return builder.ToString();
        }

        private static string Describe(string name, int token)
        {
            if (token == 0) return "NOT FOUND";
            return name + " (token 0x" + token.ToString("X8") + ")";
        }
    }
}
