using UnityEngine;
using Oculus.Interaction;
using UnityVolumeRendering;

public class UltrasoundMachineButtonsDebugVisual : MonoBehaviour
{
    private enum HoldGreenMode
    {
        None,

        // Estados independientes
        Freeze,
        ZoomPan,

        // Power
        Power,

        // Grupo exclusivo de parámetros
        Gain,
        DynamicRange,
        Depth,
        Zoom,
        Frequency
    }

    [Header("Referencias automáticas")]
    [SerializeField] private bool autoFindReferences = true;

    [Tooltip("Normalmente el Renderer está en este mismo objeto.")]
    [SerializeField] private Renderer targetRenderer;

    [Tooltip("Normalmente el Interactable View está en el padre del padre.")]
    [SerializeField, Interface(typeof(IInteractableView))]
    private UnityEngine.Object interactableViewObject;

    [Header("Colores")]
    [SerializeField] private Color normalColor = Color.red;
    [SerializeField] private Color hoverColor = Color.blue;
    [SerializeField] private Color selectColor = Color.green;
    [SerializeField] private Color disabledColor = Color.black;

    [Header("Detección automática del botón")]
    [SerializeField] private bool autoDetectButtonTypeFromName = true;
    [SerializeField] private bool logDetectedButtonType = false;

    private HoldGreenMode holdGreenMode = HoldGreenMode.None;
    private bool turnBlackWhenPowerOff = true;

    private IInteractableView interactableView;
    private Material materialInstance;
    private bool subscribed;
    private bool initialized;

    private static readonly int PropColor = Shader.PropertyToID("_Color");
    private static readonly int PropBaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int PropFaceColor = Shader.PropertyToID("_FaceColor");
    private static readonly int PropOutlineColor = Shader.PropertyToID("_OutlineColor");

    private void Awake()
    {
        AutoFindReferencesIfNeeded();

        if (autoDetectButtonTypeFromName)
            DetectButtonTypeFromName();
    }

    private void Start()
    {
        InitializeIfNeeded();
        Subscribe();
        UpdateVisual();
    }

    private void OnEnable()
    {
        AutoFindReferencesIfNeeded();

        if (autoDetectButtonTypeFromName)
            DetectButtonTypeFromName();

        InitializeIfNeeded();
        Subscribe();
        UpdateVisual();
    }

