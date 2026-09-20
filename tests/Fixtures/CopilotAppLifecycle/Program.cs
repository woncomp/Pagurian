using Microsoft.Data.Sqlite;
using Pagurian.Modules.Copilot;

var root = Path.Combine(Environment.CurrentDirectory, "artifacts", $"app-lifecycle-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
var epoch = DateTimeOffset.UtcNow.AddMinutes(-5);
try
{
    var database = Path.Combine(root, "data.db");
    using var writer = new SqliteConnection($"Data Source={database};Pooling=False");
    writer.Open();
    void Sql(string text)
    {
        using var command = writer.CreateCommand();
        command.CommandText = text;
        command.ExecuteNonQuery();
    }
    Sql("""
        PRAGMA journal_mode=WAL;
        PRAGMA wal_autocheckpoint=0;
        CREATE TABLE sessions(id TEXT PRIMARY KEY, session_type TEXT, archived_at TEXT, is_running INTEGER);
        CREATE TABLE workspaces(id TEXT PRIMARY KEY, session_id TEXT, archived_at TEXT);
        CREATE TABLE workspace_session_aliases(session_id TEXT, workspace_id TEXT);
        INSERT INTO sessions VALUES ('direct','workspace',NULL,0),('alias','workspace',NULL,0),
          ('chat','chat',NULL,0),('unmapped','workspace',NULL,0),('ambiguous','workspace',NULL,0),
          ('dangling','chat',NULL,0),('malformed','chat','not-a-date',0);
        INSERT INTO workspaces VALUES ('w1','direct',NULL),('w2','another',NULL),
          ('w3','ambiguous',NULL),('w4','ambiguous','2026-09-11T10:00:00Z');
        INSERT INTO workspace_session_aliases VALUES ('alias','w2'),('dangling','missing');
        """);
    var reader = new CopilotArchiveReader(database);
    if (args.Length == 2 && args[0] == "--bundle")
    {
        var bundle = Path.GetFullPath(args[1]);
        var context = new Pagurian.ModuleLoadContext(Path.Combine(bundle, "Pagurian.Modules.Copilot.dll"));
        var assembly = context.LoadFromAssemblyName(new("Microsoft.Data.Sqlite"));
        using var bundledConnection = (System.Data.Common.DbConnection)Activator.CreateInstance(
            assembly.GetType("Microsoft.Data.Sqlite.SqliteConnection", throwOnError: true)!,
            $"Data Source={database};Mode=ReadOnly;Pooling=False")!;
        bundledConnection.Open();
        using var bundledCommand = bundledConnection.CreateCommand();
        bundledCommand.CommandText = "SELECT COUNT(*) FROM workspaces";
        Check(Convert.ToInt64(bundledCommand.ExecuteScalar()) == 4, "Deployed module SQLite native runtime opens WAL DB");
        Check(context.Assemblies.Any(a => a.GetName().Name == "SQLitePCLRaw.core"),
            "SQLite provider dependencies resolve privately in production module load context");
        Check(!context.Assemblies.Any(a => Pagurian.ModuleLoadContext.IsHostOwnedAssemblyName(a.GetName().Name)),
            "SQLite bundle does not load private host contract assemblies");
    }
    var ids = new[] { "direct", "alias", "chat", "unmapped", "ambiguous", "dangling", "malformed", "absent" };
    var states = reader.Read(ids, CancellationToken.None);
    Check(states["direct"] == CopilotArchiveState.Live && states["alias"] == CopilotArchiveState.Live,
        "Direct and alias map live despite is_running=0");
    Check(states["chat"] == CopilotArchiveState.Live, "Standalone chat live");
    foreach (var id in ids.Skip(3)) Check(states[id] == CopilotArchiveState.Unknown, $"Unknown {id}");
    Sql("""
        UPDATE workspaces SET archived_at='2026-09-11T10:00:00Z' WHERE id IN ('w1','w2');
        UPDATE sessions SET archived_at='2026-09-11T10:00:00Z' WHERE id='chat';
        """);
    Check(File.Exists(database + "-wal") && new FileInfo(database + "-wal").Length > 0, "Fixture keeps WAL updates live");
    states = reader.Read(ids, CancellationToken.None);
    Check(states["direct"] == CopilotArchiveState.Archived && states["alias"] == CopilotArchiveState.Archived,
        "Project workspace archive overrides null session flags");
    Check(states["chat"] == CopilotArchiveState.Archived, "Standalone chat archive");
    Sql("UPDATE sessions SET archived_at='2026-09-11T10:00:00Z' WHERE id='direct'; UPDATE workspaces SET archived_at=NULL WHERE id='w1';");
    Check(reader.Read(["direct"], CancellationToken.None)["direct"] == CopilotArchiveState.Live,
        "Mapped workspace live overrides archived session flag");
    using (var transaction = writer.BeginTransaction())
    {
        using var command = writer.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE workspaces SET archived_at='2026-09-11T11:00:00Z' WHERE id='w1';";
        command.ExecuteNonQuery();
        Check(reader.Read(["direct"], CancellationToken.None)["direct"] == CopilotArchiveState.Live,
            "WAL reader sees committed snapshot under writer lock");
        transaction.Rollback();
    }
    Check(new CopilotArchiveReader(Path.Combine(root, "missing.db")).Read(ids, CancellationToken.None).Count == 0,
        "Missing DB unknown without file creation");
    Check(!File.Exists(Path.Combine(root, "missing.db")), "Read-only mode never creates DB");
    Sql("ALTER TABLE workspace_session_aliases RENAME COLUMN workspace_id TO incompatible;");
    Check(reader.Read(ids, CancellationToken.None).Count == 0, "Schema drift unknown");
    Sql("ALTER TABLE workspace_session_aliases RENAME COLUMN incompatible TO workspace_id;");
    using (var cancel = new CancellationTokenSource())
    {
        cancel.Cancel();
        bool cancelled = false;
        try { reader.Read(ids, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "Database batch observes cancellation");
    }
    var lockedDatabase = Path.Combine(root, "locked.db");
    using (var locker = new SqliteConnection($"Data Source={lockedDatabase};Pooling=False"))
    {
        locker.Open();
        using var lockCommand = locker.CreateCommand();
        lockCommand.CommandText = "CREATE TABLE sessions(id TEXT); BEGIN EXCLUSIVE;";
        lockCommand.ExecuteNonQuery();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Check(new CopilotArchiveReader(lockedDatabase).Read(ids, CancellationToken.None).Count == 0,
            "Exclusive database lock is Unknown");
        Check(watch.Elapsed < TimeSpan.FromSeconds(4), "Lock wait bounded to one-second provider timeout");
        lockCommand.CommandText = "ROLLBACK;";
        lockCommand.ExecuteNonQuery();
    }

    // Exercise production process policy with fake handles, never live user processes.
    var ownerFile = Path.Combine(root, "single-instance.owner");
    File.WriteAllText(ownerFile, """{"pid":10}""");
    var appProcess = new FakeProcess(10, epoch, @"C:\fixture\github.exe", 0);
    var sdkProcess = new FakeProcess(20, epoch.AddSeconds(1), @"C:\fixture\copilot.exe", 10);
    var handles = new Dictionary<int, FakeProcess> { [10] = appProcess, [20] = sdkProcess };
    using var processes = new CopilotAppProcesses(open: pid => handles.TryGetValue(pid, out var process)
        ? process : throw new ArgumentException("fixture missing process"));
    var app = processes.ReadOwner(ownerFile);
    Check(app is not null, "Owner requires verified executable and company");
    Check(processes.IsLoaded(20, epoch.AddSeconds(2), app!), "Verified SDK ancestry loads session");
    Check(!processes.IsLoaded(20, epoch, app!), "Stale lock predating SDK instance rejected");
    sdkProcess.Parent = 999;
    Check(!processes.IsLoaded(20, epoch.AddSeconds(2), app!), "Unverified ancestry rejected");
    sdkProcess.Parent = 10;
    sdkProcess.Denied = true;
    Check(!processes.IsLoaded(20, epoch.AddSeconds(2), app!), "Process permission denial remains unknown");
    sdkProcess.Denied = false;
    appProcess.Denied = true;
    Check(processes.Exited().Count == 0 && processes.ReadOwner(ownerFile) is null,
        "Access denied is not exit");
    appProcess.Denied = false;
    File.WriteAllText(ownerFile, "stale/not-json");
    Check(processes.ReadOwner(ownerFile) is null && processes.Exited().Count == 0, "Bad owner hint does not remove known App");
    appProcess.Exited = true;
    Check(processes.Exited().Single().Key == app!.Key, "Retained instance proves exit without hooks");
    handles[10] = new FakeProcess(10, epoch.AddSeconds(10), @"C:\fixture\github.exe", 0);
    File.WriteAllText(ownerFile, "10");
    var replacement = processes.ReadOwner(ownerFile)!;
    Check(replacement.Key != app.Key && processes.Exited().Single().Key == app.Key, "PID reuse does not replace retained handle");
    Check(!processes.IsLoaded(20, epoch.AddSeconds(20), replacement), "Reused App parent PID cannot adopt older SDK");
    handles[20] = new FakeProcess(20, epoch.AddSeconds(11), @"C:\fixture\copilot.exe", 10);
    Check(!processes.IsLoaded(20, epoch.AddSeconds(2), replacement), "Reused SDK PID cannot adopt stale lock");
    Check(processes.IsLoaded(20, epoch.AddSeconds(12), replacement), "Replacement SDK accepts new lock");
    handles[10].Executable = @"C:\fixture\not-github.exe";
    Check(processes.ReadOwner(ownerFile) is null, "Name mismatch rejected");
    handles[10].Executable = @"C:\fixture\github.exe";
    handles[10].Publisher = "Other";
    Check(processes.ReadOwner(ownerFile) is null, "Publisher mismatch rejected");

    // Full resolver adapter path: injected data directory + fake processes.
    var stateDirectory = Path.Combine(root, "session-state");
    Directory.CreateDirectory(stateDirectory);
    void Metadata(string id, bool loaded)
    {
        var directory = Path.Combine(stateDirectory, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "workspace.yaml"), $"client_name: github/autopilot\nname: {id}\n");
        File.SetLastWriteTimeUtc(Path.Combine(directory, "workspace.yaml"), DateTime.UtcNow.AddDays(-60));
        if (loaded)
        {
            var file = Path.Combine(directory, "inuse.20.lock");
            File.WriteAllText(file, "");
            File.SetLastWriteTimeUtc(file, epoch.AddSeconds(15).UtcDateTime);
        }
    }
    Metadata("direct", true); Metadata("alias", true); Metadata("history", false);
    Sql("INSERT INTO sessions VALUES('history','chat',NULL,0);");
    var fakeProcesses = new FakeProcesses(replacement);
    using var lifecycle = new CopilotAppLifecycleReader(stateDirectory, reader, fakeProcesses);
    using var resolver = new CopilotSessionIdentityResolver(stateDirectory, appLifecycle: lifecycle);
    var state = new CopilotSessionState();
    for (int i = 0; i < 3; i++) state.Resolve(resolver.Scan(), resolver.LifecycleSnapshot);
    Check(state.Find("direct") is not null, "Loaded metadata older than seven days restored");
    Check(state.Find("alias") is null && state.Find("history") is null, "Archived and historical rows stay hidden");
    Check(state.Find("direct")!.Status == CopilotSessionStatus.Idle, "No-hook initial attachment is Idle");
    fakeProcesses.Loaded = false;
    state.Resolve(resolver.Scan(), resolver.LifecycleSnapshot);
    Check(state.Find("direct") is not null, "Runtime-only detach is not archive");
    Sql("UPDATE workspaces SET archived_at='2026-09-11T10:00:00Z' WHERE id='w1';");
    state.Resolve(resolver.Scan(), resolver.LifecycleSnapshot);
    Check(state.Find("direct") is null, "No-hook DB archive removes attached root");
    for (int i = 0; i < 270; i++)
        Directory.CreateDirectory(Path.Combine(stateDirectory, $"historical-{i:D3}"));
    Metadata("late-loaded", true);
    Metadata("foreign", true);
    File.WriteAllText(Path.Combine(stateDirectory, "foreign", "workspace.yaml"), "client_name: vscode\n");
    Sql("INSERT INTO sessions VALUES('late-loaded','chat',NULL,0),('foreign','chat',NULL,0);");
    fakeProcesses.Loaded = true;
    for (int i = 0; i < 8; i++) state.Resolve(resolver.Scan(), resolver.LifecycleSnapshot);
    Check(state.Find("late-loaded") is not null && state.Find("foreign") is null,
        "Bounded discovery eventually restores old loaded App metadata, never a foreign client");
    fakeProcesses.ConfirmedExit = true;
    state.Resolve(resolver.Scan(), resolver.LifecycleSnapshot);
    Check(state.Sessions.Count == 0, "Coherent worker snapshot observes App exit without hooks");
    using (var unavailableLifecycle = new CopilotAppLifecycleReader(stateDirectory,
        new CopilotArchiveReader(Path.Combine(root, "missing.db")), new FakeProcesses(replacement)))
    using (var unavailableResolver = new CopilotSessionIdentityResolver(stateDirectory, appLifecycle: unavailableLifecycle))
    {
        var unavailableState = new CopilotSessionState();
        for (int i = 0; i < 4; i++)
            unavailableState.Resolve(unavailableResolver.Scan(), unavailableResolver.LifecycleSnapshot);
        Check(unavailableState.Sessions.Count == 0, "Loaded locks with unavailable archive DB cannot invent visible roots");
    }
    resolver.Dispose();
    Check(resolver.Scan().Count == 0, "Stopped resolver cannot publish another scan");

    AppLifecycleFixture.Run();
    Console.WriteLine($"PASS {assertions} SQLite/process/resolver adapter assertions");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, recursive: true);
}

sealed class FakeProcess(int pid, DateTimeOffset started, string path, int parent) : ICopilotProcessHandle
{
    public bool Denied, Exited;
    public string Executable = path, Publisher = "GitHub, Inc.";
    public int Parent = parent;
    private void Guard() { if (Denied) throw new UnauthorizedAccessException("fixture"); }
    public int Pid => pid;
    public DateTimeOffset StartedAt { get { Guard(); return started; } }
    public string? Path { get { Guard(); return Executable; } }
    public string? Company { get { Guard(); return Publisher; } }
    public int ParentPid { get { Guard(); return Parent; } }
    public bool HasExited { get { Guard(); return Exited; } }
    public void Dispose() { }
}

sealed class FakeProcesses(CopilotAppInstance app) : ICopilotAppProcesses
{
    public bool Loaded = true;
    public bool ConfirmedExit;
    public CopilotAppInstance? ReadOwner(string ownerFile) => app;
    public bool IsLoaded(int sdkPid, DateTimeOffset stamp, CopilotAppInstance owner) =>
        Loaded && sdkPid == 20 && owner == app;
    public IReadOnlyList<CopilotAppInstance> Exited() => ConfirmedExit ? [app] : [];
    public void Dispose() { }
}
