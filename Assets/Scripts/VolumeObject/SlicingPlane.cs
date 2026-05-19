using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.UI;

namespace UnityVolumeRendering
{
    using UVRTransferFunction = UnityVolumeRendering.TransferFunction;

    [ExecuteAlways]
    [DefaultExecutionOrder(-10000)]
    public class SlicingPlane : MonoBehaviour
    {
        private enum SelectedUltrasoundParameter
        {
            None,
            Gain,
            DynamicRange,
            Depth,
            Zoom,
            Frequency
        }

        [Serializable]
        private class AutoPowerButtonLock
        {
            public string buttonName;
            public bool keepEnabledWhenPowerOff;
            public Behaviour interactableUnityEventWrapper;
            public Collider[] colliders;
            public Renderer[] visualRenderers;

            [NonSerialized] public Material[] materialInstances;
            [NonSerialized] public Color[] originalColors;
        }

        [Header("Fuente del volumen rectificado")]
        [Tooltip("Opcional. Puede ser FetalBrainDICOM. No se usa para sacar el Mesh Renderer, solo como referencia auxiliar.")]
        public VolumeRenderedObject sourceVolumeObject;

        [Tooltip("Arrastra aquí FetalBrainDICOM/VolumeContainer(Clone), es decir, el hijo que tiene el Mesh Renderer del volumen rectificado.")]
        public Transform volumeContainer;

        [Tooltip("Arrastra aquí el Mesh Renderer de FetalBrainDICOM/VolumeContainer(Clone). De aquí se copia el _DataTex rectificado.")]
        public MeshRenderer sourceVolumeRenderer;

        [Header("Renderers de corte")]
        [Tooltip("Renderer de la pantalla del ecógrafo")]
        public MeshRenderer screenRenderer;

        [Header("Pantalla compartida entre transductores")]
        [SerializeField] private bool useSharedScreenPriority = true;
        [SerializeField] private bool defaultScreenSource = false;

        [Header("UI Sliders integrados")]
        [SerializeField] private bool manageUISlidersFromThisSlicingPlane = false;

        [Tooltip("Objeto padre que contiene todos los sliders del ecógrafo.")]
        [SerializeField] private Transform slidersRoot;

        [SerializeField] private bool autoFindPowerControlledSliders = true;

        [Tooltip("Sliders que se bloquean cuando el ecógrafo está apagado. Incluye TGC y slider general.")]
        [SerializeField] private Slider[] powerControlledSliders;

        [Tooltip("Slider único que controla el parámetro actualmente seleccionado: Gain, DR, Depth, Zoom o Frequency.")]
        [SerializeField] private Slider selectedParameterSlider;

        [SerializeField] private bool disableSelectedSliderWhenNoParameterSelected = true;

        private bool suppressSelectedParameterSliderCallback;

        private static SlicingPlane activeScreenSource;

        private static bool freezeActive = false;
        private static SlicingPlane frozenScreenSource;

        [SerializeField] private bool startPoweredOff = true;

        private static bool powerOn = false;
        private static bool powerStateInitialized = false;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticPowerState()
        {
            activeScreenSource = null;
            freezeActive = false;
            frozenScreenSource = null;

            powerOn = false;
            powerStateInitialized = false;
        }

        private void InitializePowerStateIfNeeded()
        {
            if (powerStateInitialized)
                return;

            powerOn = !startPoweredOff;
            powerStateInitialized = true;
        }

        public static bool IsPowerOn
        {
            get { return powerOn; }
        }

        public static bool IsGainSelected
        {
            get { return IsSelectedParameter(SelectedUltrasoundParameter.Gain); }
        }

        public static bool IsDynamicRangeSelected
        {
            get { return IsSelectedParameter(SelectedUltrasoundParameter.DynamicRange); }
        }

        public static bool IsDepthSelected
        {
            get { return IsSelectedParameter(SelectedUltrasoundParameter.Depth); }
        }

        public static bool IsZoomSelected
        {
            get { return IsSelectedParameter(SelectedUltrasoundParameter.Zoom); }
        }

        public static bool IsFrequencySelected
        {
            get { return IsSelectedParameter(SelectedUltrasoundParameter.Frequency); }
        }

        private static bool IsSelectedParameter(SelectedUltrasoundParameter parameter)
        {
            if (activeScreenSource == null)
                return false;

            return activeScreenSource.selectedParameter == parameter;
        }

        public static bool IsFreezeActive
        {
            get { return freezeActive; }
        }

        public static SlicingPlane ActiveScreenSource
        {
            get { return activeScreenSource; }
        }

        [Header("Bloqueo automático de botones por Power")]
        [Tooltip("Arrastra aquí el objeto padre que contiene todos los botones del ecógrafo.")]
        [SerializeField] private Transform ultrasoundButtonsRoot;

        [Tooltip("Busca automáticamente hijos que tengan un componente llamado InteractableUnityEventWrapper.")]
        [SerializeField] private bool autoFindButtonsFromRoot = true;

        [Tooltip("Nombre del componente que se buscará automáticamente.")]
        [SerializeField] private string interactableWrapperTypeName = "InteractableUnityEventWrapper";

        [Tooltip("Si el nombre del botón contiene esta palabra, no se deshabilita. Úsalo para el botón POWER.")]
        [SerializeField] private string powerButtonNameContains = "PowerButton";

        [SerializeField] private Color powerOffButtonColor = Color.black;
        [SerializeField] private Color plusMinusPowerOnColor = Color.red;

        [Tooltip("Si está activo, también deshabilita colliders de los botones cuando Power está OFF.")]
        [SerializeField] private bool disableButtonCollidersWhenPowerOff = true;

        [Tooltip("Si está activo, toma los renderers del botón y de sus hijos, excepto labels y contornos controlados por UltrasoundMachineButtonsDebugVisual.")]
        [SerializeField] private bool includeChildRenderersAsButtonVisual = true;

        [SerializeField] private bool logFoundPowerButtons = true;

        [SerializeField] private List<AutoPowerButtonLock> autoPowerButtons = new List<AutoPowerButtonLock>();

        private bool autoButtonsScanned = false;
        private bool buttonsPowerStateInitialized = false;
        private bool lastAppliedButtonPowerState = true;

        [Header("Binding del volumen rectificado")]
        [SerializeField] private bool bindDataTextureFromSourceRenderer = true;
        [SerializeField] private bool bindEveryFrame = true;
        [SerializeField] private bool autoFindReferences = true;
        [SerializeField] private bool forceSliceShaderOnPlaneAndScreen = false;
        [SerializeField] private bool logBindingOnce = true;

        [Header("Ganancia ecográfica")]
        [SerializeField] private float gain = 1.0f;
        [SerializeField] private float gainStep = 0.1f;
        [SerializeField] private float minGain = 0.5f;
        [SerializeField] private float maxGain = 2.0f;

        [Header("Atenuación y TGC")]
        [SerializeField] private bool tgcEnabled = true;

        [SerializeField] private float tgc0 = 0.8f;
        [SerializeField] private float tgc1 = 0.9f;
        [SerializeField] private float tgc2 = 1.0f;
        [SerializeField] private float tgc3 = 1.15f;
        [SerializeField] private float tgc4 = 1.35f;
        [SerializeField] private float tgc5 = 1.6f;

        [SerializeField] private float minTGC = 0.1f;
        [SerializeField] private float maxTGC = 3.0f;

        [Header("Frecuencia ecográfica")]
        [SerializeField] private float frequencyMHz = 5.0f;
        [SerializeField] private float frequencyStepMHz = 1.0f;
        [SerializeField] private float minFrequencyMHz = 2.0f;
        [SerializeField] private float maxFrequencyMHz = 15.0f;

        [Header("Rango dinámico / Contraste")]
        [SerializeField] private float dynamicRange = 60.0f;
        [SerializeField] private float dynamicRangeStep = 5.0f;
        [SerializeField] private float minDynamicRange = 40.0f;
        [SerializeField] private float maxDynamicRange = 100.0f;
        [SerializeField] private float referenceDynamicRange = 60.0f;

        [Header("Profundidad visible")]
        [SerializeField] private float depthVisible = 1.0f;
        [SerializeField] private float depthStep = 0.1f;
        [SerializeField] private float minDepthVisible = 0.3f;
        [SerializeField] private float maxDepthVisible = 1.0f;

        [Header("Zoom ecográfico")]
        [SerializeField] private float zoom = 1.0f;
        [SerializeField] private float zoomStep = 0.5f;
        [SerializeField] private float minZoom = 1.0f;
        [SerializeField] private float maxZoom = 3.0f;

        [Tooltip("Centro del zoom en coordenadas UV. 0.5, 0.5 significa centro de la imagen.")]
        [SerializeField] private Vector2 zoomCenter = new Vector2(0.5f, 0.5f);

        [Header("Minimapa de zoom")]
        [SerializeField] private bool showZoomMinimap = true;

        [Tooltip("Rectángulo del minimapa en UV: x, y, ancho, alto.")]
        [SerializeField] private Vector4 zoomMinimapRect = new Vector4(0.72f, 0.72f, 0.25f, 0.25f);

        [Tooltip("Grosor visual de la caja de zoom dentro del minimapa.")]
        [SerializeField] private float zoomBoxThickness = 0.01f;

        [Header("Trackball para mover ventana de zoom")]
        [SerializeField] private bool zoomPanModeActive = false;

        [Tooltip("Arrastra aquí la esfera visible de la trackball. Si tienes dos SlicingPlane, arrastra la misma esfera en ambos.")]
        [SerializeField] private Transform trackballVisual;

        [SerializeField] private XRNode trackballControllerNode = XRNode.RightHand;

        [Tooltip("Velocidad con la que el joystick mueve la ventana zoomeada.")]
        [SerializeField] private float zoomPanSpeed = 0.45f;

        [SerializeField] private float joystickDeadZone = 0.15f;