    private void OnValidate()
    {
        if (!autoFindReferences)
            return;

        AutoFindReferencesIfNeeded();

        if (autoDetectButtonTypeFromName)
            DetectButtonTypeFromName();
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    private void OnDestroy()
    {
        Unsubscribe();

        if (materialInstance != null)
            Destroy(materialInstance);
    }

    private void Update()
    {
        UpdateVisual();
    }

    private void AutoFindReferencesIfNeeded()
    {
        if (!autoFindReferences)
            return;

        if (targetRenderer == null)
            targetRenderer = GetComponent<Renderer>();

        if (interactableViewObject == null)
        {
            Transform candidate = transform;

            if (candidate.parent != null)
                candidate = candidate.parent;

            if (candidate.parent != null)
                candidate = candidate.parent;

            IInteractableView view = candidate.GetComponent<IInteractableView>();

            if (view != null)
                interactableViewObject = view as UnityEngine.Object;
        }

        interactableView = interactableViewObject as IInteractableView;
    }

    private void InitializeIfNeeded()
    {
        if (initialized)
            return;

        AutoFindReferencesIfNeeded();

        if (interactableView == null)
            interactableView = interactableViewObject as IInteractableView;

        if (interactableView == null)
        {
            Debug.LogError(
                "UltrasoundMachineButtonsDebugVisual: falta Interactable View. " +
                "Se esperaba encontrarlo en el padre del padre.",
                this
            );

            enabled = false;
            return;
        }

        if (targetRenderer == null)
        {
            Debug.LogError(
                "UltrasoundMachineButtonsDebugVisual: falta Target Renderer. " +
                "Se esperaba encontrarlo en este mismo objeto.",
                this
            );

            enabled = false;
            return;
        }

        materialInstance = targetRenderer.material;
        initialized = true;
    }

    private void DetectButtonTypeFromName()
    {
        string fullName = GetHierarchyNameNormalized();

        holdGreenMode = HoldGreenMode.None;
        turnBlackWhenPowerOff = true;

        if (fullName.Contains("power"))
        {
            holdGreenMode = HoldGreenMode.Power;
            turnBlackWhenPowerOff = false;
        }
        else if (fullName.Contains("freeze"))
        {
            holdGreenMode = HoldGreenMode.Freeze;
            turnBlackWhenPowerOff = true;
        }
        else if (fullName.Contains("trackball"))
        {
            holdGreenMode = HoldGreenMode.ZoomPan;
            turnBlackWhenPowerOff = true;
        }
        else if (fullName.Contains("gain"))
        {
            holdGreenMode = HoldGreenMode.Gain;
            turnBlackWhenPowerOff = true;
        }
        else if (fullName.Contains("frequency") || fullName.Contains("frecuencia") || fullName.Contains("freq"))
        {
            holdGreenMode = HoldGreenMode.Frequency;
            turnBlackWhenPowerOff = true;
        }
        else if (fullName.Contains("dr"))
        {
            holdGreenMode = HoldGreenMode.DynamicRange;
            turnBlackWhenPowerOff = true;
        }
        else if (fullName.Contains("depth"))
        {
            holdGreenMode = HoldGreenMode.Depth;
            turnBlackWhenPowerOff = true;
        }
        else if (fullName.Contains("zoom"))
        {
            holdGreenMode = HoldGreenMode.Zoom;
            turnBlackWhenPowerOff = true;
        }
        else if (fullName.Contains("plus") || fullName.Contains("minus"))
        {
            holdGreenMode = HoldGreenMode.None;
            turnBlackWhenPowerOff = true;
        }

        if (logDetectedButtonType)
        {
            Debug.Log(
                "UltrasoundMachineButtonsDebugVisual detectó: " +
                gameObject.name +
                " -> " +
                holdGreenMode +
                ", turnBlackWhenPowerOff = " +
                turnBlackWhenPowerOff,
                this
            );
        }
    }

    private string GetHierarchyNameNormalized()
    {
        string result = "";
        Transform current = transform;

        while (current != null)
        {
            result += current.name;
            current = current.parent;
        }

        result = result.ToLowerInvariant();
        result = result.Replace(" ", "");
        result = result.Replace("_", "");
        result = result.Replace("-", "");
        result = result.Replace("(", "");
        result = result.Replace(")", "");

        return result;
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        if (interactableView == null)
            return;

        interactableView.WhenStateChanged += HandleStateChanged;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        if (interactableView != null)
            interactableView.WhenStateChanged -= HandleStateChanged;

        subscribed = false;
    }

    private void HandleStateChanged(InteractableStateChangeArgs args)
    {
        UpdateVisual();
    }

    private bool ShouldHoldGreen()
    {
        if (holdGreenMode == HoldGreenMode.Freeze)
            return SlicingPlane.IsFreezeActive;

        if (holdGreenMode == HoldGreenMode.ZoomPan)
            return SlicingPlane.IsAnyZoomPanModeActive();

        if (holdGreenMode == HoldGreenMode.Power)
            return SlicingPlane.IsPowerOn;

        if (holdGreenMode == HoldGreenMode.Gain)
            return SlicingPlane.IsGainSelected;

        if (holdGreenMode == HoldGreenMode.DynamicRange)
            return SlicingPlane.IsDynamicRangeSelected;

        if (holdGreenMode == HoldGreenMode.Depth)
            return SlicingPlane.IsDepthSelected;

        if (holdGreenMode == HoldGreenMode.Zoom)
            return SlicingPlane.IsZoomSelected;

        if (holdGreenMode == HoldGreenMode.Frequency)
            return SlicingPlane.IsFrequencySelected;

        return false;
    }

    private void UpdateVisual()
    {
        if (materialInstance == null)
            return;

        if (interactableView == null)
            return;

        if (!SlicingPlane.IsPowerOn && turnBlackWhenPowerOff)
        {
            SetMaterialColor(disabledColor);
            return;
        }

        switch (interactableView.State)
        {
            case InteractableState.Hover:
                SetMaterialColor(hoverColor);
                return;

            case InteractableState.Select:
                SetMaterialColor(selectColor);
                return;

            case InteractableState.Disabled:
                SetMaterialColor(disabledColor);
                return;
        }

        if (ShouldHoldGreen())
        {
            SetMaterialColor(selectColor);
            return;
        }

        SetMaterialColor(normalColor);
    }

    private void SetMaterialColor(Color color)
    {
        if (materialInstance == null)
            return;

        if (materialInstance.HasProperty(PropColor))
            materialInstance.SetColor(PropColor, color);

        if (materialInstance.HasProperty(PropBaseColor))
            materialInstance.SetColor(PropBaseColor, color);

        if (materialInstance.HasProperty(PropFaceColor))
            materialInstance.SetColor(PropFaceColor, color);

        if (materialInstance.HasProperty(PropOutlineColor))
            materialInstance.SetColor(PropOutlineColor, color);

        materialInstance.color = color;
    }
}