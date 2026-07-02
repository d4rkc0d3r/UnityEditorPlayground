#if UNITY_EDITOR
using System.IO;
using UnityEngine;
using UnityEditor;

public class TextureFillEditor : EditorWindow
{
	private const string ComputeShaderFileName = "TextureFillJumpFlood.compute";
	private const string OutputAssetSuffix = "_ClosestPixelOffset.asset";

	[SerializeField] private Texture2D maskTexture;
	[SerializeField] private bool loadRawImageFromFile = true;
	[SerializeField] private bool resizeRawToImportedResolution;
	[SerializeField] private bool useAlphaChannel = true;
	[SerializeField] private float activeThreshold = 0.5f;
	[SerializeField] private ComputeShader jumpFloodShader;

	private Texture2D loadedRawSourceTexture;
	private Texture2D resizedRawSourceTexture;
	private string loadedRawSourcePath;
	private Texture2D bakedOutputTextureAsset;
	private RenderTexture activePreviewTexture;
	private RenderTexture outputRenderTexture;
	private bool showGeneratedPreview;
	private bool previewDirty = true;
	private bool outputDirty;
	private Vector2 inspectorScroll;

	[MenuItem("Tools/d4rkpl4y3r/Texture Fill")]
	public static void OpenWindow()
	{
		TextureFillEditor window = GetWindow<TextureFillEditor>();
		window.titleContent = new GUIContent("Texture Fill");
		window.minSize = new Vector2(360f, 420f);
		window.Show();
	}

	private void OnEnable()
	{
		titleContent = new GUIContent("Texture Fill");
		minSize = new Vector2(360f, 420f);
		previewDirty = true;
		ResolveComputeShader();
	}

	private void OnDisable()
	{
		DestroyRawSourceTextures();
		DestroyPreviewTexture();
		DestroyRenderTexture(ref outputRenderTexture);
	}

	private void OnGUI()
	{
		ResolveComputeShader();

		inspectorScroll = EditorGUILayout.BeginScrollView(inspectorScroll);

		DrawInputSection();

		if (previewDirty)
		{
			RebuildActivePreview();
		}

		EditorGUILayout.Space();
		DrawPreviewSection();
		EditorGUILayout.Space();
		DrawOutputSection();

		EditorGUILayout.EndScrollView();
	}

	private void DrawInputSection()
	{
		EditorGUILayout.LabelField("Mask Input", EditorStyles.boldLabel);

		EditorGUI.BeginChangeCheck();
		maskTexture = (Texture2D)EditorGUILayout.ObjectField("Mask Texture", maskTexture, typeof(Texture2D), false);
		bool canLoadRawImageFromFile = TryGetSupportedRawSourcePath(maskTexture, out string rawSourcePath, out string rawSourceExtension);
		using (new EditorGUI.DisabledScope(!canLoadRawImageFromFile))
		{
			loadRawImageFromFile = EditorGUILayout.Toggle("Load Raw File", canLoadRawImageFromFile && loadRawImageFromFile);
		}
		using (new EditorGUI.DisabledScope(!canLoadRawImageFromFile || !loadRawImageFromFile))
		{
			resizeRawToImportedResolution = EditorGUILayout.Toggle("Resize Raw To Imported Resolution", resizeRawToImportedResolution);
		}
		useAlphaChannel = EditorGUILayout.Toggle("Use Alpha Channel", useAlphaChannel);
		activeThreshold = EditorGUILayout.Slider("Active Threshold", activeThreshold, 1e-6f, 1f);
		jumpFloodShader = (ComputeShader)EditorGUILayout.ObjectField("Compute Shader", jumpFloodShader, typeof(ComputeShader), false);
		if (EditorGUI.EndChangeCheck())
		{
			if (!canLoadRawImageFromFile)
			{
				loadRawImageFromFile = false;
			}

			DestroyRawSourceTextures();
			showGeneratedPreview = false;
			previewDirty = true;
			outputDirty = outputRenderTexture != null;
		}

		if (maskTexture == null)
		{
			EditorGUILayout.HelpBox("Assign a mask texture to preview active pixels and generate offsets.", MessageType.Info);
			return;
		}

		Vector2Int effectiveResolution = GetEffectiveSourceResolution();
		EditorGUILayout.LabelField("Resolution", string.Format("{0} x {1}", effectiveResolution.x, effectiveResolution.y));
		EditorGUILayout.LabelField("Channel", useAlphaChannel ? "Alpha" : "Red");
		EditorGUILayout.LabelField("Source", loadRawImageFromFile && canLoadRawImageFromFile ? string.Format("Raw {0}", rawSourceExtension.ToUpperInvariant()) : "Imported Texture");

		if (!canLoadRawImageFromFile)
		{
			EditorGUILayout.HelpBox("Raw file loading is only available for PNG and EXR assets.", MessageType.None);
		}
		else if (!string.IsNullOrEmpty(rawSourcePath))
		{
			EditorGUILayout.LabelField("Raw Path", rawSourcePath);
		}

		if (jumpFloodShader == null)
		{
			EditorGUILayout.HelpBox("TextureFillJumpFlood.compute could not be found next to this editor script.", MessageType.Error);
		}
	}

