using Spectre.Console;

namespace PatchSync.CLI.Wizard;

/// <summary>
/// Runs a wizard with step-by-step navigation support.
/// </summary>
public sealed class WizardRunner
{
    private readonly List<IWizardStep> _steps = new();
    private readonly string _title;
    private readonly IWizardTheme _theme;
    private readonly WizardContext _context = new();

    public WizardRunner(string title, IWizardTheme? theme = null)
    {
        _title = title;
        _theme = theme ?? WizardTheme.Default;
    }

    /// <summary>Add a step to the wizard.</summary>
    public WizardRunner AddStep(IWizardStep step)
    {
        _steps.Add(step);
        return this;
    }

    /// <summary>Add multiple steps to the wizard.</summary>
    public WizardRunner AddSteps(params IWizardStep[] steps)
    {
        _steps.AddRange(steps);
        return this;
    }

    /// <summary>Get the context (for accessing values after completion).</summary>
    public WizardContext Context => _context;

    /// <summary>Get the theme being used.</summary>
    public IWizardTheme Theme => _theme;

    /// <summary>
    /// Run the wizard, returning whether it completed successfully.
    /// </summary>
    public async Task<bool> RunAsync()
    {
        if (_steps.Count == 0)
            return true;

        _context.TotalSteps = CountActiveSteps();
        _context.CurrentStep = 0;

        while (_context.CurrentStep < _steps.Count)
        {
            var step = _steps[_context.CurrentStep];

            // Check if step should be skipped
            if (step.ShouldSkip(_context))
            {
                _context.CurrentStep++;
                continue;
            }

            // Clear and render header with breadcrumb
            _theme.ClearFrame();
            _theme.RenderHeader(_title, GetVisibleStepIndex() + 1, _context.TotalSteps, step.DisplayName, GetBreadcrumb());
            _theme.RenderNavigationHint(_context.CurrentStep == 0);

            // Execute step - Ctrl+C throws OperationCanceledException
            WizardResult<object?> result;
            try
            {
                result = await step.ExecuteAsync(_context, _theme);
            }
            catch (OperationCanceledException)
            {
                // Treat Ctrl+C as "back" navigation
                result = WizardResult<object?>.Back;
            }

            switch (result.Outcome)
            {
                case WizardOutcome.Success:
                    // Store value and advance
                    if (result.Value != null)
                        _context.Set(step.Key, result.Value);
                    _context.CurrentStep++;
                    break;

                case WizardOutcome.Back:
                    if (_context.CurrentStep > 0)
                    {
                        // Notify current step we're leaving
                        step.OnNavigateBack(_context);

                        // Find previous non-skipped step
                        var prevIndex = FindPreviousActiveStep();
                        if (prevIndex >= 0)
                        {
                            // Remove the value from previous step so it can be re-entered
                            var prevStep = _steps[prevIndex];
                            _context.Remove(prevStep.Key);
                            _context.CurrentStep = prevIndex;
                        }
                    }
                    else
                    {
                        // On first step, Ctrl+C cancels the wizard
                        _theme.ShowCancelled();
                        return false;
                    }
                    break;

                case WizardOutcome.Cancel:
                    _theme.ShowCancelled();
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Build breadcrumb trail from completed steps.
    /// </summary>
    private string GetBreadcrumb()
    {
        var breadcrumbSteps = new List<string>();
        for (int i = 0; i <= _context.CurrentStep && i < _steps.Count; i++)
        {
            if (!_steps[i].ShouldSkip(_context))
                breadcrumbSteps.Add(_steps[i].DisplayName);
        }
        return string.Join(" > ", breadcrumbSteps);
    }

    /// <summary>
    /// Count steps that are not skipped.
    /// </summary>
    private int CountActiveSteps()
    {
        // For now, count all steps.
        // In practice, we'd need to evaluate ShouldSkip dynamically
        return _steps.Count;
    }

    /// <summary>
    /// Get the visible step index (excluding skipped steps).
    /// </summary>
    private int GetVisibleStepIndex()
    {
        var visible = 0;
        for (int i = 0; i < _context.CurrentStep; i++)
        {
            if (!_steps[i].ShouldSkip(_context))
                visible++;
        }
        return visible;
    }

    /// <summary>
    /// Find the previous step that wasn't skipped.
    /// </summary>
    private int FindPreviousActiveStep()
    {
        for (int i = _context.CurrentStep - 1; i >= 0; i--)
        {
            if (!_steps[i].ShouldSkip(_context))
                return i;
        }
        return -1;
    }
}
