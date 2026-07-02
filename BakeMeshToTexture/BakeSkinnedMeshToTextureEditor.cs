#if UNITY_EDITOR
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;

namespace d4rkpl4y3r.BakeMeshToTexture
{
    public class BakeSkinnedMeshToTextureEditor : EditorWindow
    {
        [SerializeField] private SkinnedMeshRenderer _skinnedMeshRenderer;
        [SerializeField] private string _outputName = "";

        private float _flatDataLength;
        private int _textureWidth;
        private int _textureHeight;

        private int _triCount;
        private int _vertexCount;
        private int _boneCount;
        private int _sizeCounts;
        private int _sizeIndices;
        private int _sizeVertices;
        private int _sizeBindposes;

        private Vector2 _scrollPosition;

        private void Initialize()
        {
            if (_skinnedMeshRenderer != null && string.IsNullOrEmpty(_outputName))
            {
                _outputName = GetOutputName(_skinnedMeshRenderer);
            }
            CalculatePreview();
        }

        private static string GetFileNameWithoutExtension(string assetPath)
        {
            return Path.GetFileNameWithoutExtension(assetPath);
        }

        private static string GetOutputName(SkinnedMeshRenderer smr)
        {
            string assetPath = AssetDatabase.GetAssetPath(smr);
            if (string.IsNullOrEmpty(assetPath))
            {
                GameObject prefabRoot = UnityEditor.PrefabUtility.GetOutermostPrefabInstanceRoot(smr.gameObject);
                if (prefabRoot != null)
                {
                    GameObject prefabAsset = UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(prefabRoot);
                    if (prefabAsset != null)
                        assetPath = AssetDatabase.GetAssetPath(prefabAsset);
                }
            }
            return string.IsNullOrEmpty(assetPath) ? smr.gameObject.name : GetFileNameWithoutExtension(assetPath);
        }

        private static string GetFullPath(string name)
        {
            string[] guids = AssetDatabase.FindAssets("BakeSkinnedMeshToTextureEditor t:MonoScript");
            if (guids.Length == 0)
            {
                return $"Assets/Output/{name}/MeshData.asset";
            }

            string scriptPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            string scriptDir = Path.GetDirectoryName(scriptPath);
            string outputDir = Path.Combine(scriptDir, "../Output", name);

            // Resolve the path to remove "../" segments
            string fullPath = Path.GetFullPath(outputDir);
            string projectRoot = Application.dataPath.Replace("/Assets", "");
            string relativePath = Path.GetRelativePath(projectRoot, fullPath).Replace('\\', '/');
            return $"{relativePath}/MeshData.asset";
        }

       private void CalculatePreview()
        {
            if (_skinnedMeshRenderer == null)
            {
                _flatDataLength = 0;
                _textureWidth = 0;
                _textureHeight = 0;
                _triCount = 0;
                _vertexCount = 0;
                _boneCount = 0;
                _sizeCounts = 0;
                _sizeIndices = 0;
                _sizeVertices = 0;
                _sizeBindposes = 0;
                return;
            }

            Mesh mesh = new Mesh();
            _skinnedMeshRenderer.BakeMesh(mesh);

            _triCount = mesh.GetTriangles(0).Length / 3;
            _vertexCount = mesh.vertexCount;
            _boneCount = _skinnedMeshRenderer.bones.Length;

            _sizeCounts = 3;
            _sizeIndices = 3 * _triCount;
            _sizeVertices = 20 * _vertexCount;
            _sizeBindposes = 16 * _boneCount;

            // 3 (counts: tri, vertex, bone) + 3 * triCount (indices) + 20 * vertexCount (vertex data) + 16 * boneCount (bindposes)
            int flatDataLength = _sizeCounts + _sizeIndices + _sizeVertices + _sizeBindposes;
            _flatDataLength = flatDataLength;

            _textureWidth = Mathf.CeilToInt(Mathf.Sqrt(flatDataLength));
            _textureWidth = Mathf.NextPowerOfTwo(_textureWidth);
            _textureHeight = Mathf.CeilToInt((float)flatDataLength / _textureWidth);

            Object.DestroyImmediate(mesh);
        }

        [MenuItem("Tools/d4rkpl4y3r/Bake Skinned Mesh to Texture")]
        public static void ShowWindow()
        {
            GetWindow<BakeSkinnedMeshToTextureEditor>("Bake Skinned Mesh to Texture");
        }

