namespace PatchSync.CLI.Wizard.Steps;

/// <summary>
/// A wizard step that asks a yes/no question.
/// </summary>
public sealed class ConfirmStep : IWizardStep
{
    public string Key { get; }
    public string DisplayName { get; }

    private readonly string _question;
    private readonly bool _defaultValue;

    public ConfirmStep(
        string key,
        string displayName,
        string question,
        bool defaultValue = true)
    {
        Key = key;
        DisplayName = displayName;
        _question = question;
        _defaultValue = defaultValue;
    }

    public Task<WizardResult<object?>> ExecuteAsync(WizardContext context, IWizardTheme theme)
    {
        var result = WizardPrompt.Confirm(_question, theme, _defaultValue);
        return Task.FromResult(result.ToObjectResult());
    }
}
