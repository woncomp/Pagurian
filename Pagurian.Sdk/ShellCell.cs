using Microsoft.UI.Reactor.Core;

namespace Pagurian.Sdk;

// The display/interaction unit of the tray: a Reactor component rendering one
// cell's content. Modules subclass this and override Render as usual (hooks
// are fine there). Cells have no id and receive no messages; the owning shell
// is reachable through Props.
//
// NOTE on the mechanics: Reactor embeds child components by TYPE and creates
// the instances itself, so the tray renders a cell as
// ComponentElement(ViewType, Props). Per-cell mutable state must live in the
// model object passed to Shell.AddCell (Props.Model), never in instance
// fields of the cell class.
public abstract class ShellCell : Component<ShellCellProps>
{
    protected Shell Owner => Props.Owner;

    protected object? Model => Props.Model;

    protected T ModelAs<T>() where T : notnull => (T)Props.Model!;

    protected IThemeService Theme => Props.Owner.Theme;

    protected Logger Log => Props.Owner.Log;
}

// Everything the host needs to manage a cell without owning its instance:
// the owning shell, the module's model, and the behavior delegates captured
// at AddCell time (they read live state, so they stay current).
public sealed record ShellCellProps(
    Shell Owner,
    object? Model,
    Func<string?>? GetTooltip,
    Func<Billboard?>? CreateBillboard,
    Action? OnClicked);
