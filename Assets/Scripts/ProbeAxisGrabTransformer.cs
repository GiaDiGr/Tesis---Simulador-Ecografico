using Oculus.Interaction;
using UnityEngine;

[DisallowMultipleComponent]
public class ProbeAnchorAxisGrabTransformer : MonoBehaviour, ITransformer
{
    public enum ControllerAxis
    {
        Up,
        Down,
        Forward,
        Back,
        Right,
        Left
    }

    public enum ProbeLocalAxis
    {
        LocalX,
        LocalMinusX,
        LocalY,
        LocalMinusY,
        LocalZ,
        LocalMinusZ
    }

    [Header("PUNTO FIJO DE AGARRE")]

    [SerializeField] private Transform grabAnchor;
    [SerializeField] private string autoAnchorName = "[BuildingBlock] HandGrabInstallationRoutine";
    [SerializeField] private bool autoFindAnchor = true;

    [Header("ORIENTACIÓN DEL TRANSDUCTOR")]

    [Tooltip("Eje de la pose de agarre/control hacia el cual se orienta el eje principal del transductor.")]
    [SerializeField] private ControllerAxis controllerAxisToFollow = ControllerAxis.Forward;

    [Tooltip("Eje local del transductor que representa la dirección principal.")]
    [SerializeField] private ProbeLocalAxis probePointingAxis = ProbeLocalAxis.LocalZ;

    [Tooltip("Eje de la pose de agarre/control usado para estabilizar el giro sobre el eje principal.")]
    [SerializeField] private ControllerAxis controllerSecondaryAxis = ControllerAxis.Up;

    [Tooltip("Eje local secundario del transductor.")]
    [SerializeField] private ProbeLocalAxis probeSecondaryAxis = ProbeLocalAxis.LocalY;

    [Header("OPCIONES")]

    [Tooltip("Si está activo, el Grab Anchor queda exactamente sobre el punto de agarre.")]
    [SerializeField] private bool snapAnchorToGrabPoint = true;

    [Tooltip("Distancia máxima permitida entre el origen del transductor y el anchor. Si el anchor está más lejos, se ignora para evitar saltos enormes.")]
    [SerializeField] private float maxAllowedAnchorDistance = 2.0f;

    [SerializeField] private bool resetRigidbodyVelocity = true;

    [Header("Pantalla ecográfica")]

    [Tooltip("Opcional. Arrastra aquí el SlicingPlane del transductor para que la pantalla use este transductor al agarrarlo.")]
    [SerializeField] private MonoBehaviour slicingPlaneToNotify;

    private IGrabbable grabbable;
    private Rigidbody rb;
    private Transform target;

    private Vector3 fallbackLocalPositionOffset;
    private bool hasFallbackOffset;

    private Vector3 anchorLocalPositionAtGrabStart;
    private bool hasAnchorLocalPositionAtGrabStart;

    public void Initialize(IGrabbable grabbable)
    {
        this.grabbable = grabbable;

        if (grabbable != null && grabbable.Transform != null)
        {
            target = grabbable.Transform;
            rb = target.GetComponent<Rigidbody>();
        }

        if (target == null)
        {
            target = transform;
        }

        if (rb == null)
        {
            rb = GetComponent<Rigidbody>();
        }

        FindAnchorIfNeeded();
    }

    private void Awake()
    {
        target = transform;
        rb = GetComponent<Rigidbody>();

        FindAnchorIfNeeded();
    }

    public void BeginTransform()
    {
        if (!TryGetGrabPose(out Pose grabPose)) return;

        target = grabbable != null && grabbable.Transform != null
            ? grabbable.Transform
            : transform;

        Quaternion desiredRotation = GetDesiredProbeRotation(grabPose.rotation);

        if (grabAnchor != null && target != null && grabAnchor.IsChildOf(target))
        {
            anchorLocalPositionAtGrabStart = target.InverseTransformPoint(grabAnchor.position);
            hasAnchorLocalPositionAtGrabStart = true;
        }
        else
        {
            hasAnchorLocalPositionAtGrabStart = false;
        }

        fallbackLocalPositionOffset =
            Quaternion.Inverse(desiredRotation) * (target.position - grabPose.position);

        hasFallbackOffset = true;

        ApplyProbeTransform(grabPose.position, grabPose.rotation);
    }

