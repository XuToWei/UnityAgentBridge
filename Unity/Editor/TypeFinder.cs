using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AgentBridge
{
    /// <summary>
    /// 共享类型反射工具:跨所有已加载程序集安全枚举类型、按名+谓词查找类型。
    /// 统一 ReflectionTypeLoadException 容错。供 SceneObjectResolver(找 Component 类型)、
    /// AssetSupport(找 ScriptableObject 类型)复用。
    /// </summary>
    internal static class TypeFinder
    {
        /// <summary>枚举所有已加载程序集里的类型(GetTypes 失败时退化到可加载子集)。</summary>
        public static IEnumerable<Type> AllTypes()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }

                foreach (var t in types)
                {
                    yield return t;
                }
            }
        }

        /// <summary>
        /// 按类型名找满足 predicate 的类型:先全名(Type.GetType,再各程序集 asm.GetType),
        /// 最后短名扫描。三级均要求 predicate 通过。无命中返回 null。
        /// </summary>
        public static Type Find(string name, Func<Type, bool> predicate)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            var t = Type.GetType(name);
            if (t != null && predicate(t))
            {
                return t;
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(name);
                if (t != null && predicate(t))
                {
                    return t;
                }
            }

            foreach (var candidate in AllTypes())
            {
                if (candidate.Name == name && predicate(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }
    }
}