        private void OnGUI()
        {
            Initialize();

            EditorGUILayout.LabelField("Bake Skinned Mesh to Texture", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            _skinnedMeshRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                "Skinned Mesh Renderer", _skinnedMeshRenderer, typeof(SkinnedMeshRenderer), true);

            if (_skinnedMeshRenderer != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(_skinnedMeshRenderer.gameObject);
                string fileName = string.IsNullOrEmpty(assetPath) ? _skinnedMeshRenderer.gameObject.name 
                    : GetFileNameWithoutExtension(assetPath);
                if (string.IsNullOrEmpty(_outputName) || _outputName != fileName)
                {
                    _outputName = GetOutputName(_skinnedMeshRenderer);
                }
            }

            _outputName = EditorGUILayout.TextField("Output Name", _outputName);
            EditorGUILayout.LabelField("Output Path: " + GetFullPath(_outputName), EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("Triangles: " + _triCount);
            EditorGUILayout.LabelField("Vertices: " + _vertexCount);
            EditorGUILayout.LabelField("Bones: " + _boneCount);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Data Layout Breakdown:", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("  Counts (tri + vert + bone): " + _sizeCounts);
            EditorGUILayout.LabelField("  Index Data (" + _triCount + " × 3): " + _sizeIndices);
            EditorGUILayout.LabelField("  Vertex Data (" + _vertexCount + " × 20): " + _sizeVertices);
            EditorGUILayout.LabelField("  Bindposes (" + _boneCount + " × 16): " + _sizeBindposes);
            EditorGUI.indentLevel--;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Total Flat Data Length: " + _flatDataLength);
            EditorGUILayout.LabelField("Texture Width: " + _textureWidth);
            EditorGUILayout.LabelField("Texture Height: " + _textureHeight);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space();

            EditorGUI.BeginDisabledGroup(_skinnedMeshRenderer == null);
            if (GUILayout.Button("Bake"))
            {
                Bake();
            }
            EditorGUI.EndDisabledGroup();
        }

        private void Bake()
        {
            if (_skinnedMeshRenderer == null)
            {
                EditorUtility.DisplayDialog("Bake Skinned Mesh to Texture", "Please select a Skinned Mesh Renderer.", "OK");
                return;
            }

            // Bake mesh from SkinnedMeshRenderer
            Mesh mesh = new Mesh();
            _skinnedMeshRenderer.BakeMesh(mesh);

            int[] triangles = mesh.GetTriangles(0);
            int triCount = triangles.Length / 3;
            int vertexCount = mesh.vertexCount;
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector2[] uvs = mesh.uv;
            Vector4[] tangents = mesh.tangents;
            BoneWeight[] boneWeights = _skinnedMeshRenderer.sharedMesh.boneWeights;
            Transform[] bones = _skinnedMeshRenderer.bones;
            int boneCount = bones.Length;

            // Get root transform (SkinnedMeshRenderer's transform)
            Transform rootTransform = _skinnedMeshRenderer.transform;
            Transform rootBone = _skinnedMeshRenderer.rootBone;

            // Compute the transformation from root bone space to root transform space
            // rootBoneToWorld = root bone's world transform matrix
            Matrix4x4 rootBoneToWorld = rootTransform.worldToLocalMatrix.inverse;
            // worldToRoot = root transform's worldToLocalMatrix
            Matrix4x4 worldToRoot = rootTransform.worldToLocalMatrix;

            // rootBoneToRoot = worldToRoot * rootBoneToWorld
            // This transforms from root bone space -> world space -> root transform space
            Matrix4x4 rootBoneToRoot = worldToRoot * rootBoneToWorld;
            //rootBoneToRoot *= Matrix4x4.Rotate(Quaternion.Euler(-90, 0, 0));

            // Extract rotation and scale from the transformation matrix for normals/tangents
            Quaternion rootBoneToRootRot = Quaternion.LookRotation(
                rootBoneToRoot.MultiplyVector(Vector3.forward),
                rootBoneToRoot.MultiplyVector(Vector3.up));
            Vector3 rootBoneToRootScale = new Vector3(
                rootBoneToRoot.MultiplyVector(Vector3.right).magnitude,
                rootBoneToRoot.MultiplyVector(Vector3.up).magnitude,
                rootBoneToRoot.MultiplyVector(Vector3.forward).magnitude);

            // Transform vertices to root transform space
            for (int i = 0; i < vertexCount; i++)
            {
                vertices[i] = rootBoneToRoot.MultiplyPoint(vertices[i]);
            }

            // Transform normals to root transform space (apply rotation, compensate for scale)
            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 transformedNormal = rootBoneToRootRot * normals[i];
                // Compensate for non-uniform scale
                transformedNormal.x /= rootBoneToRootScale.x;
                transformedNormal.y /= rootBoneToRootScale.y;
                transformedNormal.z /= rootBoneToRootScale.z;
                normals[i] = transformedNormal.normalized;
            }

            // Transform tangents to root transform space (apply rotation, preserve w component)
            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 tangentDir = new Vector3(tangents[i].x, tangents[i].y, tangents[i].z);
                Vector3 transformedTangent = rootBoneToRootRot * tangentDir;
                tangents[i] = new Vector4(transformedTangent.x, transformedTangent.y, transformedTangent.z, tangents[i].w);
            }

