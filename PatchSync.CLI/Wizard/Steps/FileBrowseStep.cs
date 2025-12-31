namespace PatchSync.CLI.Wizard.Steps;

/// <summary>
/// A wizard step that browses for a file.
/// </summary>
public sealed class FileBrowseStep : IWizardStep
{
    public string Key { get; }
    public string DisplayName { get; }

    private readonly string _prompt;
    private readonly string? _startPath;
    private readonly string? _pattern;

    public FileBrowseStep(
        string key,
        string displayName,
        string prompt,
        string? startPath = null,
        string? pattern = null)
    {
        Key = key;
        DisplayName = displayName;
        _prompt = prompt;
        _startPath = startPath;
        _pattern = pattern;
    }

    public Task<WizardResult<object?>> ExecuteAsync(WizardContext context, IWizardTheme theme)
    {
        var result = WizardPrompt.BrowseFile(
            _prompt,
            theme,
            _startPath,
            _pattern,
            allowBack: !context.IsFirstStep);

        return Task.FromResult(result.ToObjectResult());
    }
}