    public void UpdateTransform()
    {
        if (!TryGetGrabPose(out Pose grabPose)) return;

        ApplyProbeTransform(grabPose.position, grabPose.rotation);
    }

    public void EndTransform()
    {
        hasFallbackOffset = false;
        hasAnchorLocalPositionAtGrabStart = false;
    }

    private bool TryGetGrabPose(out Pose grabPose)
    {
        grabPose = default;

        if (grabbable == null) return false;
        if (grabbable.GrabPoints == null) return false;
        if (grabbable.GrabPoints.Count == 0) return false;

        grabPose = grabbable.GrabPoints[0];
        return true;
    }

    private void ApplyProbeTransform(Vector3 grabPosition, Quaternion grabRotation)
    {
        if (target == null)
        {
            target = grabbable != null && grabbable.Transform != null
                ? grabbable.Transform
                : transform;
        }

        Quaternion desiredRotation = GetDesiredProbeRotation(grabRotation);

        if (snapAnchorToGrabPoint && IsValidGrabAnchor() && hasAnchorLocalPositionAtGrabStart)
        {
            ApplyTransformUsingAnchor(grabPosition, desiredRotation);
        }
        else if (hasFallbackOffset)
        {
            Vector3 desiredPosition =
                grabPosition + desiredRotation * fallbackLocalPositionOffset;

            target.SetPositionAndRotation(desiredPosition, desiredRotation);
        }
        else
        {
            target.SetPositionAndRotation(grabPosition, desiredRotation);
        }

        if (resetRigidbodyVelocity)
        {
            ResetRigidbodyVelocity();
        }
    }

    private void ApplyTransformUsingAnchor(Vector3 grabPosition, Quaternion desiredRotation)
    {
        target.rotation = desiredRotation;

        Vector3 currentAnchorPosition = grabAnchor.position;
        Vector3 correction = grabPosition - currentAnchorPosition;

        target.position += correction;

        Vector3 finalCorrection = grabPosition - grabAnchor.position;
        target.position += finalCorrection;
    }

    private Quaternion GetDesiredProbeRotation(Quaternion grabRotation)
    {
        Vector3 desiredPrimaryDirection =
            GetControllerAxis(grabRotation, controllerAxisToFollow);

        Vector3 desiredSecondaryDirection =
            GetControllerAxis(grabRotation, controllerSecondaryAxis);

        Vector3 localPrimaryAxis =
            GetProbeLocalAxis(probePointingAxis);

        Vector3 localSecondaryAxis =
            GetProbeLocalAxis(probeSecondaryAxis);

        return MakeRotation(
            localPrimaryAxis,
            desiredPrimaryDirection,
            localSecondaryAxis,
            desiredSecondaryDirection
        );
    }

    private bool IsValidGrabAnchor()
    {
        if (grabAnchor == null) return false;
        if (target == null) return false;

        if (!grabAnchor.IsChildOf(target))
        {
            Debug.LogWarning(
                $"{name}: Grab Anchor no es hijo del objeto agarrable. Se ignorará para evitar saltos.",
                this
            );

            return false;
        }

        float localDistance =
            target.InverseTransformPoint(grabAnchor.position).magnitude;

        if (localDistance > maxAllowedAnchorDistance)
        {
            Debug.LogWarning(
                $"{name}: Grab Anchor está demasiado lejos del origen del transductor. " +
                $"Distancia local = {localDistance:F3}. Se ignorará para evitar teletransporte. " +
                "Si realmente ese anchor es correcto, aumenta Max Allowed Anchor Distance.",
                this
            );

            return false;
        }

        return true;
    }

