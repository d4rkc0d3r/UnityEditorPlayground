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

    private TextFilter assemblyFilter = new() { SmallButtons = true };
    private List<AssemblyEntry> cachedAssemblies;
    private Vector2 scrollPos;

    [MenuItem("Tools/d4rkpl4y3r/Reflection Inspector")]
    public static void OpenWindow()
    {
        ReflectionInspectorEditor window = GetWindow<ReflectionInspectorEditor>();
        window.titleContent = new GUIContent("Reflection Inspector");
        window.minSize = new Vector2(480f, 320f);
        window.Show();
    }

    private void OnEnable()
    {
        titleContent = new GUIContent("Reflection Inspector");
        minSize = new Vector2(480f, 320f);
        RefreshAssemblies();
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
    }

    private void OnGUI()
    {
        assemblyFilter.DrawGUI();

        int matchCount = 0;
        if (cachedAssemblies != null)
        {
            foreach (var entry in cachedAssemblies)
            {
                if (assemblyFilter.Matches(entry.Name))
                    matchCount++;
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label($"{matchCount} / {cachedAssemblies?.Count ?? 0} assemblies");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("Refresh", "Reload the list of loaded assemblies"),
                GUILayout.ExpandWidth(false)))
            {
                RefreshAssemblies();
            }
        }

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        if (cachedAssemblies == null)
        {
            EditorGUILayout.HelpBox("No assemblies loaded.", MessageType.Info);
        }
        else
        {
            foreach (var entry in cachedAssemblies)
            {
                if (!assemblyFilter.Matches(entry.Name))
                    continue;
                EditorGUILayout.SelectableLabel(entry.Name, EditorStyles.textField, GUILayout.Height(18f));
            }

            if (matchCount == 0)
                EditorGUILayout.HelpBox("No assemblies match the filter.", MessageType.None);
        }

        EditorGUILayout.EndScrollView();
    }
}
#endif
