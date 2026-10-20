using System;
using System.Reflection;

namespace PclHook
{
    /// <summary>对 PCL 内部成员的反射访问。</summary>
    internal static class Refl
    {
        internal const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        internal static Type Pcl(string name)
        {
            Type type = Type.GetType(name + ", Plain Craft Launcher 2");
            if (type == null) throw new Exception("type not found: " + name);
            return type;
        }

        internal static MethodInfo Method(Type type, string name, int paramCount)
        {
            foreach (MethodInfo method in type.GetMethods(All))
                if (method.Name == name && method.GetParameters().Length == paramCount) return method;
            throw new Exception("method not found: " + type.FullName + "." + name + "/" + paramCount);
        }

        internal static object Static(Type type, string name)
        {
            PropertyInfo property = type.GetProperty(name, All);
            if (property != null) return property.GetValue(null, null);

            FieldInfo field = type.GetField(name, All);
            if (field != null) return field.GetValue(null);

            throw new Exception("static member not found: " + type.FullName + "." + name);
        }

        /// <summary>调用 PCL 的 Hint(text, HintType, Show)。</summary>
        internal static void Hint(string text, bool red)
        {
            try
            {
                MethodInfo hint = PatchContext.Hint;
                if (hint == null) hint = Method(Pcl("PCL.ModMain"), "Hint", 3);
                Type hintType = hint.GetParameters()[1].ParameterType;
                object kind = Enum.Parse(hintType, red ? "Red" : "Info");
                hint.Invoke(null, new object[] { text, kind, true });
            }
            catch (Exception ex)
            {
                DomainManager.Log("Hint failed: " + ex.Message);
            }
        }
    }
}
