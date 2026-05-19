#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityVolumeRendering;

using UVRTransferFunction = UnityVolumeRendering.TransferFunction;
using UVRTransferFunction2D = UnityVolumeRendering.TransferFunction2D;

public class VolumeDatasetPythonBridge : EditorWindow
{
    [Serializable]
    private class VolumeMetadata
    {
        public string datasetName;
        public string sourceFilePath;
        public string rawFileName;
        public string dtype;
        public string arrayOrder;
        public string unityIndexFormula;

        public int dimX;
        public int dimY;
        public int dimZ;

        public float scaleX;
        public float scaleY;
        public float scaleZ;

        public float originalScaleX;
        public float originalScaleY;
        public float originalScaleZ;

        public int originalDimX;
        public int originalDimY;
        public int originalDimZ;

        public string axis0;
        public string axis1;
        public string axis2;

        public float volumeScale;
        public string method;
        public string description;
    }

    private enum RawType
    {
        Float32,
        UInt16
    }

    // ============================================================
    // EXPORTAR VOLUMEN ORIGINAL PARA PYTHON
    // ============================================================

    [MenuItem("Tools/Ultrasound Volume/Export Selected VolumeDataset For Python")]
    public static void ExportSelectedVolumeDatasetForPython()
    {
        VolumeDataset dataset = GetSelectedVolumeDataset();

        if (dataset == null)
        {
            EditorUtility.DisplayDialog(
                "No se encontró VolumeDataset",
                "Selecciona en el Project un asset VolumeDataset o selecciona en Hierarchy un GameObject que tenga una referencia a VolumeDataset.",
                "OK"
            );
            return;
        }

        if (dataset.data == null || dataset.data.Length == 0)
        {
            EditorUtility.DisplayDialog(
                "Dataset vacío",
                "El VolumeDataset seleccionado no tiene datos en dataset.data.",
                "OK"
            );
            return;
        }

        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string exportFolder = Path.Combine(projectRoot, "VolumePython");

        Directory.CreateDirectory(exportFolder);

        string rawPath = Path.Combine(exportFolder, "volume_original.raw");
        string metadataPath = Path.Combine(exportFolder, "volume_metadata.json");

        byte[] rawBytes = new byte[dataset.data.Length * sizeof(float)];
        Buffer.BlockCopy(dataset.data, 0, rawBytes, 0, rawBytes.Length);

        File.WriteAllBytes(rawPath, rawBytes);

        VolumeMetadata metadata = new VolumeMetadata();

        metadata.datasetName = string.IsNullOrEmpty(dataset.datasetName)
            ? "VolumeDataset"
            : dataset.datasetName;

        metadata.sourceFilePath = dataset.filePath;
        metadata.rawFileName = "volume_original.raw";
        metadata.dtype = "float32";
        metadata.arrayOrder = "Python shape = (dimZ, dimY, dimX)";
        metadata.unityIndexFormula = "index = x + y * dimX + z * (dimX * dimY)";

        metadata.dimX = dataset.dimX;
        metadata.dimY = dataset.dimY;
        metadata.dimZ = dataset.dimZ;

        metadata.scaleX = dataset.scaleX;
        metadata.scaleY = dataset.scaleY;
        metadata.scaleZ = dataset.scaleZ;

        metadata.originalScaleX = dataset.scaleX;
        metadata.originalScaleY = dataset.scaleY;
        metadata.originalScaleZ = dataset.scaleZ;

        metadata.originalDimX = dataset.dimX;
        metadata.originalDimY = dataset.dimY;
        metadata.originalDimZ = dataset.dimZ;

        metadata.axis0 = "z";
        metadata.axis1 = "y";
        metadata.axis2 = "x";

        metadata.volumeScale = dataset.volumeScale;
        metadata.method = "exported_from_unity";
        metadata.description = "VolumeDataset exportado desde Unity para procesamiento en Python.";

        string json = JsonUtility.ToJson(metadata, true);
        File.WriteAllText(metadataPath, json);

        EditorUtility.DisplayDialog(
            "Exportación completa",
            "Se exportó el volumen a:\n\n" + exportFolder +
            "\n\nArchivos creados:\nvolume_original.raw\nvolume_metadata.json",
            "OK"
        );

        EditorUtility.RevealInFinder(exportFolder);
    }

    // ============================================================
    // APLICAR RAW RECTIFICADO USANDO EL SISTEMA NATIVO
    // ============================================================

