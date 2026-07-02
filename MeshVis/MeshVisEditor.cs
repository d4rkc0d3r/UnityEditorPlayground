#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using d4rkpl4y3r.AV3ToggleUtil.Util;
using static d4rkpl4y3r.AV3ToggleUtil.Util.AV3Helper;

namespace d4rkpl4y3r.MeshVis
{
    public class MeshVisEditor : EditorWindow
    {
        private const string VisualizationObjectName = "d4rkMeshVis";
        private const string VisualizationShaderPath = "Assets/d4rkpl4y3rPrivateShaders/Utils/MeshVis/MeshVisOverlay.shader";
        private const int BlendShapesPerPage = 20;
        private static readonly string[] VertexFilterModeLabels = { "Selected Bones", "Affected Bone Count", "BlendShape" };
        private static readonly string[] VertexBoundsModeLabels = { "World Aligned", "Local Aligned", "Oriented (PCA)" };

        private class IndentedHorizontalScope : System.IDisposable
        {
            readonly int previousIndentLevel;
            public IndentedHorizontalScope()
            {
                previousIndentLevel = EditorGUI.indentLevel;
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 15f);
                EditorGUI.indentLevel = 0;
            }

            public void Dispose()
            {
                EditorGUILayout.EndHorizontal();
                EditorGUI.indentLevel = previousIndentLevel;
            }
        }

        private enum VertexFilterMode
        {
            SelectedBones,
            AffectedBoneCount,
            BlendShape
        }

        private enum VertexBoundsMode
        {
            AxisAligned,
            SelectedBoneAligned,
            Oriented
        }

        private sealed class ObservedRendererState
        {
            public ObservedRendererState(SkinnedMeshRenderer renderer, Mesh mesh, Transform[] bones, float[] blendShapeWeights, Matrix4x4 rendererLocalToWorldMatrix)
            {
                Renderer = renderer;
                Mesh = mesh;
                Bones = bones;
                BlendShapeWeights = blendShapeWeights;
                RendererLocalToWorldMatrix = rendererLocalToWorldMatrix;
            }

            public SkinnedMeshRenderer Renderer { get; }
            public Mesh Mesh { get; }
            public Transform[] Bones { get; }
            public float[] BlendShapeWeights { get; }
            public Matrix4x4 RendererLocalToWorldMatrix { get; }
        }

        private sealed class ObservedTransformState
        {
            public ObservedTransformState(Transform transform)
            {
                Transform = transform;
                LocalToWorldMatrix = transform != null ? transform.localToWorldMatrix : Matrix4x4.identity;
            }

            public Transform Transform { get; }
            public Matrix4x4 LocalToWorldMatrix { get; }
        }

        private sealed class WeightedMeshResult
        {
            public WeightedMeshResult(Mesh mesh, SkinnedMeshRenderer renderer, bool[] affectedVertexMask, int affectedVertexCount)
            {
                Mesh = mesh;
                Renderer = renderer;
                AffectedVertexMask = affectedVertexMask;
                AffectedVertexCount = affectedVertexCount;
            }

            public Mesh Mesh { get; }
            public SkinnedMeshRenderer Renderer { get; }
            public bool[] AffectedVertexMask { get; }
            public int AffectedVertexCount { get; }
        }

        private Transform root;
        private Vector2 scrollPos;
        private bool showMeshSelection = false;
        private bool showFilteredMeshes = true;
        private bool includeChildTransforms;
        private bool drawSelectedVertexBounds = true;
        private VertexBoundsMode vertexBoundsMode;
        private int visualizationTriangleMatchCount = 1;
        private VertexFilterMode vertexFilterMode;
        private float weightCutoff;
        private int affectedBoneCountCutoff = 1;
        private bool includeActiveBlendShapes = true;
        private readonly TextFilter blendShapeNameFilter = new() { SmallButtons = true, IsRegex = false };
        private bool showSelectedBlendShapes;
        private int selectedBlendShapePage;
        private bool cacheDirty = true;
        private int cachedSelectedTransformCount;
        private bool selectedVertexBoundsDirty = true;
        private bool hasCachedSelectedVertexBounds;
        private Bounds cachedSelectedVertexBounds;
        private bool hasCachedSelectedBoneAlignedVertexBounds;
        private OrientedBounds cachedSelectedBoneAlignedVertexBounds;
        private bool hasCachedOrientedSelectedVertexBounds;
        private OrientedBounds cachedOrientedSelectedVertexBounds;
        private readonly Dictionary<int, bool> selectedMeshRenderers = new();
        private readonly List<WeightedMeshResult> cachedResults = new();
        private readonly List<ObservedRendererState> observedRenderers = new();
        private readonly List<ObservedTransformState> observedSelectedVertexBoundsTransforms = new();
        private Mesh visualizationMesh;
        private Material visualizationMaterial;
        private SkinnedMeshRenderer visualizationRenderer;

        private void OnEnable()
        {
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
            cacheDirty = true;
            selectedVertexBoundsDirty = true;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            SceneView.duringSceneGui -= OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
        }

        private void OnGUI()
        {
            using var scrollView = new EditorGUILayout.ScrollViewScope(scrollPos);
            scrollPos = scrollView.scrollPosition;

            EditorGUI.BeginChangeCheck();
            vertexFilterMode = (VertexFilterMode)GUILayout.Toolbar((int)vertexFilterMode, VertexFilterModeLabels);
            if (EditorGUI.EndChangeCheck())
            {
                cacheDirty = true;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();
                root = (Transform)EditorGUILayout.ObjectField("Root Transform", root, typeof(Transform), true);
                if (EditorGUI.EndChangeCheck())
                {
                    InitializeMeshSelectionForRoot();
                    cacheDirty = true;
                }

                if (root != null)
                {
                    DrawMeshSelectionUI();
                }

                EditorGUI.BeginChangeCheck();
                drawSelectedVertexBounds = EditorGUILayout.ToggleLeft("Draw bounding box around selected vertices", drawSelectedVertexBounds);
                if (EditorGUI.EndChangeCheck())
                {
                    selectedVertexBoundsDirty = true;
                    SceneView.RepaintAll();
                }

                using (new EditorGUI.DisabledScope(!drawSelectedVertexBounds))
                {
                    EditorGUI.BeginChangeCheck();
                    vertexBoundsMode = (VertexBoundsMode)GUILayout.Toolbar((int)vertexBoundsMode, VertexBoundsModeLabels);
                    if (EditorGUI.EndChangeCheck())
                    {
                        selectedVertexBoundsDirty = true;
                        SceneView.RepaintAll();
                    }
                }

                EditorGUI.BeginChangeCheck();
                visualizationTriangleMatchCount = EditorGUILayout.IntSlider("Matched Vertex Count", visualizationTriangleMatchCount, 1, 3);
                if (EditorGUI.EndChangeCheck())
                {
                    cacheDirty = true;
                    SceneView.RepaintAll();
                }
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (vertexFilterMode == VertexFilterMode.SelectedBones)
                {
                    EditorGUI.BeginChangeCheck();
                    includeChildTransforms = EditorGUILayout.ToggleLeft("Include child transforms of selected transforms", includeChildTransforms);
                    if (EditorGUI.EndChangeCheck())
                    {
                        cacheDirty = true;
                    }

                    EditorGUI.BeginChangeCheck();
                    weightCutoff = EditorGUILayout.Slider("Weight Cutoff", weightCutoff, 0f, 1f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        cacheDirty = true;
                    }
                }
                else if (vertexFilterMode == VertexFilterMode.AffectedBoneCount)
                {
                    EditorGUI.BeginChangeCheck();
                    affectedBoneCountCutoff = EditorGUILayout.IntSlider("Affected Bone Count", affectedBoneCountCutoff, 1, 5);
                    if (EditorGUI.EndChangeCheck())
                    {
                        cacheDirty = true;
                    }
                }
                else
                {
                    EditorGUI.BeginChangeCheck();
                    includeActiveBlendShapes = EditorGUILayout.ToggleLeft("Include currently active blendshapes", includeActiveBlendShapes);
                    if (EditorGUI.EndChangeCheck())
                    {
                        cacheDirty = true;
                        selectedBlendShapePage = 0;
                    }

                    using var cc = new EditorGUI.ChangeCheckScope();
                    blendShapeNameFilter.DrawGUI("BlendShape Name Filter");
                    if (cc.changed)
                    {
                        cacheDirty = true;
                        selectedBlendShapePage = 0;
                    }
                }

                if (root == null)
                {
                    ClearObservedInputs();
                    ClearSelectedVertexBoundsCache();
                    ClearVisualization();
                    EditorGUILayout.HelpBox("Please assign a root transform.", MessageType.Error);
                    return;
                }

                if (vertexFilterMode == VertexFilterMode.BlendShape)
                {
                    DrawSelectedBlendShapeNames(CollectSourceSkinnedMeshRenderers());
                }

                RefreshCacheIfNeeded();

                if (RequiresSelection() && cachedSelectedTransformCount == 0)
                {
                    EditorGUILayout.HelpBox("Select one or more transforms to find meshes weighted to them.", MessageType.Info);
                    return;
                }

                var selectedMeshCount = CountSelectedMeshRenderers(CollectSourceSkinnedMeshRenderers());
                showFilteredMeshes = EditorGUILayout.Foldout(showFilteredMeshes, GetResultsFoldoutLabel(selectedMeshCount), true);
                if (!showFilteredMeshes)
                {
                    return;
                }

                using var indent = new EditorGUI.IndentLevelScope();
                if (cachedResults.Count == 0)
                {
                    EditorGUILayout.HelpBox("No selected meshes have vertices matching the current filter.", MessageType.Info);
                    return;
                }

                foreach (var result in cachedResults)
                {
                    var label = new GUIContent(result.AffectedVertexCount.ToString(), result.Renderer.name);
                    using var row = new EditorGUILayout.HorizontalScope();
                    EditorGUILayout.ObjectField(label, result.Mesh, typeof(Mesh), false);
                    if (GUILayout.Button("X", GUILayout.Width(20f)))
                    {
                        selectedMeshRenderers[result.Renderer.GetInstanceID()] = false;
                        cacheDirty = true;
                        selectedVertexBoundsDirty = true;
                        Repaint();
                        SceneView.RepaintAll();
                    }
                }
            }
        }