            // Compute bindposes from bone transforms
            // Bindposes are inverse world transforms of each bone, converted to root-local space
            // newBindpose[i] = inverse(boneWorldToRoot) = inverse(worldToRoot * boneToWorld)
            Matrix4x4[] adjustedBindposes = new Matrix4x4[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                Matrix4x4 boneToWorld = bones[i].worldToLocalMatrix.inverse;
                Matrix4x4 boneToRoot = worldToRoot * boneToWorld;
                adjustedBindposes[i] = boneToRoot.inverse;
            }

            Object.DestroyImmediate(mesh);

            // Calculate texture size
            // Layout: triCount(1) + vertexCount(1) + boneCount(1) + indices(3*triCount) + vertices(20*vertexCount) + bindposes(16*boneCount)
            int flatDataLength = 3 + 3 * triCount + 20 * vertexCount + 16 * boneCount;
            int width = Mathf.CeilToInt(Mathf.Sqrt(flatDataLength));
            width = Mathf.NextPowerOfTwo(width);
            int height = Mathf.CeilToInt((float)flatDataLength / width);

            float[] flatData = new float[width * height];
            int index = 0;

            // Counts
            flatData[index++] = triCount;
            flatData[index++] = vertexCount;
            flatData[index++] = boneCount;

            // Triangle vertex indices
            foreach (int vertIdx in triangles)
            {
                flatData[index++] = vertIdx;
            }

            // Vertex data: pos(3) + normal(3) + uv0(2) + tangent(4) + boneWeights(4) + boneIndices(4) = 20 per vertex
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

                flatData[index++] = tangents[i].x;
                flatData[index++] = tangents[i].y;
                flatData[index++] = tangents[i].z;
                flatData[index++] = tangents[i].w;

                flatData[index++] = boneWeights[i].weight0;
                flatData[index++] = boneWeights[i].weight1;
                flatData[index++] = boneWeights[i].weight2;
                flatData[index++] = boneWeights[i].weight3;

                flatData[index++] = boneWeights[i].boneIndex0;
                flatData[index++] = boneWeights[i].boneIndex1;
                flatData[index++] = boneWeights[i].boneIndex2;
                flatData[index++] = boneWeights[i].boneIndex3;
            }

            // Bindposes: 16 floats per bone (4x4 matrix, column-major)
            for (int i = 0; i < boneCount; i++)
            {
                flatData[index++] = adjustedBindposes[i].m00;
                flatData[index++] = adjustedBindposes[i].m01;
                flatData[index++] = adjustedBindposes[i].m02;
                flatData[index++] = adjustedBindposes[i].m03;

                flatData[index++] = adjustedBindposes[i].m10;
                flatData[index++] = adjustedBindposes[i].m11;
                flatData[index++] = adjustedBindposes[i].m12;
                flatData[index++] = adjustedBindposes[i].m13;

                flatData[index++] = adjustedBindposes[i].m20;
                flatData[index++] = adjustedBindposes[i].m21;
                flatData[index++] = adjustedBindposes[i].m22;
                flatData[index++] = adjustedBindposes[i].m23;

                flatData[index++] = adjustedBindposes[i].m30;
                flatData[index++] = adjustedBindposes[i].m31;
                flatData[index++] = adjustedBindposes[i].m32;
                flatData[index++] = adjustedBindposes[i].m33;
            }

            // Create texture
            Texture2D texture = new Texture2D(width, height, TextureFormat.RFloat, false);
            texture.filterMode = FilterMode.Point;
            texture.SetPixelData(flatData, 0);
            texture.Apply(false);

