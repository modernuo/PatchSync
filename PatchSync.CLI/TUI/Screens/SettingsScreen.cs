using PatchSync.CLI.TUI.Core;
using PatchSync.CLI.TUI.Panels;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.TUI.Screens;

/// <summary>
/// Settings screen for workspace and CDN configuration.
/// </summary>
public sealed class SettingsScreen : IScreen
{
    private readonly StatusBar _statusBar = new();
    private readonly ActionBar _actionBar = new();

    public TuiScreen ScreenType => TuiScreen.Settings;

    public IRenderable Render(TuiState state)
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("StatusBar").Size(1),
                new Layout("Main"),
                new Layout("ActionBar").Size(1));

        layout["StatusBar"].Update(_statusBar.Render(state, false));
        layout["ActionBar"].Update(_actionBar.Render(state, false));

        var content = RenderSettingsContent(state);

        var panel = new Panel(content)
        {
            Header = new PanelHeader(" Settings "),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1)
        };

        layout["Main"].Update(panel);

        return layout;
    }

    private IRenderable RenderSettingsContent(TuiState state)
    {
        var rows = new List<IRenderable>();

        // Workspace section
        rows.Add(new Rule("[aqua]Workspace[/]").LeftJustified());
        rows.Add(Text.Empty);

        var workspaceTable = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("Label").Width(14))
            .AddColumn(new TableColumn("Value"));

        workspaceTable.AddRow("[dim]Path[/]", Markup.Escape(state.Workspace.Path));
        workspaceTable.AddRow("[dim]Project[/]", Markup.Escape(state.Workspace.ProjectName));
        workspaceTable.AddRow("[dim]ID[/]", Markup.Escape(state.Workspace.ProjectId));
        workspaceTable.AddRow("[dim]Channels[/]", $"{state.Workspace.Channels.Count}");
        workspaceTable.AddRow("[dim]Versions[/]", $"{state.Workspace.Versions.Count}");

        rows.Add(workspaceTable);
        rows.Add(Text.Empty);

        // CDN Configuration section
        rows.Add(new Rule("[aqua]CDN Configuration[/]").LeftJustified());
        rows.Add(Text.Empty);

        if (state.Workspace.HasPublishProfiles)
        {
            rows.Add(new Markup("[green]✓[/] Publish profiles configured"));
            rows.Add(Text.Empty);

            // TODO: Show actual profile details when available
            rows.Add(new Markup("[dim]Press [/][yellow]c[/][dim] to modify CDN settings[/]"));
        }
        else
        {
            rows.Add(new Markup("[yellow]⚠[/] No publish profiles configured"));
            rows.Add(Text.Empty);
            rows.Add(new Markup("Publishing is disabled until CDN settings are configured."));
            rows.Add(Text.Empty);
            rows.Add(new Markup("[dim]Press [/][yellow]c[/][dim] to run the CDN setup wizard[/]"));
        }

        rows.Add(Text.Empty);

        // Quick Actions section
        rows.Add(new Rule("[aqua]Quick Actions[/]").LeftJustified());
        rows.Add(Text.Empty);

        var actionsTable = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("Key")
            .AddColumn("Action")
            .AddColumn("Description");

        actionsTable.AddRow("[yellow]c[/]", "CDN Setup", "Configure S3/R2 credentials and bucket");
        actionsTable.AddRow("[yellow]1[/]", "Dashboard", "Return to main file view");
        actionsTable.AddRow("[yellow]2[/]", "Versions", "Manage versions and channels");
        actionsTable.AddRow("[yellow]q[/]", "Quit", "Exit the application");

        rows.Add(actionsTable);

        return new Rows(rows);
    }
}
