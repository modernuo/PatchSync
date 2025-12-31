namespace PatchSync.CLI.Wizard.Steps;

/// <summary>
/// A wizard step that only executes if a condition is met.
/// Wraps another step and evaluates the condition at runtime.
/// </summary>
public sealed class ConditionalStep : IWizardStep
{
    public string Key => _innerStep.Key;
    public string DisplayName => _innerStep.DisplayName;

    private readonly IWizardStep _innerStep;
    private readonly Func<WizardContext, bool> _condition;

    public ConditionalStep(
        IWizardStep innerStep,
        Func<WizardContext, bool> condition)
    {
        _innerStep = innerStep;
        _condition = condition;
    }

    /// <summary>
    /// Create a conditional step that runs when a boolean context value is true.
    /// </summary>
    public static ConditionalStep WhenTrue(string contextKey, IWizardStep innerStep)
    {
        return new ConditionalStep(innerStep, ctx =>
            ctx.TryGet<bool>(contextKey, out var val) && val);
    }

    /// <summary>
    /// Create a conditional step that runs when a boolean context value is false.
    /// </summary>
    public static ConditionalStep WhenFalse(string contextKey, IWizardStep innerStep)
    {
        return new ConditionalStep(innerStep, ctx =>
            !ctx.TryGet<bool>(contextKey, out var val) || !val);
    }

    public bool ShouldSkip(WizardContext context) => !_condition(context);

    public Task<WizardResult<object?>> ExecuteAsync(WizardContext context, IWizardTheme theme)
    {
        return _innerStep.ExecuteAsync(context, theme);
    }

    public void OnNavigateBack(WizardContext context)
    {
        _innerStep.OnNavigateBack(context);
    }
}
