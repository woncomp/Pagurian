using Pagurian;
using Pagurian.Sdk;

static class TrayPresentationTests
{
    sealed class SimpleModel : IShellCellSimpleMode
    {
        internal bool Simple;
        public bool IsSimpleMode => Simple;
        public void SetSimpleMode(bool simple) => Simple = simple;
    }

    internal static void Run()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        static ShellCellHandle Cell(string key, object? model = null) =>
            new(key, typeof(object), new(new PlannerShell(), model, null, null, null));
        static (ShellCellHandle Cell, double Width)[] Cells(params (string Key, double Width, object? Model)[] cells) =>
            [.. cells.Select(cell => (Cell(cell.Key, cell.Model), cell.Width))];
        static bool Supports(TrayModeRequest[] requests, string key, bool simple) =>
            requests.Any(request => request.Key == key && request.Simple == simple);

        // Normal content fits: no mode requests and no affordance.
        var planner = new TrayPresentationPlanner(32);
        var plan = planner.Measure(Cells(("a", 40, null), ("b", 50, null)), 100);
        Require(!plan.Pending && !plan.Overflow, "fitting content requested a mode change");
        Require(plan.MainKeys.SequenceEqual(["a", "b"]), "fitting main order changed");

        // Simple width is unknown on the first pass: request, then settle.
        planner = new(32);
        var simple = new SimpleModel();
        plan = planner.Measure(Cells(("a", 60, simple), ("b", 50, null)), 100);
        Require(plan.Pending && Supports([.. plan.Requests], "a", true), "unknown simple width was guessed");
        simple.Simple = true;
        plan = planner.Measure(Cells(("a", 30, simple), ("b", 50, null)), 100);
        Require(!plan.Pending && !plan.Overflow, "one simple cell did not resolve capacity");
        Require(plan.MainKeys.SequenceEqual(["a", "b"]), "simple-fit main order changed");

        // Once all Simple widths are known but content still overflows, the
        // arrow consumes capacity and the first non-fitting cell stops the run.
        planner = new(32);
        var first = new SimpleModel();
        var second = new SimpleModel();
        var third = new SimpleModel();
        plan = planner.Measure(Cells(("a", 50, first), ("b", 50, second), ("c", 50, third)), 100);
        Require(plan.Pending, "overflow planning skipped unknown simple widths");
        first.Simple = second.Simple = third.Simple = true;
        plan = planner.Measure(Cells(("a", 30, first), ("b", 30, second), ("c", 30, third)), 80);
        Require(plan.Overflow && plan.HasArrow, "all-simple overflow was not detected");
        Require(plan.MainKeys.SequenceEqual(["a"]), "overflow fill did not stop at the first non-fit");
        Require(plan.OverflowCells.Select(cell => cell.Key).SequenceEqual(["b", "c"]),
            "overflow order changed");
        Require(Math.Abs(plan.MainWidthDip - 62) < .001, "arrow width was not budgeted");
        Require(plan.Pending && Supports([.. plan.Requests], "b", false) &&
            Supports([.. plan.Requests], "c", false), "overflow cells did not restore Normal");
        second.Simple = third.Simple = false;
        plan = planner.Measure(Cells(("a", 30, first), ("b", 50, second), ("c", 50, third)), 80);
        Require(!plan.Pending && plan.Overflow && plan.MainKeys.SequenceEqual(["a"]),
            "overflow Normal restore did not settle");
        Require(plan.OverflowCells.Select(cell => cell.Key).SequenceEqual(["b", "c"]),
            "Normal overflow order changed");

        // A later narrower cell cannot jump ahead of the first non-fit.
        planner = new(32);
        plan = planner.Measure(Cells(("a", 30, null), ("b", 50, null), ("c", 20, null)), 90);
        Require(plan.Overflow && plan.MainKeys.SequenceEqual(["a"]), "overflow selected a later narrow cell");
        Require(plan.OverflowCells.Select(cell => cell.Key).SequenceEqual(["b", "c"]),
            "later cells did not all overflow");

        // If even the affordance cannot fit, the surface stays empty but the
        // overflow set is retained.
        planner = new(32);
        plan = planner.Measure(Cells(("a", 40, null)), 30);
        Require(plan.Overflow && plan.MainKeys.IsEmpty && !plan.HasArrow, "unaffordable arrow remained visible");

        // Simple choices stay in anchor order; unsupported cells remain Normal.
        planner = new(32);
        var onlySecond = new SimpleModel();
        plan = planner.Measure(Cells(("a", 60, null), ("b", 60, onlySecond)), 100);
        Require(plan.Pending && Supports([.. plan.Requests], "b", true), "unsupported cell was simplified");
        onlySecond.Simple = true;
        plan = planner.Measure(Cells(("a", 60, null), ("b", 30, onlySecond)), 100);
        Require(!plan.Pending && !plan.Overflow, "supported simple choice did not fit");
    }

    sealed class PlannerShell : Shell { }
}