        [Tooltip("Cuando se presiona la trackball, selecciona automáticamente el parámetro Zoom.")]
        [SerializeField] private bool autoSelectZoomWhenTrackballPressed = true;

        [Tooltip("Velocidad visual de rotación de la bola.")]
        [SerializeField] private float trackballRotationSpeed = 240.0f;

        private InputDevice trackballControllerDevice;

        [Header("Eje de profundidad")]
        [Tooltip("Activa esto si al bajar la profundidad se recorta desde arriba en vez de abajo")]
        [SerializeField] private bool invertDepthAxis = false;

        [Header("Geometría del sector convexo")]
        [SerializeField] private float sectorAngleDegrees = 70.0f;
        [SerializeField] private float sectorApexDistanceAboveTop = 0.15f;
        [SerializeField] private float sectorInnerRadius = 0.25f;

        [Header("Ubicación local del sector en el volumen")]
        [SerializeField] private float sectorApexLocalZ = 0.45f;
        [SerializeField] private float sectorLocalXScale = 1.0f;
        [SerializeField] private float sectorLocalZScale = 1.0f;
        [SerializeField] private bool flipSectorX = true;

        [Header("Parámetro seleccionado")]
        [SerializeField] private SelectedUltrasoundParameter selectedParameter = SelectedUltrasoundParameter.None;

        private MeshRenderer planeRenderer;
        private Material runtimeMaterial;
        private Material screenMaterial;

        private Texture3D cachedDataTexture;
        private Texture2D cachedTransferFunctionTexture;

        private bool alreadyLoggedBinding;
        private bool alreadyLoggedMissingData;

        private static readonly int PropParentInverseMat = Shader.PropertyToID("_parentInverseMat");
        private static readonly int PropPlaneMat = Shader.PropertyToID("_planeMat");

        private static readonly int PropDataTex = Shader.PropertyToID("_DataTex");
        private static readonly int PropTFTex = Shader.PropertyToID("_TFTex");

        private static readonly int PropGain = Shader.PropertyToID("_Gain");

        private static readonly int PropFrequencyMHz = Shader.PropertyToID("_FrequencyMHz");

        private static readonly int PropTGCEnabled = Shader.PropertyToID("_TGCEnabled");
        private static readonly int PropTGC0 = Shader.PropertyToID("_TGC0");
        private static readonly int PropTGC1 = Shader.PropertyToID("_TGC1");
        private static readonly int PropTGC2 = Shader.PropertyToID("_TGC2");
        private static readonly int PropTGC3 = Shader.PropertyToID("_TGC3");
        private static readonly int PropTGC4 = Shader.PropertyToID("_TGC4");
        private static readonly int PropTGC5 = Shader.PropertyToID("_TGC5");

        private static readonly int PropDynamicRange = Shader.PropertyToID("_DynamicRange");
        private static readonly int PropReferenceDynamicRange = Shader.PropertyToID("_ReferenceDynamicRange");
        private static readonly int PropContrastFromDynamicRange = Shader.PropertyToID("_ContrastFromDynamicRange");

        private static readonly int PropDepthVisible = Shader.PropertyToID("_DepthVisible");
        private static readonly int PropInvertDepthAxis = Shader.PropertyToID("_InvertDepthAxis");

        private static readonly int PropZoom = Shader.PropertyToID("_Zoom");
        private static readonly int PropZoomCenter = Shader.PropertyToID("_ZoomCenter");
        private static readonly int PropShowZoomMinimap = Shader.PropertyToID("_ShowZoomMinimap");
        private static readonly int PropZoomMinimapRect = Shader.PropertyToID("_ZoomMinimapRect");
        private static readonly int PropZoomBoxThickness = Shader.PropertyToID("_ZoomBoxThickness");
        private static readonly int PropPowerOn = Shader.PropertyToID("_PowerOn");

        private static readonly int PropSectorAngleDegrees = Shader.PropertyToID("_SectorAngleDegrees");
        private static readonly int PropSectorApexDistanceAboveTop = Shader.PropertyToID("_SectorApexDistanceAboveTop");
        private static readonly int PropSectorInnerRadius = Shader.PropertyToID("_SectorInnerRadius");

        private static readonly int PropSectorApexLocalZ = Shader.PropertyToID("_SectorApexLocalZ");
        private static readonly int PropSectorLocalXScale = Shader.PropertyToID("_SectorLocalXScale");
        private static readonly int PropSectorLocalZScale = Shader.PropertyToID("_SectorLocalZScale");

        private static readonly int PropFlipSectorX = Shader.PropertyToID("_FlipSectorX");

        private static readonly int PropMaterialColor = Shader.PropertyToID("_Color");
        private static readonly int PropMaterialBaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int PropMaterialFaceColor = Shader.PropertyToID("_FaceColor");
        private static readonly int PropMaterialOutlineColor = Shader.PropertyToID("_OutlineColor");

        private void Reset()
        {
            RefreshReferences();
        }

        private void Awake()
        {
            InitializePowerStateIfNeeded();
            RefreshAll();
        }

        private void OnEnable()
        {
            InitializePowerStateIfNeeded();
            RefreshAll();

            if (defaultScreenSource || activeScreenSource == null)
                SelectAsScreenSource();

            InitializeIntegratedSliders();
            UpdateIntegratedSliders();
        }

        private void Start()
        {
            ClearSelectedParameter();

            RefreshAll();

            if (Application.isPlaying)
            {
                if (autoFindButtonsFromRoot)
                    ScanAutoButtonsFromRoot();

                ApplyAutoButtonsPowerState();
            }

            InitializeIntegratedSliders();
            UpdateIntegratedSliders();
        }

        private void OnValidate()
        {
            if (!Application.isPlaying)
                selectedParameter = SelectedUltrasoundParameter.None;

            RefreshAll();
        }

        private void Update()
        {
            InitializePowerStateIfNeeded();

            InitializeMaterials();

            if (autoFindReferences)
                RefreshReferences();

            if (bindDataTextureFromSourceRenderer && bindEveryFrame)
                BindTexturesFromSourceRenderer(false);

            UpdateMatricesAndParameters();

            UpdateTrackballJoystick();

            UpdateAutoButtonsPowerStateIfNeeded();

            UpdateIntegratedSliders();
        }

        private void RefreshAll()
        {
            InitializeMaterials();

            if (autoFindReferences)
                RefreshReferences();

            ClampParameters();

            if (bindDataTextureFromSourceRenderer)
                BindTexturesFromSourceRenderer(false);

            UpdateMatricesAndParameters();
        }

        private void InitializeMaterials()
        {
            if (planeRenderer == null)
                planeRenderer = GetComponent<MeshRenderer>();

            if (planeRenderer != null)
            {
                runtimeMaterial = Application.isPlaying
                    ? planeRenderer.material
                    : planeRenderer.sharedMaterial;
            }

            if (screenRenderer != null)
            {
                screenMaterial = Application.isPlaying
                    ? screenRenderer.material
                    : screenRenderer.sharedMaterial;
            }

            if (forceSliceShaderOnPlaneAndScreen)
                ForceSliceShader();
        }

        private void ForceSliceShader()
        {
            Shader sliceShader = Shader.Find("VolumeRendering/ConvexSectorSamplingShader");

            if (sliceShader == null)
                return;

            if (runtimeMaterial != null && runtimeMaterial.shader != sliceShader)
                runtimeMaterial.shader = sliceShader;

            if (screenMaterial != null && screenMaterial.shader != sliceShader)
                screenMaterial.shader = sliceShader;
        }

        private void RefreshReferences()
        {
            if (sourceVolumeObject == null)
            {
                sourceVolumeObject = GetComponentInParent<VolumeRenderedObject>();

                if (sourceVolumeObject == null)
                    sourceVolumeObject = FindFirstVolumeRenderedObject();
            }

            if (volumeContainer == null && sourceVolumeRenderer != null)
                volumeContainer = sourceVolumeRenderer.transform;

            if (sourceVolumeRenderer == null && volumeContainer != null)
                sourceVolumeRenderer = volumeContainer.GetComponent<MeshRenderer>();

            if (sourceVolumeRenderer == null && sourceVolumeObject != null)
                sourceVolumeRenderer = FindSourceVolumeRendererFromVolumeObject(sourceVolumeObject);

            if (volumeContainer == null && sourceVolumeRenderer != null)
                volumeContainer = sourceVolumeRenderer.transform;

            TryAssignHiddenMeshRendererOnVolumeRenderedObject();
        }

        private void TryAssignHiddenMeshRendererOnVolumeRenderedObject()
        {
            if (sourceVolumeObject == null || sourceVolumeRenderer == null)
                return;

            SetFieldOrPropertyValue(
                sourceVolumeObject,
                "meshRenderer",
                sourceVolumeRenderer
            );
        }

        private MeshRenderer FindSourceVolumeRendererFromVolumeObject(VolumeRenderedObject volObj)
        {
            if (volObj == null)
                return null;

            MeshRenderer[] renderers = volObj.GetComponentsInChildren<MeshRenderer>(true);

            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];

                if (renderer == null)
                    continue;

                if (renderer == planeRenderer || renderer == screenRenderer)
                    continue;

                Material mat = renderer.sharedMaterial;

                if (IsDirectVolumeMaterial(mat))
                    return renderer;
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];

                if (renderer == null)
                    continue;

                if (renderer == planeRenderer || renderer == screenRenderer)
                    continue;

                Material mat = renderer.sharedMaterial;