	private void DrawPreviewSection()
	{
		EditorGUILayout.LabelField(showGeneratedPreview ? "Generated Offset Preview" : "Active Pixel Preview", EditorStyles.boldLabel);

		if (maskTexture == null)
		{
			EditorGUILayout.HelpBox("The black and white active-pixel preview will appear here once a mask texture is assigned.", MessageType.None);
			return;
		}

		if (activePreviewTexture == null)
		{
			EditorGUILayout.HelpBox("Preview generation failed for the current texture.", MessageType.Warning);
			return;
		}

		float aspect = (float)activePreviewTexture.width / Mathf.Max(1, activePreviewTexture.height);
		Rect previewRect = GUILayoutUtility.GetAspectRect(aspect, GUILayout.ExpandWidth(true));
		EditorGUI.DrawPreviewTexture(previewRect, activePreviewTexture, null, ScaleMode.ScaleToFit);
	}

	private void DrawOutputSection()
	{
		EditorGUILayout.LabelField("Generate", EditorStyles.boldLabel);
		EditorGUILayout.HelpBox("The generated RGFloat render texture stores pixel-space offsets from each pixel to the closest active pixel.", MessageType.None);

		if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGFloat))
		{
			EditorGUILayout.HelpBox("This platform does not support the floating-point render texture formats required by this tool.", MessageType.Error);
			return;
		}

		bool canGenerate = maskTexture != null && jumpFloodShader != null;
		using (new EditorGUI.DisabledScope(!canGenerate))
		{
			if (GUILayout.Button("Generate Offset Render Texture"))
			{
				GenerateOffsetRenderTexture();
			}
		}

		if (outputRenderTexture == null)
		{
			return;
		}

		if (outputDirty)
		{
			EditorGUILayout.HelpBox("The input settings changed after the last generation. Press the button again to refresh the output render texture.", MessageType.Info);
		}

		using (new EditorGUI.DisabledScope(true))
		{
			EditorGUILayout.ObjectField("Output RenderTexture", outputRenderTexture, typeof(RenderTexture), false);
			EditorGUILayout.ObjectField("Baked Texture Asset", bakedOutputTextureAsset, typeof(Texture2D), false);
		}

		EditorGUILayout.LabelField("Format", outputRenderTexture.format.ToString());
	}

	private void RebuildActivePreview()
	{
		previewDirty = false;

		DestroyPreviewTexture();

		if (maskTexture == null || jumpFloodShader == null)
		{
			return;
		}

		Texture sourceTexture = GetSourceTextureForProcessing();
		if (sourceTexture == null)
		{
			return;
		}

		activePreviewTexture = CreateWorkingTexture("TextureFillActivePreview", sourceTexture.width, sourceTexture.height, RenderTextureFormat.ARGB32);

		jumpFloodShader.SetInts("_TextureSize", sourceTexture.width, sourceTexture.height);
		jumpFloodShader.SetFloat("_Threshold", activeThreshold);
		jumpFloodShader.SetInt("_UseAlpha", useAlphaChannel ? 1 : 0);

		int previewKernel = jumpFloodShader.FindKernel(showGeneratedPreview && outputRenderTexture != null && !outputDirty ? "PreviewOffsets" : "PreviewThreshold");
		jumpFloodShader.SetTexture(previewKernel, "_MaskTexture", sourceTexture);
		if (showGeneratedPreview && outputRenderTexture != null && !outputDirty)
		{
			jumpFloodShader.SetTexture(previewKernel, "_OffsetInput", outputRenderTexture);
			jumpFloodShader.SetFloat("_MaxOffsetLength", Mathf.Sqrt(sourceTexture.width * sourceTexture.width + sourceTexture.height * sourceTexture.height));
		}

		jumpFloodShader.SetTexture(previewKernel, "_PreviewOutput", activePreviewTexture);
		jumpFloodShader.Dispatch(previewKernel, Mathf.CeilToInt(sourceTexture.width / 8f), Mathf.CeilToInt(sourceTexture.height / 8f), 1);
	}

	private void GenerateOffsetRenderTexture()
	{
		if (maskTexture == null || jumpFloodShader == null)
		{
			return;
		}

		if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGFloat))
		{
			EditorUtility.DisplayDialog("Texture Fill", "The required floating-point render texture formats are not supported on this platform.", "OK");
			return;
		}

		Texture sourceTexture = GetSourceTextureForProcessing();
		if (sourceTexture == null)
		{
			EditorUtility.DisplayDialog("Texture Fill", "The selected source texture could not be loaded.", "OK");
			return;
		}

		int width = sourceTexture.width;
		int height = sourceTexture.height;
		int groupsX = Mathf.CeilToInt(width / 8f);
		int groupsY = Mathf.CeilToInt(height / 8f);

		RenderTexture seedPing = null;
		RenderTexture seedPong = null;

		try
		{
			seedPing = CreateWorkingTexture("TextureFillSeedsA", width, height, RenderTextureFormat.ARGBFloat);
			seedPong = CreateWorkingTexture("TextureFillSeedsB", width, height, RenderTextureFormat.ARGBFloat);

			DestroyRenderTexture(ref outputRenderTexture);
			outputRenderTexture = CreateWorkingTexture("TextureFillOffsets", width, height, RenderTextureFormat.RGFloat);

			jumpFloodShader.SetInts("_TextureSize", width, height);
			jumpFloodShader.SetFloat("_Threshold", activeThreshold);
			jumpFloodShader.SetInt("_UseAlpha", useAlphaChannel ? 1 : 0);

			int initKernel = jumpFloodShader.FindKernel("InitSeeds");
			jumpFloodShader.SetTexture(initKernel, "_MaskTexture", sourceTexture);
			jumpFloodShader.SetTexture(initKernel, "_SeedOutput", seedPing);
			jumpFloodShader.Dispatch(initKernel, groupsX, groupsY, 1);

			int jumpFloodKernel = jumpFloodShader.FindKernel("JumpFlood");
			int step = Mathf.NextPowerOfTwo(Mathf.Max(width, height)) >> 1;
			bool currentSourceIsPing = true;

			while (step > 0)
			{
				jumpFloodShader.SetInt("_Step", step);
				jumpFloodShader.SetTexture(jumpFloodKernel, "_SeedInput", currentSourceIsPing ? seedPing : seedPong);
				jumpFloodShader.SetTexture(jumpFloodKernel, "_SeedOutput", currentSourceIsPing ? seedPong : seedPing);
				jumpFloodShader.Dispatch(jumpFloodKernel, groupsX, groupsY, 1);

				currentSourceIsPing = !currentSourceIsPing;
				step >>= 1;
			}

			int finalizeKernel = jumpFloodShader.FindKernel("WriteOffsets");
			jumpFloodShader.SetTexture(finalizeKernel, "_SeedInput", currentSourceIsPing ? seedPing : seedPong);
			jumpFloodShader.SetTexture(finalizeKernel, "_OffsetOutput", outputRenderTexture);
			jumpFloodShader.Dispatch(finalizeKernel, groupsX, groupsY, 1);

			outputRenderTexture.name = maskTexture.name + "_ClosestPixelOffset";
			bakedOutputTextureAsset = BakeOutputTextureAsset(width, height);
			showGeneratedPreview = true;
			outputDirty = false;
			previewDirty = true;
			RebuildActivePreview();
		}
		finally
		{
			DestroyRenderTexture(ref seedPing);
			DestroyRenderTexture(ref seedPong);
		}
	}

	private Texture GetSourceTextureForProcessing()
	{
		if (maskTexture == null)
		{
			return null;
		}

		if (!loadRawImageFromFile)
		{
			return maskTexture;
		}

		if (!TryGetSupportedRawSourcePath(maskTexture, out string rawSourcePath, out _))
		{
			loadRawImageFromFile = false;
			return maskTexture;
		}

		if (loadedRawSourceTexture != null && loadedRawSourcePath == rawSourcePath)
		{
			return GetEffectiveRawSourceTexture();
		}

		DestroyRawSourceTextures();

		if (!File.Exists(rawSourcePath))
		{
			Debug.LogWarning(string.Format("Texture Fill could not find raw source file at {0}.", rawSourcePath));
			return null;
		}

		byte[] imageBytes = File.ReadAllBytes(rawSourcePath);
		Texture2D rawTexture = new Texture2D(2, 2, TextureFormat.RGBAFloat, false, true);
		if (!ImageConversion.LoadImage(rawTexture, imageBytes, false))
		{
			DestroyImmediate(rawTexture);
			Debug.LogWarning(string.Format("Texture Fill failed to load raw source image from {0}.", rawSourcePath));
			return null;
		}

		rawTexture.name = maskTexture.name + "_RawSource";
		rawTexture.filterMode = FilterMode.Point;
		rawTexture.wrapMode = TextureWrapMode.Clamp;
		rawTexture.hideFlags = HideFlags.HideAndDontSave;

		loadedRawSourceTexture = rawTexture;
		loadedRawSourcePath = rawSourcePath;
		return GetEffectiveRawSourceTexture();
	}

	private Vector2Int GetEffectiveSourceResolution()
	{
		Texture sourceTexture = GetSourceTextureForProcessing();
		if (sourceTexture != null)
		{
			return new Vector2Int(sourceTexture.width, sourceTexture.height);
		}

		return maskTexture != null
			? new Vector2Int(maskTexture.width, maskTexture.height)
			: Vector2Int.zero;
	}

	private Texture GetEffectiveRawSourceTexture()
	{
		if (loadedRawSourceTexture == null)
		{
			return null;
		}

		if (!resizeRawToImportedResolution || maskTexture == null)
		{
			DestroyResizedRawSourceTexture();
			return loadedRawSourceTexture;
		}

		if (loadedRawSourceTexture.width == maskTexture.width && loadedRawSourceTexture.height == maskTexture.height)
		{
			DestroyResizedRawSourceTexture();
			return loadedRawSourceTexture;
		}

		if (resizedRawSourceTexture != null && resizedRawSourceTexture.width == maskTexture.width && resizedRawSourceTexture.height == maskTexture.height)
		{
			return resizedRawSourceTexture;
		}

		DestroyResizedRawSourceTexture();
		resizedRawSourceTexture = ResizeTexture(loadedRawSourceTexture, maskTexture.width, maskTexture.height, maskTexture.name + "_RawSourceResized");
		return resizedRawSourceTexture;
	}

	private static Texture2D ResizeTexture(Texture sourceTexture, int width, int height, string textureName)
	{
		RenderTexture temporaryRenderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
		RenderTexture previousActive = RenderTexture.active;
		try
		{
			temporaryRenderTexture.filterMode = FilterMode.Point;
			temporaryRenderTexture.wrapMode = TextureWrapMode.Clamp;
			Graphics.Blit(sourceTexture, temporaryRenderTexture);

			Texture2D resizedTexture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
			resizedTexture.name = textureName;
			resizedTexture.filterMode = FilterMode.Point;
			resizedTexture.wrapMode = TextureWrapMode.Clamp;
			resizedTexture.hideFlags = HideFlags.HideAndDontSave;

			RenderTexture.active = temporaryRenderTexture;
			resizedTexture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
			resizedTexture.Apply(false, false);
			return resizedTexture;
		}
		finally
		{
			RenderTexture.active = previousActive;
			RenderTexture.ReleaseTemporary(temporaryRenderTexture);
		}
	}

	private Texture2D BakeOutputTextureAsset(int width, int height)
	{
		Texture2D bakedTexture = new Texture2D(width, height, TextureFormat.RGFloat, false, true);
		bakedTexture.name = maskTexture.name + "_ClosestPixelOffset";
		bakedTexture.filterMode = FilterMode.Point;
		bakedTexture.wrapMode = TextureWrapMode.Clamp;

		RenderTexture previousActive = RenderTexture.active;
		try
		{
			RenderTexture.active = outputRenderTexture;
			bakedTexture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
			bakedTexture.Apply(false, false);
		}
		finally
		{
			RenderTexture.active = previousActive;
		}

		string assetPath = GetOutputAssetPath();
		Texture2D existingAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
		if (existingAsset == null)
		{
			AssetDatabase.CreateAsset(bakedTexture, assetPath);
			existingAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
		}
		else
		{
			EditorUtility.CopySerialized(bakedTexture, existingAsset);
			EditorUtility.SetDirty(existingAsset);
			DestroyImmediate(bakedTexture);
		}

		AssetDatabase.SaveAssets();
		AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
		Texture2D asset = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
		if (asset != null)
		{
			EditorGUIUtility.PingObject(asset);
		}

		return asset;
	}

	private string GetOutputAssetPath()
	{
		string inputAssetPath = maskTexture != null ? AssetDatabase.GetAssetPath(maskTexture) : null;
		string directory = !string.IsNullOrEmpty(inputAssetPath) ? Path.GetDirectoryName(inputAssetPath) : "Assets";
		string baseName = maskTexture != null ? maskTexture.name : "TextureFillOutput";
		string outputPath = Path.Combine(directory ?? "Assets", baseName + OutputAssetSuffix).Replace('\\', '/');
		return outputPath;
	}

	private static bool TryGetSupportedRawSourcePath(Texture2D texture, out string rawSourcePath, out string rawSourceExtension)
	{
		rawSourcePath = null;
		rawSourceExtension = null;

		if (texture == null)
		{
			return false;
		}

		string assetPath = AssetDatabase.GetAssetPath(texture);
		if (string.IsNullOrEmpty(assetPath))
		{
			return false;
		}

		string fullPath = Path.GetFullPath(assetPath);
		string extension = Path.GetExtension(fullPath);
		if (string.IsNullOrEmpty(extension))
		{
			return false;
		}

		extension = extension.ToLowerInvariant();
		if (extension != ".png" && extension != ".exr")
		{
			return false;
		}

		rawSourcePath = fullPath;
		rawSourceExtension = extension.TrimStart('.');
		return true;
	}

	private void ResolveComputeShader()
	{
		if (jumpFloodShader != null)
		{
			return;
		}

		MonoScript script = MonoScript.FromScriptableObject(this);
		if (script == null)
		{
			return;
		}

		string scriptPath = AssetDatabase.GetAssetPath(script);
		if (string.IsNullOrEmpty(scriptPath))
		{
			return;
		}

		string directory = Path.GetDirectoryName(scriptPath);
		if (string.IsNullOrEmpty(directory))
		{
			return;
		}

		string computeShaderPath = Path.Combine(directory, ComputeShaderFileName).Replace('\\', '/');
		jumpFloodShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(computeShaderPath);
	}

	private static RenderTexture CreateWorkingTexture(string textureName, int width, int height, RenderTextureFormat format)
	{
		RenderTexture texture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear);
		texture.name = textureName;
		texture.enableRandomWrite = true;
		texture.filterMode = FilterMode.Point;
		texture.wrapMode = TextureWrapMode.Clamp;
		texture.hideFlags = HideFlags.HideAndDontSave;
		texture.Create();
		return texture;
	}

	private void DestroyPreviewTexture()
	{
		DestroyRenderTexture(ref activePreviewTexture);
	}

	private void DestroyRawSourceTextures()
	{
		DestroyResizedRawSourceTexture();

		if (loadedRawSourceTexture == null)
		{
			return;
		}

		DestroyImmediate(loadedRawSourceTexture);
		loadedRawSourceTexture = null;
		loadedRawSourcePath = null;
	}

	private void DestroyResizedRawSourceTexture()
	{
		if (resizedRawSourceTexture == null)
		{
			return;
		}

		DestroyImmediate(resizedRawSourceTexture);
		resizedRawSourceTexture = null;
	}

	private static void DestroyRenderTexture(ref RenderTexture texture)
	{
		if (texture == null)
		{
			return;
		}

		if (texture.IsCreated())
		{
			texture.Release();
		}

		DestroyImmediate(texture);
		texture = null;
	}
}
#endif