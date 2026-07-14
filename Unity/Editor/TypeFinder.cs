using System;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// 通过 Unity TypeCache 按程序集限定名、完整名或短名解析指定基类的类型。
    /// </summary>
    internal static class TypeFinder
    {
        public static Type Find(string name, Type assignableTo)
        {
            return Find(name, assignableTo, out _);
        }

        public static Type Find(string name, Type assignableTo, out bool ambiguous)
        {
            ambiguous = false;
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            if (assignableTo == null)
            {
                throw new ArgumentNullException(nameof(assignableTo));
            }

            // Assembly-qualified names identify one type explicitly and do not need a cache scan.
            var qualified = ResolveAssemblyQualifiedName(name);
            if (qualified != null && assignableTo.IsAssignableFrom(qualified))
            {
                return qualified;
            }

            // A full name always takes precedence over a short-name match.
            var match = FindUnique(name, assignableTo, fullName: true, out ambiguous);
            if (match != null || ambiguous)
            {
                return match;
            }
            return FindUnique(name, assignableTo, fullName: false, out ambiguous);
        }

        private static Type ResolveAssemblyQualifiedName(string name)
        {
            if (name.IndexOf(',') < 0)
            {
                return null;
            }

            try
            {
                return Type.GetType(name, throwOnError: false);
            }
            catch (Exception)
            {
                // Invalid or unavailable assembly-qualified names are simply not matches.
                return null;
            }
        }

        private static Type FindUnique(
            string name,
            Type assignableTo,
            bool fullName,
            out bool ambiguous)
        {
            ambiguous = false;
            Type match = NameMatches(assignableTo, name, fullName) ? assignableTo : null;
            foreach (var candidate in TypeCache.GetTypesDerivedFrom(assignableTo))
            {
                if (!NameMatches(candidate, name, fullName))
                {
                    continue;
                }
                if (match != null && match != candidate)
                {
                    ambiguous = true;
                    return null;
                }
                match = candidate;
            }
            return match;
        }

        private static bool NameMatches(Type candidate, string name, bool fullName)
        {
            return string.Equals(fullName ? candidate.FullName : candidate.Name,
                name, StringComparison.Ordinal);
        }
    }
}