                if (mat != null && mat.HasProperty(PropDataTex))
                    return renderer;
            }

            return null;
        }

        private bool IsDirectVolumeMaterial(Material mat)
        {
            if (mat == null)
                return false;

            if (mat.shader != null && mat.shader.name.Contains("DirectVolumeRendering"))
                return true;

            if (
                mat.HasProperty("_DataTex") &&
                mat.HasProperty("_GradientTex") &&
                mat.HasProperty("_NoiseTex")
            )
            {
                return true;
            }

            return false;
        }

        private void ClampParameters()
        {
            gain = Mathf.Clamp(gain, minGain, maxGain);

            frequencyMHz = Mathf.Clamp(frequencyMHz, minFrequencyMHz, maxFrequencyMHz);

            tgc0 = Mathf.Clamp(tgc0, minTGC, maxTGC);
            tgc1 = Mathf.Clamp(tgc1, minTGC, maxTGC);
            tgc2 = Mathf.Clamp(tgc2, minTGC, maxTGC);
            tgc3 = Mathf.Clamp(tgc3, minTGC, maxTGC);
            tgc4 = Mathf.Clamp(tgc4, minTGC, maxTGC);
            tgc5 = Mathf.Clamp(tgc5, minTGC, maxTGC);

            dynamicRange = Mathf.Clamp(dynamicRange, minDynamicRange, maxDynamicRange);
            depthVisible = Mathf.Clamp(depthVisible, minDepthVisible, maxDepthVisible);
            zoom = Mathf.Clamp(zoom, minZoom, maxZoom);

            zoomCenter = ClampZoomCenterToVisibleArea(zoomCenter);

            zoomMinimapRect.x = Mathf.Clamp01(zoomMinimapRect.x);
            zoomMinimapRect.y = Mathf.Clamp01(zoomMinimapRect.y);
            zoomMinimapRect.z = Mathf.Clamp(zoomMinimapRect.z, 0.05f, 1.0f);
            zoomMinimapRect.w = Mathf.Clamp(zoomMinimapRect.w, 0.05f, 1.0f);

            if (zoomMinimapRect.x + zoomMinimapRect.z > 1.0f)
                zoomMinimapRect.x = 1.0f - zoomMinimapRect.z;

            if (zoomMinimapRect.y + zoomMinimapRect.w > 1.0f)
                zoomMinimapRect.y = 1.0f - zoomMinimapRect.w;

            zoomBoxThickness = Mathf.Clamp(zoomBoxThickness, 0.001f, 0.05f);
            joystickDeadZone = Mathf.Clamp(joystickDeadZone, 0.0f, 0.95f);
            zoomPanSpeed = Mathf.Max(0.0f, zoomPanSpeed);
            trackballRotationSpeed = Mathf.Max(0.0f, trackballRotationSpeed);
        }

        private SlicingPlane GetControlTarget()
        {
            if (useSharedScreenPriority && activeScreenSource != null)
                return activeScreenSource;

            return this;
        }

        private bool IgnoreButtonBecausePowerOff(string buttonName)
        {
            if (powerOn)
                return false;

            Debug.Log(buttonName + " ignorado: ecógrafo apagado");
            return true;
        }

        public static bool IsAnyZoomPanModeActive()
        {
            return activeScreenSource != null &&
                   activeScreenSource.zoomPanModeActive &&
                   activeScreenSource.zoom > 1.0001f;
        }

        private void ClearSelectedParameter()
        {
            selectedParameter = SelectedUltrasoundParameter.None;
        }

        private static void ClearSelectedParameterOnAllSlicingPlanes()
        {
            SlicingPlane[] slicingPlanes = FindObjectsByType<SlicingPlane>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );

            for (int i = 0; i < slicingPlanes.Length; i++)
            {
                if (slicingPlanes[i] == null)
                    continue;

                slicingPlanes[i].ClearSelectedParameter();
            }
        }

        private Vector2 ClampZoomCenterToVisibleArea(Vector2 center)
        {
            float safeZoom = Mathf.Max(zoom, 1.0f);

            if (safeZoom <= 1.0001f)
                return new Vector2(0.5f, 0.5f);

            float halfWidth = 0.5f / safeZoom;
            float halfHeight = 0.5f / safeZoom;

            center.x = Mathf.Clamp(center.x, halfWidth, 1.0f - halfWidth);
            center.y = Mathf.Clamp(center.y, halfHeight, 1.0f - halfHeight);

            return center;
        }

        private void UpdateTrackballJoystick()
        {
            if (!Application.isPlaying)
                return;

            if (!powerOn)
                return;

            if (activeScreenSource != this)
                return;

            if (!zoomPanModeActive)
                return;

            if (zoom <= 1.0001f)
                return;

            if (!trackballControllerDevice.isValid)
                trackballControllerDevice = InputDevices.GetDeviceAtXRNode(trackballControllerNode);

            if (!trackballControllerDevice.isValid)
                return;

            Vector2 joystickAxis;

            bool hasJoystick = trackballControllerDevice.TryGetFeatureValue(
                CommonUsages.primary2DAxis,
                out joystickAxis
            );

            if (!hasJoystick)
                return;

            if (joystickAxis.magnitude < joystickDeadZone)
                return;

            MoveZoomWindowWithJoystick(joystickAxis, Time.deltaTime);
        }

        public void MoveZoomWindowWithJoystick(Vector2 joystickAxis, float deltaTime)
        {
            if (!powerOn)
                return;

            if (!zoomPanModeActive)
                return;

            if (zoom <= 1.0001f)
                return;

            if (joystickAxis.magnitude < joystickDeadZone)
                return;

            Vector2 movement = new Vector2(
                joystickAxis.x,
                joystickAxis.y
            );

            float safeZoom = Mathf.Max(zoom, 1.0f);

            zoomCenter -= movement * zoomPanSpeed * deltaTime / Mathf.Sqrt(safeZoom);
            zoomCenter = ClampZoomCenterToVisibleArea(zoomCenter);

            RotateTrackballVisual(movement, deltaTime);

            ApplyUltrasoundParameters();
        }

        private void RotateTrackballVisual(Vector2 joystickAxis, float deltaTime)
        {
            if (trackballVisual == null)
                return;

            if (joystickAxis.sqrMagnitude < 0.0001f)
                return;

            Vector3 localRotationAxis = new Vector3(
                joystickAxis.y,
                -joystickAxis.x,
                0.0f
            );

            float angle =
                joystickAxis.magnitude *
                trackballRotationSpeed *
                deltaTime;

            trackballVisual.Rotate(
                localRotationAxis.normalized,
                angle,
                Space.Self
            );
        }

        [ContextMenu("Bind Textures From Source Renderer Now")]
        public void BindTexturesFromSourceRendererNow()
        {
            InitializeMaterials();

            if (autoFindReferences)
                RefreshReferences();

            BindTexturesFromSourceRenderer(true);
        }

        private void BindTexturesFromSourceRenderer(bool forceLog)
        {
            Texture3D dataTexture = ResolveDataTextureFromSourceRenderer();
            Texture2D transferFunctionTexture = ResolveTransferFunctionTexture();

            if (dataTexture == null)
            {
                if (!alreadyLoggedMissingData || forceLog)
                {
                    Debug.LogWarning(
                        "SlicingPlane: no se encontró _DataTex en Source Volume Renderer.\n" +
                        "Source Volume Renderer debe ser el Mesh Renderer de FetalBrainDICOM/VolumeContainer(Clone).\n" +
                        "Renderer actual = " + (sourceVolumeRenderer != null ? sourceVolumeRenderer.name : "null") + "\n" +
                        "Material actual = " + GetSourceMaterialDebugName()
                    );
                }

                alreadyLoggedMissingData = true;
                return;
            }

            ApplyTexturesToMaterial(runtimeMaterial, dataTexture, transferFunctionTexture);

            if (powerOn && !freezeActive && (!useSharedScreenPriority || activeScreenSource == this))
                ApplyTexturesToMaterial(screenMaterial, dataTexture, transferFunctionTexture);

            if ((!alreadyLoggedBinding || forceLog) && (!logBindingOnce || forceLog))
            {
                Debug.Log(
                    "SlicingPlane: texturas copiadas desde Source Volume Renderer.\n" +
                    "_DataTex = " + dataTexture.name + "\n" +
                    "_TFTex = " + (transferFunctionTexture != null ? transferFunctionTexture.name : "null") + "\n" +
                    "Source Volume Renderer = " + (sourceVolumeRenderer != null ? sourceVolumeRenderer.name : "null") + "\n" +
                    "Volume Container = " + (volumeContainer != null ? volumeContainer.name : "null")
                );
            }

            alreadyLoggedBinding = true;
        }

        private void ApplyTexturesToMaterial(
            Material mat,
            Texture3D dataTexture,
            Texture2D transferFunctionTexture
        )
        {
            if (mat == null)
                return;

            if (mat.HasProperty(PropDataTex) && dataTexture != null)
                mat.SetTexture(PropDataTex, dataTexture);

            if (mat.HasProperty(PropTFTex) && transferFunctionTexture != null)
                mat.SetTexture(PropTFTex, transferFunctionTexture);
        }

        private void CopyTexturesFromRuntimeToScreen()
        {
            if (runtimeMaterial == null || screenMaterial == null)
                return;

            if (runtimeMaterial.HasProperty(PropDataTex) && screenMaterial.HasProperty(PropDataTex))
            {
                Texture dataTexture = runtimeMaterial.GetTexture(PropDataTex);

                if (dataTexture != null)
                    screenMaterial.SetTexture(PropDataTex, dataTexture);
            }

            if (runtimeMaterial.HasProperty(PropTFTex) && screenMaterial.HasProperty(PropTFTex))
            {
                Texture tfTexture = runtimeMaterial.GetTexture(PropTFTex);

                if (tfTexture != null)
                    screenMaterial.SetTexture(PropTFTex, tfTexture);
            }
        }

        private Texture3D ResolveDataTextureFromSourceRenderer()
        {
            Material sourceMaterial = ResolveSourceVolumeMaterial();

            if (sourceMaterial == null)
                return cachedDataTexture;

            if (!sourceMaterial.HasProperty(PropDataTex))
            {
                Debug.LogWarning(
                    "SlicingPlane: el material fuente no tiene propiedad _DataTex.\n" +
                    "Material = " + sourceMaterial.name + "\n" +
                    "Shader = " + (sourceMaterial.shader != null ? sourceMaterial.shader.name : "null")
                );

                return cachedDataTexture;
            }

            Texture texture = sourceMaterial.GetTexture(PropDataTex);

            if (texture == null)
            {
                Debug.LogWarning(
                    "SlicingPlane: el material fuente tiene _DataTex, pero está en null.\n" +
                    "Material = " + sourceMaterial.name + "\n" +
                    "Shader = " + (sourceMaterial.shader != null ? sourceMaterial.shader.name : "null")
                );

                return cachedDataTexture;
            }

            Texture3D texture3D = texture as Texture3D;

            if (texture3D == null)
            {
                Debug.LogWarning(
                    "SlicingPlane: _DataTex existe, pero no es Texture3D.\n" +
                    "Tipo real = " + texture.GetType().Name
                );

                return cachedDataTexture;
            }

            cachedDataTexture = texture3D;
            return cachedDataTexture;
        }

        private Texture2D ResolveTransferFunctionTexture()
        {
            Material sourceMaterial = ResolveSourceVolumeMaterial();

            if (sourceMaterial != null && sourceMaterial.HasProperty(PropTFTex))
            {
                Texture2D tfFromSourceMaterial = sourceMaterial.GetTexture(PropTFTex) as Texture2D;

                if (tfFromSourceMaterial != null)
                {
                    cachedTransferFunctionTexture = tfFromSourceMaterial;
                    return cachedTransferFunctionTexture;
                }
            }

            if (runtimeMaterial != null && runtimeMaterial.HasProperty(PropTFTex))
            {
                Texture2D tfFromPlane = runtimeMaterial.GetTexture(PropTFTex) as Texture2D;

                if (tfFromPlane != null)
                {
                    cachedTransferFunctionTexture = tfFromPlane;
                    return cachedTransferFunctionTexture;
                }
            }

            if (screenMaterial != null && screenMaterial.HasProperty(PropTFTex))
            {
                Texture2D tfFromScreen = screenMaterial.GetTexture(PropTFTex) as Texture2D;

                if (tfFromScreen != null)
                {
                    cachedTransferFunctionTexture = tfFromScreen;
                    return cachedTransferFunctionTexture;
                }
            }

            if (sourceVolumeObject != null)
            {
                UVRTransferFunction tf = GetFieldOrPropertyValue(sourceVolumeObject, "transferFunction") as UVRTransferFunction;

                if (tf == null)
                {
                    tf = TransferFunctionDatabase.CreateTransferFunction();

                    SetFieldOrPropertyValue(
                        sourceVolumeObject,
                        "transferFunction",
                        tf
                    );
                }

                if (tf != null)
                {
                    cachedTransferFunctionTexture = tf.GetTexture();
                    return cachedTransferFunctionTexture;
                }
            }

            return cachedTransferFunctionTexture;
        }

        private Material ResolveSourceVolumeMaterial()
        {
            if (sourceVolumeRenderer == null)
                return null;

            Material mat = sourceVolumeRenderer.sharedMaterial;

            if (mat != null)
                return mat;

            return null;
        }

        private string GetSourceMaterialDebugName()
        {
            if (sourceVolumeRenderer == null)
                return "null";

            Material mat = sourceVolumeRenderer.sharedMaterial;

            if (mat == null)
                return "null";

            string shaderName = mat.shader != null ? mat.shader.name : "null";

            return mat.name + " / " + shaderName;
        }

        private void UpdateMatricesAndParameters()
        {
            if (runtimeMaterial == null)
                return;

            Transform coordinateTransform = GetCoordinateTransform();

            if (coordinateTransform == null)
                return;

            Matrix4x4 parentInverseMat = coordinateTransform.worldToLocalMatrix;

            Matrix4x4 planeMat = Matrix4x4.TRS(
                transform.position,
                transform.rotation,
                coordinateTransform.lossyScale
            );

            runtimeMaterial.SetMatrix(PropParentInverseMat, parentInverseMat);
            runtimeMaterial.SetMatrix(PropPlaneMat, planeMat);

            ApplyUltrasoundParametersToMaterial(runtimeMaterial);

            if (ShouldWriteToScreen())
            {
                CopyRuntimeMaterialToScreen(parentInverseMat, planeMat);
            }
        }

        private Transform GetCoordinateTransform()
        {
            if (volumeContainer != null)
                return volumeContainer;

            if (sourceVolumeRenderer != null)
                return sourceVolumeRenderer.transform;

            if (sourceVolumeObject != null)
                return sourceVolumeObject.transform;

            return null;
        }

        public void NotifyGrabbed()
        {
            SelectAsScreenSource();
        }

        public void SelectAsScreenSource()
        {
            activeScreenSource = this;

            InitializeMaterials();

            if (autoFindReferences)
                RefreshReferences();

            if (bindDataTextureFromSourceRenderer)
                BindTexturesFromSourceRenderer(false);

            UpdateMatricesAndParameters();
        }

        public bool IsActiveScreenSource()
        {
            return activeScreenSource == this;
        }

        private bool ShouldWriteToScreen()
        {
            if (screenRenderer == null || screenMaterial == null)
                return false;

            if (!powerOn)
                return false;

            if (freezeActive)
                return false;

            if (!useSharedScreenPriority)
                return true;

            if (activeScreenSource == null)
                activeScreenSource = this;

            return activeScreenSource == this;
        }

        private void CopyRuntimeMaterialToScreen(
            Matrix4x4 parentInverseMat,
            Matrix4x4 planeMat
        )
        {
            if (runtimeMaterial == null || screenMaterial == null)
                return;

            if (
                runtimeMaterial.shader != null &&
                screenMaterial.shader != runtimeMaterial.shader
            )
            {
                screenMaterial.shader = runtimeMaterial.shader;
            }

            CopyTexturesFromRuntimeToScreen();

            screenMaterial.SetMatrix(PropParentInverseMat, parentInverseMat);
            screenMaterial.SetMatrix(PropPlaneMat, planeMat);

            ApplyUltrasoundParametersToMaterial(screenMaterial);
        }

        public void PressTrackball()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.PressTrackball();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Trackball"))
                return;

            if (!zoomPanModeActive && zoom <= 1.0001f)
            {
                Debug.Log("Trackball ignorada: no hay zoom aplicado");
                return;
            }

            zoomPanModeActive = !zoomPanModeActive;

            if (zoomPanModeActive)
            {
                if (autoSelectZoomWhenTrackballPressed)
                    selectedParameter = SelectedUltrasoundParameter.Zoom;

                zoomCenter = ClampZoomCenterToVisibleArea(zoomCenter);
            }

            ApplyUltrasoundParameters();

            Debug.Log(
                zoomPanModeActive
                    ? "Trackball activada: joystick derecho reservado para mover la ventana de zoom"
                    : "Trackball desactivada: joystick derecho liberado"
            );
        }

        public void PressFreeze()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.PressFreeze();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Freeze"))
                return;

            ToggleFreeze();
        }

        public void ToggleFreeze()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.ToggleFreeze();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Freeze"))
                return;

            if (!freezeActive)
            {
                if (activeScreenSource == null)
                    activeScreenSource = this;

                SlicingPlane sourceToFreeze = activeScreenSource;

                if (sourceToFreeze != null)
                {
                    freezeActive = false;

                    sourceToFreeze.InitializeMaterials();

                    if (sourceToFreeze.autoFindReferences)
                        sourceToFreeze.RefreshReferences();

                    if (sourceToFreeze.bindDataTextureFromSourceRenderer)
                        sourceToFreeze.BindTexturesFromSourceRenderer(false);

                    sourceToFreeze.UpdateMatricesAndParameters();
                }

                frozenScreenSource = sourceToFreeze;
                freezeActive = true;

                Debug.Log(
                    "Freeze activado. Imagen congelada desde: " +
                    (frozenScreenSource != null ? frozenScreenSource.name : "null")
                );

                return;
            }

            freezeActive = false;
            frozenScreenSource = null;

            if (activeScreenSource != null)
            {
                activeScreenSource.InitializeMaterials();

                if (activeScreenSource.autoFindReferences)
                    activeScreenSource.RefreshReferences();

                if (activeScreenSource.bindDataTextureFromSourceRenderer)
                    activeScreenSource.BindTexturesFromSourceRenderer(false);

                activeScreenSource.UpdateMatricesAndParameters();
            }
            else
            {
                UpdateMatricesAndParameters();
            }

            Debug.Log("Freeze desactivado. La pantalla vuelve al transductor activo.");
        }

        public bool IsFrozen()
        {
            return freezeActive;
        }

        public void PressPower()
        {
            TogglePower();
        }

        public void TogglePower()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.TogglePower();
                return;
            }

            powerOn = !powerOn;

            ClearSelectedParameterOnAllSlicingPlanes();

            if (!powerOn)
            {
                freezeActive = false;
                frozenScreenSource = null;

                if (activeScreenSource != null)
                    activeScreenSource.zoomPanModeActive = false;

                zoomPanModeActive = false;

                ApplyPowerStateToScreenMaterials();
                ApplyAutoButtonsPowerStateToAllSlicingPlanes();

                Debug.Log("Ecógrafo apagado");
                return;
            }

            ApplyPowerStateToScreenMaterials();
            ApplyAutoButtonsPowerStateToAllSlicingPlanes();

            if (activeScreenSource != null)
            {
                activeScreenSource.InitializeMaterials();

                if (activeScreenSource.autoFindReferences)
                    activeScreenSource.RefreshReferences();

                if (activeScreenSource.bindDataTextureFromSourceRenderer)
                    activeScreenSource.BindTexturesFromSourceRenderer(false);

                activeScreenSource.UpdateMatricesAndParameters();
            }
            else
            {
                UpdateMatricesAndParameters();
            }

            Debug.Log("Ecógrafo encendido");
        }

        private void ApplyPowerStateToScreenMaterials()
        {
            SlicingPlane[] slicingPlanes = FindObjectsByType<SlicingPlane>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );

            for (int i = 0; i < slicingPlanes.Length; i++)
            {
                SlicingPlane slicingPlane = slicingPlanes[i];

                if (slicingPlane == null)
                    continue;

                slicingPlane.InitializeMaterials();

                if (slicingPlane.runtimeMaterial != null &&
                    slicingPlane.runtimeMaterial.HasProperty(PropPowerOn))
                {
                    slicingPlane.runtimeMaterial.SetFloat(
                        PropPowerOn,
                        powerOn ? 1.0f : 0.0f
                    );
                }

                if (slicingPlane.screenMaterial != null &&
                    slicingPlane.screenMaterial.HasProperty(PropPowerOn))
                {
                    slicingPlane.screenMaterial.SetFloat(
                        PropPowerOn,
                        powerOn ? 1.0f : 0.0f
                    );
                }
            }
        }

        [ContextMenu("Buscar botones automaticamente desde ultrasound Buttons Root")]
        public void RescanultrasoundButtonsFromRoot()
        {
            autoButtonsScanned = false;
            ScanAutoButtonsFromRoot();
            ApplyAutoButtonsPowerState();
        }

        private void UpdateAutoButtonsPowerStateIfNeeded()
        {
            if (!Application.isPlaying)
                return;

            if (autoFindButtonsFromRoot && !autoButtonsScanned)
                ScanAutoButtonsFromRoot();

            if (
                !buttonsPowerStateInitialized ||
                lastAppliedButtonPowerState != powerOn ||
                !powerOn
            )
            {
                ApplyAutoButtonsPowerState();
            }
        }

        private void ScanAutoButtonsFromRoot()
        {
            autoButtonsScanned = true;

            if (!autoFindButtonsFromRoot)
                return;

            if (ultrasoundButtonsRoot == null)
                return;

            autoPowerButtons.Clear();

            HashSet<Transform> addedButtonRoots = new HashSet<Transform>();

            Component[] components = ultrasoundButtonsRoot.GetComponentsInChildren<Component>(true);

            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];

                if (component == null)
                    continue;

                Type componentType = component.GetType();

                if (componentType == null)
                    continue;

                if (componentType.Name != interactableWrapperTypeName)
                    continue;

                Behaviour wrapperBehaviour = component as Behaviour;

                if (wrapperBehaviour == null)
                    continue;

                Transform buttonRoot = GetButtonRootUnderButtonsRoot(component.transform);

                if (buttonRoot == null)
                    continue;

                if (addedButtonRoots.Contains(buttonRoot))
                    continue;

                addedButtonRoots.Add(buttonRoot);

                AutoPowerButtonLock button = new AutoPowerButtonLock();

                button.buttonName = buttonRoot.name;
                button.keepEnabledWhenPowerOff = IsPowerButton(buttonRoot);
                button.interactableUnityEventWrapper = wrapperBehaviour;

                if (disableButtonCollidersWhenPowerOff)
                    button.colliders = buttonRoot.GetComponentsInChildren<Collider>(true);
                else
                    button.colliders = new Collider[0];

                if (includeChildRenderersAsButtonVisual)
                    button.visualRenderers = GetPowerControlledRenderers(buttonRoot);
                else
                {
                    Renderer renderer = buttonRoot.GetComponent<Renderer>();

                    button.visualRenderers =
                        renderer != null && !ShouldIgnoreRendererForPowerVisual(renderer, buttonRoot)
                            ? new Renderer[] { renderer }
                            : new Renderer[0];
                }

                CacheOriginalButtonColors(button);

                autoPowerButtons.Add(button);
            }

            if (logFoundPowerButtons)
            {
                Debug.Log(
                    "SlicingPlane: botones encontrados automaticamente = " +
                    autoPowerButtons.Count
                );
            }
        }

        private Transform GetButtonRootUnderButtonsRoot(Transform current)
        {
            if (current == null)
                return null;

            if (ultrasoundButtonsRoot == null)
                return current;

            if (current == ultrasoundButtonsRoot)
                return current;

            Transform candidate = current;

            while (
                candidate.parent != null &&
                candidate.parent != ultrasoundButtonsRoot
            )
            {
                candidate = candidate.parent;
            }

            return candidate;
        }

        private bool IsPowerButton(Transform buttonRoot)
        {
            if (buttonRoot == null)
                return false;

            if (string.IsNullOrEmpty(powerButtonNameContains))
                return false;

            string token = powerButtonNameContains.ToLowerInvariant();

            Transform current = buttonRoot;

            while (current != null)
            {
                string currentName = current.name.ToLowerInvariant();

                if (currentName.Contains(token))
                    return true;

                if (current == ultrasoundButtonsRoot)
                    break;

                current = current.parent;
            }

            return false;
        }

        private Renderer[] GetPowerControlledRenderers(Transform buttonRoot)
        {
            if (buttonRoot == null)
                return new Renderer[0];

            Renderer[] allRenderers = buttonRoot.GetComponentsInChildren<Renderer>(true);
            List<Renderer> filteredRenderers = new List<Renderer>();

            for (int i = 0; i < allRenderers.Length; i++)
            {
                Renderer renderer = allRenderers[i];

                if (renderer == null)
                    continue;

                if (ShouldIgnoreRendererForPowerVisual(renderer, buttonRoot))
                    continue;

                filteredRenderers.Add(renderer);
            }

            return filteredRenderers.ToArray();
        }

        private bool ShouldIgnoreRendererForPowerVisual(Renderer renderer, Transform buttonRoot)
        {
            if (renderer == null)
                return true;

            if (RendererBelongsToText(renderer))
                return true;

            if (IsPlusMinusButton(buttonRoot))
                return false;

            if (RendererIsControlledByCustomButtonVisual(renderer, buttonRoot))
                return true;

            return false;
        }

        private bool IsPlusMinusButton(Transform buttonRoot)
        {
            if (buttonRoot == null)
                return false;

            string name = buttonRoot.name.ToLowerInvariant();

            name = name.Replace(" ", "");
            name = name.Replace("_", "");
            name = name.Replace("-", "");
            name = name.Replace("(", "");
            name = name.Replace(")", "");

            if (name.Contains("plus"))
                return true;

            if (name.Contains("minus"))
                return true;

            if (name.Contains("mas"))
                return true;

            if (name.Contains("más"))
                return true;

            if (name.Contains("menos"))
                return true;

            if (name.Contains("increase"))
                return true;

            if (name.Contains("decrease"))
                return true;

            if (name.Contains("aumentar"))
                return true;

            if (name.Contains("disminuir"))
                return true;

            return false;
        }

        private bool RendererBelongsToText(Renderer renderer)
        {
            if (renderer == null)
                return true;

            Component[] components = renderer.GetComponents<Component>();

            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];

                if (component == null)
                    continue;

                string typeName = component.GetType().Name;

                if (typeName.Contains("TMP_Text"))
                    return true;

                if (typeName.Contains("TextMeshPro"))
                    return true;
            }

            string objectName = renderer.gameObject.name.ToLowerInvariant();

            if (objectName.Contains("label"))
                return true;

            if (objectName.Contains("text"))
                return true;

            return false;
        }

        private bool RendererIsControlledByCustomButtonVisual(Renderer renderer, Transform buttonRoot)
        {
            if (renderer == null)
                return false;

            Component[] ownComponents = renderer.GetComponents<Component>();

            for (int i = 0; i < ownComponents.Length; i++)
            {
                Component component = ownComponents[i];

                if (component == null)
                    continue;

                string typeName = component.GetType().Name;

                if (typeName == "UltrasoundMachineButtonsDebugVisual")
                    return true;

                if (typeName == "InteractableDebugVisual")
                    return true;
            }

            if (buttonRoot == null)
                return false;

            Component[] allComponents = buttonRoot.GetComponentsInChildren<Component>(true);

            for (int i = 0; i < allComponents.Length; i++)
            {
                Component component = allComponents[i];

                if (component == null)
                    continue;

                string typeName = component.GetType().Name;

                if (typeName == "UltrasoundMachineButtonsDebugVisual")
                {
                    Renderer targetRenderer = GetRendererFieldOrPropertyValue(
                        component,
                        "targetRenderer"
                    );

                    if (targetRenderer == renderer)
                        return true;
                }

                if (typeName == "InteractableDebugVisual")
                {
                    Renderer targetRenderer = GetRendererFieldOrPropertyValue(
                        component,
                        "_renderer"
                    );

                    if (targetRenderer == renderer)
                        return true;
                }
            }

            return false;
        }

        private Renderer GetRendererFieldOrPropertyValue(Component component, string memberName)
        {
            if (component == null || string.IsNullOrEmpty(memberName))
                return null;

            Type type = component.GetType();

            while (type != null)
            {
                FieldInfo field = type.GetField(
                    memberName,
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance
                );

                if (field != null)
                    return field.GetValue(component) as Renderer;

                PropertyInfo property = type.GetProperty(
                    memberName,
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance
                );

                if (property != null && property.CanRead)
                {
                    try
                    {
                        return property.GetValue(component, null) as Renderer;
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

        private void CacheOriginalButtonColors(AutoPowerButtonLock button)
        {
            if (button == null || button.visualRenderers == null)
                return;

            button.materialInstances = new Material[button.visualRenderers.Length];
            button.originalColors = new Color[button.visualRenderers.Length];

            for (int i = 0; i < button.visualRenderers.Length; i++)
            {
                Renderer renderer = button.visualRenderers[i];

                if (renderer == null)
                    continue;

                Material material = Application.isPlaying
                    ? renderer.material
                    : renderer.sharedMaterial;

                button.materialInstances[i] = material;
                button.originalColors[i] = GetMaterialColor(material);
            }
        }

        private Color GetMaterialColor(Material material)
        {
            if (material == null)
                return Color.white;

            if (material.HasProperty(PropMaterialColor))
                return material.GetColor(PropMaterialColor);

            if (material.HasProperty(PropMaterialBaseColor))
                return material.GetColor(PropMaterialBaseColor);

            if (material.HasProperty(PropMaterialFaceColor))
                return material.GetColor(PropMaterialFaceColor);

            return Color.white;
        }

        private void SetMaterialColor(Material material, Color color)
        {
            if (material == null)
                return;

            if (material.HasProperty(PropMaterialColor))
                material.SetColor(PropMaterialColor, color);

            if (material.HasProperty(PropMaterialBaseColor))
                material.SetColor(PropMaterialBaseColor, color);

            if (material.HasProperty(PropMaterialFaceColor))
                material.SetColor(PropMaterialFaceColor, color);

            if (material.HasProperty(PropMaterialOutlineColor))
                material.SetColor(PropMaterialOutlineColor, color);
        }

        private void ApplyAutoButtonsPowerState()
        {
            buttonsPowerStateInitialized = true;
            lastAppliedButtonPowerState = powerOn;

            if (autoPowerButtons == null)
                return;

            for (int i = 0; i < autoPowerButtons.Count; i++)
            {
                AutoPowerButtonLock button = autoPowerButtons[i];

                if (button == null)
                    continue;

                bool shouldStayEnabled =
                    powerOn ||
                    button.keepEnabledWhenPowerOff;

                if (button.interactableUnityEventWrapper != null)
                    button.interactableUnityEventWrapper.enabled = shouldStayEnabled;

                if (button.colliders != null)
                {
                    for (int j = 0; j < button.colliders.Length; j++)
                    {
                        Collider collider = button.colliders[j];

                        if (collider != null)
                            collider.enabled = shouldStayEnabled;
                    }
                }

                ApplyAutoButtonVisualState(button);
            }
        }

        private void ApplyAutoButtonVisualState(AutoPowerButtonLock button)
        {
            if (button == null)
                return;

            if (button.visualRenderers == null)
                return;

            if (
                button.materialInstances == null ||
                button.originalColors == null ||
                button.materialInstances.Length != button.visualRenderers.Length ||
                button.originalColors.Length != button.visualRenderers.Length
            )
            {
                CacheOriginalButtonColors(button);
            }

            for (int i = 0; i < button.visualRenderers.Length; i++)
            {
                Renderer renderer = button.visualRenderers[i];

                if (renderer == null)
                    continue;

                Material material = null;

                if (button.materialInstances != null && i < button.materialInstances.Length)
                    material = button.materialInstances[i];

                if (material == null)
                {
                    material = renderer.material;

                    if (button.materialInstances != null && i < button.materialInstances.Length)
                        button.materialInstances[i] = material;
                }

                if (!powerOn && !button.keepEnabledWhenPowerOff)
                {
                    SetMaterialColor(material, powerOffButtonColor);
                }
                else
                {
                    if (powerOn && IsPlusMinusButtonName(button.buttonName))
                    {
                        bool refreshedByDebugVisual = TryRefreshInteractableDebugVisual(
                            renderer,
                            plusMinusPowerOnColor
                        );

                        if (!refreshedByDebugVisual)
                            SetMaterialColor(material, plusMinusPowerOnColor);
                    }
                    else
                    {
                        Color originalColor = Color.white;

                        if (button.originalColors != null && i < button.originalColors.Length)
                            originalColor = button.originalColors[i];

                        SetMaterialColor(material, originalColor);
                    }
                }
            }
        }

        private bool IsPlusMinusButtonName(string buttonName)
        {
            if (string.IsNullOrEmpty(buttonName))
                return false;

            string name = buttonName.ToLowerInvariant();

            name = name.Replace(" ", "");
            name = name.Replace("_", "");
            name = name.Replace("-", "");
            name = name.Replace("(", "");
            name = name.Replace(")", "");

            if (name.Contains("plus"))
                return true;

            if (name.Contains("minus"))
                return true;

            if (name.Contains("mas"))
                return true;

            if (name.Contains("más"))
                return true;

            if (name.Contains("menos"))
                return true;

            if (name.Contains("increase"))
                return true;

            if (name.Contains("decrease"))
                return true;

            if (name.Contains("aumentar"))
                return true;

            if (name.Contains("disminuir"))
                return true;

            return false;
        }

        private bool TryRefreshInteractableDebugVisual(Renderer renderer, Color normalColor)
        {
            if (renderer == null)
                return false;

            Component[] components = renderer.GetComponentsInParent<Component>(true);

            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];

                if (component == null)
                    continue;

                if (component.GetType().Name != "InteractableDebugVisual")
                    continue;

                MethodInfo method = component.GetType().GetMethod(
                    "SetNormalColor",
                    BindingFlags.Public |
                    BindingFlags.Instance
                );

                if (method == null)
                    continue;

                method.Invoke(component, new object[] { normalColor });
                return true;
            }

            components = renderer.GetComponentsInChildren<Component>(true);

            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];

                if (component == null)
                    continue;

                if (component.GetType().Name != "InteractableDebugVisual")
                    continue;

                MethodInfo method = component.GetType().GetMethod(
                    "SetNormalColor",
                    BindingFlags.Public |
                    BindingFlags.Instance
                );

                if (method == null)
                    continue;

                method.Invoke(component, new object[] { normalColor });
                return true;
            }

            return false;
        }

        private static void ApplyAutoButtonsPowerStateToAllSlicingPlanes()
        {
            SlicingPlane[] slicingPlanes = FindObjectsByType<SlicingPlane>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );

            for (int i = 0; i < slicingPlanes.Length; i++)
            {
                SlicingPlane slicingPlane = slicingPlanes[i];

                if (slicingPlane == null)
                    continue;

                if (slicingPlane.autoFindButtonsFromRoot && !slicingPlane.autoButtonsScanned)
                    slicingPlane.ScanAutoButtonsFromRoot();

                slicingPlane.ApplyAutoButtonsPowerState();
            }
        }

        public void SetZoomPanMode(bool active)
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SetZoomPanMode(active);
                return;
            }

            if (IgnoreButtonBecausePowerOff("Trackball"))
            {
                zoomPanModeActive = false;
                ApplyUltrasoundParameters();
                return;
            }

            if (active && zoom <= 1.0001f)
            {
                zoomPanModeActive = false;
                ApplyUltrasoundParameters();
                Debug.Log("Trackball ignorada: no hay zoom aplicado");
                return;
            }

            zoomPanModeActive = active;

            if (zoomPanModeActive)
            {
                if (autoSelectZoomWhenTrackballPressed)
                    selectedParameter = SelectedUltrasoundParameter.Zoom;

                zoomCenter = ClampZoomCenterToVisibleArea(zoomCenter);
            }

            ApplyUltrasoundParameters();
        }

        public bool IsZoomPanModeActive()
        {
            return zoomPanModeActive && zoom > 1.0001f;
        }

        public void SelectGain()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SelectGain();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Gain"))
                return;

            selectedParameter = SelectedUltrasoundParameter.Gain;
            Debug.Log("Parámetro seleccionado: Ganancia");
        }

        public void SelectDynamicRange()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SelectDynamicRange();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Dynamic Range"))
                return;

            selectedParameter = SelectedUltrasoundParameter.DynamicRange;
            Debug.Log("Parámetro seleccionado: Rango dinámico");
        }

        public void SelectDepth()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SelectDepth();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Depth"))
                return;

            selectedParameter = SelectedUltrasoundParameter.Depth;
            Debug.Log("Parámetro seleccionado: Profundidad");
        }

        public void SelectZoom()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SelectZoom();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Zoom"))
                return;

            selectedParameter = SelectedUltrasoundParameter.Zoom;
            Debug.Log("Parámetro seleccionado: Zoom");
        }

        public void IncreaseSelectedParameter()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.IncreaseSelectedParameter();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Aumentar parámetro"))
                return;

            if (selectedParameter == SelectedUltrasoundParameter.None)
                return;

            if (selectedParameter == SelectedUltrasoundParameter.Gain)
            {
                IncreaseGain();
            }
            else if (selectedParameter == SelectedUltrasoundParameter.DynamicRange)
            {
                IncreaseDynamicRange();
            }
            else if (selectedParameter == SelectedUltrasoundParameter.Depth)
            {
                IncreaseDepth();
            }
            else if (selectedParameter == SelectedUltrasoundParameter.Zoom)
            {
                IncreaseZoom();
            }
            else if (selectedParameter == SelectedUltrasoundParameter.Frequency)
            {
                IncreaseFrequency();
            }
        }

        public void DecreaseSelectedParameter()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.DecreaseSelectedParameter();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Disminuir parámetro"))
                return;

            if (selectedParameter == SelectedUltrasoundParameter.None)
                return;

            if (selectedParameter == SelectedUltrasoundParameter.Gain)
            {
                DecreaseGain();
            }
            else if (selectedParameter == SelectedUltrasoundParameter.DynamicRange)
            {
                DecreaseDynamicRange();
            }
            else if (selectedParameter == SelectedUltrasoundParameter.Depth)
            {
                DecreaseDepth();
            }
            else if (selectedParameter == SelectedUltrasoundParameter.Zoom)
            {
                DecreaseZoom();
            }
            else if (selectedParameter == SelectedUltrasoundParameter.Frequency)
            {
                DecreaseFrequency();
            }
        }

        public void IncreaseGain()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.IncreaseGain();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Gain +"))
                return;

            gain = Mathf.Clamp(gain + gainStep, minGain, maxGain);
            ApplyUltrasoundParameters();
            Debug.Log("Ganancia aumentada: " + gain);
        }

        public void DecreaseGain()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.DecreaseGain();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Gain -"))
                return;

            gain = Mathf.Clamp(gain - gainStep, minGain, maxGain);
            ApplyUltrasoundParameters();
            Debug.Log("Ganancia disminuida: " + gain);
        }

        public bool TryGetSelectedParameterSliderRange(
    out float minValue,
    out float maxValue,
    out float currentValue
)
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                return target.TryGetSelectedParameterSliderRange(
                    out minValue,
                    out maxValue,
                    out currentValue
                );
            }

            minValue = 0.0f;
            maxValue = 1.0f;
            currentValue = 0.0f;

            if (selectedParameter == SelectedUltrasoundParameter.Gain)
            {
                minValue = minGain;
                maxValue = maxGain;
                currentValue = gain;
                return true;
            }

            if (selectedParameter == SelectedUltrasoundParameter.DynamicRange)
            {
                minValue = minDynamicRange;
                maxValue = maxDynamicRange;
                currentValue = dynamicRange;
                return true;
            }

            if (selectedParameter == SelectedUltrasoundParameter.Depth)
            {
                minValue = minDepthVisible;
                maxValue = maxDepthVisible;
                currentValue = depthVisible;
                return true;
            }

            if (selectedParameter == SelectedUltrasoundParameter.Zoom)
            {
                minValue = minZoom;
                maxValue = maxZoom;
                currentValue = zoom;
                return true;
            }

            if (selectedParameter == SelectedUltrasoundParameter.Frequency)
            {
                minValue = minFrequencyMHz;
                maxValue = maxFrequencyMHz;
                currentValue = frequencyMHz;
                return true;
            }

            return false;
        }

        public void SetSelectedParameterFromSlider(float value)
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SetSelectedParameterFromSlider(value);
                return;
            }

            if (IgnoreButtonBecausePowerOff("Slider de parámetro seleccionado"))
                return;

            if (selectedParameter == SelectedUltrasoundParameter.Gain)
            {
                gain = Mathf.Clamp(value, minGain, maxGain);
            }
            else if (selectedParameter == SelectedUltrasoundParameter.DynamicRange)
            {
                dynamicRange = Mathf.Clamp(value, minDynamicRange, maxDynamicRange);
            }
            else if (selectedParameter == SelectedUltrasoundParameter.Depth)
            {
                depthVisible = Mathf.Clamp(value, minDepthVisible, maxDepthVisible);
            }
            else if (selectedParameter == SelectedUltrasoundParameter.Zoom)
            {
                zoom = Mathf.Clamp(value, minZoom, maxZoom);
                zoomCenter = ClampZoomCenterToVisibleArea(zoomCenter);

                if (zoom <= 1.0001f)
                    zoomPanModeActive = false;
            }
            else if (selectedParameter == SelectedUltrasoundParameter.Frequency)
            {
                frequencyMHz = Mathf.Clamp(value, minFrequencyMHz, maxFrequencyMHz);
            }
            else
            {
                return;
            }

            ApplyUltrasoundParameters();
        }

        public void SetGain(float newGain)
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SetGain(newGain);
                return;
            }

            if (IgnoreButtonBecausePowerOff("Set Gain"))
                return;

            gain = Mathf.Clamp(newGain, minGain, maxGain);
            ApplyUltrasoundParameters();
            Debug.Log("Ganancia establecida: " + gain);
        }

        public void SelectFrequency()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SelectFrequency();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Frequency"))
                return;

            selectedParameter = SelectedUltrasoundParameter.Frequency;
            Debug.Log("Parámetro seleccionado: Frecuencia");
        }

        public void IncreaseFrequency()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.IncreaseFrequency();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Frequency +"))
                return;

            frequencyMHz = Mathf.Clamp(
                frequencyMHz + frequencyStepMHz,
                minFrequencyMHz,
                maxFrequencyMHz
            );

            ApplyUltrasoundParameters();

            Debug.Log("Frecuencia aumentada: " + frequencyMHz + " MHz");
        }

        public void DecreaseFrequency()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.DecreaseFrequency();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Frequency -"))
                return;

            frequencyMHz = Mathf.Clamp(
                frequencyMHz - frequencyStepMHz,
                minFrequencyMHz,
                maxFrequencyMHz
            );

            ApplyUltrasoundParameters();

            Debug.Log("Frecuencia disminuida: " + frequencyMHz + " MHz");
        }

        public void SetFrequencyMHz(float newFrequencyMHz)
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SetFrequencyMHz(newFrequencyMHz);
                return;
            }

            if (IgnoreButtonBecausePowerOff("Set Frequency MHz"))
                return;

            frequencyMHz = Mathf.Clamp(
                newFrequencyMHz,
                minFrequencyMHz,
                maxFrequencyMHz
            );

            ApplyUltrasoundParameters();

            Debug.Log("Frecuencia establecida: " + frequencyMHz + " MHz");
        }

        public void SetTGC0(float value)
        {
            SetTGCValue(0, value);
        }

        public void SetTGC1(float value)
        {
            SetTGCValue(1, value);
        }

        public void SetTGC2(float value)
        {
            SetTGCValue(2, value);
        }

        public void SetTGC3(float value)
        {
            SetTGCValue(3, value);
        }

        public void SetTGC4(float value)
        {
            SetTGCValue(4, value);
        }

        public void SetTGC5(float value)
        {
            SetTGCValue(5, value);
        }

        public void SetTGCEnabled(bool enabled)
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SetTGCEnabled(enabled);
                return;
            }

            if (IgnoreButtonBecausePowerOff("Set TGC Enabled"))
                return;

            tgcEnabled = enabled;
            ApplyUltrasoundParameters();

            Debug.Log("TGC Enabled: " + tgcEnabled);
        }

        private void SetTGCValue(int index, float value)
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SetTGCValue(index, value);
                return;
            }

            if (IgnoreButtonBecausePowerOff("Set TGC"))
                return;

            value = Mathf.Clamp(value, minTGC, maxTGC);

            switch (index)
            {
                case 0:
                    tgc0 = value;
                    break;

                case 1:
                    tgc1 = value;
                    break;

                case 2:
                    tgc2 = value;
                    break;

                case 3:
                    tgc3 = value;
                    break;

                case 4:
                    tgc4 = value;
                    break;

                case 5:
                    tgc5 = value;
                    break;
            }

            ApplyUltrasoundParameters();

            Debug.Log("TGC" + index + ": " + value);
        }

        public void IncreaseDynamicRange()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.IncreaseDynamicRange();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Dynamic Range +"))
                return;

            dynamicRange = Mathf.Clamp(dynamicRange + dynamicRangeStep, minDynamicRange, maxDynamicRange);
            ApplyUltrasoundParameters();
            Debug.Log("Rango dinámico aumentado: " + dynamicRange + " dB");
        }

        public void DecreaseDynamicRange()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.DecreaseDynamicRange();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Dynamic Range -"))
                return;

            dynamicRange = Mathf.Clamp(dynamicRange - dynamicRangeStep, minDynamicRange, maxDynamicRange);
            ApplyUltrasoundParameters();
            Debug.Log("Rango dinámico disminuido: " + dynamicRange + " dB");
        }

        public void SetDynamicRange(float newDynamicRange)
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SetDynamicRange(newDynamicRange);
                return;
            }

            if (IgnoreButtonBecausePowerOff("Set Dynamic Range"))
                return;

            dynamicRange = Mathf.Clamp(newDynamicRange, minDynamicRange, maxDynamicRange);
            ApplyUltrasoundParameters();
            Debug.Log("Rango dinámico establecido: " + dynamicRange + " dB");
        }

        public void IncreaseDepth()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.IncreaseDepth();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Depth +"))
                return;

            depthVisible = Mathf.Clamp(depthVisible + depthStep, minDepthVisible, maxDepthVisible);
            ApplyUltrasoundParameters();
            Debug.Log("Profundidad visible aumentada: " + depthVisible);
        }

        public void DecreaseDepth()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.DecreaseDepth();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Depth -"))
                return;

            depthVisible = Mathf.Clamp(depthVisible - depthStep, minDepthVisible, maxDepthVisible);
            ApplyUltrasoundParameters();
            Debug.Log("Profundidad visible disminuida: " + depthVisible);
        }

        public void SetDepth(float newDepthVisible)
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SetDepth(newDepthVisible);
                return;
            }

            if (IgnoreButtonBecausePowerOff("Set Depth"))
                return;

            depthVisible = Mathf.Clamp(newDepthVisible, minDepthVisible, maxDepthVisible);
            ApplyUltrasoundParameters();
            Debug.Log("Profundidad visible establecida: " + depthVisible);
        }

        public void IncreaseZoom()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.IncreaseZoom();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Zoom +"))
                return;

            zoom = Mathf.Clamp(zoom + zoomStep, minZoom, maxZoom);
            zoomCenter = ClampZoomCenterToVisibleArea(zoomCenter);
            ApplyUltrasoundParameters();
            Debug.Log("Zoom aumentado: x" + zoom);
        }

        public void DecreaseZoom()
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.DecreaseZoom();
                return;
            }

            if (IgnoreButtonBecausePowerOff("Zoom -"))
                return;

            zoom = Mathf.Clamp(zoom - zoomStep, minZoom, maxZoom);
            zoomCenter = ClampZoomCenterToVisibleArea(zoomCenter);

            if (zoom <= 1.0001f)
                zoomPanModeActive = false;

            ApplyUltrasoundParameters();
            Debug.Log("Zoom disminuido: x" + zoom);
        }

        public void SetZoom(float newZoom)
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SetZoom(newZoom);
                return;
            }

            if (IgnoreButtonBecausePowerOff("Set Zoom"))
                return;

            zoom = Mathf.Clamp(newZoom, minZoom, maxZoom);
            zoomCenter = ClampZoomCenterToVisibleArea(zoomCenter);

            if (zoom <= 1.0001f)
                zoomPanModeActive = false;

            ApplyUltrasoundParameters();
            Debug.Log("Zoom establecido: x" + zoom);
        }

        public void SetZoomCenter(Vector2 newZoomCenter)
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SetZoomCenter(newZoomCenter);
                return;
            }

            if (IgnoreButtonBecausePowerOff("Set Zoom Center"))
                return;

            zoomCenter = ClampZoomCenterToVisibleArea(newZoomCenter);

            ApplyUltrasoundParameters();

            Debug.Log("Centro de zoom establecido: " + zoomCenter);
        }

        public void SetShowZoomMinimap(bool show)
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SetShowZoomMinimap(show);
                return;
            }

            if (IgnoreButtonBecausePowerOff("Set Show Zoom Minimap"))
                return;

            showZoomMinimap = show;
            ApplyUltrasoundParameters();
            Debug.Log("Mostrar minimapa de zoom: " + showZoomMinimap);
        }

        public void SetInvertDepthAxis(bool invert)
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SetInvertDepthAxis(invert);
                return;
            }

            if (IgnoreButtonBecausePowerOff("Set Invert Depth Axis"))
                return;

            invertDepthAxis = invert;
            ApplyUltrasoundParameters();
            Debug.Log("Invertir eje de profundidad: " + invertDepthAxis);
        }

        public void SetSectorApexDistanceAboveTop(float newValue)
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SetSectorApexDistanceAboveTop(newValue);
                return;
            }

            if (IgnoreButtonBecausePowerOff("Set Sector Apex Distance Above Top"))
                return;

            sectorApexDistanceAboveTop = newValue;
            ApplyUltrasoundParameters();
            Debug.Log("Sector Apex Distance Above Top: " + sectorApexDistanceAboveTop);
        }

        public void SetSectorInnerRadius(float newValue)
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SetSectorInnerRadius(newValue);
                return;
            }

            if (IgnoreButtonBecausePowerOff("Set Sector Inner Radius"))
                return;

            sectorInnerRadius = newValue;
            ApplyUltrasoundParameters();
            Debug.Log("Sector Inner Radius: " + sectorInnerRadius);
        }

        public void SetSectorAngleDegrees(float newValue)
        {
            SlicingPlane target = GetControlTarget();

            if (target != this)
            {
                target.SetSectorAngleDegrees(newValue);
                return;
            }

            if (IgnoreButtonBecausePowerOff("Set Sector Angle Degrees"))
                return;

            sectorAngleDegrees = newValue;
            ApplyUltrasoundParameters();
            Debug.Log("Sector Angle Degrees: " + sectorAngleDegrees);
        }

        private void ApplyUltrasoundParameters()
        {
            ClampParameters();

            if (runtimeMaterial != null)
                ApplyUltrasoundParametersToMaterial(runtimeMaterial);

            if (!freezeActive && ShouldWriteToScreen() && screenMaterial != null)
                ApplyUltrasoundParametersToMaterial(screenMaterial);
        }

        private void ApplyUltrasoundParametersToMaterial(Material mat)
        {
            if (mat == null)
                return;

            if (mat.HasProperty(PropPowerOn))
                mat.SetFloat(PropPowerOn, powerOn ? 1.0f : 0.0f);

            float safeDynamicRange = Mathf.Max(0.0001f, dynamicRange);
            float contrastFromDynamicRange = referenceDynamicRange / safeDynamicRange;

            if (mat.HasProperty(PropGain))
                mat.SetFloat(PropGain, gain);

            if (mat.HasProperty(PropFrequencyMHz))
                mat.SetFloat(PropFrequencyMHz, frequencyMHz);

            if (mat.HasProperty(PropTGCEnabled))
                mat.SetFloat(PropTGCEnabled, tgcEnabled ? 1.0f : 0.0f);

            if (mat.HasProperty(PropTGC0))
                mat.SetFloat(PropTGC0, tgc0);

            if (mat.HasProperty(PropTGC1))
                mat.SetFloat(PropTGC1, tgc1);

            if (mat.HasProperty(PropTGC2))
                mat.SetFloat(PropTGC2, tgc2);

            if (mat.HasProperty(PropTGC3))
                mat.SetFloat(PropTGC3, tgc3);

            if (mat.HasProperty(PropTGC4))
                mat.SetFloat(PropTGC4, tgc4);

            if (mat.HasProperty(PropTGC5))
                mat.SetFloat(PropTGC5, tgc5);

            if (mat.HasProperty(PropDynamicRange))
                mat.SetFloat(PropDynamicRange, dynamicRange);

            if (mat.HasProperty(PropReferenceDynamicRange))
                mat.SetFloat(PropReferenceDynamicRange, referenceDynamicRange);

            if (mat.HasProperty(PropContrastFromDynamicRange))
                mat.SetFloat(PropContrastFromDynamicRange, contrastFromDynamicRange);

            if (mat.HasProperty(PropDepthVisible))
                mat.SetFloat(PropDepthVisible, depthVisible);

            if (mat.HasProperty(PropInvertDepthAxis))
                mat.SetFloat(PropInvertDepthAxis, invertDepthAxis ? 1.0f : 0.0f);

            if (mat.HasProperty(PropZoom))
                mat.SetFloat(PropZoom, zoom);

            if (mat.HasProperty(PropZoomCenter))
                mat.SetVector(PropZoomCenter, new Vector4(zoomCenter.x, zoomCenter.y, 0.0f, 0.0f));

            if (mat.HasProperty(PropShowZoomMinimap))
                mat.SetFloat(PropShowZoomMinimap, showZoomMinimap ? 1.0f : 0.0f);

            if (mat.HasProperty(PropZoomMinimapRect))
                mat.SetVector(PropZoomMinimapRect, zoomMinimapRect);

            if (mat.HasProperty(PropZoomBoxThickness))
                mat.SetFloat(PropZoomBoxThickness, zoomBoxThickness);

            if (mat.HasProperty(PropSectorAngleDegrees))
                mat.SetFloat(PropSectorAngleDegrees, sectorAngleDegrees);

            if (mat.HasProperty(PropSectorApexDistanceAboveTop))
                mat.SetFloat(PropSectorApexDistanceAboveTop, sectorApexDistanceAboveTop);

            if (mat.HasProperty(PropSectorInnerRadius))
                mat.SetFloat(PropSectorInnerRadius, sectorInnerRadius);

            if (mat.HasProperty(PropSectorApexLocalZ))
                mat.SetFloat(PropSectorApexLocalZ, sectorApexLocalZ);

            if (mat.HasProperty(PropSectorLocalXScale))
                mat.SetFloat(PropSectorLocalXScale, sectorLocalXScale);

            if (mat.HasProperty(PropSectorLocalZScale))
                mat.SetFloat(PropSectorLocalZScale, sectorLocalZScale);

            if (mat.HasProperty(PropFlipSectorX))
                mat.SetFloat(PropFlipSectorX, flipSectorX ? 1.0f : 0.0f);
        }

        private static VolumeRenderedObject FindFirstVolumeRenderedObject()
        {
#if UNITY_2023_1_OR_NEWER
            return FindFirstObjectByType<VolumeRenderedObject>();
#else
            return FindObjectOfType<VolumeRenderedObject>();
#endif
        }

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

        private void InitializeIntegratedSliders()
        {
            if (!manageUISlidersFromThisSlicingPlane)
                return;

            if (autoFindPowerControlledSliders && slidersRoot != null)
                powerControlledSliders = slidersRoot.GetComponentsInChildren<Slider>(true);

            SubscribeSelectedParameterSlider();
        }

        private void SubscribeSelectedParameterSlider()
        {
            if (selectedParameterSlider == null)
                return;

            selectedParameterSlider.onValueChanged.RemoveListener(HandleSelectedParameterSliderChanged);
            selectedParameterSlider.onValueChanged.AddListener(HandleSelectedParameterSliderChanged);
        }

        private void UnsubscribeSelectedParameterSlider()
        {
            if (selectedParameterSlider == null)
                return;

            selectedParameterSlider.onValueChanged.RemoveListener(HandleSelectedParameterSliderChanged);
        }

        private void UpdateIntegratedSliders()
        {
            if (!manageUISlidersFromThisSlicingPlane)
                return;

            ApplyPowerStateToIntegratedSliders();
            SyncSelectedParameterSlider();
        }

        private void ApplyPowerStateToIntegratedSliders()
        {
            if (powerControlledSliders == null)
                return;

            for (int i = 0; i < powerControlledSliders.Length; i++)
            {
                Slider slider = powerControlledSliders[i];

                if (slider == null)
                    continue;

                slider.interactable = powerOn;
            }
        }

        private void SyncSelectedParameterSlider()
        {
            if (selectedParameterSlider == null)
                return;

            SlicingPlane target = GetControlTarget();

            if (target == null)
            {
                selectedParameterSlider.interactable = false;
                return;
            }

            float minValue;
            float maxValue;
            float currentValue;

            bool hasParameter = target.TryGetSelectedParameterSliderRange(
                out minValue,
                out maxValue,
                out currentValue
            );

            bool canUse = powerOn && hasParameter;

            if (disableSelectedSliderWhenNoParameterSelected)
                selectedParameterSlider.interactable = canUse;
            else
                selectedParameterSlider.interactable = powerOn;

            if (!hasParameter)
                return;

            suppressSelectedParameterSliderCallback = true;

            selectedParameterSlider.minValue = minValue;
            selectedParameterSlider.maxValue = maxValue;
            selectedParameterSlider.SetValueWithoutNotify(currentValue);

            suppressSelectedParameterSliderCallback = false;
        }

        private void HandleSelectedParameterSliderChanged(float value)
        {
            if (suppressSelectedParameterSliderCallback)
                return;

            if (!powerOn)
                return;

            SlicingPlane target = GetControlTarget();

            if (target == null)
                return;

            target.SetSelectedParameterFromSlider(value);
        }

        private void OnDisable()
        {
            UnsubscribeSelectedParameterSlider();
        }

        private void OnDestroy()
        {
            if (activeScreenSource == this)
                activeScreenSource = null;

            if (Application.isPlaying)
            {
                if (runtimeMaterial != null)
                    Destroy(runtimeMaterial);

                if (screenMaterial != null && screenMaterial != runtimeMaterial)
                    Destroy(screenMaterial);
            }
        }
    }
}