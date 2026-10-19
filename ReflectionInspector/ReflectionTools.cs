#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace d4rkpl4y3r.ReflectionInspector
{
    /// <summary>
    /// Reflection MCP tool implementations. Each method receives parsed
    /// arguments and returns plain-text content, which
    /// <see cref="ReflectionInspectorMCP"/> wraps in the MCP result envelope.
    /// Errors are reported by throwing an exception with a user-facing message.
    /// </summary>
    public static class ReflectionTools
    {
        public const int DefaultPageSize = 50;

        // ============================ list_assemblies ============================

        public static string ListAssemblies()
        {
            var names = TypeIndex.GetAssemblies().Select(e => e.Name).ToList();
            var sb = new StringBuilder();
            sb.Append(names.Count).Append(" loaded assemblies:\n");
            foreach (var n in names)
                sb.Append(' ').Append(n).Append('\n');
            return sb.ToString().TrimEnd();
        }

        // ============================ search_types ============================

        public static string SearchTypes(string query, string kind, string assembly,
            string derivedFrom, bool includePrivate, string range)
        {
            if (string.IsNullOrEmpty(query))
                throw new ArgumentException("query is required (regex against the type name; full name if it contains '.')");

            Regex queryRx;
            try
            {
                queryRx = new Regex(query, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException e)
            {
                throw new ArgumentException($"Invalid regex for query: {e.Message}");
            }

            var (start, end) = ParseRange(range);

            Type baseType = null;
            if (!string.IsNullOrEmpty(derivedFrom))
            {
                baseType = ResolveSingleType(derivedFrom);
            }

            var (matchFullName, matchSimple) = ClassifyQuery(query);

            var hits = new List<TypeEntry>();
            foreach (var e in TypeIndex.GetAllTypes())
            {
                if (kind != null && kind != "" && e.Kind != kind)
                    continue;
                if (assembly != null && assembly != ""
                    && (e.AssemblyName ?? "").IndexOf(assembly, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!includePrivate && !TypeIndex.IsVisible(e.Type))
                    continue;
                if (baseType != null && !baseType.IsAssignableFrom(e.Type))
                    continue;
                if ((matchFullName ? queryRx.IsMatch(e.FullName ?? "") : queryRx.IsMatch(e.Name)))
                    hits.Add(e);
            }

            hits.Sort((a, b) => string.CompareOrdinal(a.FullName ?? a.Name, b.FullName ?? b.Name));

            var sb = new StringBuilder();
            AppendPagedSection(sb, "Types", hits, start, end,
                e => $"[{e.Kind}] {e.FullName} ({e.AssemblyName}) : {FormatType(e.Type.BaseType)}");
            if (hits.Count == 0)
                sb.Append("  No types match.\n");
            return sb.ToString().TrimEnd();
        }

        // ============================ inspect_type ============================

        public static string InspectType(string typeName, bool includePrivate, bool includeInherited,
            List<string> members, string memberFilter, string range)
        {
            if (string.IsNullOrEmpty(typeName))
                throw new ArgumentException("typeName is required");

            var candidates = TypeIndex.ResolveTypes(typeName);
            if (candidates.Count == 0)
                throw new ArgumentException($"No type named '{typeName}' found in the loaded assemblies");
            if (candidates.Count > 1)
                throw new ArgumentException($"Type '{typeName}' is ambiguous, candidates (use the full name):\n  "
                    + string.Join("\n  ", candidates.Select(t => t.FullName).Take(10)));

            var type = candidates[0];
            var (start, end) = ParseRange(range);

            var sb = new StringBuilder();

            // --- header ---
            sb.Append("Type: ").Append(type.FullName)
              .Append(" (").Append(TypeIndex.Classify(type)).Append(")")
              .Append(" — ").Append(type.Assembly.GetName().Name ?? "<unknown>").Append('\n');
            if (type.BaseType != null)
                sb.Append("Base: ").Append(FormatType(type.BaseType)).Append('\n');
            sb.Append("Inheritance: ").Append(FormatInheritanceChain(type)).Append('\n');
            var allIfaces = type.GetInterfaces();
            var ifaces = allIfaces
                .Where(t => !allIfaces.Any(i => i.IsAssignableFrom(t))) // direct only
                .Select(FormatType).OrderBy(n => n, StringComparer.Ordinal);
            if (ifaces.Any())
                sb.Append("Implements: ").Append(string.Join(", ", ifaces)).Append('\n');
            var typeAttrs = FormatAttributes(type.GetCustomAttributesData());
            if (typeAttrs != null)
                sb.Append("Attributes: ").Append(typeAttrs).Append('\n');
            sb.Append("\n");

            Predicate<string> filter = string.IsNullOrEmpty(memberFilter)
                ? null
                : n => n.IndexOf(memberFilter, StringComparison.OrdinalIgnoreCase) >= 0;

            bool want(string m) => members == null || members.Count == 0 || members.Contains(m);

            // --- enums: always full, never paged ---
            if (type.IsEnum)
            {
                // Avoid value__ (its visibility is quirky on Mono); use the boxed values directly.
                var values = (Array)Enum.GetValues(type);
                sb.Append("Values (").Append(values.Length).Append("):\n");
                foreach (var v in values)
                    sb.Append(' ').Append(Enum.GetName(type, v)).Append(" = ")
                      .Append(((IConvertible)v).ToInt64(null)).Append('\n');
                if (type.IsDefined(typeof(FlagsAttribute)))
                    sb.Append("[Flags] values act as bitmasks\n");
                return sb.ToString().TrimEnd();
            }

            // The runtime ignores Public/FlattenHierarchy flags reliably, so fetch the
            // full superset and filter own-vs-inherited and visibility ourselves.
            var memberFlags = BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic;
            bool own(Type d) => d == type;
            bool ownOrInherited(Type d) => includeInherited || own(d);

            // --- fields ---
            if (want("fields"))
            {
                var all = type.GetFields(memberFlags)
                    .Where(f => ownOrInherited(f.DeclaringType))
                    .Where(f => f.IsPublic || includePrivate)
                    .Where(f => filter == null || filter(f.Name))
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
                    .ToList();
                AppendPagedSection(sb, "Fields", all, start, end, f =>
                {
                    var s = FormatType(f.FieldType) + " " + f.Name;
                    var flags = new List<string>();
                    if (f.IsStatic) flags.Add("static");
                    if (f.IsInitOnly) flags.Add("readonly");
                    if (f.IsLiteral)
                    {
                        flags.Add("const");
                        try
                        {
                            var v = f.GetRawConstantValue();
                            s += " = " + (v is string str ? "\"" + str + "\"" : v?.ToString() ?? "null");
                        }
                        catch { }
                    }
                    if (flags.Count > 0) s = string.Join(" ", flags) + " " + s;
                    if (!f.IsPublic) s += " [" + VisibilityOf(f) + "]";
                    return s;
                });
            }

            // --- properties ---
            if (want("properties"))
            {
                var all = type.GetProperties(memberFlags)
                    .Where(p => (p.GetMethod ?? p.SetMethod) != null)
                    .Where(p => ownOrInherited(p.DeclaringType))
                    .Where(p => (p.GetMethod ?? p.SetMethod).IsPublic || includePrivate)
                    .Where(p => filter == null || filter(p.Name))
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToList();
                AppendPagedSection(sb, "Properties", all, start, end, p =>
                {
                    var accessors = new List<string>();
                    if (p.CanRead) accessors.Add("get");
                    if (p.CanWrite) accessors.Add("set");
                    var vis = VisibilityOf(p.GetMethod ?? p.SetMethod);
                    return $"{vis} {FormatType(p.PropertyType)} {p.Name} {{ {string.Join("; ", accessors)}; }}";
                });
            }

            // --- methods (non-ctor) ---
            if (want("methods"))
            {
                var all = type.GetMethods(memberFlags)
                    .Where(m => !m.IsConstructor)
                    .Where(m => m.Name != "Finalize" && !m.IsSpecialName /* hide accessors/ops */)
                    .Where(m => ownOrInherited(m.DeclaringType))
                    .Where(m => m.IsPublic || includePrivate)
                    .Where(m => filter == null || filter(m.Name))
                    .OrderBy(m => m.Name, StringComparer.Ordinal)
                    .ThenBy(m => m.GetParameters().Length)
                    .ToList();
                AppendPagedSection(sb, "Methods", all, start, end, m => MethodSignature(m));
            }

            // --- constructors ---
            if (want("constructors"))
            {
                var all = type.GetConstructors(BindingFlags.Instance | BindingFlags.Static
                        | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.IsPublic || includePrivate)
                    .OrderBy(m => m.GetParameters().Length)
                    .ToList();
                AppendPagedSection(sb, "Constructors", all, start, end, m => MethodSignature(m));
            }

            return sb.ToString().TrimEnd();
        }

        // ============================ shared helpers ============================

        public static (int start, int end) ParseRange(string range)
        {
            range = string.IsNullOrWhiteSpace(range)
                ? $"1-{DefaultPageSize}"
                : range.Trim();
            int start = 1, end = DefaultPageSize;
            try
            {
                var parts = range.Split('-');
                start = parts.Length > 0 && parts[0] != "" ? int.Parse(parts[0]) : 1;
                end = parts.Length > 1 && parts[1] != "" ? int.Parse(parts[1]) : start;
            }
            catch (Exception)
            {
                throw new ArgumentException($"Invalid range '{range}', expected a number or 'start-end' (default 1-{DefaultPageSize})");
            }
            if (start < 1) start = 1;
            if (end < start) end = start;
            return (start, end);
        }

        static void AppendNumbered(StringBuilder sb, int n, string text)
        {
            sb.Append(n.ToString().PadLeft(4)).Append("  ").Append(text).Append('\n');
        }

        /// <summary>
        /// Renders a numbered, paged section:
        /// "Title (N total, showing a-b):" followed by up to (end-start+1) lines and,
        /// when items remain, a hint with the range to request next.
        /// </summary>
        static void AppendPagedSection<T>(StringBuilder sb, string title, List<T> all, int start, int end,
            Func<T, string> lineFor)
        {
            var total = all.Count;
            if (total == 0)
            {
                sb.Append(title).Append(": 0\n\n");
                return;
            }
            var first = Math.Min(Math.Max(start, 1), total);
            var last = Math.Min(end, total);
            var count = Math.Max(0, last - first + 1);
            var isPaged = start > 1 || end < total;

            sb.Append(title)
              .Append(" (").Append(total).Append(total == 1 ? "" : " total");
            if (isPaged)
                sb.Append(", showing ").Append(first).Append('-').Append(last);
            sb.Append("):\n");

            for (var i = 0; i < count; i++)
            {
                var n = first + i;
                AppendNumbered(sb, n, lineFor(all[n - 1]));
            }

            if (last < total)
                sb.Append("  [... ").Append(total - last)
                  .Append(" more — request range=").Append(last + 1).Append('-').Append(total).Append("]\n");
            sb.Append('\n');
        }

        static Type ResolveSingleType(string name)
        {
            var resolved = TypeIndex.ResolveTypes(name);
            if (resolved.Count == 0)
                throw new ArgumentException($"No type named '{name}' found");
            if (resolved.Count > 1)
                throw new ArgumentException($"Type '{name}' is ambiguous: "
                    + string.Join(", ", resolved.Select(t => t.FullName).Take(10)));
            return resolved[0];
        }

        /// <summary>Matches against the full name when the query contains '.', otherwise against the simple name.</summary>
        static (bool matchFullName, bool matchSimple) ClassifyQuery(string query)
        {
            return (query.Contains('.'), !query.Contains('.'));
        }

        static string FormatInheritanceChain(Type type)
        {
            var chain = new List<string>();
            var t = type;
            var depth = 0;
            while (t != null && depth < 8)
            {
                chain.Insert(0, FormatType(t));
                t = t.BaseType;
                depth++;
            }
            return string.Join(" -> ", chain);
        }

        static string MethodSignature(MethodBase m)
        {
            var flags = new List<string>();
            if (m.IsStatic) flags.Add("static");
            if (m.IsVirtual && !m.IsAbstract) flags.Add("virtual");
            if (m.IsAbstract) flags.Add("abstract");

            var sb = new StringBuilder();
            if (flags.Count > 0)
                sb.Append(string.Join(" ", flags)).Append(' ');
            if (m is MethodInfo mi)
                sb.Append(FormatType(mi.ReturnType)).Append(' ');
            sb.Append(m.Name).Append('(')
              .Append(string.Join(", ", m.GetParameters().Select(p =>
                  FormatType(p.ParameterType) + " " + p.Name))).Append(')');
            if (!m.IsPublic)
                sb.Append(" [").Append(VisibilityOf(m)).Append(']');
            return sb.ToString();
        }

        static string VisibilityOf(MethodBase m)
        {
            if (m.IsPublic) return "public";
            if (m.IsFamily) return "protected";
            if (m.IsFamilyOrAssembly || m.IsAssembly) return "internal";
            return "private";
        }

        static string VisibilityOf(FieldInfo f)
        {
            if (f.IsPublic) return "public";
            if (f.IsFamily) return "protected";
            if (f.IsFamilyOrAssembly || f.IsAssembly) return "internal";
            return "private";
        }

        /// <summary>
        /// Unambiguous type name: namespace-qualified for non-generics (Nested types use '+'),
        /// anglebracket form for generics, ref and array suffixes included.
        /// </summary>
        public static string FormatType(Type t)
        {
            if (t == null) return "void";
            if (t.IsByRef) return FormatType(t.GetElementType()) + " ref";
            if (t.IsArray)
            {
                int rank = t.GetArrayRank();
                return FormatType(t.GetElementType()) + "[" + new string(',', rank - 1) + "]";
            }
            var @namespace = t.Namespace;
            if (!t.IsGenericType)
                return (@namespace != null ? @namespace + "." : "") + t.Name;
            // Generic: Name is "List`1" (or "Outer+Inner`1" when nested) — strip arity, prefix namespace.
            var name = t.Name.Substring(0, t.Name.IndexOf('`'));
            if (@namespace != null) name = @namespace + "." + name;
            return name + "<" + string.Join(", ", t.GetGenericArguments().Select(FormatType)) + ">";
        }

        /// <summary>Custom attribute summary; skips pure-System attributes. Null when nothing to show.</summary>
        static string FormatAttributes(IEnumerable<CustomAttributeData> attrs)
        {
            var parts = new List<string>();
            foreach (var a in attrs)
            {
                var declaring = a.Constructor.DeclaringType;
                var ns = declaring?.Namespace ?? "";
                if (ns == "System" || ns.StartsWith("System."))
                    continue;
                var tname = declaring.Name;
                if (tname.EndsWith("`1"))
                    tname = tname.Substring(0, tname.LastIndexOf('`', 0) + 1) + "<>";
                if (tname.EndsWith("Attribute"))
                    tname = tname.Substring(0, tname.Length - "Attribute".Length);
                if (a.ConstructorArguments.Count > 0)
                    tname += "(" + string.Join(", ", a.ConstructorArguments.Select(FormatArg)) + ")";
                parts.Add(tname);
            }
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }

        static string FormatArg(CustomAttributeTypedArgument arg)
        {
            switch (arg.Value)
            {
                case null: return "null";
                case string s: return "\"" + s + "\"";
                case Type t: return t.Name;
                default: return arg.Value.ToString();
            }
        }
    }
}
#endif