    [MenuItem("Tools/Ultrasound Volume/Apply Rectified RAW To Volume")]
    public static void OpenApplyRectifiedRawWindow()
    {
        ApplyRectifiedRawWindow window = GetWindow<ApplyRectifiedRawWindow>();
        window.titleContent = new GUIContent("Apply Rectified RAW");
        window.minSize = new Vector2(570, 610);
        window.Show();
    }

    public class ApplyRectifiedRawWindow : EditorWindow
    {
        private GameObject targetObject;
        private Renderer targetRenderer;
        private Material baseMaterialOverride;

        private string rawPath = "";

        private RawType rawType = RawType.Float32;

        private int dimX = 199;
        private int dimY = 208;
        private int dimZ = 147;

        private string datasetName = "RectifiedVolumeFromPython";

        private bool updateVolumeRenderedObject = true;
        private bool replaceDataTexture = true;
        private bool replaceGradientTexture = true;
        private bool alwaysEnsureNoiseTexture = true;
        private bool alwaysEnsureTransferFunctionTexture = true;
        private bool setMinMaxToZeroOne = true;
        private bool saveGeneratedAssets = true;

        private void OnGUI()
        {
            GUILayout.Label("Aplicar RAW rectificado al volumen actual", EditorStyles.boldLabel);

            GUILayout.Space(8);

            targetObject = (GameObject)EditorGUILayout.ObjectField(
                "Objeto de volumen",
                targetObject,
                typeof(GameObject),
                true
            );

            targetRenderer = (Renderer)EditorGUILayout.ObjectField(
                "Renderer opcional",
                targetRenderer,
                typeof(Renderer),
                true
            );

            baseMaterialOverride = (Material)EditorGUILayout.ObjectField(
                "Material base opcional",
                baseMaterialOverride,
                typeof(Material),
                false
            );

            GUILayout.Space(6);

            if (GUILayout.Button("Usar selección actual"))
            {
                UseCurrentSelection();
            }

            GUILayout.Space(8);

            EditorGUILayout.HelpBox(
                "Selecciona FetalBrainDICOM o volumeContainer(Clone). El script cargará el RAW rectificado como VolumeDataset, usará dataset.GetDataTexture(), generará Noise Texture si falta y recuperará o creará la Transfer Function desde el padre.",
                MessageType.Info
            );

            GUILayout.Space(8);

            EditorGUILayout.LabelField(
                "RAW seleccionado",
                string.IsNullOrEmpty(rawPath) ? "Ninguno" : rawPath
            );

            if (GUILayout.Button("Seleccionar RAW rectificado"))
            {
                string selected = EditorUtility.OpenFilePanel(
                    "Selecciona el RAW rectificado",
                    Application.dataPath,
                    "raw"
                );

                if (!string.IsNullOrEmpty(selected))
                    rawPath = selected;
            }

            GUILayout.Space(8);

            rawType = (RawType)EditorGUILayout.EnumPopup("Tipo de RAW", rawType);

            dimX = EditorGUILayout.IntField("Dim X", dimX);
            dimY = EditorGUILayout.IntField("Dim Y", dimY);
            dimZ = EditorGUILayout.IntField("Dim Z", dimZ);

            datasetName = EditorGUILayout.TextField("Nombre del dataset", datasetName);

            GUILayout.Space(8);

            GUILayout.Label("Opciones", EditorStyles.boldLabel);

            updateVolumeRenderedObject = EditorGUILayout.ToggleLeft(
                "Actualizar VolumeRenderedObject",
                updateVolumeRenderedObject
            );

            replaceDataTexture = EditorGUILayout.ToggleLeft(
                "Reemplazar Data Texture con dataset.GetDataTexture()",
                replaceDataTexture
            );

            replaceGradientTexture = EditorGUILayout.ToggleLeft(
                "Reemplazar Gradient Texture con dataset.GetGradientTexture()",
                replaceGradientTexture
            );

            alwaysEnsureNoiseTexture = EditorGUILayout.ToggleLeft(
                "Asegurar Noise Texture",
                alwaysEnsureNoiseTexture
            );

            alwaysEnsureTransferFunctionTexture = EditorGUILayout.ToggleLeft(
                "Asegurar Transfer Function Texture",
                alwaysEnsureTransferFunctionTexture
            );

            setMinMaxToZeroOne = EditorGUILayout.ToggleLeft(
                "Poner Min val = 0 y Max val = 1",
                setMinMaxToZeroOne
            );

            saveGeneratedAssets = EditorGUILayout.ToggleLeft(
                "Guardar dataset, texturas y material como assets",
                saveGeneratedAssets
            );

            GUILayout.Space(10);

            EditorGUILayout.HelpBox(
                "Para usar el RAW float32:\n" +
                "volume_spherical_rectified_cropped_filled.raw\n" +
                "Tipo: Float32\n\n" +
                "Para usar el RAW uint16:\n" +
                "volume_spherical_rectified_cropped_filled_matched_uint16.raw\n" +
                "Tipo: UInt16\n\n" +
                "Dimensiones actuales:\n" +
                "X = 199, Y = 208, Z = 147",
                MessageType.None
            );

            GUILayout.Space(10);

            if (GUILayout.Button("Crear VolumeDataset y aplicar", GUILayout.Height(36)))
            {
                try
                {
                    CreateDatasetAndApply();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);

                    EditorUtility.DisplayDialog(
                        "Error",
                        ex.Message,
                        "OK"
                    );
                }
            }
        }

