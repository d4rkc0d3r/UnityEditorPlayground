#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace d4rkpl4y3r.AtlasToArray
{
    public class AtlasToArrayEditor : EditorWindow
    {
        private enum OutputCompressionMode
        {
            Uncompressed,
            SameAsSource,
            DXT1,
            DXT5,
            BC4,
            BC5,
            BC6H,
            BC7,
        }

        [Serializable]
        private sealed class TileRegion
        {
            public int x;
            public int y;
            public int width = 1;
            public int height = 1;

            public RectInt AsRect()
            {
                return new RectInt(x, y, Mathf.Max(1, width), Mathf.Max(1, height));
            }

            public bool Contains(Vector2Int tile)
            {
                RectInt rect = AsRect();
                return rect.Contains(tile);
            }
        }

        private Texture2D atlas;
        private int tileWidth = 16;
        private int tileHeight = 16;
        private bool hideTilesWithTransparency;
        private OutputCompressionMode compressionMode;
        private float previewZoom = 1f;
        private Vector2 inspectorScroll;
        private Vector2 previewScroll;
        private readonly List<TileRegion> regions = new List<TileRegion>();
        private readonly HashSet<int> transparentTileKeys = new HashSet<int>();
        private Texture2D cachedTransparencyAtlas;
        private int cachedTransparencyTileWidth = -1;
        private int cachedTransparencyTileHeight = -1;
        private int selectedRegionIndex = -1;
        private bool isDraggingSelection;
        private bool dragSelectionChanged;
        private int dragStartedOnRegionIndex = -1;
        private Vector2Int dragStartTile;
        private Vector2Int dragCurrentTile;

        [MenuItem("Tools/d4rkpl4y3r/Atlas To Texture Array")]
        public static void OpenWindow()
        {
            AtlasToArrayEditor window = GetWindow<AtlasToArrayEditor>();
            window.titleContent = new GUIContent("Atlas To Array");
            window.minSize = new Vector2(520f, 640f);
            window.Show();
        }

        private void OnGUI()
        {
            inspectorScroll = EditorGUILayout.BeginScrollView(inspectorScroll);

            DrawInputSection();
            EditorGUILayout.Space();
            DrawGenerateSection();
            EditorGUILayout.Space();
            DrawPreviewSection();
            EditorGUILayout.Space();
            DrawRegionSection();

            EditorGUILayout.EndScrollView();
        }

        private void DrawInputSection()
        {
            EditorGUILayout.LabelField("Atlas Input", EditorStyles.boldLabel);
            atlas = (Texture2D)EditorGUILayout.ObjectField("Atlas", atlas, typeof(Texture2D), false);

            tileWidth = EditorGUILayout.IntField("Tile Width", tileWidth);
            tileHeight = EditorGUILayout.IntField("Tile Height", tileHeight);
            hideTilesWithTransparency = EditorGUILayout.Toggle("Hide Transparent Tiles", hideTilesWithTransparency);
            compressionMode = (OutputCompressionMode)EditorGUILayout.EnumPopup("Compression", compressionMode);

            if (atlas == null)
            {
                EditorGUILayout.HelpBox("Assign a Texture2D atlas to start defining regions.", MessageType.Info);
                return;
            }

            if (!HasValidTileSize())
            {
                EditorGUILayout.HelpBox("Tile width and height must both be greater than zero.", MessageType.Warning);
                return;
            }

            string atlasPath = AssetDatabase.GetAssetPath(atlas);
            if (!string.IsNullOrEmpty(atlasPath))
            {
                EditorGUILayout.LabelField("Output", GetOutputAssetPath());
            }

            EditorGUILayout.LabelField("Grid", string.Format("{0} x {1} tiles", GetTileColumns(), GetTileRows()));
            if (atlas.width % tileWidth != 0 || atlas.height % tileHeight != 0)
            {
                EditorGUILayout.HelpBox("Atlas size is not evenly divisible by the tile size. Only fully covered tiles are selectable.", MessageType.Warning);
            }
        }

        private void DrawPreviewSection()
        {
            EditorGUILayout.LabelField("Region Picker", EditorStyles.boldLabel);

            if (atlas == null || !HasValidTileSize())
            {
                EditorGUILayout.HelpBox("The preview will appear once both the atlas and valid tile dimensions are set.", MessageType.None);
                return;
            }

            EditorGUILayout.HelpBox("Drag on the atlas to add a region. Left-click an existing region to select it. Right-click a region to remove it. Tile Y increases from top to bottom to match the preview.", MessageType.None);
            previewZoom = EditorGUILayout.Slider("Preview Zoom", previewZoom, 0.25f, 8f);

            float visibleHeight = Mathf.Min(position.height * 0.55f, 520f);
            previewScroll = EditorGUILayout.BeginScrollView(previewScroll, GUILayout.Height(visibleHeight));
            Rect previewRect = GUILayoutUtility.GetRect(atlas.width * previewZoom, atlas.height * previewZoom, GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false));

            EditorGUI.DrawPreviewTexture(previewRect, atlas, null, ScaleMode.StretchToFill);
            DrawTileGrid(previewRect);
            DrawRegions(previewRect);
            HandlePreviewInput(previewRect);

            EditorGUILayout.EndScrollView();
        }

        private void DrawRegionSection()
        {
            EditorGUILayout.LabelField("Regions", EditorStyles.boldLabel);

            if (GUILayout.Button("Add Empty Region"))
            {
                AddRegion(new RectInt(0, 0, 1, 1), true);
            }

            if (regions.Count == 0)
            {
                EditorGUILayout.HelpBox("No regions added yet.", MessageType.Info);
                return;
            }

            for (int i = 0; i < regions.Count; i++)
            {
                TileRegion region = regions[i];
                Color previousBackground = GUI.backgroundColor;
                if (i == selectedRegionIndex)
                {
                    GUI.backgroundColor = Color.Lerp(previousBackground, GetRegionColor(i), 0.5f);
                }

                EditorGUILayout.BeginVertical("box");
                GUI.backgroundColor = previousBackground;

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Toggle(i == selectedRegionIndex, string.Format("Region {0}", i + 1), "Button"))
                {
                    selectedRegionIndex = i;
                }

                if (GUILayout.Button("Remove", GUILayout.Width(80f)))
                {
                    RemoveRegionAt(i);

                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                    break;
                }
                EditorGUILayout.EndHorizontal();

                region.x = EditorGUILayout.IntField("Tile X", region.x);
                region.y = EditorGUILayout.IntField("Tile Y", region.y);
                region.width = Mathf.Max(1, EditorGUILayout.IntField("Width", region.width));
                region.height = Mathf.Max(1, EditorGUILayout.IntField("Height", region.height));
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawGenerateSection()
        {
            EditorGUILayout.LabelField("Generate", EditorStyles.boldLabel);

            int uniqueTileCount;
            string validationMessage;
            bool canGenerate = TryCollectUniqueTiles(out uniqueTileCount, out validationMessage, false);
            string compressionValidationMessage;
            bool compressionIsValid = TryValidateOutputFormat(out compressionValidationMessage);
            canGenerate &= compressionIsValid;

            EditorGUILayout.LabelField("Unique Tiles", uniqueTileCount.ToString());
            if (!string.IsNullOrEmpty(validationMessage))
            {
                EditorGUILayout.HelpBox(validationMessage, canGenerate ? MessageType.Info : MessageType.Warning);
            }

            if (!string.IsNullOrEmpty(compressionValidationMessage))
            {
                EditorGUILayout.HelpBox(compressionValidationMessage, compressionIsValid ? MessageType.Info : MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                if (GUILayout.Button("Generate Texture Array", GUILayout.Height(32f)))
                {
                    GenerateTextureArray();
                }
            }
        }

        private void DrawTileGrid(Rect previewRect)
        {
            int columns = GetTileColumns();
            int rows = GetTileRows();
            if (columns <= 0 || rows <= 0)
            {
                return;
            }

            Handles.BeginGUI();
            Color previousColor = Handles.color;
            Handles.color = new Color(1f, 1f, 1f, 0.2f);

            float stepX = tileWidth * previewZoom;
            float stepY = tileHeight * previewZoom;
            for (int x = 0; x <= columns; x++)
            {
                float drawX = previewRect.xMin + (x * stepX);
                Handles.DrawLine(new Vector3(drawX, previewRect.yMin), new Vector3(drawX, previewRect.yMin + (rows * stepY)));
            }

            for (int y = 0; y <= rows; y++)
            {
                float drawY = previewRect.yMin + (y * stepY);
                Handles.DrawLine(new Vector3(previewRect.xMin, drawY), new Vector3(previewRect.xMin + (columns * stepX), drawY));
            }

            Handles.color = previousColor;
            Handles.EndGUI();

            DrawTransparentTileMask(previewRect, columns, rows);
        }

        private void DrawRegions(Rect previewRect)
        {
            for (int i = 0; i < regions.Count; i++)
            {
                Rect regionRect = TileRectToPreviewRect(regions[i].AsRect(), previewRect);
                Color fill = GetRegionColor(i);
                fill.a = i == selectedRegionIndex ? 0.35f : 0.2f;
                EditorGUI.DrawRect(regionRect, fill);
                DrawRectOutline(regionRect, i == selectedRegionIndex ? Color.white : GetRegionColor(i), i == selectedRegionIndex ? 2f : 1f);
            }

            if (isDraggingSelection)
            {
                Rect dragRect = TileRectToPreviewRect(CreateSelectionRect(dragStartTile, dragCurrentTile), previewRect);
                Color dragColor = new Color(1f, 1f, 1f, 0.15f);
                EditorGUI.DrawRect(dragRect, dragColor);
                DrawRectOutline(dragRect, Color.white, 2f);
            }
        }

        private void HandlePreviewInput(Rect previewRect)
        {
            Event currentEvent = Event.current;
            if (!previewRect.Contains(currentEvent.mousePosition))
            {
                return;
            }

            Vector2Int hoveredTile;
            if (!TryGetTileAtMouse(previewRect, currentEvent.mousePosition, out hoveredTile))
            {
                return;
            }

            if (currentEvent.type == EventType.MouseDown && currentEvent.button == 1)
            {
                int hoveredRegionIndex = FindRegionContainingTile(hoveredTile);
                if (hoveredRegionIndex >= 0)
                {
                    RemoveRegionAt(hoveredRegionIndex);
                    isDraggingSelection = false;
                    dragSelectionChanged = false;
                    dragStartedOnRegionIndex = -1;
                    currentEvent.Use();
                }

                return;
            }

            if (currentEvent.type == EventType.MouseDown && currentEvent.button == 0)
            {
                isDraggingSelection = true;
                dragSelectionChanged = false;
                dragStartTile = hoveredTile;
                dragCurrentTile = hoveredTile;
                int hoveredRegionIndex = FindRegionContainingTile(hoveredTile);
                dragStartedOnRegionIndex = hoveredRegionIndex;
                if (hoveredRegionIndex >= 0)
                {
                    selectedRegionIndex = hoveredRegionIndex;
                }
                currentEvent.Use();
            }
            else if (currentEvent.type == EventType.MouseDrag && currentEvent.button == 0 && isDraggingSelection)
            {
                dragCurrentTile = hoveredTile;
                dragSelectionChanged = dragSelectionChanged || dragCurrentTile != dragStartTile;
                Repaint();
                currentEvent.Use();
            }
            else if (currentEvent.type == EventType.MouseUp && currentEvent.button == 0 && isDraggingSelection)
            {
                dragCurrentTile = hoveredTile;
                if (dragSelectionChanged || dragStartedOnRegionIndex < 0)
                {
                    RectInt selection = CreateSelectionRect(dragStartTile, dragCurrentTile);
                    AddRegion(selection, true);
                }

                isDraggingSelection = false;
                dragSelectionChanged = false;
                dragStartedOnRegionIndex = -1;
                currentEvent.Use();
            }
        }

        private void AddRegion(RectInt selection, bool selectNewRegion)
        {
            TileRegion region = new TileRegion();
            region.x = selection.x;
            region.y = selection.y;
            region.width = Mathf.Max(1, selection.width);
            region.height = Mathf.Max(1, selection.height);
            regions.Add(region);

            if (selectNewRegion)
            {
                selectedRegionIndex = regions.Count - 1;
            }
        }

        private void RemoveRegionAt(int index)
        {
            if (index < 0 || index >= regions.Count)
            {
                return;
            }

            regions.RemoveAt(index);
            if (selectedRegionIndex == index)
            {
                selectedRegionIndex = -1;
            }
            else if (selectedRegionIndex > index)
            {
                selectedRegionIndex--;
            }
        }

        private int FindRegionContainingTile(Vector2Int tile)
        {
            for (int i = regions.Count - 1; i >= 0; i--)
            {
                if (regions[i].Contains(tile))
                {
                    return i;
                }
            }

            return -1;
        }

        private bool TryGetTileAtMouse(Rect previewRect, Vector2 mousePosition, out Vector2Int tile)
        {
            tile = Vector2Int.zero;
            if (!HasValidTileSize())
            {
                return false;
            }

            float localX = (mousePosition.x - previewRect.xMin) / previewZoom;
            float localY = (mousePosition.y - previewRect.yMin) / previewZoom;
            int tileX = Mathf.FloorToInt(localX / tileWidth);
            int tileY = Mathf.FloorToInt(localY / tileHeight);

            if (tileX < 0 || tileY < 0 || tileX >= GetTileColumns() || tileY >= GetTileRows())
            {
                return false;
            }

            tile = new Vector2Int(tileX, tileY);
            return true;
        }

        private RectInt CreateSelectionRect(Vector2Int startTile, Vector2Int endTile)
        {
            int minX = Mathf.Min(startTile.x, endTile.x);
            int minY = Mathf.Min(startTile.y, endTile.y);
            int maxX = Mathf.Max(startTile.x, endTile.x);
            int maxY = Mathf.Max(startTile.y, endTile.y);
            return new RectInt(minX, minY, (maxX - minX) + 1, (maxY - minY) + 1);
        }

        private Rect TileRectToPreviewRect(RectInt tileRect, Rect previewRect)
        {
            return new Rect(
                previewRect.xMin + (tileRect.x * tileWidth * previewZoom),
                previewRect.yMin + (tileRect.y * tileHeight * previewZoom),
                tileRect.width * tileWidth * previewZoom,
                tileRect.height * tileHeight * previewZoom);
        }

        private void DrawRectOutline(Rect rect, Color color, float thickness)
        {
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMin, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMax - thickness, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMin, thickness, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - thickness, rect.yMin, thickness, rect.height), color);
        }

        private bool TryCollectUniqueTiles(out int uniqueTileCount, out string message, bool requireStrictValidity)
        {
            uniqueTileCount = 0;
            message = string.Empty;

            if (atlas == null)
            {
                message = "Assign an atlas before generating.";
                return false;
            }

            if (!HasValidTileSize())
            {
                message = "Tile width and height must both be greater than zero.";
                return false;
            }

            if (regions.Count == 0)
            {
                message = "Add at least one region.";
                return false;
            }

            int columns = GetTileColumns();
            int rows = GetTileRows();
            HashSet<int> seenTiles = new HashSet<int>();
            bool skippedTransparentTiles = false;

            EnsureTransparentTileCache();

            for (int regionIndex = 0; regionIndex < regions.Count; regionIndex++)
            {
                RectInt rect = regions[regionIndex].AsRect();
                if (rect.x < 0 || rect.y < 0 || rect.xMax > columns || rect.yMax > rows)
                {
                    message = string.Format("Region {0} extends outside the atlas tile grid.", regionIndex + 1);
                    return false;
                }

                for (int y = rect.yMin; y < rect.yMax; y++)
                {
                    for (int x = rect.xMin; x < rect.xMax; x++)
                    {
                        int key = (y * columns) + x;
                        if (ShouldHideTile(key))
                        {
                            skippedTransparentTiles = true;
                            continue;
                        }

                        seenTiles.Add(key);
                    }
                }
            }

            uniqueTileCount = seenTiles.Count;
            if (uniqueTileCount == 0)
            {
                message = "No valid tiles were found inside the selected regions.";
                return false;
            }

            if (!requireStrictValidity && uniqueTileCount < GetSelectedTileCount())
            {
                message = skippedTransparentTiles
                    ? "Overlapping regions and tiles with transparent pixels are excluded from the generated array."
                    : "Overlapping regions will be deduplicated when the array is generated.";
            }
            else if (!requireStrictValidity && skippedTransparentTiles)
            {
                message = "Tiles with transparent pixels are excluded from the generated array.";
            }

            return true;
        }

        private int GetSelectedTileCount()
        {
            int total = 0;
            EnsureTransparentTileCache();
            int columns = GetTileColumns();

            for (int i = 0; i < regions.Count; i++)
            {
                RectInt rect = regions[i].AsRect();
                for (int y = rect.yMin; y < rect.yMax; y++)
                {
                    for (int x = rect.xMin; x < rect.xMax; x++)
                    {
                        if (!ShouldHideTile((y * columns) + x))
                        {
                            total++;
                        }
                    }
                }
            }

            return total;
        }

        private void GenerateTextureArray()
        {
            int uniqueTileCount;
            string message;
            if (!TryCollectUniqueTiles(out uniqueTileCount, out message, true))
            {
                EditorUtility.DisplayDialog("Atlas To Array", message, "OK");
                return;
            }

            string assetPath = GetOutputAssetPath();
            if (string.IsNullOrEmpty(assetPath))
            {
                EditorUtility.DisplayDialog("Atlas To Array", "The atlas must be saved as a project asset before creating a texture array.", "OK");
                return;
            }

            TextureFormat outputFormat;
            string formatValidationMessage;
            if (!TryGetOutputTextureFormat(out outputFormat, out formatValidationMessage))
            {
                EditorUtility.DisplayDialog("Atlas To Array", formatValidationMessage, "OK");
                return;
            }

            Texture2D readableAtlas = null;

            try
            {
                List<Vector2Int> tiles = CollectUniqueTilesInOrder();
                readableAtlas = CreateReadableCopy(atlas);

                Texture2DArray textureArray = new Texture2DArray(tileWidth, tileHeight, tiles.Count, outputFormat, true, false);
                textureArray.filterMode = atlas.filterMode;
                textureArray.wrapMode = atlas.wrapMode;
                textureArray.anisoLevel = atlas.anisoLevel;
                textureArray.name = atlas.name + "_array";

                for (int i = 0; i < tiles.Count; i++)
                {
                    Vector2Int tile = tiles[i];
                    int pixelX = tile.x * tileWidth;
                    int pixelY = atlas.height - ((tile.y + 1) * tileHeight);
                    Color[] pixels = readableAtlas.GetPixels(pixelX, pixelY, tileWidth, tileHeight);

                    Texture2D sliceTexture = CreateSliceTexture(pixels, outputFormat);
                    try
                    {
                        for (int mipLevel = 0; mipLevel < sliceTexture.mipmapCount; mipLevel++)
                        {
                            Graphics.CopyTexture(sliceTexture, 0, mipLevel, textureArray, i, mipLevel);
                        }
                    }
                    finally
                    {
                        DestroyImmediate(sliceTexture);
                    }
                }

                textureArray.Apply(false, true);

                if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
                {
                    AssetDatabase.DeleteAsset(assetPath);
                }

                AssetDatabase.CreateAsset(textureArray, assetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Selection.activeObject = textureArray;
                EditorGUIUtility.PingObject(textureArray);
                ShowNotification(new GUIContent(string.Format("Generated {0} slices.", uniqueTileCount)));
            }
            finally
            {
                if (readableAtlas != null)
                {
                    DestroyImmediate(readableAtlas);
                }
            }
        }

        private List<Vector2Int> CollectUniqueTilesInOrder()
        {
            List<Vector2Int> orderedTiles = new List<Vector2Int>();
            HashSet<int> seenTiles = new HashSet<int>();
            int columns = GetTileColumns();

            EnsureTransparentTileCache();

            for (int regionIndex = 0; regionIndex < regions.Count; regionIndex++)
            {
                RectInt rect = regions[regionIndex].AsRect();
                for (int y = rect.yMin; y < rect.yMax; y++)
                {
                    for (int x = rect.xMin; x < rect.xMax; x++)
                    {
                        int key = (y * columns) + x;
                        if (ShouldHideTile(key))
                        {
                            continue;
                        }

                        if (seenTiles.Add(key))
                        {
                            orderedTiles.Add(new Vector2Int(x, y));
                        }
                    }
                }
            }

            return orderedTiles;
        }

        private Texture2D CreateReadableCopy(Texture2D source)
        {
            RenderTexture temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;

            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;

            Texture2D readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, false);
            readable.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
            readable.Apply(false, false);

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
            return readable;
        }

        private Texture2D CreateSliceTexture(Color[] pixels, TextureFormat outputFormat)
        {
            Texture2D sliceTexture = new Texture2D(tileWidth, tileHeight, TextureFormat.RGBA32, true, false);
            sliceTexture.SetPixels(pixels, 0);
            sliceTexture.Apply(true, false);

            if (outputFormat != TextureFormat.RGBA32)
            {
                EditorUtility.CompressTexture(sliceTexture, outputFormat, TextureCompressionQuality.Normal);
                sliceTexture.Apply(false, false);
            }

            return sliceTexture;
        }

        private void DrawTransparentTileMask(Rect previewRect, int columns, int rows)
        {
            if (!hideTilesWithTransparency)
            {
                return;
            }

            EnsureTransparentTileCache();
            if (transparentTileKeys.Count == 0)
            {
                return;
            }

            Color hiddenTileColor = new Color(0f, 0f, 0f, 0.85f);
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < columns; x++)
                {
                    int key = (y * columns) + x;
                    if (!transparentTileKeys.Contains(key))
                    {
                        continue;
                    }

                    Rect hiddenTileRect = TileRectToPreviewRect(new RectInt(x, y, 1, 1), previewRect);
                    EditorGUI.DrawRect(hiddenTileRect, hiddenTileColor);
                }
            }
        }

        private bool ShouldHideTile(int tileKey)
        {
            return hideTilesWithTransparency && transparentTileKeys.Contains(tileKey);
        }

        private void EnsureTransparentTileCache()
        {
            if (!HasValidTileSize() || atlas == null)
            {
                transparentTileKeys.Clear();
                cachedTransparencyAtlas = null;
                cachedTransparencyTileWidth = -1;
                cachedTransparencyTileHeight = -1;
                return;
            }

            if (cachedTransparencyAtlas == atlas && cachedTransparencyTileWidth == tileWidth && cachedTransparencyTileHeight == tileHeight)
            {
                return;
            }

            transparentTileKeys.Clear();
            cachedTransparencyAtlas = atlas;
            cachedTransparencyTileWidth = tileWidth;
            cachedTransparencyTileHeight = tileHeight;

            int columns = GetTileColumns();
            int rows = GetTileRows();
            if (columns <= 0 || rows <= 0)
            {
                return;
            }

            Texture2D readableAtlas = null;
            try
            {
                readableAtlas = CreateReadableCopy(atlas);
                for (int tileY = 0; tileY < rows; tileY++)
                {
                    for (int tileX = 0; tileX < columns; tileX++)
                    {
                        int pixelX = tileX * tileWidth;
                        int pixelY = atlas.height - ((tileY + 1) * tileHeight);
                        Color[] pixels = readableAtlas.GetPixels(pixelX, pixelY, tileWidth, tileHeight);
                        for (int pixelIndex = 0; pixelIndex < pixels.Length; pixelIndex++)
                        {
                            if (pixels[pixelIndex].a < 1f)
                            {
                                transparentTileKeys.Add((tileY * columns) + tileX);
                                break;
                            }
                        }
                    }
                }
            }
            finally
            {
                if (readableAtlas != null)
                {
                    DestroyImmediate(readableAtlas);
                }
            }
        }

        private string GetOutputAssetPath()
        {
            if (atlas == null)
            {
                return string.Empty;
            }

            string atlasPath = AssetDatabase.GetAssetPath(atlas);
            if (string.IsNullOrEmpty(atlasPath))
            {
                return string.Empty;
            }

            string directory = System.IO.Path.GetDirectoryName(atlasPath);
            string fileName = System.IO.Path.GetFileNameWithoutExtension(atlasPath);
            return string.Format("{0}/{1}_array.asset", directory.Replace('\\', '/'), fileName);
        }

        private bool TryValidateOutputFormat(out string message)
        {
            TextureFormat outputFormat;
            return TryGetOutputTextureFormat(out outputFormat, out message);
        }

        private bool TryGetOutputTextureFormat(out TextureFormat outputFormat, out string message)
        {
            outputFormat = TextureFormat.RGBA32;
            message = string.Empty;

            if (atlas == null)
            {
                return true;
            }

            switch (compressionMode)
            {
                case OutputCompressionMode.Uncompressed:
                    outputFormat = TextureFormat.RGBA32;
                    break;
                case OutputCompressionMode.SameAsSource:
                    outputFormat = atlas.format;
                    break;
                case OutputCompressionMode.DXT1:
                    outputFormat = TextureFormat.DXT1;
                    break;
                case OutputCompressionMode.DXT5:
                    outputFormat = TextureFormat.DXT5;
                    break;
                case OutputCompressionMode.BC4:
                    outputFormat = TextureFormat.BC4;
                    break;
                case OutputCompressionMode.BC5:
                    outputFormat = TextureFormat.BC5;
                    break;
                case OutputCompressionMode.BC6H:
                    outputFormat = TextureFormat.BC6H;
                    break;
                case OutputCompressionMode.BC7:
                    outputFormat = TextureFormat.BC7;
                    break;
                default:
                    message = "Unsupported compression mode selected.";
                    return false;
            }

            if (!SystemInfo.SupportsTextureFormat(outputFormat))
            {
                message = string.Format("{0} is not supported on this platform.", outputFormat);
                return false;
            }

            if (RequiresFourPixelBlocks(outputFormat) && ((tileWidth % 4) != 0 || (tileHeight % 4) != 0))
            {
                message = string.Format("{0} requires tile width and height to be multiples of 4.", outputFormat);
                return false;
            }

            if (compressionMode == OutputCompressionMode.SameAsSource && outputFormat == TextureFormat.RGBA32)
            {
                message = "The source texture is uncompressed, so Same As Source will generate an uncompressed RGBA32 array.";
            }

            return true;
        }

        private bool RequiresFourPixelBlocks(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT5:
                case TextureFormat.BC4:
                case TextureFormat.BC5:
                case TextureFormat.BC6H:
                case TextureFormat.BC7:
                    return true;
                default:
                    return false;
            }
        }

        private bool HasValidTileSize()
        {
            return tileWidth > 0 && tileHeight > 0;
        }

        private int GetTileColumns()
        {
            return atlas == null || tileWidth <= 0 ? 0 : atlas.width / tileWidth;
        }

        private int GetTileRows()
        {
            return atlas == null || tileHeight <= 0 ? 0 : atlas.height / tileHeight;
        }

        private Color GetRegionColor(int index)
        {
            float hue = Mathf.Repeat(index * 0.173f, 1f);
            return Color.HSVToRGB(hue, 0.7f, 1f);
        }

    }
}
#endif