#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

    private struct TypeEntry
    {
        public Type Type;
        public string Name;
        public string AssemblyName;
        public string Kind;
    }

    private TextFilter assemblyFilter = new() { SmallButtons = true };
    private TextFilter typeFilter = new()
    {
        SmallButtons = true,
        IsRegex = true,
        Text = "^(?!<)" // hide compiler-generated types (e.g. <>c__DisplayClass)
    };
    private SplitterState splitter = new();
    private List<AssemblyEntry> cachedAssemblies;
    private Dictionary<Assembly, List<TypeEntry>> typeCache = new();
    private Vector2 leftScrollPos;
    private Vector2 rightScrollPos;
    private bool enumFoldout = true;
    private bool structFoldout = true;
    private bool classFoldout = true;

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
            if (type == null || type.IsInterface || type.IsArray)
                continue;
            string kind;
            if (type.IsEnum)
                kind = "Enum";
            else if (type.IsValueType)
                kind = "Struct";
            else
            {
                if (!type.IsClass)
                    continue;
                kind = "Class";
            }
            entries.Add(new TypeEntry
            {
                Type = type,
                Name = type.Name,
                AssemblyName = assembly.GetName().Name,
                Kind = kind
            });
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

        var enumEntries = new List<TypeEntry>();
        var structEntries = new List<TypeEntry>();
        var classEntries = new List<TypeEntry>();

        foreach (var assemblyEntry in filteredAssemblies)
        {
            foreach (var entry in GetTypeEntries(assemblyEntry.Assembly))
            {
                if (!typeFilter.Matches(entry.Name))
                    continue;
                switch (entry.Kind)
                {
                    case "Enum":
                        enumEntries.Add(entry);
                        break;
                    case "Struct":
                        structEntries.Add(entry);
                        break;
                    default:
                        classEntries.Add(entry);
                        break;
                }
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

        DrawTypeBox(ref enumFoldout, "Enums", enumEntries);
        DrawTypeBox(ref structFoldout, "Structs", structEntries);
        DrawTypeBox(ref classFoldout, "Classes", classEntries);
    }

    private void DrawTypeBox(ref bool foldout, string title, List<TypeEntry> entries)
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            foldout = EditorGUILayout.Foldout(foldout, $"{title} ({entries.Count})", true, EditorStyles.boldLabel);

            if (entries.Count == 0)
                return;

            if (!foldout)
                return;

            foreach (var entry in entries.Take(maxEntriesPerBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(ColumnGrid.InnerIndent);
                    GUILayout.Label(new GUIContent(entry.Name, $"{entry.Type.FullName}\n[{entry.AssemblyName}]"),
                        EditorStyles.label, GUILayout.Height(18f));
                }
            }

            if (entries.Count > maxEntriesPerBox)
                EditorGUILayout.HelpBox($"Limited to {maxEntriesPerBox} entries.", MessageType.None);
        }
    }
}
#endif
