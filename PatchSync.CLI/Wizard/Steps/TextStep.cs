using Spectre.Console;

namespace PatchSync.CLI.Wizard.Steps;

/// <summary>
/// A wizard step that collects text input.
/// </summary>
public sealed class TextStep : IWizardStep
{
    public string Key { get; }
    public string DisplayName { get; }

    private readonly string _prompt;
    private readonly string? _defaultValue;
    private readonly Func<WizardContext, string?>? _defaultValueFactory;
    private readonly bool _allowEmpty;
    private readonly Func<string, ValidationResult>? _validator;

    public TextStep(
        string key,
        string displayName,
        string prompt,
        string? defaultValue = null,
        bool allowEmpty = false,
        Func<string, ValidationResult>? validator = null)
    {
        Key = key;
        DisplayName = displayName;
        _prompt = prompt;
        _defaultValue = defaultValue;
        _allowEmpty = allowEmpty;
        _validator = validator;
    }

    public TextStep(
        string key,
        string displayName,
        string prompt,
        Func<WizardContext, string?> defaultValueFactory,
        bool allowEmpty = false,
        Func<string, ValidationResult>? validator = null)
    {
        Key = key;
        DisplayName = displayName;
        _prompt = prompt;
        _defaultValueFactory = defaultValueFactory;
        _allowEmpty = allowEmpty;
        _validator = validator;
    }

    public Task<WizardResult<object?>> ExecuteAsync(WizardContext context, IWizardTheme theme)
    {
        var defaultVal = _defaultValueFactory?.Invoke(context) ?? _defaultValue;

        var result = WizardPrompt.Text(
            _prompt,
            theme,
            defaultVal,
            _allowEmpty,
            allowBack: !context.IsFirstStep,
            _validator);

        return Task.FromResult(result.ToObjectResult());
    }
}
