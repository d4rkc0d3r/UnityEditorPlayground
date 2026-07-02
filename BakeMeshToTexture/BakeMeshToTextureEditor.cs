#if UNITY_EDITOR
using System.IO;
using UnityEngine;
using UnityEditor;

namespace d4rkpl4y3r.BakeMeshToTexture
{
    public class BakeMeshToTextureEditor : EditorWindow
    {
        [SerializeField] private Mesh _mesh;
        [SerializeField] private string _outputPath = "";
        [SerializeField] private bool _normalizePositions = false;

        private float _flatDataLength;
        private int _textureWidth;
        private int _textureHeight;

        private Vector2 _scrollPosition;

        private void Initialize()
        {
            if (_mesh != null && string.IsNullOrEmpty(_outputPath))
            {
                _outputPath = GetOutputPath(_mesh);
            }
            CalculatePreview();
        }

        private static string GetFileNameWithoutExtension(string assetPath)
        {
            return Path.GetFileNameWithoutExtension(assetPath);
        }

        private static string GetOutputPath(Mesh mesh)
        {
            string assetPath = AssetDatabase.GetAssetPath(mesh);
            string fileName = string.IsNullOrEmpty(assetPath) ? mesh.name : GetFileNameWithoutExtension(assetPath);
            return $"Assets/Meshes/BakedTextures/{fileName}.asset";
        }

        private void CalculatePreview()
        {
            if (_mesh == null)
            {
                _flatDataLength = 0;
                _textureWidth = 0;
                _textureHeight = 0;
                return;
            }

            int triCount = _mesh.GetTriangles(0).Length / 3;
            int vertexCount = _mesh.vertexCount;

            // 1 (triangle count) + 3 * triCount (indices) + 8 * vertexCount (vertex data)
            int flatDataLength = 1 + 3 * triCount + 8 * vertexCount;
            _flatDataLength = flatDataLength;

            _textureWidth = Mathf.CeilToInt(Mathf.Sqrt(flatDataLength));
            _textureWidth = Mathf.NextPowerOfTwo(_textureWidth);
            _textureHeight = Mathf.CeilToInt((float)flatDataLength / _textureWidth);
        }

        [MenuItem("Tools/d4rkpl4y3r/Bake Mesh to Texture")]
        public static void ShowWindow()
        {
            GetWindow<BakeMeshToTextureEditor>("Bake Mesh to Texture");
        }

        private void OnGUI()
        {
            Initialize();

            EditorGUILayout.LabelField("Bake Mesh to Texture", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            _mesh = (Mesh)EditorGUILayout.ObjectField("Mesh", _mesh, typeof(Mesh), false);

            if (_mesh != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(_mesh);
                string fileName = string.IsNullOrEmpty(assetPath) ? _mesh.name : GetFileNameWithoutExtension(assetPath);
                if (string.IsNullOrEmpty(_outputPath) || !_outputPath.Contains(fileName))
                {
                    _outputPath = GetOutputPath(_mesh);
                }
            }

            _outputPath = EditorGUILayout.TextField("Output Path", _outputPath);

            EditorGUILayout.Space();

            _normalizePositions = EditorGUILayout.Toggle("Normalize Positions", _normalizePositions);

            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Flat Data Length: " + _flatDataLength);
            EditorGUILayout.LabelField("Texture Width: " + _textureWidth);
            EditorGUILayout.LabelField("Texture Height: " + _textureHeight);

            EditorGUILayout.Space();

            EditorGUI.BeginDisabledGroup(_mesh == null);
            if (GUILayout.Button("Bake"))
            {
                Bake();
            }
            EditorGUI.EndDisabledGroup();
        }

        private void Bake()
        {
            if (_mesh == null)
            {
                EditorUtility.DisplayDialog("Bake Mesh to Texture", "Please select a mesh.", "OK");
                return;
            }

            int[] triangles = _mesh.GetTriangles(0);
            int triCount = triangles.Length / 3;
            int vertexCount = _mesh.vertexCount;
            Vector3[] vertices = _mesh.vertices;
            Vector3[] normals = _mesh.normals;
            Vector2[] uvs = _mesh.uv;

            // Normalize vertex positions if enabled
            if (_normalizePositions)
            {
                // Calculate bounding box center
                Vector3 min = vertices[0];
                Vector3 max = vertices[0];
                for (int i = 1; i < vertexCount; i++)
                {
                    min = Vector3.Min(min, vertices[i]);
                    max = Vector3.Max(max, vertices[i]);
                }
                Vector3 center = (min + max) * 0.5f;

                // Subtract center from all vertices
                for (int i = 0; i < vertexCount; i++)
                {
                    vertices[i] -= center;
                }

                // Find the max absolute component to determine scale
                float maxAbs = 0;
                for (int i = 0; i < vertexCount; i++)
                {
                    maxAbs = Mathf.Max(maxAbs, Mathf.Abs(vertices[i].x));
                    maxAbs = Mathf.Max(maxAbs, Mathf.Abs(vertices[i].y));
                    maxAbs = Mathf.Max(maxAbs, Mathf.Abs(vertices[i].z));
                }

                // Rescale so all vertices are within -0.5 to 0.5 range
                if (maxAbs > 0)
                {
                    float scale = 0.5f / maxAbs;
                    for (int i = 0; i < vertexCount; i++)
                    {
                        vertices[i] *= scale;
                    }
                }
            }

            int flatDataLength = 1 + 3 * triCount + 8 * vertexCount;
            int width = Mathf.CeilToInt(Mathf.Sqrt(flatDataLength));
            width = Mathf.NextPowerOfTwo(width);
            int height = Mathf.CeilToInt((float)flatDataLength / width);

            float[] flatData = new float[width * height];
            int index = 0;

            // Triangle count
            flatData[index++] = triCount;

            // Triangle vertex indices
            foreach (int vertIdx in triangles)
            {
                flatData[index++] = vertIdx;
            }

            // Vertex data: pos (3) + normal (3) + uv0 (2) = 8 per vertex
            for (int i = 0; i < vertexCount; i++)
            {
                flatData[index++] = vertices[i].x;
                flatData[index++] = vertices[i].y;
                flatData[index++] = vertices[i].z;

                flatData[index++] = normals[i].x;
                flatData[index++] = normals[i].y;
                flatData[index++] = normals[i].z;

                flatData[index++] = uvs[i].x;
                flatData[index++] = uvs[i].y;
            }

            // Create texture
            Texture2D texture = new Texture2D(width, height, TextureFormat.RFloat, false);
            texture.filterMode = FilterMode.Point;
            texture.SetPixelData(flatData, 0);
            texture.Apply(false);

            // Ensure directory exists and is visible to the asset database
            string directory = Path.GetDirectoryName(_outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                AssetDatabase.Refresh();
            }

            // Create the texture asset
            AssetDatabase.CreateAsset(texture, _outputPath);

            Object asset = AssetDatabase.LoadAssetAtPath<Object>(_outputPath);
            if (asset != null)
            {
                EditorGUIUtility.PingObject(asset);
            }

            EditorUtility.DisplayDialog("Bake Mesh to Texture", 
                $"Successfully baked mesh '{_mesh.name}' to:\n{_outputPath}\n\n" +
                $"Flat Data Length: {_flatDataLength}\n" +
                $"Texture Size: {_textureWidth}x{_textureHeight}", "OK");
        }
    }
}
#endif