        private void OnInspectorUpdate()
        {
            var sceneViewNeedsRepaint = false;
            if (MarkCacheDirtyIfObservedInputsChanged())
            {
                selectedVertexBoundsDirty = true;
                Repaint();
                sceneViewNeedsRepaint = true;
            }

            if (MarkSelectedVertexBoundsDirtyIfObservedInputsChanged())
            {
                sceneViewNeedsRepaint = true;
            }

            if (sceneViewNeedsRepaint)
            {
                SceneView.RepaintAll();
            }
        }
        
        [MenuItem("Tools/d4rkpl4y3r/Mesh Vis")]
        public static void CreateMenuItem()
        {
            var window = GetWindow<MeshVisEditor>();
            window.titleContent = new GUIContent("d4rk Mesh Vis");
            window.Show();
        }

        private void OnSelectionChange()
        {
            cacheDirty = true;
            selectedVertexBoundsDirty = true;
            Repaint();
        }

        private void OnHierarchyChanged()
        {
            cacheDirty = true;
            selectedVertexBoundsDirty = true;
            Repaint();
        }

        private void OnUndoRedoPerformed()
        {
            cacheDirty = true;
            selectedVertexBoundsDirty = true;
            Repaint();
        }

        private void OnSceneGUI(SceneView sceneView)
        {
            if (!drawSelectedVertexBounds || root == null)
            {
                return;
            }

            RefreshCacheIfNeeded();
            if ((RequiresSelection() && cachedSelectedTransformCount == 0) || cachedResults.Count == 0)
            {
                return;
            }

            UpdateSelectedVertexBoundsIfNeeded();
            if (!hasCachedSelectedVertexBounds)
            {
                return;
            }

            var previousColor = Handles.color;
            var previousMatrix = Handles.matrix;
            var previousZTest = Handles.zTest;
            Handles.color = new Color(1f, 0.6f, 0.2f, 1f);
            Handles.zTest = CompareFunction.Always;
            if (vertexBoundsMode == VertexBoundsMode.SelectedBoneAligned)
            {
                if (!hasCachedSelectedBoneAlignedVertexBounds)
                {
                    Handles.zTest = previousZTest;
                    Handles.color = previousColor;
                    Handles.matrix = previousMatrix;
                    return;
                }

                Handles.matrix = Matrix4x4.TRS(cachedSelectedBoneAlignedVertexBounds.Center, cachedSelectedBoneAlignedVertexBounds.Rotation, Vector3.one);
                Handles.DrawWireCube(Vector3.zero, cachedSelectedBoneAlignedVertexBounds.Size);
            }
            else if (vertexBoundsMode == VertexBoundsMode.Oriented)
            {
                if (!hasCachedOrientedSelectedVertexBounds)
                {
                    Handles.zTest = previousZTest;
                    Handles.color = previousColor;
                    Handles.matrix = previousMatrix;
                    return;
                }

                Handles.matrix = Matrix4x4.TRS(cachedOrientedSelectedVertexBounds.Center, cachedOrientedSelectedVertexBounds.Rotation, Vector3.one);
                Handles.DrawWireCube(Vector3.zero, cachedOrientedSelectedVertexBounds.Size);
            }
            else
            {
                Handles.DrawWireCube(cachedSelectedVertexBounds.center, cachedSelectedVertexBounds.size);
            }

            Handles.matrix = previousMatrix;
            Handles.zTest = previousZTest;
            Handles.color = previousColor;
        }

        private bool MarkCacheDirtyIfObservedInputsChanged()
        {
            if (cacheDirty || root == null || (RequiresSelection() && cachedSelectedTransformCount == 0))
            {
                return false;
            }

            if (HaveObservedRenderersChanged())
            {
                cacheDirty = true;
                return true;
            }

            return false;
        }

        private bool MarkSelectedVertexBoundsDirtyIfObservedInputsChanged()
        {
            if (selectedVertexBoundsDirty || !drawSelectedVertexBounds || root == null || (RequiresSelection() && cachedSelectedTransformCount == 0) || cachedResults.Count == 0)
            {
                return false;
            }

            if (HaveObservedSelectedVertexBoundsTransformsChanged())
            {
                selectedVertexBoundsDirty = true;
                return true;
            }

            return false;
        }

