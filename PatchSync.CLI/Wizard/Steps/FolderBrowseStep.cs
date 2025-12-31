namespace PatchSync.CLI.Wizard.Steps;

/// <summary>
/// A wizard step that browses for a folder.
/// </summary>
public sealed class FolderBrowseStep : IWizardStep
{
    public string Key { get; }
    public string DisplayName { get; }

    private readonly string _prompt;
    private readonly string? _startPath;
    private readonly Func<WizardContext, string?>? _startPathFactory;
    private readonly bool _allowNew;

    public FolderBrowseStep(
        string key,
        string displayName,
        string prompt,
        string? startPath = null,
        bool allowNew = false)
    {
        Key = key;
        DisplayName = displayName;
        _prompt = prompt;
        _startPath = startPath;
        _allowNew = allowNew;
    }

    public FolderBrowseStep(
        string key,
        string displayName,
        string prompt,
        Func<WizardContext, string?> startPathFactory,
        bool allowNew = false)
    {
        Key = key;
        DisplayName = displayName;
        _prompt = prompt;
        _startPathFactory = startPathFactory;
        _allowNew = allowNew;
    }

    public Task<WizardResult<object?>> ExecuteAsync(WizardContext context, IWizardTheme theme)
    {
        var startPath = _startPathFactory?.Invoke(context) ?? _startPath;

        var result = WizardPrompt.BrowseFolder(
            _prompt,
            theme,
            startPath,
            _allowNew,
            allowBack: !context.IsFirstStep);

        return Task.FromResult(result.ToObjectResult());
    }
}