        private void UseCurrentSelection()
        {
            GameObject selected = Selection.activeGameObject;

            if (selected == null)
            {
                EditorUtility.DisplayDialog(
                    "Nada seleccionado",
                    "Selecciona FetalBrainDICOM o volumeContainer(Clone).",
                    "OK"
                );
                return;
            }

            targetObject = selected;

            Renderer foundRenderer = FindVolumeRenderer(selected);

            if (foundRenderer != null)
                targetRenderer = foundRenderer;
        }

        private void CreateDatasetAndApply()
        {
            if (targetObject == null && targetRenderer == null)
            {
                EditorUtility.DisplayDialog(
                    "Falta objeto",
                    "Arrastra FetalBrainDICOM, volumeContainer(Clone) o un Renderer válido.",
                    "OK"
                );
                return;
            }

            if (string.IsNullOrEmpty(rawPath) || !File.Exists(rawPath))
            {
                EditorUtility.DisplayDialog(
                    "Falta RAW",
                    "Selecciona el archivo RAW rectificado.",
                    "OK"
                );
                return;
            }

            if (dimX <= 0 || dimY <= 0 || dimZ <= 0)
            {
                EditorUtility.DisplayDialog(
                    "Dimensiones inválidas",
                    "Dim X, Dim Y y Dim Z deben ser mayores que cero.",
                    "OK"
                );
                return;
            }

            int voxelCount = checked(dimX * dimY * dimZ);

            float[] data = LoadRawAsFloatData(
                rawPath,
                voxelCount,
                rawType
            );

            GameObject root = targetObject;

            if (root == null && targetRenderer != null)
                root = targetRenderer.gameObject;

            VolumeRenderedObject volObj = FindVolumeRenderedObject(root);

            Renderer rendererToUse = targetRenderer;

            if (rendererToUse == null && volObj != null)
                rendererToUse = GetRendererFromVolumeRenderedObject(volObj);

            if (rendererToUse == null && root != null)
                rendererToUse = FindVolumeRenderer(root);

            if (rendererToUse == null)
            {
                EditorUtility.DisplayDialog(
                    "No se encontró Renderer",
                    "No se encontró un Renderer con material compatible con _DataTex.",
                    "OK"
                );
                return;
            }

            Material currentMaterial = rendererToUse.sharedMaterial;

            if (currentMaterial == null && baseMaterialOverride == null)
            {
                EditorUtility.DisplayDialog(
                    "No se encontró material",
                    "El Renderer seleccionado no tiene material y no se indicó material base.",
                    "OK"
                );
                return;
            }

            Material materialTemplate = baseMaterialOverride != null
                ? baseMaterialOverride
                : currentMaterial;

            VolumeDataset oldDataset = null;

            if (volObj != null)
                oldDataset = FindVolumeDatasetByReflection(volObj);

            if (oldDataset == null && root != null)
                oldDataset = GetSelectedOrObjectVolumeDataset(root);

            VolumeDataset newDataset = CreateRectifiedVolumeDataset(
                data,
                dimX,
                dimY,
                dimZ,
                datasetName,
                rawPath,
                oldDataset
            );

            Texture3D dataTexture = null;
            Texture3D gradientTexture = null;

            if (replaceDataTexture)
                dataTexture = newDataset.GetDataTexture();

            if (replaceGradientTexture)
                gradientTexture = newDataset.GetGradientTexture();

            UVRTransferFunction transferFunction = null;
            UVRTransferFunction2D transferFunction2D = null;
            Texture2D transferFunctionTexture = null;
            Texture2D noiseTexture = null;

            if (alwaysEnsureTransferFunctionTexture)
            {
                transferFunction = EnsureTransferFunction(volObj);
                transferFunction2D = EnsureTransferFunction2D(volObj);

                if (transferFunction != null)
                    transferFunctionTexture = transferFunction.GetTexture();
            }

            if (alwaysEnsureNoiseTexture)
            {
                noiseTexture = GetExistingNoiseTexture(materialTemplate);

                if (noiseTexture == null)
                    noiseTexture = NoiseTextureGenerator.GenerateNoiseTexture(512, 512);
            }

            Material newMaterial = new Material(materialTemplate);
            newMaterial.name = materialTemplate.name + "_RectifiedDataset";

            ApplyTexturesToMaterial(
                newMaterial,
                dataTexture,
                gradientTexture,
                noiseTexture,
                transferFunctionTexture,
                setMinMaxToZeroOne
            );

            if (saveGeneratedAssets)
            {
                SaveGeneratedAssets(
                    newDataset,
                    dataTexture,
                    gradientTexture,
                    noiseTexture,
                    transferFunctionTexture,
                    transferFunction,
                    transferFunction2D,
                    newMaterial
                );
            }

            Undo.RecordObject(rendererToUse, "Apply Rectified RAW To Volume");

            rendererToUse.sharedMaterial = newMaterial;

            if (volObj != null && updateVolumeRenderedObject)
            {
                SetRendererOnVolumeRenderedObject(volObj, rendererToUse);
                SetVolumeDatasetByReflection(volObj, newDataset);

                if (transferFunction != null)
                    SetFieldOrPropertyValue(volObj, "transferFunction", transferFunction);

                if (transferFunction2D != null)
                    SetFieldOrPropertyValue(volObj, "transferFunction2D", transferFunction2D);

                EditorUtility.SetDirty(volObj);
            }

            EditorUtility.SetDirty(rendererToUse);
            EditorUtility.SetDirty(newMaterial);
            EditorUtility.SetDirty(newDataset);

            if (dataTexture != null)
                EditorUtility.SetDirty(dataTexture);

            if (gradientTexture != null)
                EditorUtility.SetDirty(gradientTexture);

            if (noiseTexture != null)
                EditorUtility.SetDirty(noiseTexture);

            if (transferFunctionTexture != null)
                EditorUtility.SetDirty(transferFunctionTexture);

            if (root != null && root.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(root.scene);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeObject = newMaterial;
            EditorGUIUtility.PingObject(newMaterial);

            EditorUtility.DisplayDialog(
                "Listo",
                "Se creó un VolumeDataset desde el RAW rectificado y se aplicó al volumen actual.\n\n" +
                "Debe quedar con:\n" +
                "_DataTex asignado\n" +
                "_NoiseTex asignado\n" +
                "_TFTex asignado\n\n" +
                "Dimensiones:\n" +
                "X = " + dimX + "\n" +
                "Y = " + dimY + "\n" +
                "Z = " + dimZ,
                "OK"
            );
        }
    }