        private void RefreshCacheIfNeeded()
        {
            if (!cacheDirty)
            {
                return;
            }

            cacheDirty = false;
            cachedResults.Clear();
            ClearObservedInputs();
            ClearSelectedVertexBoundsCache();

            var selectedTransforms = Selection.GetTransforms(SelectionMode.Unfiltered);
            cachedSelectedTransformCount = selectedTransforms.Length;
            if (RequiresSelection() && cachedSelectedTransformCount == 0)
            {
                ClearVisualization();
                return;
            }

            var skinnedMeshRenderers = CollectSourceSkinnedMeshRenderers();
            SyncMeshSelectionState(skinnedMeshRenderers);
            var selectedTransformSet = BuildSelectedTransformSet(selectedTransforms, includeChildTransforms);
            foreach (var skinnedMeshRenderer in skinnedMeshRenderers)
            {
                if (!IsMeshRendererSelected(skinnedMeshRenderer))
                {
                    continue;
                }

                var mesh = skinnedMeshRenderer.sharedMesh;
                var bones = skinnedMeshRenderer.bones;
                TrackObservedRendererInputs(skinnedMeshRenderer, mesh, bones);
                if (mesh == null || bones == null || bones.Length == 0)
                {
                    continue;
                }

                var selectedBoneMask = BuildSelectedBoneMask(bones, selectedTransformSet, out var selectedBoneCount);
                if (vertexFilterMode == VertexFilterMode.SelectedBones && selectedBoneCount == 0)
                {
                    continue;
                }

                var affectedVertexMask = BuildAffectedVertexMask(mesh, skinnedMeshRenderer, selectedBoneMask, vertexFilterMode, weightCutoff, affectedBoneCountCutoff, out var affectedVertexCount);
                if (affectedVertexCount == 0)
                {
                    continue;
                }

                cachedResults.Add(new WeightedMeshResult(mesh, skinnedMeshRenderer, affectedVertexMask, affectedVertexCount));
            }

            cachedResults.Sort((left, right) =>
            {
                var countComparison = right.AffectedVertexCount.CompareTo(left.AffectedVertexCount);
                if (countComparison != 0)
                {
                    return countComparison;
                }

                return string.CompareOrdinal(left.Mesh.name, right.Mesh.name);
            });

            UpdateVisualization();
        }

        private bool RequiresSelection()
        {
            return vertexFilterMode == VertexFilterMode.SelectedBones;
        }

        private void DrawSelectedBlendShapeNames(List<SkinnedMeshRenderer> skinnedMeshRenderers)
        {
            var selectedBlendShapeNames = CollectSelectedBlendShapeNames(skinnedMeshRenderers);
            showSelectedBlendShapes = EditorGUILayout.Foldout(showSelectedBlendShapes, $"Selected BlendShapes ({selectedBlendShapeNames.Count})", true);
            if (!showSelectedBlendShapes)
            {
                return;
            }

            using var indent = new EditorGUI.IndentLevelScope();
            if (selectedBlendShapeNames.Count == 0)
            {
                EditorGUILayout.HelpBox("No blendshapes match the current filters.", MessageType.Info);
                return;
            }

            if (selectedBlendShapeNames.Count > BlendShapesPerPage)
            {
                using var _ = new EditorGUILayout.HorizontalScope();
                var totalPages = Mathf.CeilToInt(selectedBlendShapeNames.Count / (float)BlendShapesPerPage);
                GUILayout.Label($"Page ({selectedBlendShapePage + 1}/{totalPages})", GUILayout.Width(100));
                using (new EditorGUI.DisabledScope(totalPages <= 1 || selectedBlendShapePage <= 0))
                {
                    if (GUILayout.Button("<", GUILayout.Width(20)))
                    {
                        selectedBlendShapePage = Mathf.Max(selectedBlendShapePage - 1, 0);
                    }
                }
                using (new EditorGUI.DisabledScope(totalPages <= 1 || selectedBlendShapePage >= totalPages - 1))
                {
                    if (GUILayout.Button(">", GUILayout.Width(20)))
                    {
                        selectedBlendShapePage = Mathf.Min(selectedBlendShapePage + 1, totalPages - 1);
                    }
                }
                GUILayout.Space(10);
                selectedBlendShapePage = EditorGUILayout.IntField(selectedBlendShapePage + 1, GUILayout.Width(50)) - 1;
                selectedBlendShapePage = Mathf.Clamp(selectedBlendShapePage, 0, totalPages - 1);
            }
            else
            {
                selectedBlendShapePage = 0;
            }

            var startIndex = selectedBlendShapePage * BlendShapesPerPage;
            var endIndex = Mathf.Min(startIndex + BlendShapesPerPage, selectedBlendShapeNames.Count);
            for (var blendShapeIndex = startIndex; blendShapeIndex < endIndex; blendShapeIndex++)
            {
                var blendShapeName = selectedBlendShapeNames[blendShapeIndex];
                EditorGUILayout.LabelField(blendShapeName);
                if (!ClickableLastRect())
                {
                    continue;
                }

                includeActiveBlendShapes = false;
                blendShapeNameFilter.IsRegex = true;
                blendShapeNameFilter.Invert = false;
                blendShapeNameFilter.Text = $"^{Regex.Escape(blendShapeName)}$";
                cacheDirty = true;
                selectedBlendShapePage = 0;
                GUI.FocusControl(null);
                Repaint();
            }
        }

        private List<string> CollectSelectedBlendShapeNames(List<SkinnedMeshRenderer> skinnedMeshRenderers)
        {
            var selectedBlendShapeNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var skinnedMeshRenderer in skinnedMeshRenderers)
            {
                if (!IsMeshRendererSelected(skinnedMeshRenderer))
                {
                    continue;
                }

                var mesh = skinnedMeshRenderer.sharedMesh;
                if (mesh == null || mesh.blendShapeCount == 0)
                {
                    continue;
                }

                for (var blendShapeIndex = 0; blendShapeIndex < mesh.blendShapeCount; blendShapeIndex++)
                {
                    if (!IsBlendShapeSelected(mesh, skinnedMeshRenderer, blendShapeIndex))
                    {
                        continue;
                    }

                    var blendShapeName = mesh.GetBlendShapeName(blendShapeIndex);
                    if (string.IsNullOrEmpty(blendShapeName))
                    {
                        continue;
                    }

                    selectedBlendShapeNames.Add(blendShapeName);
                }
            }

