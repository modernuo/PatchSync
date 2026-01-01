using PatchSync.CLI.TUI.Core;
using PatchSync.CLI.TUI.Panels;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.TUI.Screens;

/// <summary>
/// Version management screen with channels and versions panels.
/// </summary>
public sealed class VersionsScreen : IScreen
{
    private readonly ChannelsPanel _channelsPanel = new();
    private readonly VersionsPanel _versionsPanel = new();
    private readonly StatusBar _statusBar = new();
    private readonly ActionBar _actionBar = new();

    public TuiScreen ScreenType => TuiScreen.Versions;

    public IRenderable Render(TuiState state)
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("StatusBar").Size(1),
                new Layout("Main"),
                new Layout("ActionBar").Size(1));

        layout["StatusBar"].Update(_statusBar.Render(state, false));
        layout["ActionBar"].Update(_actionBar.Render(state, false));

        // Main area splits into channels (1/3) and versions (2/3)
        var mainLayout = new Layout("MainContent")
            .SplitColumns(
                new Layout("Channels").Ratio(1),
                new Layout("Versions").Ratio(2));

        mainLayout["Channels"].Update(
            _channelsPanel.Render(state, state.ActivePanel == TuiPanel.Channels));
        mainLayout["Versions"].Update(
            _versionsPanel.Render(state, state.ActivePanel == TuiPanel.Versions));

        layout["Main"].Update(mainLayout);

        return layout;
    }
}
