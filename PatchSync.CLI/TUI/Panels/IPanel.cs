using PatchSync.CLI.TUI.Core;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.TUI.Panels;

/// <summary>
/// Base interface for TUI panel components.
/// Panels are reusable UI components that render based on state.
/// </summary>
public interface IPanel
{
    /// <summary>
    /// Render the panel based on current state.
    /// </summary>
    IRenderable Render(TuiState state, bool isActive);
}
