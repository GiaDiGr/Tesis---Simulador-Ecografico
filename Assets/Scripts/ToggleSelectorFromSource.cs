using System;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;

[DisallowMultipleComponent]
public class ToggleControllerSelector : MonoBehaviour, ISelector
{
    public enum ButtonRequirement
    {
        Any,
        All
    }

    [Header("CONTROLADOR")]

    [Tooltip("Arrastra aquí el Controller Ref del mismo lado: Left o Right.")]
    [SerializeField] private ControllerRef controller;

    [Header("BOTÓN")]

    [Tooltip("Debe coincidir con el botón usado por el Controller Selector original.")]
    [SerializeField] private ControllerButtonUsage controllerButtonUsage = ControllerButtonUsage.GripButton;

    [Tooltip("Debe coincidir con Require Button Usages del Controller Selector original.")]
    [SerializeField] private ButtonRequirement requireButtonUsages = ButtonRequirement.Any;

    [Header("OPCIONES")]

    [SerializeField] private bool resetOnDisable = true;
    [SerializeField] private bool debugLogs = false;

    private bool isSelected;
    private bool wasButtonPressed;

    public event Action WhenSelected = delegate { };
    public event Action WhenUnselected = delegate { };

    private void OnEnable()
    {
        wasButtonPressed = IsButtonPressed();
    }

    private void OnDisable()
    {
        wasButtonPressed = false;

        if (resetOnDisable && isSelected)
        {
            SetSelected(false);
        }
    }

    private void Update()
    {
        bool buttonPressed = IsButtonPressed();

        if (buttonPressed && !wasButtonPressed)
        {
            SetSelected(!isSelected);
        }

        wasButtonPressed = buttonPressed;
    }

    private bool IsButtonPressed()
    {
        if (controller == null) return false;
        if (!controller.Active) return false;
        if (!controller.IsConnected) return false;

        switch (requireButtonUsages)
        {
            case ButtonRequirement.Any:
                return controller.IsButtonUsageAnyActive(controllerButtonUsage);

            case ButtonRequirement.All:
                return controller.IsButtonUsageAllActive(controllerButtonUsage);

            default:
                return controller.IsButtonUsageAnyActive(controllerButtonUsage);
        }
    }

    private void SetSelected(bool selected)
    {
        if (isSelected == selected) return;

        isSelected = selected;

        if (isSelected)
        {
            if (debugLogs)
            {
                Debug.Log($"{name}: SELECT toggle.", this);
            }

            WhenSelected.Invoke();
        }
        else
        {
            if (debugLogs)
            {
                Debug.Log($"{name}: UNSELECT toggle.", this);
            }

            WhenUnselected.Invoke();
        }
    }

    [ContextMenu("Force Select")]
    public void ForceSelect()
    {
        SetSelected(true);
    }

    [ContextMenu("Force Unselect")]
    public void ForceUnselect()
    {
        SetSelected(false);
    }

    private void OnValidate()
    {
        if (controller == null)
        {
            return;
        }

        if (controllerButtonUsage == 0)
        {
            Debug.LogWarning(
                $"{name}: Controller Button Usage está vacío. Usa GripButton si quieres el botón de agarre.",
                this
            );
        }
    }
}