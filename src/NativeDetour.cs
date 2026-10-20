using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PclHook
{
    /// <summary>
    /// 直接把 JIT 后的方法入口改成跳转。
    /// 用于 Harmony 无法复制方法体的目标（ModpackInstall）。
    /// </summary>
    internal static class NativeDetour
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

        [DllImport("kernel32.dll")]
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        internal static void Install(MethodInfo target, MethodInfo replacement)
        {
            if (!SignatureMatches(target, replacement))
                throw new Exception("detour signature mismatch: " + target + " vs " + replacement);

            RuntimeHelpers.PrepareMethod(target.MethodHandle);
            RuntimeHelpers.PrepareMethod(replacement.MethodHandle);

            IntPtr source = target.MethodHandle.GetFunctionPointer();
            IntPtr destination = replacement.MethodHandle.GetFunctionPointer();

            byte[] jump;
            if (IntPtr.Size == 8)
            {
                // jmp qword ptr [rip+0]; <addr>
                jump = new byte[14];
                jump[0] = 0xFF;
                jump[1] = 0x25;
                BitConverter.GetBytes(destination.ToInt64()).CopyTo(jump, 6);
            }
            else
            {
                // push <addr>; ret
                jump = new byte[6];
                jump[0] = 0x68;
                BitConverter.GetBytes(destination.ToInt32()).CopyTo(jump, 1);
                jump[5] = 0xC3;
            }

            uint oldProtect;
            if (!VirtualProtect(source, (UIntPtr)jump.Length, 0x40, out oldProtect))
                throw new Exception("VirtualProtect failed: " + Marshal.GetLastWin32Error());

            try
            {
                Marshal.Copy(jump, 0, source, jump.Length);
            }
            finally
            {
                uint ignored;
                VirtualProtect(source, (UIntPtr)jump.Length, oldProtect, out ignored);
            }

            if (!FlushInstructionCache(GetCurrentProcess(), source, (UIntPtr)jump.Length))
                DomainManager.Log("FlushInstructionCache failed: " + Marshal.GetLastWin32Error());

            DomainManager.Log("  detour " + source.ToString("X") + " -> " + destination.ToString("X"));
        }

        private static bool SignatureMatches(MethodInfo target, MethodInfo replacement)
        {
            if (target.IsStatic != replacement.IsStatic) return false;

            ParameterInfo[] a = target.GetParameters();
            ParameterInfo[] b = replacement.GetParameters();
            if (a.Length != b.Length) return false;

            for (int i = 0; i < a.Length; i++)
                if (a[i].ParameterType != b[i].ParameterType) return false;

            // 返回类型按引用兼容（目标返回 LoaderCombo，替换方法返回 object）
            return !target.ReturnType.IsValueType && !replacement.ReturnType.IsValueType;
        }
    }
}
