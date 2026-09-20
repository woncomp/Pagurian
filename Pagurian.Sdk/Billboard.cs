using Microsoft.UI.Reactor.Core;

namespace Pagurian.Sdk;

// A details panel popped up by clicking a shell cell. The module defines the
// content, the design size, and the open/close behavior; the host owns the
// window chrome (borderless, rounded, acrylic, NoActivate, always-on-top),
// the placement (above the owner cell, below when the taskbar is at the top
// edge), outside-click dismissal, one-at-a-time mutual exclusion, and closing
// the billboard when its owner cell is removed.
//
// A fresh billboard instance is created on every show via the cell's
// CreateBillboard delegate, so per-open subscriptions can simply follow the
// instance lifetime.
public abstract class Billboard : Component
{
    // The cell this billboard belongs to, bound by the host when shown.
    internal ShellCellHandle OwnerCell { get; set; } = null!;

    public Shell Shell => OwnerCell.Props.Owner;

    protected IThemeService Theme => Shell.Theme;

    protected Logger Log => Shell.Log;

    public abstract double WidthDip { get; }

    // Initial/fallback height used until the host completes the first content
    // layout before reveal. The shown billboard adopts the natural content height,
    // capped to the owner cell's monitor work area, and keeps that height for
    // the rest of this open instance.
    public abstract double HeightDip { get; }

    // Window title (mostly invisible with the borderless style; used for
    // accessibility and debugging).
    public virtual string Title => "Pagurian";

    // Called on the UI thread after the prepared window is shown / closed.
    // Initial XAML rendering may run in a DWM-cloaked window before reveal.
    // Dismissal hides the intact window before teardown. An opening cancelled
    // before reveal receives neither hook; Render effects still unmount normally.
    // Self-report hooks: e.g. the metrics billboards switch their tracker to
    // fast sampling in OnOpened and back in OnClosed.
    public virtual void OnOpened() { }

    public virtual void OnClosed() { }
}
