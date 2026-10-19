#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace d4rkpl4y3r.ReflectionInspector
{
    public class AssemblyEntry
    {
        public Assembly Assembly;
        public string Name;
    }

    public class TypeEntry
    {
        public Type Type;
        public string Name;
        public string FullName;
        public string Kind; // Class / Struct / Enum / Interface
        public string AssemblyName;
    }

    /// <summary>
    /// Lazy, per-assembly index of the types in the editor's AppDomain.
    /// Built once on first use; the set of loaded assemblies is considered
    /// stable for the editor's lifetime.
    /// </summary>
    public static class TypeIndex
    {
        static List<AssemblyEntry> s_assemblies;
        static List<TypeEntry> s_all;
        static readonly Dictionary<Assembly, List<TypeEntry>> s_cache = new();

        public static List<AssemblyEntry> GetAssemblies()
        {
            if (s_assemblies == null)
            {
                s_assemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => new AssemblyEntry
                    {
                        Assembly = a,
                        Name = a.GetName().Name ?? "<unknown>"
                    })
                    .OrderBy(e => e.Name, StringComparer.Ordinal)
                    .ToList();
            }
            return s_assemblies;
        }

        public static List<TypeEntry> GetTypeEntries(Assembly assembly)
        {
            if (s_cache.TryGetValue(assembly, out var cached))
                return cached;

            var list = new List<TypeEntry>();
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                // SDK / plugin DLLs occasionally have un-loadable generic deps.
                types = e.Types.Where(t => t != null).ToArray();
            }

            foreach (var type in types)
            {
                if (type == null || type.IsArray)
                    continue;
                var kind = Classify(type);
                if (kind == null)
                    continue;
                list.Add(new TypeEntry
                {
                    Type = type,
                    Name = type.Name,
                    FullName = type.FullName ?? type.Name,
                    Kind = kind,
                    AssemblyName = assembly.GetName().Name ?? "<unknown>"
                });
            }
            list.Sort((a, b) => string.CompareOrdinal(a.FullName ?? a.Name, b.FullName ?? b.Name));
            s_cache[assembly] = list;
            return list;
        }

        /// <summary>All type entries of all assemblies (lazily built, cached).</summary>
        public static List<TypeEntry> GetAllTypes()
        {
            s_all ??= GetAssemblies().SelectMany(e => GetTypeEntries(e.Assembly)).ToList();
            return s_all;
        }

        /// <returns>"Class" / "Struct" / "Enum" / "Interface", or null for skip-pref (generics defs are Class, delegates are Class, etc.)</returns>
        public static string Classify(Type type)
        {
            if (type.IsEnum) return "Enum";
            if (type.IsValueType) return "Struct";
            if (type.IsClass) return "Class";
            if (type.IsInterface) return "Interface";
            return null;
        }

        /// <summary>true when the type is publicly reachable (it and every declaring type are public).</summary>
        public static bool IsVisible(Type type)
        {
            var t = type;
            while (t != null)
            {
                if (!t.IsPublic)
                    return false;
                t = t.DeclaringType;
            }
            return true;
        }

        /// <summary>
        /// Fuzzy type resolution, most specific first:
        /// exact full name → exact simple name → full-name suffix → case-insensitive substring.
        /// </summary>
        public static List<Type> ResolveTypes(string query)
        {
            if (string.IsNullOrEmpty(query))
                return new List<Type>();

            var all = GetAllTypes();

            var byFullName = all.Where(e => e.FullName == query).Select(e => e.Type).ToList();
            if (byFullName.Count > 0)
                return byFullName;

            var bySimpleName = all.Where(e => e.Name == query).Select(e => e.Type).ToList();
            if (bySimpleName.Count > 0)
                return bySimpleName;

            var bySuffix = all
                .Where(e => (e.FullName ?? "").EndsWith(query, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Type).ToList();
            if (bySuffix.Count > 0)
                return bySuffix;

            return all
                .Where(e => (e.FullName ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(e => e.Type).ToList();
        }
    }
}
#endif
