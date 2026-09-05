#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using d4rkpl4y3r.AV3ToggleUtil.Util;

public class ReflectionInspectorEditor : EditorWindow
{
    private struct AssemblyEntry
    {
        public Assembly Assembly;
        public string Name;
    }

    private class TypeEntry
    {
        public Type Type;
        public string Name;
        public string FullName;
        public string Kind;
        public List<string> AssemblyNames = new();
    }

    private TextFilter assemblyFilter = new() { SmallButtons = true };
    private TextFilter typeFilter = new()
    {
        SmallButtons = true,
        IsRegex = true,
        // hide compiler-generated types (<), nested types (+) and generic types (`)
        Text = "^[^+`<]+$"
    };
    private SplitterState splitter = new();
    private List<AssemblyEntry> cachedAssemblies;
    private Dictionary<Assembly, List<TypeEntry>> typeCache = new();
    private Vector2 leftScrollPos;
    private Vector2 rightScrollPos;
    private bool enumFoldout = true;
    private bool structFoldout = true;
    private bool classFoldout = true;
    private bool showFullName = false;
    private string baseTypeFilter = "";
    private string baseTypeResolveKey = null;
    private Type resolvedBaseType;

    [MenuItem("Tools/d4rkpl4y3r/Reflection Inspector")]
    public static void OpenWindow()
    {
        ReflectionInspectorEditor window = GetWindow<ReflectionInspectorEditor>();
        window.titleContent = new GUIContent("Reflection Inspector");
        window.minSize = new Vector2(640f, 320f);
        window.Show();
    }

    private void OnEnable()
    {
        titleContent = new GUIContent("Reflection Inspector");
        minSize = new Vector2(640f, 320f);
        RefreshAssemblies();
    }

    private void OnDisable()
    {
        cachedAssemblies = null;
        typeCache.Clear();
    }

