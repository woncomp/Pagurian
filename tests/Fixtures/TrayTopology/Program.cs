using Pagurian;
using Pagurian.Sdk;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using DisplayInfo = Pagurian.DisplayInfo;

// Binding-engine and config-migration fixture: no windows, no real displays.
// DisplayInfo records are fabricated, history is a dictionary, and the config
// lives in an isolated temp folder (see HostStubs). The callback runs on the
// UI thread so ThemeService (brushes) is legal; everything is synchronous
// because ApplyConfig/SetTopology publish inline.
ReactorApp.Run(_ =>
{
    var checks = 0;
    void Require(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    try
    {
        var kind = typeof(ProbeShell).FullName!;
        ModuleLoader.Catalog[kind] = new ShellAttribute { ShellType = typeof(ProbeShell) };
        TrayConfig.Entry E(string id) => new(kind, id, null);
        TrayConfig.TrayGroup G(string monitor, params TrayConfig.Entry[] entries) =>
            new(new TrayId(monitor, TrayEdge.Left), entries);
        DisplayInfo D(string key, int w, int h, bool primary = false, bool taskbar = true, int x = 0)
        {
            var rect = new TaskbarInterop.RECT { Left = x, Top = 0, Right = x + w, Bottom = h };
            return new DisplayInfo(key, key, $@"\\.\{key}", (nint)1, rect, rect, primary, taskbar ? (nint)2 : (nint)0);
        }
        SurfaceKey Left(string display) => new(display, TrayEdge.Left);
        SurfaceKey Right(string display) => new(display, TrayEdge.Right);

        // --- config v1 migration ---
        var configPath = Path.Combine(HostSettings.TestDir, "config.json");
        Directory.CreateDirectory(HostSettings.TestDir);
        File.WriteAllText(configPath,
            "{ \"tray\": [ { \"shell\": \"" + kind + "\", \"id\": \"1001\" }," +
            " { \"shell\": \"Pagurian.Modules.Hello.HelloShell\", \"id\": \"2002\" } ] }");
        var migrated = TrayConfig.Load();
        Require(migrated.Count == 1 && migrated[0].Id == TrayId.PrimaryLeft,
            "v1 config did not map to a single primary tray");
        Require(migrated[0].Entries.Count == 2, "v1 config lost entries");
        Require(migrated[0].Entries[1].ShellType == "Pagurian.Modules.Hello.WorldClockShell",
            "Hello clock was not remapped");

        // --- config v2 roundtrip: edge persisted, left omitted, empty dropped ---
        TrayConfig.Save([
            G(TrayId.PrimaryMonitorKey, E("1001")),
            new TrayConfig.TrayGroup(new TrayId("MON-B", TrayEdge.Right), [E("2002")]),
            G("MON-EMPTY"),
        ]);
        var saved = File.ReadAllText(configPath);
        Require(!saved.Contains("\"edge\": \"left\""), "default edge was written");
        Require(saved.Contains("\"edge\": \"right\""), "right edge was not persisted");
        Require(!saved.Contains("MON-EMPTY"), "empty tray was persisted");
        var reloaded = TrayConfig.Load();
        Require(reloaded.Count == 2, "v2 roundtrip changed group count");
        Require(reloaded[1].Id == new TrayId("MON-B", TrayEdge.Right), "right edge did not round-trip");

        // --- duplicate ids across trays: first wins ---
        File.WriteAllText(configPath,
            "{ \"trays\": [ { \"monitor\": \"primary\", \"shells\": [ { \"shell\": \"" + kind + "\", \"id\": \"1001\" } ] }," +
            " { \"monitor\": \"MON-B\", \"shells\": [ { \"shell\": \"" + kind + "\", \"id\": \"1001\" } ] } ] }");
        var duplicated = TrayConfig.Load();
        Require(duplicated.Count == 2 && duplicated[1].Entries.Count == 0,
            "duplicate id across trays was not dropped");

        // --- edge parsing: unknown degrades to left ---
        Require(TrayId.ParseEdge(null, "test") == TrayEdge.Left, "missing edge did not default to left");
        Require(TrayId.ParseEdge("right", "test") == TrayEdge.Right, "right edge did not parse");
        Require(TrayId.ParseEdge("top", "test") == TrayEdge.Left, "unknown edge did not degrade to left");

        // --- binding: exact identity + primary alias ---
        var history = new Dictionary<string, RecordedMonitor>();
        TrayManager.MonitorHistory = key => history.TryGetValue(key, out var r) ? r : null;

        var primary = D("MON-P", 1920, 1200, primary: true);
        var monB = D("MON-B", 2560, 1440, x: 1920);
        TrayManager.ApplyConfig([
            G(TrayId.PrimaryMonitorKey, E("1001")),
            G("MON-B", E("2002")),
        ]);
        TrayManager.SetTopology([primary, monB]);
        Require(TrayManager.BindingOf(TrayId.PrimaryLeft) == Left("MON-P"),
            "primary alias did not bind to the primary display");
        Require(TrayManager.BindingOf(new TrayId("MON-B", TrayEdge.Left)) == Left("MON-B"),
            "identity-keyed tray did not bind to its display");
        Require(TrayManager.CellsForSurface(Left("MON-P")).Count == 1, "primary surface cell count wrong");
        Require(TrayManager.CellsForSurface(Left("MON-B")).Count == 1, "secondary surface cell count wrong");

        // --- fallback: absent display, no history, no empty display → primary ---
        TrayManager.SetTopology([primary]);
        Require(TrayManager.BindingOf(new TrayId("MON-B", TrayEdge.Left)) == Left("MON-P"),
            "absent display did not fall back to primary");
        Require(TrayManager.CellsForSurface(Left("MON-P")).Count == 2,
            "fallback did not append cells after the surface's own");

        // --- fallback step 1: aspect ratio beats the primary ---
        history["MON-B"] = new RecordedMonitor("MON-B", "B", 2560, 1440, DateTimeOffset.Now);
        var monC = D("MON-C", 2560, 1440, x: -2560);
        TrayManager.SetTopology([primary, monC]);
        Require(TrayManager.BindingOf(new TrayId("MON-B", TrayEdge.Left)) == Left("MON-C"),
            "aspect-ratio step did not pick the matching display");

        // --- fallback step 2: without history, the unoccupied display wins ---
        history.Remove("MON-B");
        TrayManager.SetTopology([primary, monC]);
        Require(TrayManager.BindingOf(new TrayId("MON-B", TrayEdge.Left)) == Left("MON-C"),
            "unoccupied display was not chosen");

        // --- fallback step 3: no empty display → primary ---
        TrayManager.ApplyConfig([
            G(TrayId.PrimaryMonitorKey, E("1001")),
            G("MON-B", E("2002")),
            G("MON-C", E("3003")),
        ]);
        TrayManager.SetTopology([primary, monC]);
        Require(TrayManager.BindingOf(new TrayId("MON-B", TrayEdge.Left)) == Left("MON-P"),
            "all-occupied fallback did not pick the primary");
        Require(TrayManager.CellsForSurface(Left("MON-P")).Count == 2, "primary fallback composition wrong");
        Require(TrayManager.CellsForSurface(Left("MON-C")).Count == 1, "occupied display lost its own cells");

        // --- reconnection restores the configured display ---
        TrayManager.SetTopology([primary, monB, monC]);
        Require(TrayManager.BindingOf(new TrayId("MON-B", TrayEdge.Left)) == Left("MON-B"),
            "reconnected display did not reclaim its tray");

        // --- right-edge trays bind to the display's right surface ---
        TrayManager.ApplyConfig([
            G(TrayId.PrimaryMonitorKey, E("1001")),
            new TrayConfig.TrayGroup(new TrayId("MON-B", TrayEdge.Right), [E("2002")]),
        ]);
        TrayManager.SetTopology([primary, monB]);
        Require(TrayManager.BindingOf(new TrayId("MON-B", TrayEdge.Right)) == Right("MON-B"),
            "right-edge tray did not bind to the right surface");
        Require(TrayManager.CellsForSurface(Right("MON-B")).Count == 1,
            "right-edge cells did not compose on the right surface");
        Require(TrayManager.CellsForSurface(Left("MON-B")).Count == 0,
            "left surface picked up right-edge cells");

        // --- fallback preserves the configured edge ---
        history.Remove("MON-B");
        TrayManager.SetTopology([primary, monC]);
        Require(TrayManager.BindingOf(new TrayId("MON-B", TrayEdge.Right)) == Right("MON-C"),
            "fallback did not keep the right edge");

        // --- right trays fold to the left surface on vertical taskbars ---
        TrayManager.ApplyConfig([
            G(TrayId.PrimaryMonitorKey, E("1001")),
            new TrayConfig.TrayGroup(new TrayId("MON-V", TrayEdge.Right), [E("2002")]),
        ]);
        var monV = D("MON-V", 1080, 1920, x: 4480);
        TrayManager.SetTopology([primary, monV]);
        Require(TrayManager.BindingOf(new TrayId("MON-V", TrayEdge.Right)) == Left("MON-V"),
            "right tray did not fold to the left surface on a vertical taskbar");

        // --- post routing is global across trays ---
        var target = (ProbeShell)TrayManager.Shells.First(s => s.InstanceId == "2002");
        TrayManager.RouteMessage("2002", new ShellMessage("ping", [], null, DateTimeOffset.Now));
        Require(target.Messages.Contains("ping"), "post did not reach a shell on a secondary tray");
        TrayManager.RouteMessage("9999", new ShellMessage("ping", [], null, DateTimeOffset.Now));

        // --- moving a shell between trays restarts it, survivors keep running ---
        var startedBefore = ProbeShell.Started;
        var stoppedBefore = ProbeShell.Stopped;
        TrayManager.ApplyConfig([
            new TrayConfig.TrayGroup(TrayId.PrimaryLeft, [E("1001"), E("2002")]),
        ]);
        Require(ProbeShell.Stopped == stoppedBefore + 1 && ProbeShell.Started == startedBefore + 1,
            "cross-tray move did not restart exactly the moved shell");
        Require(TrayManager.Trays.Count == 1, "removed tray survived");
        Require(TrayManager.Shells.Count == 2, "shell count wrong after move");

        TrayManager.ShutdownAll();
        Console.WriteLine($"Tray topology passed: {checks} assertions.");
        Environment.Exit(0);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        Environment.Exit(1);
    }
});

public sealed class ProbeShell : Shell
{
    internal static int Started, Stopped;
    internal readonly List<string> Messages = new();
    public override void Startup() { Started++; AddCell<ProbeCell>(); }
    public override void Shutdown() { Stopped++; }
    public override void OnMessage(ShellMessage message) => Messages.Add(message.Command);
}

public sealed class ProbeCell : ShellCell
{
    public override Element Render() => Border(null);
}
