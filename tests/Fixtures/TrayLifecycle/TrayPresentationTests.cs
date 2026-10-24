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

    sealed class PolicyModel : IShellCellSimpleMode, IShellCellSimpleModePolicy
    {
        internal bool Simple;
        internal bool Allowed = true;
        internal long Priority;
        public bool IsSimpleMode => Simple;
        public void SetSimpleMode(bool simple) => Simple = simple;
        public bool CanUseSimpleMode => Allowed;
        public long SimpleModePriority => Priority;
    }

    internal static void Run()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        static ShellCellHandle Cell(string key, object? model = null, Shell? owner = null) =>
            new(key, typeof(object), new(owner ?? new PlannerShell(), model, null, null, null));
        static (ShellCellHandle Cell, double Width)[] Cells(params (string Key, double Width, object? Model)[] cells) =>
            [.. cells.Select(cell => (Cell(cell.Key, cell.Model), cell.Width))];
        static (ShellCellHandle Cell, double Width)[] GroupedCells(
            params (string Key, double Width, object? Model, Shell? Owner)[] cells) =>
            [.. cells.Select(cell => (Cell(cell.Key, cell.Model, cell.Owner), cell.Width))];
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

        // Inside one Shell, policy priority beats anchor order: the oldest
        // eligible cell is simplified first. Equal priorities stay anchored.
        planner = new(32);
        var shell = new PlannerShell();
        var oldest = new PolicyModel { Priority = 10 };
        var newest = new PolicyModel { Priority = 20 };
        var tied = new PolicyModel { Priority = 20 };
        plan = planner.Measure(
            GroupedCells(("new", 60, newest, shell), ("old", 60, oldest, shell), ("tie", 60, tied, shell)), 160);
        Require(plan.Pending && Supports([.. plan.Requests], "old", true) &&
                !Supports([.. plan.Requests], "new", true) && !Supports([.. plan.Requests], "tie", true),
            "policy priority did not simplify the oldest cell first");
        oldest.Simple = true;
        plan = planner.Measure(
            GroupedCells(("new", 60, newest, shell), ("old", 30, oldest, shell), ("tie", 60, tied, shell)), 160);
        Require(!plan.Pending && !plan.Overflow, "oldest-first simple choice did not fit");

        // Shell order still wins over another Shell's older cell.
        planner = new(32);
        var firstShell = new PlannerShell();
        var secondShell = new PlannerShell();
        var firstShellCell = new PolicyModel { Priority = 20 };
        var secondShellCell = new PolicyModel { Priority = 10 };
        plan = planner.Measure(GroupedCells(
            ("first", 60, firstShellCell, firstShell), ("second", 60, secondShellCell, secondShell)), 100);
        Require(plan.Pending && Supports([.. plan.Requests], "first", true),
            "priority leaked across Shell boundaries");

        // An ineligible cell is never simplified. If it is still marked Simple
        // from an earlier epoch, the next plan asks it back to Normal.
        planner = new(32);
        var ineligibleShell = new PlannerShell();
        var ineligible = new PolicyModel { Allowed = false, Simple = true };
        plan = planner.Measure(GroupedCells(("working", 30, ineligible, ineligibleShell)), 100);
        Require(plan.Pending && Supports([.. plan.Requests], "working", false),
            "ineligible Simple cell was not restored to Normal");
        ineligible.Simple = false;
        plan = planner.Measure(GroupedCells(("working", 60, ineligible, ineligibleShell)), 30);
        Require(plan.Overflow && !Supports([.. plan.Requests], "working", true),
            "ineligible cell was simplified for overflow");

        // Overflow planning keeps an ineligible main-row cell in Normal width;
        // only the eligible cell may remain Simple beside the arrow.
        planner = new(32);
        var overflowShell = new PlannerShell();
        var eligible = new PolicyModel();
        var blocked = new PolicyModel { Allowed = false };
        plan = planner.Measure(GroupedCells(
            ("idle", 60, eligible, overflowShell), ("blocked", 60, blocked, overflowShell)), 70);
        Require(plan.Pending && Supports([.. plan.Requests], "idle", true),
            "overflow planning skipped the eligible Simple width");
        eligible.Simple = true;
        plan = planner.Measure(GroupedCells(
            ("idle", 30, eligible, overflowShell), ("blocked", 60, blocked, overflowShell)), 70);
        Require(plan.Overflow && plan.MainKeys.SequenceEqual(["idle"]) &&
                plan.OverflowCells.Select(cell => cell.Key).SequenceEqual(["blocked"]),
            "ineligible overflow cell did not stay Normal");
        Require(Math.Abs(plan.MainWidthDip - 62) < .001,
            "overflow width did not use the ineligible Normal width");

        // A stale Simple request against a cell that became ineligible is
        // dropped, and repeated drops never mark the mode unsupported.
        var layout = new TaskbarTrayLayout();
        var stale = new PolicyModel { Allowed = false };
        layout.SetCells([Cell("stale", stale)]);
        var stalePlan = new TrayPresentationPlan(
            Pending: true, Overflow: false, MainKeys: ["stale"], OverflowCells: [],
            MainWidthDip: 60, CapacityDip: 60, ArrowWidthDip: 0,
            Requests: [new("stale", true)]);
        for (int attempt = 0; attempt < 4; attempt++)
            layout.ApplyRequests(stalePlan);
        Require(!stale.Simple, "stale request simplified an ineligible cell");
        var livePlanner = (TrayPresentationPlanner)typeof(TaskbarTrayLayout)
            .GetField("_presentation", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.GetValue(layout)!;
        stale.Allowed = true;
        var recovered = livePlanner.Measure(Cells(("stale", 60, stale)), 30);
        Require(recovered.Pending && Supports([.. recovered.Requests], "stale", true),
            "dropped stale requests marked Simple mode unsupported");
    }

    sealed class PlannerShell : Shell { }
}
