namespace PatchSync.CLI.Wizard.Steps;

/// <summary>
/// A wizard step that allows selecting multiple items.
/// </summary>
public sealed class MultiSelectStep : IWizardStep
{
    public string Key { get; }
    public string DisplayName { get; }

    private readonly string _prompt;
    private readonly IEnumerable<string> _choices;
    private readonly IEnumerable<string>? _preselected;
    private readonly bool _required;

    public MultiSelectStep(
        string key,
        string displayName,
        string prompt,
        IEnumerable<string> choices,
        IEnumerable<string>? preselected = null,
        bool required = true)
    {
        Key = key;
        DisplayName = displayName;
        _prompt = prompt;
        _choices = choices;
        _preselected = preselected;
        _required = required;
    }

    public Task<WizardResult<object?>> ExecuteAsync(WizardContext context, IWizardTheme theme)
    {
        var result = WizardPrompt.MultiSelect(
            _prompt,
            _choices,
            theme,
            _preselected,
            allowBack: !context.IsFirstStep,
            required: _required);

        return Task.FromResult(result.ToObjectResult());
    }
}
