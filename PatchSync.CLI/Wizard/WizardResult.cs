namespace PatchSync.CLI.Wizard;

/// <summary>
/// Represents the outcome of a wizard step or wizard execution.
/// </summary>
public enum WizardOutcome
{
    /// <summary>Step completed successfully with a value.</summary>
    Success,
    /// <summary>User requested to go back to previous step.</summary>
    Back,
    /// <summary>User requested to cancel the entire wizard.</summary>
    Cancel
}

/// <summary>
/// Result of a wizard step execution.
/// </summary>
public readonly struct WizardResult<T>
{
    public WizardOutcome Outcome { get; }
    public T? Value { get; }

    private WizardResult(WizardOutcome outcome, T? value = default)
    {
        Outcome = outcome;
        Value = value;
    }

    public static WizardResult<T> Success(T value) => new(WizardOutcome.Success, value);
    public static WizardResult<T> Back => new(WizardOutcome.Back);
    public static WizardResult<T> Cancel => new(WizardOutcome.Cancel);

    public bool IsSuccess => Outcome == WizardOutcome.Success;
    public bool IsBack => Outcome == WizardOutcome.Back;
    public bool IsCancel => Outcome == WizardOutcome.Cancel;

    /// <summary>
    /// Convert to object result for step interface compatibility.
    /// </summary>
    public WizardResult<object?> ToObjectResult()
    {
        return Outcome switch
        {
            WizardOutcome.Success => WizardResult<object?>.Success(Value),
            WizardOutcome.Back => WizardResult<object?>.Back,
            _ => WizardResult<object?>.Cancel
        };
    }
}
