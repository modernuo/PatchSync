using PatchSync.CLI.TUI.Core;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.TUI.Screens;

/// <summary>
/// Base interface for TUI screen implementations.
/// Screens compose panels into complete layouts.
/// </summary>
public interface IScreen
{
    /// <summary>
    /// The screen type this implementation handles.
    /// </summary>
    TuiScreen ScreenType { get; }

    /// <summary>
    /// Render the complete screen layout.
    /// </summary>
    IRenderable Render(TuiState state);
}
