namespace PatchSync.CLI.Wizard;

/// <summary>
/// Represents a single step in a wizard.
/// </summary>
public interface IWizardStep
{
    /// <summary>Unique key for storing this step's value in the context.</summary>
    string Key { get; }

    /// <summary>Display name shown in step indicator.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Execute the step and collect user input.
    /// </summary>
    /// <param name="context">Wizard context with previous values.</param>
    /// <param name="theme">Theme for rendering.</param>
    /// <returns>Result indicating success, back, or cancel.</returns>
    Task<WizardResult<object?>> ExecuteAsync(WizardContext context, IWizardTheme theme);

    /// <summary>
    /// Called when user navigates back FROM this step.
    /// Override to clean up any side effects.
    /// </summary>
    void OnNavigateBack(WizardContext context) { }

    /// <summary>
    /// Whether this step should be skipped based on context.
    /// Override to implement conditional steps.
    /// </summary>
    bool ShouldSkip(WizardContext context) => false;
}