    private Vector3 GetControllerAxis(Quaternion rotation, ControllerAxis axis)
    {
        switch (axis)
        {
            case ControllerAxis.Up:
                return rotation * Vector3.up;

            case ControllerAxis.Down:
                return rotation * Vector3.down;

            case ControllerAxis.Forward:
                return rotation * Vector3.forward;

            case ControllerAxis.Back:
                return rotation * Vector3.back;

            case ControllerAxis.Right:
                return rotation * Vector3.right;

            case ControllerAxis.Left:
                return rotation * Vector3.left;

            default:
                return rotation * Vector3.forward;
        }
    }

    private Vector3 GetProbeLocalAxis(ProbeLocalAxis axis)
    {
        switch (axis)
        {
            case ProbeLocalAxis.LocalX:
                return Vector3.right;

            case ProbeLocalAxis.LocalMinusX:
                return Vector3.left;

            case ProbeLocalAxis.LocalY:
                return Vector3.up;

            case ProbeLocalAxis.LocalMinusY:
                return Vector3.down;

            case ProbeLocalAxis.LocalZ:
                return Vector3.forward;

            case ProbeLocalAxis.LocalMinusZ:
                return Vector3.back;

            default:
                return Vector3.forward;
        }
    }

    private Quaternion MakeRotation(
        Vector3 localPrimaryAxis,
        Vector3 worldPrimaryAxis,
        Vector3 localSecondaryAxis,
        Vector3 worldSecondaryReference
    )
    {
        Quaternion localBasis =
            BuildBasis(localPrimaryAxis, localSecondaryAxis);

        Quaternion worldBasis =
            BuildBasis(worldPrimaryAxis, worldSecondaryReference);

        return worldBasis * Quaternion.Inverse(localBasis);
    }

    private Quaternion BuildBasis(Vector3 primaryAxis, Vector3 secondaryAxis)
    {
        Vector3 forward = primaryAxis.normalized;

        Vector3 up = Vector3.ProjectOnPlane(secondaryAxis, forward);

        if (up.sqrMagnitude < 0.0001f)
        {
            up = Vector3.ProjectOnPlane(Vector3.up, forward);
        }

        if (up.sqrMagnitude < 0.0001f)
        {
            up = Vector3.ProjectOnPlane(Vector3.right, forward);
        }

        up.Normalize();

        return Quaternion.LookRotation(forward, up);
    }

    private void ResetRigidbodyVelocity()
    {
        if (rb == null) return;

#if UNITY_6000_0_OR_NEWER
        rb.linearVelocity = Vector3.zero;
#else
        rb.velocity = Vector3.zero;
#endif

        rb.angularVelocity = Vector3.zero;
    }

    private void FindAnchorIfNeeded()
    {
        if (!autoFindAnchor) return;
        if (grabAnchor != null) return;

        Transform searchRoot = target != null ? target : transform;

        grabAnchor = FindChildByName(searchRoot, autoAnchorName);

        if (grabAnchor == null)
        {
            Debug.LogWarning(
                $"{name}: no se encontró el hijo llamado {autoAnchorName}. " +
                "Asígnalo manualmente en Grab Anchor.",
                this
            );
        }
    }

    private Transform FindChildByName(Transform root, string childName)
    {
        if (root == null) return null;

        Transform[] children =
            root.GetComponentsInChildren<Transform>(true);

        foreach (Transform child in children)
        {
            if (child.name == childName)
            {
                return child;
            }
        }

        return null;
    }

    private void OnValidate()
    {
        if (probePointingAxis == probeSecondaryAxis)
        {
            Debug.LogWarning(
                $"{name}: el eje principal y el eje secundario del transductor no deberían ser iguales.",
                this
            );
        }
    }
}