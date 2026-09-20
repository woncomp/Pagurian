using System.Runtime.InteropServices;

namespace Pagurian;

// Raw display enumeration for the topology layer: EnumDisplayMonitors for
// geometry/primary flags, joined with QueryDisplayConfig active paths for
// EDID identity (manufacturer + product + per-unit UID) and the monitor's
// friendly name. Pure interop — no state, safe to call repeatedly.
static class DisplayInterop
{
    // Letters come from the EDID manufacturer id: three 5-bit fields, each
    // biased by 'A' - 1 (0x10AC → "DEL").
    internal static string EdidManufacturerName(ushort id) =>
        string.Create(3, id, static (chars, packed) =>
        {
            chars[0] = (char)(((packed >> 10) & 0x1F) + 'A' - 1);
            chars[1] = (char)(((packed >> 5) & 0x1F) + 'A' - 1);
            chars[2] = (char)((packed & 0x1F) + 'A' - 1);
        });

    // Identity/window-key safe form: letters, digits and dashes only.
    internal static string SanitizeKey(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (!char.IsLetterOrDigit(chars[i]))
                chars[i] = '-';
        return new string(chars);
    }

    internal static IReadOnlyList<DisplayInfo> EnumerateDisplays()
    {
        var configByDevice = QueryActiveTargets();
        var taskbarByMonitor = TaskbarInterop.FindAllTaskbars()
            .GroupBy(t => t.Monitor)
            .ToDictionary(g => g.Key, g => g.First().Hwnd);

        var displays = new List<DisplayInfo>();
        EnumMonitorsDelegate callback = (nint monitor, nint _, ref TaskbarInterop.RECT _, nint _) =>
        {
            if (TaskbarInterop.TryGetMonitorInfo(monitor, out var info))
            {
                configByDevice.TryGetValue(info.DeviceName, out var target);
                var identity = target.Identity ?? $"gdi-{SanitizeKey(info.DeviceName)}";
                displays.Add(new DisplayInfo(
                    identity,
                    target.FriendlyName ?? identity,
                    info.DeviceName,
                    monitor,
                    info.MonitorRect,
                    info.WorkRect,
                    info.IsPrimary,
                    taskbarByMonitor.TryGetValue(monitor, out var taskbar) ? taskbar : 0));
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return DeduplicateIdentities(displays);
    }

    // Identical monitors with no EDID serial produce identical keys. Split
    // them deterministically by desktop position so the assignment is stable
    // for this topology, and say so in the log.
    private static IReadOnlyList<DisplayInfo> DeduplicateIdentities(List<DisplayInfo> displays)
    {
        foreach (var group in displays.GroupBy(d => d.IdentityKey).Where(g => g.Count() > 1))
        {
            var ordered = group
                .OrderBy(d => d.Rect.Left)
                .ThenBy(d => d.Rect.Top)
                .ThenBy(d => d.DeviceName, StringComparer.Ordinal)
                .ToArray();
            for (var i = 0; i < ordered.Length; i++)
            {
                PagurianLog.Host(
                    $"displays: {ordered.Length} monitors share identity {group.Key}; " +
                    $"assigning {group.Key}-{i + 1} to the one at {ordered[i].Rect.Left},{ordered[i].Rect.Top}");
                displays[displays.IndexOf(ordered[i])] = ordered[i] with
                {
                    IdentityKey = $"{group.Key}-{i + 1}",
                };
            }
        }
        return displays;
    }

    private readonly record struct TargetIdentity(string Identity, string FriendlyName);

    // Maps GDI device names (\\.\DISPLAYn) to the EDID-derived identity and
    // friendly name of the display driving that source.
    private static Dictionary<string, TargetIdentity> QueryActiveTargets()
    {
        var result = new Dictionary<string, TargetIdentity>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0 ||
            pathCount == 0)
            return result;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        // Mode info is never read here, but QueryDisplayConfig still writes it
        // with its own struct stride, so hand it a pinned buffer sized by the
        // native DISPLAYCONFIG_MODE_INFO footprint (64 bytes on x64).
        const int modeInfoSize = 64;
        var modesBuffer = new byte[(long)modeCount * modeInfoSize];
        var modesHandle = GCHandle.Alloc(modesBuffer, GCHandleType.Pinned);
        int queryResult;
        try
        {
            queryResult = QueryDisplayConfig(
                QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount,
                modesHandle.AddrOfPinnedObject(), IntPtr.Zero);
        }
        finally
        {
            modesHandle.Free();
        }
        if (queryResult != 0)
            return result;

        foreach (var path in paths)
        {
            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = path.sourceInfo.adapterId,
                    id = path.sourceInfo.id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref source) != 0)
                continue;
            var deviceName = source.viewGdiDeviceName;
            if (string.IsNullOrWhiteSpace(deviceName) || result.ContainsKey(deviceName))
                continue;

            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = path.targetInfo.adapterId,
                    id = path.targetInfo.id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref target) != 0)
                continue;

            var identity = BuildIdentity(target);
            var friendly = string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName)
                ? identity
                : target.monitorFriendlyDeviceName.Trim();
            result[deviceName] = new TargetIdentity(identity, friendly);
        }
        return result;
    }

    // monitorDevicePath looks like
    // \\?\DISPLAY#DEL40A6#5&2e8c6a6&0&UID4354#{e6f07b5f-...}: the middle
    // segment is manufacturer+product, and the instance segment carries the
    // per-unit UID derived from the EDID serial. Both survive reboots; the
    // port-specific prefix and trailing interface GUID do not, so the durable
    // key is "model-uid". Without a UID (serial-less EDID) the whole instance
    // segment is hashed instead.
    private static string BuildIdentity(DISPLAYCONFIG_TARGET_DEVICE_NAME target)
    {
        var segments = target.monitorDevicePath?.Split('#');
        var model = segments is { Length: >= 3 } && segments[1].Length > 0
            ? segments[1]
            : EdidManufacturerName(target.edidManufactureId) + target.edidProductCodeId.ToString("X4");
        var instance = segments is { Length: >= 3 } ? segments[2] : "";
        var uidIndex = instance.LastIndexOf("UID", StringComparison.Ordinal);
        if (uidIndex >= 0)
        {
            var uid = instance[uidIndex..];
            var end = uid.IndexOf('&');
            if (end > 0) uid = uid[..end];
            if (uid.Length > 3)
                return SanitizeKey($"{model}-{uid}");
        }
        var hash = StableHash(instance.Length > 0 ? instance : target.monitorDevicePath ?? model);
        return SanitizeKey($"{model}-{hash:X8}");
    }

    private static uint StableHash(string value)
    {
        // FNV-1a: stable across runs; only a discriminator is needed.
        uint hash = 2166136261;
        foreach (var c in value)
            hash = (hash ^ c) * 16777619;
        return hash;
    }

    private delegate bool EnumMonitorsDelegate(
        nint monitor, nint hdc, ref TaskbarInterop.RECT rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        nint hdc, nint clip, EnumMonitorsDelegate callback, nint data);

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public int outputTechnology;
        public int rotation;
        public int scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public int scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public int outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string? monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        nint modeInfoArray,
        nint currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME deviceName);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME deviceName);
}