    private void RefreshAssemblies()
    {
        cachedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => new AssemblyEntry
            {
                Assembly = a,
                Name = a.GetName().Name ?? "<unknown>"
            })
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        typeCache.Clear();
        baseTypeResolveKey = null;
        resolvedBaseType = null;
    }

    private List<TypeEntry> GetTypeEntries(Assembly assembly)
    {
        if (typeCache.TryGetValue(assembly, out var cached))
            return cached;

        var entries = new List<TypeEntry>();
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.Where(t => t != null).ToArray();
        }

        foreach (var type in types)
        {
            if (type == null || type.IsArray)
                continue;
            string kind;
            if (type.IsEnum)
                kind = "Enum";
            else if (type.IsValueType)
                kind = "Struct";
            else if (type.IsClass)
                kind = "Class";
            else if (type.IsInterface)
                kind = "Interface";
            else
                continue;
            var entry = new TypeEntry
            {
                Type = type,
                Name = type.Name,
                FullName = type.FullName ?? type.Name,
                Kind = kind
            };
            entry.AssemblyNames.Add(assembly.GetName().Name);
            entries.Add(entry);
        }
        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        typeCache[assembly] = entries;
        return entries;
    }

    private void OnGUI()
    {
        if (cachedAssemblies == null)
            return;

        var filteredAssemblies = cachedAssemblies
            .Where(e => assemblyFilter.Matches(e.Name))
            .ToList();

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(splitter.leftPanelWidth)))
            {
                DrawAssemblyPanel(filteredAssemblies);
            }

            splitter.DrawSplitter(this, 160f, 520f);

            using (new EditorGUILayout.VerticalScope())
            {
                DrawTypePanel(filteredAssemblies);
            }
        }
    }

    private void DrawAssemblyPanel(List<AssemblyEntry> filteredAssemblies)
    {
        assemblyFilter.DrawGUI();

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label($"{filteredAssemblies.Count} / {cachedAssemblies.Count} assemblies");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("Refresh", "Reload the list of loaded assemblies"),
                GUILayout.ExpandWidth(false)))
            {
                RefreshAssemblies();
            }
        }

        using var scroll = new EditorGUILayout.ScrollViewScope(leftScrollPos,
            GUILayout.ExpandHeight(true));
        leftScrollPos = scroll.scrollPosition;

        foreach (var entry in filteredAssemblies)
        {
            EditorGUILayout.SelectableLabel(entry.Name, EditorStyles.label, GUILayout.Height(18f));
        }

        if (filteredAssemblies.Count == 0)
            EditorGUILayout.HelpBox("No assemblies match the filter.", MessageType.None);
    }

    private const int maxEntriesPerBox = 500;

    private void DrawTypePanel(List<AssemblyEntry> filteredAssemblies)
    {
        typeFilter.DrawGUI("Type Filter");

        ResolveBaseType();

        using (new EditorGUILayout.HorizontalScope())
        {
            baseTypeFilter = EditorGUILayout.TextField("Base Type", baseTypeFilter);

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(baseTypeFilter)))
            {
                if (GUILayout.Button("X", GUILayout.ExpandWidth(false)))
                {
                    baseTypeFilter = "";
                }
            }
        }

        if (!string.IsNullOrEmpty(baseTypeFilter) && resolvedBaseType == null)
        {
            var prevContentColor = GUI.contentColor;
            GUI.contentColor = new Color(1f, 1f, 0.3f);
            EditorGUILayout.LabelField($"No type named '{baseTypeFilter}' found in the loaded assemblies");
            GUI.contentColor = prevContentColor;
        }

        showFullName = GUILayout.Toggle(showFullName, "Full Name", GUI.skin.toggle);

        var deduplicated = new Dictionary<string, TypeEntry>();
        foreach (var assemblyEntry in filteredAssemblies)
        {
            foreach (var entry in GetTypeEntries(assemblyEntry.Assembly))
            {
                if (!deduplicated.TryGetValue(entry.FullName, out var existing))
                {
                    // fresh copy so we never mutate the per-assembly cache
                    existing = new TypeEntry
                    {
                        Type = entry.Type,
                        Name = entry.Name,
                        FullName = entry.FullName,
                        Kind = entry.Kind
                    };
                    existing.AssemblyNames.AddRange(entry.AssemblyNames);
                    deduplicated[entry.FullName] = existing;
                }
                else
                {
                    existing.AssemblyNames.AddRange(entry.AssemblyNames);
                }
            }
        }

        var enumEntries = new List<TypeEntry>();
        var structEntries = new List<TypeEntry>();
        var classEntries = new List<TypeEntry>();
        foreach (var entry in deduplicated.Values)
        {
            if (!string.IsNullOrEmpty(baseTypeFilter)
                && (resolvedBaseType == null || !resolvedBaseType.IsAssignableFrom(entry.Type)))
                continue;
            var filterTarget = showFullName ? entry.FullName : entry.Name;
            if (!typeFilter.Matches(filterTarget))
                continue;
            switch (entry.Kind)
            {
                case "Enum":
                    enumEntries.Add(entry);
                    break;
                case "Struct":
                    structEntries.Add(entry);
                    break;
                case "Class":
                    classEntries.Add(entry);
                    break;
                default:
                    break;
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(
                $"{enumEntries.Count + structEntries.Count + classEntries.Count} types " +
                $"({classEntries.Count} classes, {structEntries.Count} structs, {enumEntries.Count} enums)");
        }

        using var scroll = new EditorGUILayout.ScrollViewScope(rightScrollPos,
            GUILayout.ExpandHeight(true));
        rightScrollPos = scroll.scrollPosition;

        if (enumEntries.Count + structEntries.Count + classEntries.Count == 0)
        {
            EditorGUILayout.HelpBox("No types match the filter.", MessageType.None);
            return;
        }

        var typeComparer = showFullName
            ? Comparer<TypeEntry>.Create((a, b) => string.CompareOrdinal(a.FullName, b.FullName))
            : Comparer<TypeEntry>.Create((a, b) => string.CompareOrdinal(a.Name, b.Name));

        DrawTypeBox(ref enumFoldout, "Enums", enumEntries, typeComparer);
        DrawTypeBox(ref structFoldout, "Structs", structEntries, typeComparer);
        DrawTypeBox(ref classFoldout, "Classes", classEntries, typeComparer);
    }

    private void ResolveBaseType()
    {
        var resolveKey = baseTypeFilter;
        if (baseTypeResolveKey == resolveKey)
            return;
        baseTypeResolveKey = resolveKey;
        resolvedBaseType = null;
        if (string.IsNullOrEmpty(baseTypeFilter) || cachedAssemblies == null)
            return;
        foreach (var assemblyEntry in cachedAssemblies)
        {
            foreach (var entry in GetTypeEntries(assemblyEntry.Assembly))
            {
                if (string.Equals(entry.FullName, baseTypeFilter, StringComparison.OrdinalIgnoreCase))
                {
                    resolvedBaseType = entry.Type;
                    break;
                }
            }
            if (resolvedBaseType != null)
                break;
        }
    }

    private string FormatStringList(List<TypeEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("new()\n{");
        for (var i = 0; i < entries.Count; i++)
        {
            var name = showFullName ? entries[i].FullName : entries[i].Name;
            sb.Append($"\n    \"{name}\"");
            if (i < entries.Count - 1)
                sb.Append(',');
        }
        sb.Append("\n}");
        return sb.ToString();
    }

    private void DrawTypeBox(ref bool foldout, string title, List<TypeEntry> entries, Comparer<TypeEntry> comparer)
    {
        if (entries.Count == 0)
            return;

        entries.Sort(comparer);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                foldout = EditorGUILayout.Foldout(foldout, $"{title} ({entries.Count})", true, EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Copy All", "Copy all filtered names as a C# collection initializer for a List<string>: new() { ... }"),
                    GUILayout.ExpandWidth(false)))
                    GUIUtility.systemCopyBuffer = FormatStringList(entries);
            }

            if (!foldout)
                return;

            foreach (var entry in entries.Take(maxEntriesPerBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(ColumnGrid.InnerIndent);
                    var assemblyTooltip = string.Join("\n", entry.AssemblyNames.Distinct().OrderBy(x => x, StringComparer.Ordinal));
                    var countSuffix = entry.AssemblyNames.Distinct().Count() > 1
                        ? $" ({entry.AssemblyNames.Distinct().Count()})"
                        : "";
                    var displayName = (showFullName ? entry.FullName : entry.Name) + countSuffix;
                    GUILayout.Label(new GUIContent(displayName, assemblyTooltip + "\nClick to set as Base Type"),
                        EditorStyles.label, GUILayout.Height(18f));
                    if (AV3Helper.ClickableLastRect())
                        baseTypeFilter = entry.FullName;
                }
            }

            if (entries.Count > maxEntriesPerBox)
                EditorGUILayout.HelpBox($"Limited to {maxEntriesPerBox} entries.", MessageType.None);
        }
    }
}
#endif
