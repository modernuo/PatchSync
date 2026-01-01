using PatchSync.CLI.TUI.Core;
using PatchSync.CLI.TUI.Panels;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.TUI.Screens;

/// <summary>
/// Main dashboard screen with file list and details panels.
/// </summary>
public sealed class DashboardScreen : IScreen
{
    private readonly FileListPanel _fileListPanel = new();
    private readonly DetailsPanel _detailsPanel = new();
    private readonly StatusBar _statusBar = new();
    private readonly ActionBar _actionBar = new();

    public TuiScreen ScreenType => TuiScreen.Dashboard;

    public IRenderable Render(TuiState state)
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("StatusBar").Size(1),
                new Layout("Main"),
                new Layout("ActionBar").Size(1));

        layout["StatusBar"].Update(_statusBar.Render(state, false));
        layout["ActionBar"].Update(_actionBar.Render(state, false));

        // Main area splits into file list (2/3) and details (1/3)
        var mainLayout = new Layout("MainContent")
            .SplitColumns(
                new Layout("FileList").Ratio(2),
                new Layout("Details").Ratio(1));

        mainLayout["FileList"].Update(
            _fileListPanel.Render(state, state.ActivePanel == TuiPanel.FileList));
        mainLayout["Details"].Update(
            _detailsPanel.Render(state, state.ActivePanel == TuiPanel.Details));

        layout["Main"].Update(mainLayout);

        return layout;
    }
}
