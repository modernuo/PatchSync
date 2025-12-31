namespace PatchSync.CLI.Wizard.Steps;

/// <summary>
/// A wizard step with custom execution logic.
/// Use this for complex steps that don't fit the standard patterns.
/// </summary>
public sealed class CustomStep : IWizardStep
{
    public string Key { get; }
    public string DisplayName { get; }

    private readonly Func<WizardContext, IWizardTheme, Task<WizardResult<object?>>> _executor;
    private readonly Func<WizardContext, bool>? _skipCondition;
    private readonly Action<WizardContext>? _onBack;

    public CustomStep(
        string key,
        string displayName,
        Func<WizardContext, IWizardTheme, Task<WizardResult<object?>>> executor,
        Func<WizardContext, bool>? skipCondition = null,
        Action<WizardContext>? onBack = null)
    {
        Key = key;
        DisplayName = displayName;
        _executor = executor;
        _skipCondition = skipCondition;
        _onBack = onBack;
    }

    /// <summary>
    /// Create a custom step with a synchronous executor.
    /// </summary>
    public CustomStep(
        string key,
        string displayName,
        Func<WizardContext, IWizardTheme, WizardResult<object?>> executor,
        Func<WizardContext, bool>? skipCondition = null,
        Action<WizardContext>? onBack = null)
        : this(key, displayName, (ctx, theme) => Task.FromResult(executor(ctx, theme)), skipCondition, onBack)
    {
    }

    public bool ShouldSkip(WizardContext context) => _skipCondition?.Invoke(context) ?? false;

    public Task<WizardResult<object?>> ExecuteAsync(WizardContext context, IWizardTheme theme)
    {
        return _executor(context, theme);
    }

    public void OnNavigateBack(WizardContext context)
    {
        _onBack?.Invoke(context);
    }
}
