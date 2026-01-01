namespace PatchSync.CLI.Wizard.Steps;

/// <summary>
/// A wizard step that presents a selection of choices.
/// </summary>
public sealed class SelectionStep : IWizardStep
{
    public string Key { get; }
    public string DisplayName { get; }

    private readonly string _prompt;
    private readonly Func<WizardContext, IEnumerable<string>> _choicesFactory;

    public SelectionStep(
        string key,
        string displayName,
        string prompt,
        IEnumerable<string> choices)
        : this(key, displayName, prompt, _ => choices) { }

    public SelectionStep(
        string key,
        string displayName,
        string prompt,
        Func<WizardContext, IEnumerable<string>> choicesFactory)
    {
        Key = key;
        DisplayName = displayName;
        _prompt = prompt;
        _choicesFactory = choicesFactory;
    }

    public Task<WizardResult<object?>> ExecuteAsync(WizardContext context, IWizardTheme theme)
    {
        var choices = _choicesFactory(context);
        var result = WizardPrompt.Selection(_prompt, choices, theme);
        return Task.FromResult(result.ToObjectResult());
    }
}