    // ============================================================
    // CREACIÓN DE VOLUMEDATASET DESDE RAW
    // ============================================================

    private static VolumeDataset CreateRectifiedVolumeDataset(
        float[] data,
        int dimX,
        int dimY,
        int dimZ,
        string datasetName,
        string rawPath,
        VolumeDataset oldDataset
    )
    {
        VolumeDataset dataset = ScriptableObject.CreateInstance<VolumeDataset>();

        dataset.datasetName = string.IsNullOrEmpty(datasetName)
            ? "RectifiedVolumeFromPython"
            : datasetName;

        dataset.filePath = rawPath;

        dataset.dimX = dimX;
        dataset.dimY = dimY;
        dataset.dimZ = dimZ;

        dataset.data = data;

        if (oldDataset != null)
        {
            dataset.scaleX = oldDataset.scaleX;
            dataset.scaleY = oldDataset.scaleY;
            dataset.scaleZ = oldDataset.scaleZ;
            dataset.volumeScale = oldDataset.volumeScale;
        }
        else
        {
            dataset.scaleX = dimX;
            dataset.scaleY = dimY;
            dataset.scaleZ = dimZ;
            dataset.volumeScale = 0.0f;
        }

        return dataset;
    }

    private static float[] LoadRawAsFloatData(
        string path,
        int expectedVoxelCount,
        RawType rawType
    )
    {
        byte[] bytes = File.ReadAllBytes(path);

        if (rawType == RawType.Float32)
        {
            int expectedBytes = expectedVoxelCount * sizeof(float);

            if (bytes.Length != expectedBytes)
            {
                throw new Exception(
                    "El RAW Float32 tiene " + bytes.Length + " bytes, pero se esperaban " + expectedBytes + ".\n\n" +
                    "Vóxeles esperados: " + expectedVoxelCount + "\n\n" +
                    "Si seleccionaste el archivo matched_uint16.raw, cambia Tipo de RAW a UInt16."
                );
            }

            float[] values = new float[expectedVoxelCount];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);

            CleanFloatValues(values);

            return values;
        }

