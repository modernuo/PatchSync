using PatchSync.CLI.TUI.Core;
using Spectre.Console;

namespace PatchSync.CLI.TUI.Integration;

/// <summary>
/// Handles wizard overlay execution within the TUI.
/// Pauses TUI rendering, runs wizard, then resumes.
/// </summary>
public sealed class WizardOverlay
{
    private readonly TuiApplication _app;

    public WizardOverlay(TuiApplication app)
    {
        _app = app;
    }

    /// <summary>
    /// Run a wizard with proper TUI coordination.
    /// The TUI is paused while the wizard runs.
    /// </summary>
    public async Task<T?> RunWizardAsync<T>(
        Func<CancellationToken, Task<T?>> wizardAction,
        CancellationToken cancellationToken = default)
        where T : class
    {
        try
        {
            // Clear screen for wizard
            AnsiConsole.Clear();

            // Run the wizard
            var result = await wizardAction(cancellationToken);

            // Clear screen before resuming TUI
            AnsiConsole.Clear();

            // Notify TUI that wizard is complete
            if (result != null)
            {
                _app.PostMessage(new WizardCompleted());
            }
            else
            {
                _app.PostMessage(new WizardCancelled());
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.Clear();
            _app.PostMessage(new WizardCancelled());
            return null;
        }
        catch (Exception ex)
        {
            AnsiConsole.Clear();
            _app.PostMessage(new ShowErrorDialog("Wizard Error", ex.Message));
            return null;
        }
    }

    /// <summary>
    /// Run a void wizard action.
    /// </summary>
    public async Task<bool> RunWizardAsync(
        Func<CancellationToken, Task<bool>> wizardAction,
        CancellationToken cancellationToken = default)
    {
        try
        {
            AnsiConsole.Clear();
            var success = await wizardAction(cancellationToken);
            AnsiConsole.Clear();

            if (success)
            {
                _app.PostMessage(new WizardCompleted());
            }
            else
            {
                _app.PostMessage(new WizardCancelled());
            }

            return success;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.Clear();
            _app.PostMessage(new WizardCancelled());
            return false;
        }
        catch (Exception ex)
        {
            AnsiConsole.Clear();
            _app.PostMessage(new ShowErrorDialog("Wizard Error", ex.Message));
            return false;
        }
    }

    /// <summary>
    /// Display a simple prompt and return the result.
    /// </summary>
    public async Task<string?> PromptAsync(
        string prompt,
        string? defaultValue = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var textPrompt = new TextPrompt<string>(prompt);

            if (defaultValue != null)
            {
                textPrompt.DefaultValue(defaultValue);
            }

            textPrompt.AllowEmpty();

            return await Task.Run(() =>
            {
                try
                {
                    return AnsiConsole.Prompt(textPrompt);
                }
                catch
                {
                    return null;
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Display a confirmation prompt.
    /// </summary>
    public async Task<bool> ConfirmAsync(
        string prompt,
        bool defaultValue = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    return AnsiConsole.Confirm(prompt, defaultValue);
                }
                catch
                {
                    return false;
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Display a selection prompt.
    /// </summary>
    public async Task<T?> SelectAsync<T>(
        string prompt,
        IEnumerable<T> choices,
        Func<T, string>? displaySelector = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    var selection = new SelectionPrompt<T>()
                        .Title(prompt)
                        .PageSize(10)
                        .AddChoices(choices);

                    if (displaySelector != null)
                    {
                        selection.UseConverter(displaySelector);
                    }

                    return AnsiConsole.Prompt(selection);
                }
                catch
                {
                    return null;
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