            string outputPath = GetFullPath(_outputName);

            // Ensure directory exists and is visible to the asset database
            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                AssetDatabase.Refresh();
            }

            // Create the texture asset
            AssetDatabase.CreateAsset(texture, outputPath);

            Object asset = AssetDatabase.LoadAssetAtPath<Object>(outputPath);
            if (asset != null)
            {
                EditorGUIUtility.PingObject(asset);
            }

            // Create bone point mesh
            Mesh bonePointMesh = new Mesh();
            bonePointMesh.name = "BonePointMesh";

            Vector3[] bpVertices = new Vector3[boneCount];
            Vector3[] bpNormals = new Vector3[boneCount];
            Vector2[] bpUvs = new Vector2[boneCount];
            Vector4[] bpTangents = new Vector4[boneCount];
            int[] bpIndices = new int[boneCount];
            BoneWeight[] bpBoneWeights = new BoneWeight[boneCount];
            Matrix4x4[] bpBindposes = new Matrix4x4[boneCount];

            for (int i = 0; i < boneCount; i++)
            {
                bpVertices[i] = Vector3.zero;
                bpNormals[i] = new Vector3(1, 0, 0);
                bpUvs[i] = Vector2.zero;
                bpTangents[i] = new Vector4(0, 1, 0, 1);
                bpIndices[i] = i;
                bpBoneWeights[i] = new BoneWeight { boneIndex0 = i, weight0 = 1 };
                bpBindposes[i] = Matrix4x4.identity;
            }

            bonePointMesh.vertices = bpVertices;
            bonePointMesh.normals = bpNormals;
            bonePointMesh.uv = bpUvs;
            bonePointMesh.tangents = bpTangents;
            bonePointMesh.SetIndices(bpIndices, MeshTopology.Points, 0);
            bonePointMesh.boneWeights = bpBoneWeights;
            bonePointMesh.bindposes = bpBindposes;

            string meshAssetPath = Path.Combine(directory, "BonePointMesh.asset");
            AssetDatabase.CreateAsset(bonePointMesh, meshAssetPath);

            // Create sibling game object with SkinnedMeshRenderer
            GameObject bonePointObject = new GameObject("BonePointMesh");
            bonePointObject.transform.SetParent(_skinnedMeshRenderer.transform.parent, false);
            bonePointObject.transform.SetSiblingIndex(_skinnedMeshRenderer.transform.GetSiblingIndex() + 1);

            SkinnedMeshRenderer bonePointSMR = bonePointObject.AddComponent<SkinnedMeshRenderer>();
            bonePointSMR.sharedMesh = bonePointMesh;
            bonePointSMR.rootBone = _skinnedMeshRenderer.rootBone;
            bonePointSMR.bones = bones;
            bonePointSMR.quality = _skinnedMeshRenderer.quality;
            bonePointSMR.updateWhenOffscreen = _skinnedMeshRenderer.updateWhenOffscreen;
            bonePointSMR.localBounds = _skinnedMeshRenderer.localBounds;

            // Create material with baked skinned mesh render shader
            Shader renderShader = Shader.Find("d4rkpl4y3r/BakeMeshToTexture/RenderBakedSkinnedMesh");
            Material renderMat = new Material(renderShader);
            renderMat.SetTexture("_DataTex", texture);
            renderMat.SetFloat("_DataTexWidth", width);
            renderMat.SetFloat("_DataTexHeight", height);

            // Calculate tessellation: each bone point generates tessX * tessY * 2 triangles
            // We need: boneCount * tessX * tessY * 2 >= triCount
            // So: tess = ceil(sqrt(triCount / 2 / boneCount))
            int tessFactor = Mathf.CeilToInt(Mathf.Sqrt((float)triCount / 2f / boneCount));
            renderMat.SetFloat("_TessX", tessFactor);
            renderMat.SetFloat("_TessY", tessFactor);

            AssetDatabase.CreateAsset(renderMat, Path.Combine(directory, "RenderMat.asset"));

            bonePointSMR.materials = new[] { renderMat };

            EditorUtility.DisplayDialog("Bake Skinned Mesh to Texture",
                $"Successfully baked skinned mesh '{_skinnedMeshRenderer.gameObject.name}' to:\n{outputPath}\n\n" +
                $"Flat Data Length: {_flatDataLength}\n" +
                $"Texture Size: {_textureWidth}x{_textureHeight}", "OK");
        }
    }
}
#endif