        if (rawType == RawType.UInt16)
        {
            int expectedBytes = expectedVoxelCount * sizeof(ushort);

            if (bytes.Length != expectedBytes)
            {
                throw new Exception(
                    "El RAW UInt16 tiene " + bytes.Length + " bytes, pero se esperaban " + expectedBytes + ".\n\n" +
                    "Vóxeles esperados: " + expectedVoxelCount + "\n\n" +
                    "Si seleccionaste el archivo .raw float32, cambia Tipo de RAW a Float32."
                );
            }

            ushort[] valuesU16 = new ushort[expectedVoxelCount];
            Buffer.BlockCopy(bytes, 0, valuesU16, 0, bytes.Length);

            float[] values = new float[expectedVoxelCount];

            for (int i = 0; i < expectedVoxelCount; i++)
                values[i] = valuesU16[i];

            return values;
        }

        throw new Exception("Tipo de RAW no soportado.");
    }

    private static void CleanFloatValues(float[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            float v = values[i];

            if (float.IsNaN(v) || float.IsInfinity(v))
                values[i] = 0.0f;
        }
    }

    // ============================================================
    // APLICACIÓN AL MATERIAL
    // ============================================================

    private static void ApplyTexturesToMaterial(
        Material material,
        Texture3D dataTexture,
        Texture3D gradientTexture,
        Texture2D noiseTexture,
        Texture2D transferFunctionTexture,
        bool setMinMaxToZeroOne
    )
    {
        if (material == null)
            throw new Exception("El material es null.");

        if (!material.HasProperty("_DataTex"))
        {
            throw new Exception(
                "El material no tiene la propiedad _DataTex. Revisa que use el shader VolumeRendering/DirectVolumeRenderingShader."
            );
        }

        if (dataTexture != null)
        {
            material.SetTexture("_DataTex", dataTexture);
            Debug.Log("Asignado _DataTex desde VolumeDataset.GetDataTexture().");
        }

        if (gradientTexture != null && material.HasProperty("_GradientTex"))
        {
            material.SetTexture("_GradientTex", gradientTexture);
            Debug.Log("Asignado _GradientTex desde VolumeDataset.GetGradientTexture().");
        }

        if (noiseTexture != null && material.HasProperty("_NoiseTex"))
        {
            material.SetTexture("_NoiseTex", noiseTexture);
            Debug.Log("Asignado _NoiseTex.");
        }

        if (transferFunctionTexture != null && material.HasProperty("_TFTex"))
        {
            material.SetTexture("_TFTex", transferFunctionTexture);
            Debug.Log("Asignado _TFTex.");
        }

        if (material.HasProperty("_NoiseTex") && material.GetTexture("_NoiseTex") == null)
        {
            Texture2D generatedNoise = NoiseTextureGenerator.GenerateNoiseTexture(512, 512);
            material.SetTexture("_NoiseTex", generatedNoise);
            Debug.Log("Se generó _NoiseTex porque estaba null.");
        }

        if (material.HasProperty("_TFTex") && material.GetTexture("_TFTex") == null)
        {
            UVRTransferFunction tf = TransferFunctionDatabase.CreateTransferFunction();
            material.SetTexture("_TFTex", tf.GetTexture());
            Debug.Log("Se generó _TFTex porque estaba null.");
        }

        if (setMinMaxToZeroOne)
        {
            SetFloatIfExists(material, "_MinVal", 0.0f);
            SetFloatIfExists(material, "_MaxVal", 1.0f);
        }

        material.EnableKeyword("MODE_DVR");
        material.DisableKeyword("MODE_MIP");
        material.DisableKeyword("MODE_SURF");

        EditorUtility.SetDirty(material);
    }

    private static void SetFloatIfExists(
        Material material,
        string propertyName,
        float value
    )
    {
        if (!material.HasProperty(propertyName))
            return;

        material.SetFloat(propertyName, value);
    }

    private static Texture2D GetExistingNoiseTexture(Material material)
    {
        if (material == null)
            return null;

        if (!material.HasProperty("_NoiseTex"))
            return null;

        return material.GetTexture("_NoiseTex") as Texture2D;
    }

    // ============================================================
    // TRANSFER FUNCTION
    // ============================================================

    private static UVRTransferFunction EnsureTransferFunction(VolumeRenderedObject volObj)
    {
        UVRTransferFunction tf = null;

        if (volObj != null)
        {
            object value = GetFieldOrPropertyValue(volObj, "transferFunction");
            tf = value as UVRTransferFunction;
        }

        if (tf == null)
            tf = TransferFunctionDatabase.CreateTransferFunction();

        if (volObj != null)
            SetFieldOrPropertyValue(volObj, "transferFunction", tf);

        return tf;
    }

    private static UVRTransferFunction2D EnsureTransferFunction2D(VolumeRenderedObject volObj)
    {
        UVRTransferFunction2D tf2D = null;

        if (volObj != null)
        {
            object value = GetFieldOrPropertyValue(volObj, "transferFunction2D");
            tf2D = value as UVRTransferFunction2D;
        }

        if (tf2D == null)
            tf2D = TransferFunctionDatabase.CreateTransferFunction2D();

        if (volObj != null)
            SetFieldOrPropertyValue(volObj, "transferFunction2D", tf2D);

        return tf2D;
    }

    // ============================================================
    // GUARDADO DE ASSETS GENERADOS
    // ============================================================

    private static void SaveGeneratedAssets(
        VolumeDataset dataset,
        Texture3D dataTexture,
        Texture3D gradientTexture,
        Texture2D noiseTexture,
        Texture2D transferFunctionTexture,
        UVRTransferFunction transferFunction,
        UVRTransferFunction2D transferFunction2D,
        Material material
    )
    {
        string datasetFolder = "Assets/RectifiedVolumes/GeneratedDatasets";
        string textureFolder = "Assets/RectifiedVolumes/GeneratedTextures";
        string materialFolder = "Assets/RectifiedVolumes/GeneratedMaterials";
        string transferFunctionFolder = "Assets/RectifiedVolumes/GeneratedTransferFunctions";

        EnsureFolder(datasetFolder);
        EnsureFolder(textureFolder);
        EnsureFolder(materialFolder);
        EnsureFolder(transferFunctionFolder);

        string safeDatasetName = MakeSafeFileName(dataset.datasetName);

        if (dataset != null && !AssetDatabase.Contains(dataset))
        {
            string datasetPath = AssetDatabase.GenerateUniqueAssetPath(
                datasetFolder + "/" + safeDatasetName + ".asset"
            );

            AssetDatabase.CreateAsset(dataset, datasetPath);
            Debug.Log("VolumeDataset guardado en: " + datasetPath);
        }

        if (dataTexture != null && !AssetDatabase.Contains(dataTexture))
        {
            dataTexture.name = safeDatasetName + "_DataTexture";

            string dataTexturePath = AssetDatabase.GenerateUniqueAssetPath(
                textureFolder + "/" + MakeSafeFileName(dataTexture.name) + ".asset"
            );

            AssetDatabase.CreateAsset(dataTexture, dataTexturePath);
            Debug.Log("Data Texture guardada en: " + dataTexturePath);
        }

        if (gradientTexture != null && !AssetDatabase.Contains(gradientTexture))
        {
            gradientTexture.name = safeDatasetName + "_GradientTexture";

            string gradientTexturePath = AssetDatabase.GenerateUniqueAssetPath(
                textureFolder + "/" + MakeSafeFileName(gradientTexture.name) + ".asset"
            );

            AssetDatabase.CreateAsset(gradientTexture, gradientTexturePath);
            Debug.Log("Gradient Texture guardada en: " + gradientTexturePath);
        }

        if (noiseTexture != null && !AssetDatabase.Contains(noiseTexture))
        {
            noiseTexture.name = safeDatasetName + "_NoiseTexture";

            string noiseTexturePath = AssetDatabase.GenerateUniqueAssetPath(
                textureFolder + "/" + MakeSafeFileName(noiseTexture.name) + ".asset"
            );

            AssetDatabase.CreateAsset(noiseTexture, noiseTexturePath);
            Debug.Log("Noise Texture guardada en: " + noiseTexturePath);
        }

        if (transferFunctionTexture != null && !AssetDatabase.Contains(transferFunctionTexture))
        {
            transferFunctionTexture.name = safeDatasetName + "_TransferFunctionTexture";

            string tfTexturePath = AssetDatabase.GenerateUniqueAssetPath(
                textureFolder + "/" + MakeSafeFileName(transferFunctionTexture.name) + ".asset"
            );

            AssetDatabase.CreateAsset(transferFunctionTexture, tfTexturePath);
            Debug.Log("Transfer Function Texture guardada en: " + tfTexturePath);
        }

        if (transferFunction != null && !AssetDatabase.Contains(transferFunction))
        {
            string tfPath = AssetDatabase.GenerateUniqueAssetPath(
                transferFunctionFolder + "/" + safeDatasetName + "_TransferFunction.asset"
            );

            AssetDatabase.CreateAsset(transferFunction, tfPath);
            Debug.Log("TransferFunction guardada en: " + tfPath);
        }

        if (transferFunction2D != null && !AssetDatabase.Contains(transferFunction2D))
        {
            string tf2DPath = AssetDatabase.GenerateUniqueAssetPath(
                transferFunctionFolder + "/" + safeDatasetName + "_TransferFunction2D.asset"
            );

            AssetDatabase.CreateAsset(transferFunction2D, tf2DPath);
            Debug.Log("TransferFunction2D guardada en: " + tf2DPath);
        }

        if (material != null && !AssetDatabase.Contains(material))
        {
            string materialPath = AssetDatabase.GenerateUniqueAssetPath(
                materialFolder + "/" + MakeSafeFileName(material.name) + ".mat"
            );

            AssetDatabase.CreateAsset(material, materialPath);
            Debug.Log("Material guardado en: " + materialPath);
        }
    }

    // ============================================================
    // BÚSQUEDA DE OBJETOS, RENDERERS Y DATASET
    // ============================================================

    private static VolumeRenderedObject FindVolumeRenderedObject(GameObject root)
    {
        if (root == null)
            return null;

        VolumeRenderedObject volObj = root.GetComponent<VolumeRenderedObject>();

        if (volObj != null)
            return volObj;

        volObj = root.GetComponentInChildren<VolumeRenderedObject>(true);

        if (volObj != null)
            return volObj;

        volObj = root.GetComponentInParent<VolumeRenderedObject>();

        return volObj;
    }

    private static Renderer FindVolumeRenderer(GameObject root)
    {
        if (root == null)
            return null;

        Renderer renderer = root.GetComponent<Renderer>();

        if (IsVolumeRenderer(renderer))
            return renderer;

        Renderer[] childRenderers = root.GetComponentsInChildren<Renderer>(true);

        foreach (Renderer r in childRenderers)
        {
            if (IsVolumeRenderer(r))
                return r;
        }

        Renderer[] parentRenderers = root.GetComponentsInParent<Renderer>(true);

        foreach (Renderer r in parentRenderers)
        {
            if (IsVolumeRenderer(r))
                return r;
        }

        return null;
    }

    private static bool IsVolumeRenderer(Renderer renderer)
    {
        if (renderer == null)
            return false;

        Material material = renderer.sharedMaterial;

        if (material == null)
            return false;

        if (material.HasProperty("_DataTex"))
            return true;

        if (material.shader != null && material.shader.name.Contains("DirectVolumeRendering"))
            return true;

        return false;
    }

    private static Renderer GetRendererFromVolumeRenderedObject(
        VolumeRenderedObject volObj
    )
    {
        if (volObj == null)
            return null;

        object value = GetFieldOrPropertyValue(
            volObj,
            "meshRenderer"
        );

        Renderer renderer = value as Renderer;

        if (renderer != null)
            return renderer;

        return volObj.GetComponentInChildren<Renderer>(true);
    }

    private static void SetRendererOnVolumeRenderedObject(
        VolumeRenderedObject volObj,
        Renderer renderer
    )
    {
        if (volObj == null || renderer == null)
            return;

        SetFieldOrPropertyValue(
            volObj,
            "meshRenderer",
            renderer
        );
    }

    private static VolumeDataset GetSelectedVolumeDataset()
    {
        VolumeDataset selectedDataset = Selection.activeObject as VolumeDataset;

        if (selectedDataset != null)
            return selectedDataset;

        GameObject selectedGameObject = Selection.activeGameObject;

        if (selectedGameObject == null)
            return null;

        return GetSelectedOrObjectVolumeDataset(selectedGameObject);
    }

    private static VolumeDataset GetSelectedOrObjectVolumeDataset(GameObject obj)
    {
        if (obj == null)
            return null;

        Component[] childComponents = obj.GetComponentsInChildren<Component>(true);

        foreach (Component component in childComponents)
        {
            if (component == null)
                continue;

            VolumeDataset dataset = FindVolumeDatasetByReflection(component);

            if (dataset != null)
                return dataset;
        }

        Component[] parentComponents = obj.GetComponentsInParent<Component>(true);

        foreach (Component component in parentComponents)
        {
            if (component == null)
                continue;

            VolumeDataset dataset = FindVolumeDatasetByReflection(component);

            if (dataset != null)
                return dataset;
        }

        return null;
    }

    private static VolumeDataset FindVolumeDatasetByReflection(Component component)
    {
        Type type = component.GetType();

        while (type != null)
        {
            FieldInfo[] fields = type.GetFields(
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance
            );

            foreach (FieldInfo field in fields)
            {
                if (!typeof(VolumeDataset).IsAssignableFrom(field.FieldType))
                    continue;

                VolumeDataset dataset = field.GetValue(component) as VolumeDataset;

                if (dataset != null)
                    return dataset;
            }

            PropertyInfo[] properties = type.GetProperties(
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance
            );

            foreach (PropertyInfo property in properties)
            {
                if (!typeof(VolumeDataset).IsAssignableFrom(property.PropertyType))
                    continue;

                if (!property.CanRead)
                    continue;

                try
                {
                    VolumeDataset dataset = property.GetValue(component, null) as VolumeDataset;

                    if (dataset != null)
                        return dataset;
                }
                catch
                {
                }
            }

            type = type.BaseType;
        }

        return null;
    }

    private static void SetVolumeDatasetByReflection(
        Component component,
        VolumeDataset dataset
    )
    {
        if (component == null || dataset == null)
            return;

        Type type = component.GetType();

        while (type != null)
        {
            FieldInfo[] fields = type.GetFields(
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance
            );

            foreach (FieldInfo field in fields)
            {
                if (!typeof(VolumeDataset).IsAssignableFrom(field.FieldType))
                    continue;

                field.SetValue(component, dataset);
                EditorUtility.SetDirty(component);

                Debug.Log(
                    "VolumeDataset asignado en campo: " +
                    component.GetType().Name +
                    "." +
                    field.Name
                );

                return;
            }

            PropertyInfo[] properties = type.GetProperties(
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance
            );

            foreach (PropertyInfo property in properties)
            {
                if (!typeof(VolumeDataset).IsAssignableFrom(property.PropertyType))
                    continue;

                if (!property.CanWrite)
                    continue;

                try
                {
                    property.SetValue(component, dataset, null);
                    EditorUtility.SetDirty(component);

                    Debug.Log(
                        "VolumeDataset asignado en propiedad: " +
                        component.GetType().Name +
                        "." +
                        property.Name
                    );

                    return;
                }
                catch
                {
                }
            }

            type = type.BaseType;
        }
    }

    // ============================================================
    // REFLEXIÓN GENÉRICA
    // ============================================================

    private static object GetFieldOrPropertyValue(
        object obj,
        string name
    )
    {
        if (obj == null || string.IsNullOrEmpty(name))
            return null;

        Type type = obj.GetType();

        while (type != null)
        {
            FieldInfo field = type.GetField(
                name,
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance
            );

            if (field != null)
                return field.GetValue(obj);

            PropertyInfo property = type.GetProperty(
                name,
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance
            );

            if (property != null && property.CanRead)
            {
                try
                {
                    return property.GetValue(obj, null);
                }
                catch
                {
                    return null;
                }
            }

            type = type.BaseType;
        }

        return null;
    }

    private static void SetFieldOrPropertyValue(
        object obj,
        string name,
        object value
    )
    {
        if (obj == null || string.IsNullOrEmpty(name))
            return;

        Type type = obj.GetType();

        while (type != null)
        {
            FieldInfo field = type.GetField(
                name,
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance
            );

            if (field != null)
            {
                field.SetValue(obj, value);

                UnityEngine.Object unityObject = obj as UnityEngine.Object;

                if (unityObject != null)
                    EditorUtility.SetDirty(unityObject);

                return;
            }

            PropertyInfo property = type.GetProperty(
                name,
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance
            );

            if (property != null && property.CanWrite)
            {
                try
                {
                    property.SetValue(obj, value, null);

                    UnityEngine.Object unityObject = obj as UnityEngine.Object;

                    if (unityObject != null)
                        EditorUtility.SetDirty(unityObject);

                    return;
                }
                catch
                {
                    return;
                }
            }

            type = type.BaseType;
        }
    }

    // ============================================================
    // UTILIDADES
    // ============================================================

    private static void EnsureFolder(string folderPath)
    {
        string[] parts = folderPath.Split('/');

        if (parts.Length == 0)
            return;

        string current = parts[0];

        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];

            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);

            current = next;
        }
    }

    private static string MakeSafeFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "GeneratedAsset";

        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return name;
    }
}
#endif