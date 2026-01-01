using PatchSync.CLI.TUI.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PatchSync.CLI.TUI.Integration;

/// <summary>
/// Renders dialog and input overlays on top of screen content.
/// </summary>
public sealed class DialogOverlay
{
    private static readonly Style ErrorStyle = new(Color.Red);
    private static readonly Style WarningStyle = new(Color.Yellow);
    private static readonly Style InfoStyle = new(Color.Aqua);
    private static readonly Style ConfirmStyle = new(Color.Aqua);

    /// <summary>
    /// Wrap content with dialog overlay if a dialog is active.
    /// </summary>
    public static IRenderable ApplyOverlay(IRenderable content, TuiState state)
    {
        if (state.Dialog != null)
        {
            return RenderWithDialog(content, state.Dialog);
        }

        if (state.Input != null)
        {
            return RenderWithInput(content, state.Input);
        }

        return content;
    }

    private static IRenderable RenderWithDialog(IRenderable background, DialogState dialog)
    {
        var dialogStyle = dialog.Type switch
        {
            DialogType.Error => ErrorStyle,
            DialogType.Warning => WarningStyle,
            DialogType.Confirm => ConfirmStyle,
            DialogType.Selection => InfoStyle,
            _ => Style.Plain
        };

        var content = new List<IRenderable>
        {
            new Markup(Markup.Escape(dialog.Message))
        };

        // Add type-specific content
        switch (dialog.Type)
        {
            case DialogType.Confirm:
                content.Add(Text.Empty);
                content.Add(new Markup("[dim]Press [/][yellow]y[/][dim] to confirm or [/][yellow]n[/][dim] to cancel[/]"));
                break;

            case DialogType.Selection when dialog.Choices != null:
                content.Add(Text.Empty);
                for (int i = 0; i < dialog.Choices.Count; i++)
                {
                    var choice = dialog.Choices[i];
                    var isSelected = i == dialog.SelectedChoice;
                    content.Add(new Markup(isSelected
                        ? $"[aqua]> {Markup.Escape(choice)}[/]"
                        : $"  {Markup.Escape(choice)}"));
                }
                content.Add(Text.Empty);
                content.Add(new Markup("[dim]Press [/][yellow]Enter[/][dim] to select[/]"));
                break;

            case DialogType.Info:
            case DialogType.Error:
            case DialogType.Warning:
                content.Add(Text.Empty);
                content.Add(new Markup("[dim]Press [/][yellow]Enter[/][dim] or [/][yellow]Esc[/][dim] to close[/]"));
                break;
        }

        var dialogPanel = new Panel(new Rows(content))
        {
            Header = new PanelHeader($" {dialog.Title} "),
            Border = BoxBorder.Double,
            BorderStyle = dialogStyle,
            Padding = new Padding(2, 1)
        };

        // Calculate dialog size
        var dialogHeight = content.Count + 4; // Account for border and padding

        return new Layout("Overlay")
            .SplitRows(
                new Layout("Background").Ratio(1).Update(
                    new Panel(background) { Border = BoxBorder.None }),
                new Layout("Dialog").Size(dialogHeight).Update(
                    new Padder(dialogPanel, new Padding(10, 0))),
                new Layout("Bottom").Size(2));
    }

    private static IRenderable RenderWithInput(IRenderable background, InputState input)
    {
        var cursor = "[blink]_[/]";
        var inputText = Markup.Escape(input.Text);

        var inputPanel = new Panel(
            new Markup($"{inputText}{cursor}"))
        {
            Header = new PanelHeader($" {input.Prompt} "),
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.Aqua),
            Padding = new Padding(1, 0)
        };

        return new Layout("Overlay")
            .SplitRows(
                new Layout("Background").Ratio(1).Update(background),
                new Layout("Input").Size(3).Update(
                    new Padder(inputPanel, new Padding(10, 0))),
                new Layout("Bottom").Size(1));
    }

    /// <summary>
    /// Create a centered modal dialog.
    /// </summary>
    public static IRenderable CreateModal(string title, IRenderable content, Style? borderStyle = null)
    {
        return new Panel(content)
        {
            Header = new PanelHeader($" {title} "),
            Border = BoxBorder.Double,
            BorderStyle = borderStyle ?? new Style(Color.Aqua),
            Padding = new Padding(2, 1)
        };
    }

    /// <summary>
    /// Create an error modal.
    /// </summary>
    public static IRenderable CreateErrorModal(string title, string message)
    {
        var content = new Rows(
            new Markup($"[red]{Markup.Escape(message)}[/]"),
            Text.Empty,
            new Markup("[dim]Press any key to continue[/]"));

        return CreateModal(title, content, ErrorStyle);
    }

    /// <summary>
    /// Create a confirmation modal.
    /// </summary>
    public static IRenderable CreateConfirmModal(string title, string message)
    {
        var content = new Rows(
            new Markup(Markup.Escape(message)),
            Text.Empty,
            new Markup("[dim]Press [/][yellow]y[/][dim] to confirm or [/][yellow]n[/][dim] to cancel[/]"));

        return CreateModal(title, content, ConfirmStyle);
    }

    /// <summary>
    /// Create a selection modal.
    /// </summary>
    public static IRenderable CreateSelectionModal(string title, IReadOnlyList<string> choices, int selectedIndex)
    {
        var rows = new List<IRenderable>();

        for (int i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            var isSelected = i == selectedIndex;
            rows.Add(new Markup(isSelected
                ? $"[aqua]> {Markup.Escape(choice)}[/]"
                : $"  {Markup.Escape(choice)}"));
        }

        rows.Add(Text.Empty);
        rows.Add(new Markup("[dim]Press [/][yellow]Enter[/][dim] to select, [/][yellow]Esc[/][dim] to cancel[/]"));

        return CreateModal(title, new Rows(rows), InfoStyle);
    }
}