            var blendShapeNames = new List<string>(selectedBlendShapeNames);
            blendShapeNames.Sort(StringComparer.OrdinalIgnoreCase);
            return blendShapeNames;
        }

        private bool IsBlendShapeSelected(Mesh mesh, SkinnedMeshRenderer renderer, int blendShapeIndex)
        {
            if (mesh == null || renderer == null || blendShapeIndex < 0 || blendShapeIndex >= mesh.blendShapeCount)
            {
                return false;
            }

            var isActiveBlendShape = includeActiveBlendShapes && !Mathf.Approximately(renderer.GetBlendShapeWeight(blendShapeIndex), 0f);
            var hasNameFilter = !string.IsNullOrEmpty(blendShapeNameFilter.Text);
            var matchesNameFilter = hasNameFilter
                ? blendShapeNameFilter.Matches(mesh.GetBlendShapeName(blendShapeIndex))
                : !includeActiveBlendShapes;
            return isActiveBlendShape || matchesNameFilter;
        }

        private string GetResultsFoldoutLabel(int selectedMeshCount)
        {
            return $"Filtered Meshes ({cachedResults.Count}/{selectedMeshCount})";
        }

        private void DrawMeshSelectionUI()
        {
            var skinnedMeshRenderers = CollectSourceSkinnedMeshRenderers();
            SyncMeshSelectionState(skinnedMeshRenderers);

            showMeshSelection = EditorGUILayout.Foldout(showMeshSelection, $"Mesh Selection ({CountSelectedMeshRenderers(skinnedMeshRenderers)}/{skinnedMeshRenderers.Count})", true);
            if (!showMeshSelection)
            {
                return;
            }

            using (new EditorGUI.IndentLevelScope())
            {
                using (new IndentedHorizontalScope())
                {
                    if (GUILayout.Button("All"))
                    {
                        SetMeshSelection(skinnedMeshRenderers, true);
                    }

                    if (GUILayout.Button("None"))
                    {
                        SetMeshSelection(skinnedMeshRenderers, false);
                    }

                    if (GUILayout.Button("Active"))
                    {
                        SetMeshSelectionToActiveState(skinnedMeshRenderers, true);
                    }

                    if (GUILayout.Button("Inactive"))
                    {
                        SetMeshSelectionToActiveState(skinnedMeshRenderers, false);
                    }
                }

                foreach (var skinnedMeshRenderer in skinnedMeshRenderers)
                {
                    var instanceId = skinnedMeshRenderer.GetInstanceID();
                    using (new IndentedHorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        var isSelected = GUILayout.Toggle(selectedMeshRenderers[instanceId], "S", "Button", GUILayout.Width(20f));
                        if (EditorGUI.EndChangeCheck())
                        {
                            selectedMeshRenderers[instanceId] = isSelected;
                            cacheDirty = true;
                            selectedVertexBoundsDirty = true;
                            SceneView.RepaintAll();
                        }

                        EditorGUILayout.ObjectField(skinnedMeshRenderer, typeof(SkinnedMeshRenderer), true);
                    }
                }
            }
        }

        private void InitializeMeshSelectionForRoot()
        {
            selectedMeshRenderers.Clear();
            if (root == null)
            {
                return;
            }

            foreach (var skinnedMeshRenderer in CollectSourceSkinnedMeshRenderers())
            {
                selectedMeshRenderers[skinnedMeshRenderer.GetInstanceID()] = skinnedMeshRenderer.gameObject.activeInHierarchy;
            }

            selectedVertexBoundsDirty = true;
        }

        private void SyncMeshSelectionState(List<SkinnedMeshRenderer> skinnedMeshRenderers)
        {
            var currentRendererIds = new HashSet<int>();
            foreach (var skinnedMeshRenderer in skinnedMeshRenderers)
            {
                var instanceId = skinnedMeshRenderer.GetInstanceID();
                currentRendererIds.Add(instanceId);
                if (!selectedMeshRenderers.ContainsKey(instanceId))
                {
                    selectedMeshRenderers[instanceId] = skinnedMeshRenderer.gameObject.activeInHierarchy;
                }
            }

            var removedRendererIds = new List<int>();
            foreach (var selectedMeshRenderer in selectedMeshRenderers)
            {
                if (!currentRendererIds.Contains(selectedMeshRenderer.Key))
                {
                    removedRendererIds.Add(selectedMeshRenderer.Key);
                }
            }

            foreach (var removedRendererId in removedRendererIds)
            {
                selectedMeshRenderers.Remove(removedRendererId);
            }
        }

        private int CountSelectedMeshRenderers(List<SkinnedMeshRenderer> skinnedMeshRenderers)
        {
            var selectedMeshRendererCount = 0;
            foreach (var skinnedMeshRenderer in skinnedMeshRenderers)
            {
                if (IsMeshRendererSelected(skinnedMeshRenderer))
                {
                    selectedMeshRendererCount++;
                }
            }

            return selectedMeshRendererCount;
        }

        private void SetMeshSelection(List<SkinnedMeshRenderer> skinnedMeshRenderers, bool isSelected)
        {
            foreach (var skinnedMeshRenderer in skinnedMeshRenderers)
            {
                selectedMeshRenderers[skinnedMeshRenderer.GetInstanceID()] = isSelected;
            }

            cacheDirty = true;
            selectedVertexBoundsDirty = true;
            Repaint();
            SceneView.RepaintAll();
        }

        private void SetMeshSelectionToActiveState(List<SkinnedMeshRenderer> skinnedMeshRenderers, bool isActive)
        {
            foreach (var skinnedMeshRenderer in skinnedMeshRenderers)
            {
                selectedMeshRenderers[skinnedMeshRenderer.GetInstanceID()] = skinnedMeshRenderer.gameObject.activeInHierarchy == isActive;
            }

            cacheDirty = true;
            selectedVertexBoundsDirty = true;
            Repaint();
            SceneView.RepaintAll();
        }

        private bool IsMeshRendererSelected(SkinnedMeshRenderer skinnedMeshRenderer)
        {
            return selectedMeshRenderers.TryGetValue(skinnedMeshRenderer.GetInstanceID(), out var isSelected) && isSelected;
        }

        private List<SkinnedMeshRenderer> CollectSourceSkinnedMeshRenderers()
        {
            var sourceRenderers = new List<SkinnedMeshRenderer>();
            var currentVisualizationRenderer = GetVisualizationRenderer(false);
            foreach (var skinnedMeshRenderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skinnedMeshRenderer == currentVisualizationRenderer)
                {
                    continue;
                }

                sourceRenderers.Add(skinnedMeshRenderer);
            }

            return sourceRenderers;
        }

        private void TrackObservedRendererInputs(SkinnedMeshRenderer renderer, Mesh mesh, Transform[] bones)
        {
            var observedBones = new Transform[bones != null ? bones.Length : 0];
            if (bones != null && bones.Length > 0)
            {
                bones.CopyTo(observedBones, 0);
            }

            observedRenderers.Add(new ObservedRendererState(renderer, mesh, observedBones, CaptureBlendShapeWeights(renderer, mesh), renderer.transform.localToWorldMatrix));
        }

        private void ClearObservedInputs()
        {
            observedRenderers.Clear();
        }

        private void ClearSelectedVertexBoundsCache()
        {
            selectedVertexBoundsDirty = true;
            hasCachedSelectedVertexBounds = false;
            hasCachedSelectedBoneAlignedVertexBounds = false;
            hasCachedOrientedSelectedVertexBounds = false;
            observedSelectedVertexBoundsTransforms.Clear();
        }

        private bool HaveObservedRenderersChanged()
        {
            var currentRenderers = CollectSourceSkinnedMeshRenderers();
            if (currentRenderers.Count != observedRenderers.Count)
            {
                return true;
            }

            foreach (var currentRenderer in currentRenderers)
            {
                if (!ContainsObservedRenderer(currentRenderer))
                {
                    return true;
                }
            }

            foreach (var observedRenderer in observedRenderers)
            {
                var renderer = observedRenderer.Renderer;
                if (renderer == null || renderer.sharedMesh != observedRenderer.Mesh)
                {
                    return true;
                }

                if (!BonesMatch(renderer.bones, observedRenderer.Bones))
                {
                    return true;
                }

                if (!BlendShapeWeightsMatch(renderer, observedRenderer.Mesh, observedRenderer.BlendShapeWeights))
                {
                    return true;
                }

                if (!MatricesMatch(renderer.transform.localToWorldMatrix, observedRenderer.RendererLocalToWorldMatrix))
                {
                    return true;
                }
            }

            return false;
        }

        private static float[] CaptureBlendShapeWeights(SkinnedMeshRenderer renderer, Mesh mesh)
        {
            var blendShapeCount = mesh != null ? mesh.blendShapeCount : 0;
            if (blendShapeCount == 0)
            {
                return new float[0];
            }

            var blendShapeWeights = new float[blendShapeCount];
            for (var blendShapeIndex = 0; blendShapeIndex < blendShapeCount; blendShapeIndex++)
            {
                blendShapeWeights[blendShapeIndex] = renderer.GetBlendShapeWeight(blendShapeIndex);
            }

            return blendShapeWeights;
        }

        private static bool BlendShapeWeightsMatch(SkinnedMeshRenderer renderer, Mesh mesh, float[] observedBlendShapeWeights)
        {
            var blendShapeCount = mesh != null ? mesh.blendShapeCount : 0;
            if (blendShapeCount != observedBlendShapeWeights.Length)
            {
                return false;
            }

            for (var blendShapeIndex = 0; blendShapeIndex < blendShapeCount; blendShapeIndex++)
            {
                if (!Mathf.Approximately(renderer.GetBlendShapeWeight(blendShapeIndex), observedBlendShapeWeights[blendShapeIndex]))
                {
                    return false;
                }
            }

            return true;
        }

        private bool ContainsObservedRenderer(SkinnedMeshRenderer renderer)
        {
            foreach (var observedRenderer in observedRenderers)
            {
                if (observedRenderer.Renderer == renderer)
                {
                    return true;
                }
            }

            return false;
        }

        private bool HaveObservedSelectedVertexBoundsTransformsChanged()
        {
            foreach (var observedTransform in observedSelectedVertexBoundsTransforms)
            {
                var transform = observedTransform.Transform;
                if (transform == null)
                {
                    return true;
                }

                if (!MatricesMatch(transform.localToWorldMatrix, observedTransform.LocalToWorldMatrix))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool BonesMatch(Transform[] currentBones, Transform[] observedBones)
        {
            var currentBoneCount = currentBones != null ? currentBones.Length : 0;
            var observedBoneCount = observedBones != null ? observedBones.Length : 0;
            if (currentBoneCount != observedBoneCount)
            {
                return false;
            }

            for (var boneIndex = 0; boneIndex < currentBoneCount; boneIndex++)
            {
                if (currentBones[boneIndex] != observedBones[boneIndex])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool MatricesMatch(Matrix4x4 left, Matrix4x4 right)
        {
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    if (!Mathf.Approximately(left[row, column], right[row, column]))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static HashSet<Transform> BuildSelectedTransformSet(Transform[] selectedTransforms, bool includeChildren)
        {
            var selectedTransformSet = new HashSet<Transform>(selectedTransforms);
            if (!includeChildren)
            {
                return selectedTransformSet;
            }

            foreach (var selectedTransform in selectedTransforms)
            {
                foreach (var transform in selectedTransform.GetComponentsInChildren<Transform>(true))
                {
                    selectedTransformSet.Add(transform);
                }
            }

            return selectedTransformSet;
        }

        private static bool[] BuildSelectedBoneMask(Transform[] bones, HashSet<Transform> selectedTransforms, out int selectedBoneCount)
        {
            var selectedBoneMask = new bool[bones.Length];
            selectedBoneCount = 0;
            for (var boneIndex = 0; boneIndex < bones.Length; boneIndex++)
            {
                if (!selectedTransforms.Contains(bones[boneIndex]))
                {
                    continue;
                }

                selectedBoneMask[boneIndex] = true;
                selectedBoneCount++;
            }

            return selectedBoneMask;
        }

        private bool[] BuildAffectedVertexMask(Mesh mesh, SkinnedMeshRenderer renderer, bool[] selectedBoneMask, VertexFilterMode vertexFilterMode, float weightCutoff, int affectedBoneCountCutoff, out int affectedVertexCount)
        {
            if (vertexFilterMode == VertexFilterMode.BlendShape)
            {
                return BuildBlendShapeAffectedVertexMask(mesh, renderer, out affectedVertexCount);
            }

            return BuildBoneAffectedVertexMask(mesh, selectedBoneMask, vertexFilterMode, weightCutoff, affectedBoneCountCutoff, out affectedVertexCount);
        }

        private static bool[] BuildBoneAffectedVertexMask(Mesh mesh, bool[] selectedBoneMask, VertexFilterMode vertexFilterMode, float weightCutoff, int affectedBoneCountCutoff, out int affectedVertexCount)
        {
            var affectedVertexMask = new bool[mesh.vertexCount];
            affectedVertexCount = 0;
            using var bonesPerVertex = mesh.GetBonesPerVertex();
            using var allBoneWeights = mesh.GetAllBoneWeights();

            var weightIndex = 0;
            for (var vertexIndex = 0; vertexIndex < bonesPerVertex.Length; vertexIndex++)
            {
                var selectedWeightSum = 0f;
                var nonZeroBoneWeightCount = 0;
                var influences = bonesPerVertex[vertexIndex];
                for (var influenceIndex = 0; influenceIndex < influences; influenceIndex++, weightIndex++)
                {
                    var boneWeight = allBoneWeights[weightIndex];
                    if (boneWeight.weight <= 0f)
                    {
                        continue;
                    }

                    nonZeroBoneWeightCount++;

                    var boneIndex = boneWeight.boneIndex;
                    if (boneIndex < 0 || boneIndex >= selectedBoneMask.Length)
                    {
                        continue;
                    }

                    if (selectedBoneMask[boneIndex])
                    {
                        selectedWeightSum += boneWeight.weight;
                    }
                }

                var isAffectedVertex = vertexFilterMode switch
                {
                    VertexFilterMode.SelectedBones => selectedWeightSum >= weightCutoff && selectedWeightSum > 0f,
                    VertexFilterMode.AffectedBoneCount => affectedBoneCountCutoff >= 5
                        ? nonZeroBoneWeightCount >= affectedBoneCountCutoff
                        : nonZeroBoneWeightCount == affectedBoneCountCutoff,
                    _ => false
                };

                if (isAffectedVertex)
                {
                    affectedVertexMask[vertexIndex] = true;
                    affectedVertexCount++;
                }
            }

            return affectedVertexMask;
        }

        private bool[] BuildBlendShapeAffectedVertexMask(Mesh mesh, SkinnedMeshRenderer renderer, out int affectedVertexCount)
        {
            var affectedVertexMask = new bool[mesh.vertexCount];
            affectedVertexCount = 0;
            if (mesh.blendShapeCount == 0)
            {
                return affectedVertexMask;
            }

            Vector3[] deltaVertices = null;
            Vector3[] deltaNormals = null;
            Vector3[] deltaTangents = null;
            for (var blendShapeIndex = 0; blendShapeIndex < mesh.blendShapeCount; blendShapeIndex++)
            {
                if (!IsBlendShapeSelected(mesh, renderer, blendShapeIndex))
                {
                    continue;
                }

                var frameCount = mesh.GetBlendShapeFrameCount(blendShapeIndex);
                if (frameCount == 0)
                {
                    continue;
                }

                deltaVertices ??= new Vector3[mesh.vertexCount];
                deltaNormals ??= new Vector3[mesh.vertexCount];
                deltaTangents ??= new Vector3[mesh.vertexCount];
                for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    mesh.GetBlendShapeFrameVertices(blendShapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);
                    for (var vertexIndex = 0; vertexIndex < affectedVertexMask.Length; vertexIndex++)
                    {
                        if (affectedVertexMask[vertexIndex])
                        {
                            continue;
                        }

                        if (deltaVertices[vertexIndex] == Vector3.zero && deltaNormals[vertexIndex] == Vector3.zero && deltaTangents[vertexIndex] == Vector3.zero)
                        {
                            continue;
                        }

                        affectedVertexMask[vertexIndex] = true;
                        affectedVertexCount++;
                    }
                }
            }

            return affectedVertexMask;
        }

        private void UpdateSelectedVertexBoundsIfNeeded()
        {
            if (!selectedVertexBoundsDirty && !HaveObservedSelectedVertexBoundsTransformsChanged())
            {
                return;
            }

            selectedVertexBoundsDirty = false;
            hasCachedSelectedVertexBounds = false;
            hasCachedSelectedBoneAlignedVertexBounds = false;
            hasCachedOrientedSelectedVertexBounds = false;
            observedSelectedVertexBoundsTransforms.Clear();

            var trackedTransforms = new HashSet<Transform>();
            var selectedVertexPositions = vertexBoundsMode == VertexBoundsMode.AxisAligned ? null : new List<Vector3>();
            foreach (var result in cachedResults)
            {
                AppendSelectedVertexBounds(result, trackedTransforms, selectedVertexPositions);
            }

            if (selectedVertexPositions == null || selectedVertexPositions.Count == 0)
            {
                return;
            }

            if (vertexBoundsMode == VertexBoundsMode.SelectedBoneAligned)
            {
                var selectedTransforms = Selection.GetTransforms(SelectionMode.Unfiltered);
                var alignmentTransform = selectedTransforms.Length > 0 ? selectedTransforms[0] : null;
                TrackObservedSelectedVertexBoundsTransform(alignmentTransform, trackedTransforms);
                hasCachedSelectedBoneAlignedVertexBounds = TryCreateTransformAlignedBounds(selectedVertexPositions, alignmentTransform, out cachedSelectedBoneAlignedVertexBounds);
                return;
            }

            if (vertexBoundsMode == VertexBoundsMode.Oriented)
            {
                hasCachedOrientedSelectedVertexBounds = PcaOrientedBounds.TryCreate(selectedVertexPositions, out cachedOrientedSelectedVertexBounds);
            }
        }

        private void AppendSelectedVertexBounds(WeightedMeshResult result, HashSet<Transform> trackedTransforms, List<Vector3> selectedVertexPositions)
        {
            var sourceMesh = result.Mesh;
            var sourceVertices = GetDeformedVertices(sourceMesh, result.Renderer);
            var sourceBindPoses = sourceMesh.bindposes;
            var sourceBones = result.Renderer.bones;
            if (sourceVertices.Length == 0 || result.AffectedVertexMask.Length != sourceVertices.Length)
            {
                return;
            }

            if (sourceBindPoses == null || sourceBones == null || sourceBindPoses.Length == 0 || sourceBindPoses.Length != sourceBones.Length)
            {
                return;
            }

            var boneMatrices = new Matrix4x4[sourceBones.Length];
            var validBoneMatrices = new bool[sourceBones.Length];
            for (var boneIndex = 0; boneIndex < sourceBones.Length; boneIndex++)
            {
                var bone = sourceBones[boneIndex];
                if (bone == null)
                {
                    continue;
                }

                boneMatrices[boneIndex] = bone.localToWorldMatrix * sourceBindPoses[boneIndex];
                validBoneMatrices[boneIndex] = true;
                TrackObservedSelectedVertexBoundsTransform(bone, trackedTransforms);
            }

            using var sourceBonesPerVertex = sourceMesh.GetBonesPerVertex();
            using var sourceAllBoneWeights = sourceMesh.GetAllBoneWeights();
            var sourceBoneWeightStarts = BuildBoneWeightStarts(sourceBonesPerVertex);
            for (var vertexIndex = 0; vertexIndex < sourceVertices.Length; vertexIndex++)
            {
                if (!result.AffectedVertexMask[vertexIndex])
                {
                    continue;
                }

                if (!TryGetSkinnedVertexWorldPosition(sourceVertices[vertexIndex], sourceBonesPerVertex[vertexIndex], sourceBoneWeightStarts[vertexIndex], sourceAllBoneWeights, boneMatrices, validBoneMatrices, out var worldPosition))
                {
                    continue;
                }

                if (!hasCachedSelectedVertexBounds)
                {
                    cachedSelectedVertexBounds = new Bounds(worldPosition, Vector3.zero);
                    hasCachedSelectedVertexBounds = true;
                }
                else
                {
                    cachedSelectedVertexBounds.Encapsulate(worldPosition);
                }

                selectedVertexPositions?.Add(worldPosition);
            }
        }

        private void TrackObservedSelectedVertexBoundsTransform(Transform transform, HashSet<Transform> trackedTransforms)
        {
            if (transform == null || !trackedTransforms.Add(transform))
            {
                return;
            }

            observedSelectedVertexBoundsTransforms.Add(new ObservedTransformState(transform));
        }

        private static bool TryCreateTransformAlignedBounds(IReadOnlyList<Vector3> points, Transform alignmentTransform, out OrientedBounds bounds)
        {
            bounds = default;
            if (alignmentTransform == null || points == null || points.Count == 0)
            {
                return false;
            }

            var worldToLocalMatrix = alignmentTransform.worldToLocalMatrix;
            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (var pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                var localPoint = worldToLocalMatrix.MultiplyPoint3x4(points[pointIndex]);
                min = Vector3.Min(min, localPoint);
                max = Vector3.Max(max, localPoint);
            }

            var localCenter = (min + max) * 0.5f;
            bounds = new OrientedBounds(alignmentTransform.TransformPoint(localCenter), alignmentTransform.rotation, max - min);
            return true;
        }

        private static bool TryGetSkinnedVertexWorldPosition(Vector3 sourceVertex, byte boneInfluenceCount, int sourceBoneWeightStart, NativeArray<BoneWeight1> sourceAllBoneWeights, Matrix4x4[] boneMatrices, bool[] validBoneMatrices, out Vector3 worldPosition)
        {
            worldPosition = Vector3.zero;
            var accumulatedWeight = 0f;
            for (var influenceIndex = 0; influenceIndex < boneInfluenceCount; influenceIndex++)
            {
                var sourceBoneWeight = sourceAllBoneWeights[sourceBoneWeightStart + influenceIndex];
                if (sourceBoneWeight.weight <= 0f)
                {
                    continue;
                }

                var boneIndex = sourceBoneWeight.boneIndex;
                if (boneIndex < 0 || boneIndex >= boneMatrices.Length || !validBoneMatrices[boneIndex])
                {
                    continue;
                }

                worldPosition += boneMatrices[boneIndex].MultiplyPoint3x4(sourceVertex) * sourceBoneWeight.weight;
                accumulatedWeight += sourceBoneWeight.weight;
            }

            if (accumulatedWeight <= 0f)
            {
                return false;
            }

            if (!Mathf.Approximately(accumulatedWeight, 1f))
            {
                worldPosition /= accumulatedWeight;
            }

            return true;
        }

        private static Vector3[] GetDeformedVertices(Mesh mesh, SkinnedMeshRenderer renderer)
        {
            var deformedVertices = mesh.vertices;
            var blendShapeCount = mesh.blendShapeCount;
            if (blendShapeCount == 0)
            {
                return deformedVertices;
            }

            Vector3[] deltaVertices = null;
            Vector3[] deltaNormals = null;
            Vector3[] deltaTangents = null;
            for (var blendShapeIndex = 0; blendShapeIndex < blendShapeCount; blendShapeIndex++)
            {
                var blendShapeWeight = renderer.GetBlendShapeWeight(blendShapeIndex);
                if (Mathf.Approximately(blendShapeWeight, 0f))
                {
                    continue;
                }

                var frameCount = mesh.GetBlendShapeFrameCount(blendShapeIndex);
                if (frameCount == 0)
                {
                    continue;
                }

                deltaVertices ??= new Vector3[deformedVertices.Length];
                deltaNormals ??= new Vector3[deformedVertices.Length];
                deltaTangents ??= new Vector3[deformedVertices.Length];

                ApplyBlendShape(mesh, deformedVertices, blendShapeIndex, blendShapeWeight, deltaVertices, deltaNormals, deltaTangents);
            }

            return deformedVertices;
        }

        private static void ApplyBlendShape(Mesh mesh, Vector3[] deformedVertices, int blendShapeIndex, float blendShapeWeight, Vector3[] deltaVertices, Vector3[] deltaNormals, Vector3[] deltaTangents)
        {
            var frameCount = mesh.GetBlendShapeFrameCount(blendShapeIndex);
            if (frameCount == 0)
            {
                return;
            }

            var targetFrameWeight = Mathf.Clamp01(blendShapeWeight / 100f);
            var previousFrameIndex = -1;
            var nextFrameIndex = -1;
            for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                var frameWeight = GetNormalizedBlendShapeFrameWeight(mesh, blendShapeIndex, frameIndex);
                if (frameWeight <= targetFrameWeight)
                {
                    previousFrameIndex = frameIndex;
                }

                if (frameWeight >= targetFrameWeight)
                {
                    nextFrameIndex = frameIndex;
                    break;
                }
            }

            if (previousFrameIndex < 0)
            {
                var firstFrameWeight = GetNormalizedBlendShapeFrameWeight(mesh, blendShapeIndex, 0);
                if (!Mathf.Approximately(firstFrameWeight, 0f))
                {
                    ScaleBlendShapeFrame(mesh, deformedVertices, blendShapeIndex, 0, targetFrameWeight / firstFrameWeight, deltaVertices, deltaNormals, deltaTangents);
                }

                return;
            }

            if (nextFrameIndex < 0)
            {
                var lastFrameIndex = frameCount - 1;
                var lastFrameWeight = GetNormalizedBlendShapeFrameWeight(mesh, blendShapeIndex, lastFrameIndex);
                if (!Mathf.Approximately(lastFrameWeight, 0f))
                {
                    ScaleBlendShapeFrame(mesh, deformedVertices, blendShapeIndex, lastFrameIndex, targetFrameWeight / lastFrameWeight, deltaVertices, deltaNormals, deltaTangents);
                }

                return;
            }

            if (previousFrameIndex == nextFrameIndex)
            {
                var frameWeight = GetNormalizedBlendShapeFrameWeight(mesh, blendShapeIndex, previousFrameIndex);
                if (!Mathf.Approximately(frameWeight, 0f))
                {
                    ScaleBlendShapeFrame(mesh, deformedVertices, blendShapeIndex, previousFrameIndex, targetFrameWeight / frameWeight, deltaVertices, deltaNormals, deltaTangents);
                }

                return;
            }

            var previousFrameWeight = GetNormalizedBlendShapeFrameWeight(mesh, blendShapeIndex, previousFrameIndex);
            var nextFrameWeight = GetNormalizedBlendShapeFrameWeight(mesh, blendShapeIndex, nextFrameIndex);
            var frameRange = nextFrameWeight - previousFrameWeight;
            if (Mathf.Approximately(frameRange, 0f))
            {
                return;
            }

            var interpolation = (targetFrameWeight - previousFrameWeight) / frameRange;
            ScaleBlendShapeFrame(mesh, deformedVertices, blendShapeIndex, previousFrameIndex, 1f - interpolation, deltaVertices, deltaNormals, deltaTangents);
            ScaleBlendShapeFrame(mesh, deformedVertices, blendShapeIndex, nextFrameIndex, interpolation, deltaVertices, deltaNormals, deltaTangents);
        }

        private static float GetNormalizedBlendShapeFrameWeight(Mesh mesh, int blendShapeIndex, int frameIndex)
        {
            return mesh.GetBlendShapeFrameWeight(blendShapeIndex, frameIndex) / 100f;
        }

        private static void ScaleBlendShapeFrame(Mesh mesh, Vector3[] deformedVertices, int blendShapeIndex, int frameIndex, float scale, Vector3[] deltaVertices, Vector3[] deltaNormals, Vector3[] deltaTangents)
        {
            if (Mathf.Approximately(scale, 0f))
            {
                return;
            }

            mesh.GetBlendShapeFrameVertices(blendShapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);
            for (var vertexIndex = 0; vertexIndex < deformedVertices.Length; vertexIndex++)
            {
                deformedVertices[vertexIndex] += deltaVertices[vertexIndex] * scale;
            }
        }

        private void UpdateVisualization()
        {
            var renderer = GetVisualizationRenderer(true);
            if (renderer == null)
            {
                return;
            }

            var visualizationObject = renderer.gameObject;
            visualizationObject.transform.SetParent(root, false);
            visualizationObject.transform.localPosition = Vector3.zero;
            visualizationObject.transform.localRotation = Quaternion.identity;
            visualizationObject.transform.localScale = Vector3.one;

            if (cachedResults.Count == 0)
            {
                ClearVisualization(renderer);
                return;
            }

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(VisualizationShaderPath);
            if (shader == null)
            {
                ClearVisualization(renderer);
                return;
            }

            EnsureVisualizationResources(shader);
            renderer.enabled = false;
            renderer.sharedMesh = null;
            if (!BuildVisualizationMesh(renderer, out var combinedBones))
            {
                ClearVisualization(renderer);
                return;
            }

            if (visualizationMesh.vertexCount == 0 || visualizationMesh.subMeshCount == 0)
            {
                ClearVisualization(renderer);
                return;
            }

            renderer.rootBone = root;
            renderer.bones = combinedBones.ToArray();
            renderer.localBounds = visualizationMesh.bounds;
            renderer.updateWhenOffscreen = true;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sharedMaterial = visualizationMaterial;
            renderer.sharedMesh = visualizationMesh;
            renderer.enabled = true;

            SceneView.RepaintAll();
        }

        private void EnsureVisualizationResources(Shader shader)
        {
            if (visualizationMesh == null)
            {
                visualizationMesh = new Mesh
                {
                    name = VisualizationObjectName,
                    hideFlags = HideFlags.HideAndDontSave
                };
                visualizationMesh.MarkDynamic();
            }

            if (visualizationMaterial != null && visualizationMaterial.shader != shader)
            {
                DestroyImmediate(visualizationMaterial);
                visualizationMaterial = null;
            }

            if (visualizationMaterial == null)
            {
                visualizationMaterial = new Material(shader)
                {
                    name = $"{VisualizationObjectName}Material",
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (visualizationMaterial.HasProperty("_Color"))
                {
                    visualizationMaterial.SetColor("_Color", new Color(1f, 0.25f, 0.2f, 0.35f));
                }
            }
        }

        private bool BuildVisualizationMesh(SkinnedMeshRenderer visualizationSkinnedMeshRenderer, out List<Transform> combinedBones)
        {
            visualizationMesh.Clear(false);

            combinedBones = new List<Transform>();
            var combinedVertices = new List<Vector3>();
            var combinedTriangles = new List<int>();
            var combinedBindPoses = new List<Matrix4x4>();
            var combinedBonesPerVertex = new List<byte>();
            var combinedBoneWeights = new List<BoneWeight1>();
            foreach (var result in cachedResults)
            {
                if (result.Renderer == null || result.Mesh == null)
                {
                    continue;
                }

                AppendAffectedTriangles(result, visualizationSkinnedMeshRenderer.transform, visualizationTriangleMatchCount, combinedVertices, combinedTriangles, combinedBindPoses, combinedBones, combinedBonesPerVertex, combinedBoneWeights);
            }

            if (combinedVertices.Count == 0 || combinedTriangles.Count == 0)
            {
                return false;
            }

            visualizationMesh.indexFormat = combinedVertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            visualizationMesh.SetVertices(combinedVertices);
            visualizationMesh.SetTriangles(combinedTriangles, 0, false);
            visualizationMesh.bindposes = combinedBindPoses.ToArray();
            SetCombinedBoneWeights(visualizationMesh, combinedBonesPerVertex, combinedBoneWeights);
            visualizationMesh.RecalculateBounds();
            return true;
        }

        private static void AppendAffectedTriangles(WeightedMeshResult result, Transform visualizationTransform, int requiredMatchedVertexCount, List<Vector3> combinedVertices, List<int> combinedTriangles, List<Matrix4x4> combinedBindPoses, List<Transform> combinedBones, List<byte> combinedBonesPerVertex, List<BoneWeight1> combinedBoneWeights)
        {
            var sourceMesh = result.Mesh;
            var sourceVertices = GetDeformedVertices(sourceMesh, result.Renderer);
            var sourceBindPoses = sourceMesh.bindposes;
            var sourceBones = result.Renderer.bones;
            if (sourceVertices.Length == 0 || result.AffectedVertexMask.Length != sourceVertices.Length)
            {
                return;
            }

            if (sourceBindPoses == null || sourceBones == null || sourceBindPoses.Length == 0 || sourceBindPoses.Length != sourceBones.Length)
            {
                return;
            }

            var bindPoseOffset = combinedBindPoses.Count;
            var sourceToTarget = visualizationTransform.worldToLocalMatrix * result.Renderer.transform.localToWorldMatrix;
            var targetToSource = sourceToTarget.inverse;
            for (var bindPoseIndex = 0; bindPoseIndex < sourceBindPoses.Length; bindPoseIndex++)
            {
                combinedBindPoses.Add(sourceBindPoses[bindPoseIndex] * targetToSource);
                combinedBones.Add(sourceBones[bindPoseIndex]);
            }

            using var sourceBonesPerVertex = sourceMesh.GetBonesPerVertex();
            using var sourceAllBoneWeights = sourceMesh.GetAllBoneWeights();
            var sourceBoneWeightStarts = BuildBoneWeightStarts(sourceBonesPerVertex);
            var vertexMap = new int[sourceVertices.Length];
            for (var vertexIndex = 0; vertexIndex < vertexMap.Length; vertexIndex++)
            {
                vertexMap[vertexIndex] = -1;
            }

            for (var subMeshIndex = 0; subMeshIndex < sourceMesh.subMeshCount; subMeshIndex++)
            {
                if (sourceMesh.GetTopology(subMeshIndex) != MeshTopology.Triangles)
                {
                    continue;
                }

                var triangles = sourceMesh.GetTriangles(subMeshIndex);
                for (var triangleIndex = 0; triangleIndex + 2 < triangles.Length; triangleIndex += 3)
                {
                    var index0 = triangles[triangleIndex];
                    var index1 = triangles[triangleIndex + 1];
                    var index2 = triangles[triangleIndex + 2];
                    if (!TriangleTouchesAffectedVertex(result.AffectedVertexMask, index0, index1, index2, requiredMatchedVertexCount))
                    {
                        continue;
                    }

                    combinedTriangles.Add(GetOrAddCombinedVertex(index0, vertexMap, sourceVertices, sourceToTarget, sourceBonesPerVertex, sourceAllBoneWeights, sourceBoneWeightStarts, bindPoseOffset, combinedVertices, combinedBonesPerVertex, combinedBoneWeights));
                    combinedTriangles.Add(GetOrAddCombinedVertex(index1, vertexMap, sourceVertices, sourceToTarget, sourceBonesPerVertex, sourceAllBoneWeights, sourceBoneWeightStarts, bindPoseOffset, combinedVertices, combinedBonesPerVertex, combinedBoneWeights));
                    combinedTriangles.Add(GetOrAddCombinedVertex(index2, vertexMap, sourceVertices, sourceToTarget, sourceBonesPerVertex, sourceAllBoneWeights, sourceBoneWeightStarts, bindPoseOffset, combinedVertices, combinedBonesPerVertex, combinedBoneWeights));
                }
            }
        }

        private static int[] BuildBoneWeightStarts(NativeArray<byte> bonesPerVertex)
        {
            var sourceBoneWeightStarts = new int[bonesPerVertex.Length];
            var sourceBoneWeightIndex = 0;
            for (var vertexIndex = 0; vertexIndex < bonesPerVertex.Length; vertexIndex++)
            {
                sourceBoneWeightStarts[vertexIndex] = sourceBoneWeightIndex;
                sourceBoneWeightIndex += bonesPerVertex[vertexIndex];
            }

            return sourceBoneWeightStarts;
        }

        private static bool TriangleTouchesAffectedVertex(bool[] affectedVertexMask, int index0, int index1, int index2, int requiredMatchedVertexCount)
        {
            var matchedVertexCount = 0;
            if (IsAffectedVertex(affectedVertexMask, index0))
            {
                matchedVertexCount++;
            }

            if (IsAffectedVertex(affectedVertexMask, index1))
            {
                matchedVertexCount++;
            }

            if (IsAffectedVertex(affectedVertexMask, index2))
            {
                matchedVertexCount++;
            }

            return matchedVertexCount >= Mathf.Clamp(requiredMatchedVertexCount, 1, 3);
        }

        private static bool IsAffectedVertex(bool[] affectedVertexMask, int vertexIndex)
        {
            return vertexIndex >= 0 && vertexIndex < affectedVertexMask.Length && affectedVertexMask[vertexIndex];
        }

        private static int GetOrAddCombinedVertex(int sourceVertexIndex, int[] vertexMap, Vector3[] sourceVertices, Matrix4x4 sourceToTarget, NativeArray<byte> sourceBonesPerVertex, NativeArray<BoneWeight1> sourceAllBoneWeights, int[] sourceBoneWeightStarts, int bindPoseOffset, List<Vector3> combinedVertices, List<byte> combinedBonesPerVertex, List<BoneWeight1> combinedBoneWeights)
        {
            var combinedVertexIndex = vertexMap[sourceVertexIndex];
            if (combinedVertexIndex >= 0)
            {
                return combinedVertexIndex;
            }

            combinedVertexIndex = combinedVertices.Count;
            vertexMap[sourceVertexIndex] = combinedVertexIndex;
            combinedVertices.Add(sourceToTarget.MultiplyPoint3x4(sourceVertices[sourceVertexIndex]));

            var boneInfluenceCount = sourceBonesPerVertex[sourceVertexIndex];
            combinedBonesPerVertex.Add(boneInfluenceCount);
            var sourceBoneWeightStart = sourceBoneWeightStarts[sourceVertexIndex];
            for (var influenceIndex = 0; influenceIndex < boneInfluenceCount; influenceIndex++)
            {
                var sourceBoneWeight = sourceAllBoneWeights[sourceBoneWeightStart + influenceIndex];
                sourceBoneWeight.boneIndex += bindPoseOffset;
                combinedBoneWeights.Add(sourceBoneWeight);
            }

            return combinedVertexIndex;
        }

        private static void SetCombinedBoneWeights(Mesh mesh, List<byte> bonesPerVertex, List<BoneWeight1> boneWeights)
        {
            using var nativeBonesPerVertex = new NativeArray<byte>(bonesPerVertex.ToArray(), Allocator.Temp);
            using var nativeBoneWeights = new NativeArray<BoneWeight1>(boneWeights.ToArray(), Allocator.Temp);

            mesh.SetBoneWeights(nativeBonesPerVertex, nativeBoneWeights);
        }

        private void ClearVisualization()
        {
            ClearVisualization(GetVisualizationRenderer(false));
        }

        private void ClearVisualization(SkinnedMeshRenderer renderer)
        {
            if (renderer == null)
            {
                return;
            }

            renderer.sharedMesh = null;
            renderer.sharedMaterial = null;
            renderer.enabled = false;
            SceneView.RepaintAll();
        }

        private SkinnedMeshRenderer GetVisualizationRenderer(bool createIfMissing)
        {
            if (visualizationRenderer != null)
            {
                return visualizationRenderer;
            }

            var visualizationObject = GameObject.Find(VisualizationObjectName);
            if (visualizationObject == null)
            {
                if (!createIfMissing)
                {
                    return null;
                }

                visualizationObject = new GameObject(VisualizationObjectName);
                visualizationObject.hideFlags = HideFlags.HideAndDontSave;
                Undo.RegisterCreatedObjectUndo(visualizationObject, $"Create {VisualizationObjectName}");
            }

            visualizationRenderer = visualizationObject.GetComponent<SkinnedMeshRenderer>();
            if (visualizationRenderer == null && createIfMissing)
            {
                visualizationRenderer = Undo.AddComponent<SkinnedMeshRenderer>(visualizationObject);
                visualizationRenderer.hideFlags = HideFlags.HideAndDontSave;
            }

            return visualizationRenderer;
        }
    }
}
#endif