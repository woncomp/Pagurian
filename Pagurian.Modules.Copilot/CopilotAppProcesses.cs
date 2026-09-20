using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Pagurian.Modules.Copilot;

internal interface ICopilotProcessHandle : IDisposable
{
    int Pid { get; }
    DateTimeOffset StartedAt { get; }
    string? Path { get; }
    string? Company { get; }
    int ParentPid { get; }
    bool HasExited { get; }
}

internal sealed class CopilotAppProcesses : ICopilotAppProcesses
{
    private readonly Dictionary<string, (CopilotAppInstance Identity, ICopilotProcessHandle Process)> _apps = new();
    private readonly Dictionary<string, CopilotAppInstance> _exited = new();
    private readonly Func<int, ICopilotProcessHandle> _open;
    private readonly Action<string>? _diagnostic;

    public CopilotAppProcesses(Action<string>? diagnostic = null,
        Func<int, ICopilotProcessHandle>? open = null)
    {
        _diagnostic = diagnostic;
        _open = open ?? (pid => new WindowsProcess(pid));
    }

    public CopilotAppInstance? ReadOwner(string ownerFile)
    {
        try
        {
            using var stream = new FileStream(ownerFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[4097];
            int count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (count == buffer.Length) return null;
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, count).Trim();
            if (!int.TryParse(text, out int pid))
            {
                using var json = JsonDocument.Parse(text);
                if (!json.RootElement.TryGetProperty("pid", out var property) || !property.TryGetInt32(out pid))
                    return null;
            }
            using var candidate = _open(pid);
            var path = candidate.Path;
            if (path is null || !System.IO.Path.IsPathFullyQualified(path) ||
                !System.IO.Path.GetFileName(path).Equals("github.exe", StringComparison.OrdinalIgnoreCase))
                return null;
            if (candidate.Company?.Contains("GitHub", StringComparison.OrdinalIgnoreCase) != true)
                return null;
            var identity = new CopilotAppInstance(pid, candidate.StartedAt, path);
            if (candidate.HasExited || _exited.ContainsKey(identity.Key)) return null;
            if (!_apps.ContainsKey(identity.Key))
            {
                var retained = _open(pid);
                if (retained.StartedAt != identity.StartedAt || retained.Path != path || retained.HasExited)
                {
                    retained.Dispose();
                    return null;
                }
                _apps.Add(identity.Key, (identity, retained));
            }
            return identity;
        }
        catch (Exception e) when (Unavailable(e))
        {
            _diagnostic?.Invoke("app-owner-unavailable");
            return null;
        }
    }

    public bool IsLoaded(int sdkPid, DateTimeOffset lockWrittenAt, CopilotAppInstance app)
    {
        try
        {
            if (!_apps.TryGetValue(app.Key, out var owner) || owner.Process.HasExited) return false;
            using var sdk = _open(sdkPid);
            var path = sdk.Path;
            if (path is null || !System.IO.Path.IsPathFullyQualified(path) ||
                !System.IO.Path.GetFileName(path).Equals("copilot.exe", StringComparison.OrdinalIgnoreCase) ||
                sdk.StartedAt > lockWrittenAt || sdk.HasExited)
                return false;
            DateTimeOffset childStart = sdk.StartedAt;
            int parent = sdk.ParentPid;
            for (int depth = 0; depth < 8 && parent > 0; depth++)
            {
                using var process = _open(parent);
                DateTimeOffset start = process.StartedAt;
                if (start > childStart || process.HasExited) return false;
                if (parent == app.Pid)
                    return start == app.StartedAt && process.Path == app.Path &&
                        !owner.Process.HasExited && !sdk.HasExited;
                childStart = start;
                parent = process.ParentPid;
            }
        }
        catch (Exception e) when (Unavailable(e)) { _diagnostic?.Invoke("app-process-unavailable"); }
        return false;
    }

    public IReadOnlyList<CopilotAppInstance> Exited()
    {
        foreach (var (key, (identity, process)) in _apps.ToArray())
        {
            try
            {
                if (!process.HasExited) continue;
                _exited[key] = identity;
                _apps.Remove(key);
                process.Dispose();
            }
            catch (Exception e) when (Unavailable(e)) { _diagnostic?.Invoke("app-exit-unavailable"); }
        }
        return _exited.Values.ToArray();
    }

    private static int Parent(Process process)
    {
        var info = new ProcessBasicInformation();
        return NtQueryInformationProcess(process.SafeHandle.DangerousGetHandle(), 0, ref info,
            Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
            ? checked((int)info.ParentId) : 0;
    }

    private sealed class WindowsProcess : ICopilotProcessHandle
    {
        private readonly Process _process;
        public WindowsProcess(int pid)
        {
            _process = Process.GetProcessById(pid);
            try { _ = _process.SafeHandle; }
            catch { _process.Dispose(); throw; }
        }
        public int Pid => _process.Id;
        public DateTimeOffset StartedAt => _process.StartTime.ToUniversalTime();
        public string? Path => _process.MainModule?.FileName;
        public string? Company => Path is { } path ? FileVersionInfo.GetVersionInfo(path).CompanyName : null;
        public int ParentPid => Parent(_process);
        public bool HasExited => _process.HasExited;
        public void Dispose() => _process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint Reserved1, Peb, Reserved2, Reserved3, ProcessId, ParentId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int informationClass,
        ref ProcessBasicInformation information, int length, out int returned);

    private static bool Unavailable(Exception e) =>
        e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
        or InvalidOperationException or ArgumentException or JsonException or OverflowException;

    public void Dispose()
    {
        foreach (var (_, (_, process)) in _apps) process.Dispose();
        _apps.Clear();
        _exited.Clear();
    }
}
