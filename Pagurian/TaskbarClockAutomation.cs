using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Pagurian;

internal static class TaskbarClockAutomation
{
    internal readonly record struct Result(int? LeftPx, string Reason);

    private const int MaximumDepth = 8;
    private const int MaximumNodes = 128;
    private const uint ProviderTimeoutMs = 250;
    private const int QueryBudgetMs = 1000;
    private const int BoundingRectangleProperty = 30001;
    private const int ProcessIdProperty = 30002;
    private const int ControlTypeProperty = 30003;
    private const int AutomationIdProperty = 30011;
    private const int ClassNameProperty = 30012;
    private const int IsOffscreenProperty = 30022;
    private const int ButtonControlType = 50000;
    private const int TextControlType = 50020;
    private static readonly Guid AutomationClass = new("e22ad333-b25f-460c-83d0-0581107395c9");

    internal static Result Read(nint taskbar, uint taskbarProcessId, TaskbarInterop.RECT contentRect)
    {
        if (taskbar == 0 || taskbarProcessId == 0 || taskbarProcessId == (uint)Environment.ProcessId ||
            contentRect.Right <= contentRect.Left || contentRect.Bottom <= contentRect.Top ||
            !IsTaskbar(taskbar, taskbarProcessId))
            return new(null, "uia-invalid-taskbar");

        // S_FALSE also acquires an apartment initialization reference. An existing STA must
        // not be used or uninitialized by this reader.
        int initialized = CoInitializeEx(0, 0);
        if (initialized < 0)
            return new(null, initialized == unchecked((int)0x80010106)
                ? "uia-apartment-mismatch" : "uia-com-initialize");

        try
        {
            using var query = new Query(taskbarProcessId, contentRect);
            Guid clsid = AutomationClass;
            Guid iid = typeof(IUIAutomation2).GUID;
            int hr = CoCreateInstance(in clsid, 0, 1, in iid, out IUIAutomation2? automation);
            query.Own(automation);
            query.Check(hr);
            if (automation is null)
                return new(null, "uia-unavailable");

            query.Check(automation.put_ConnectionTimeout(ProviderTimeoutMs));
            query.Check(automation.put_TransactionTimeout(ProviderTimeoutMs));
            // Read back the settings: never silently run with the multi-second defaults.
            query.Check(automation.get_ConnectionTimeout(out uint connectionTimeout));
            query.Check(automation.get_TransactionTimeout(out uint transactionTimeout));
            if (connectionTimeout != ProviderTimeoutMs || transactionTimeout != ProviderTimeoutMs)
                return new(null, "uia-timeout-configuration");

            query.BeforeCall();
            hr = automation.ElementFromHandle(taskbar, out IUIAutomationElement? root);
            query.Own(root);
            query.Check(hr);
            if (root is null || !query.IsOwner(root))
                return new(null, "uia-owner-mismatch");

            query.BeforeCall();
            hr = automation.get_RawViewWalker(out IUIAutomationTreeWalker? walker);
            query.Own(walker);
            query.Check(hr);
            if (walker is null)
                return new(null, "uia-unavailable");

            Result result = query.Search(root, walker, 0);
            query.BeforeCall();
            return IsTaskbar(taskbar, taskbarProcessId) ? result : new(null, "uia-invalid-taskbar");
        }
        catch (QueryFailure error)
        {
            return new(null, error.Reason);
        }
        catch (COMException error)
        {
            return new(null, FailureReason(error.HResult));
        }
        catch (InvalidComObjectException)
        {
            return new(null, "uia-unavailable");
        }
        finally
        {
            CoUninitialize();
        }
    }

    private static bool IsTaskbar(nint taskbar, uint expectedProcessId)
    {
        if (GetWindowThreadProcessId(taskbar, out uint processId) == 0 ||
            processId != expectedProcessId || !IsWindowVisible(taskbar))
            return false;

        var className = new StringBuilder(64);
        return GetClassName(taskbar, className, className.Capacity) > 0 &&
            className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static string FailureReason(int hr) => hr switch
    {
        unchecked((int)0x80131505) or unchecked((int)0x800705B4) or
        unchecked((int)0x80070102) or unchecked((int)0x8001011F) => "uia-timeout",
        unchecked((int)0x80040201) => "uia-element-unavailable",
        _ => "uia-unavailable"
    };

    private sealed class QueryFailure(string reason) : Exception
    {
        internal string Reason { get; } = reason;
    }

    private sealed class Query(uint processId, TaskbarInterop.RECT content) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly List<object> _owned = [];
        private int _nodes;
        private string _missingReason = "uia-clock-missing";

        internal void Own(object? value)
        {
            if (value is not null)
                _owned.Add(value);
        }

        public void Dispose()
        {
            // Each COM out-parameter acquisition increments the RCW's reference count,
            // even if it returns an existing RCW. Release each acquisition, not each
            // unique object; FinalReleaseComObject would invalidate aliases prematurely.
            for (int index = _owned.Count - 1; index >= 0; index--)
                Marshal.ReleaseComObject(_owned[index]);
        }

        internal void BeforeCall()
        {
            // This bounds scheduling between calls, not an in-progress COM call.
            // Provider timeouts are best effort; cancellation cannot kill native UIA.
            if (Stopwatch.GetElapsedTime(_started).TotalMilliseconds >= QueryBudgetMs)
                throw new QueryFailure("uia-timeout");
        }

        internal void Check(int hr)
        {
            if (hr < 0)
                throw new QueryFailure(FailureReason(hr));
            BeforeCall();
        }

        private void Visit()
        {
            BeforeCall();
            if (++_nodes > MaximumNodes)
                throw new QueryFailure("uia-query-limit");
        }

        private object? Property(IUIAutomationElement element, int property)
        {
            BeforeCall();
            int hr = element.GetCurrentPropertyValueEx(property, 1, out object? value);
            // Unsupported properties may return UIA's reserved IUnknown instead of a
            // scalar. Its acquisition belongs to this call too.
            if (value is not null && Marshal.IsComObject(value))
            {
                Marshal.ReleaseComObject(value);
                value = null;
            }
            Check(hr);
            return value;
        }

        internal bool IsOwner(IUIAutomationElement element) =>
            Property(element, ProcessIdProperty) is int owner && owner > 0 && (uint)owner == processId;

        private bool Visible(IUIAutomationElement element) =>
            Property(element, IsOffscreenProperty) is false;

        private bool HasId(IUIAutomationElement element, string id) =>
            Property(element, AutomationIdProperty) is string actual &&
            string.Equals(actual, id, StringComparison.Ordinal);

        private bool HasClass(IUIAutomationElement element, string className) =>
            Property(element, ClassNameProperty) is string actual &&
            string.Equals(actual, className, StringComparison.Ordinal);

        private bool HasType(IUIAutomationElement element, int type) =>
            Property(element, ControlTypeProperty) is int actual && actual == type;

        private bool TryBounds(IUIAutomationElement element, out TaskbarInterop.RECT bounds)
        {
            bounds = default;
            // The property VARIANT contains SAFEARRAY(double) [left, top, width, height],
            // unlike get_CurrentBoundingRectangle's native, already-rounded RECT.
            if (Property(element, BoundingRectangleProperty) is not double[] { Length: 4 } values)
                return false;

            double left = values[0], top = values[1], width = values[2], height = values[3];
            double right = left + width, bottom = top + height;
            if (!double.IsFinite(left) || !double.IsFinite(top) ||
                !double.IsFinite(width) || !double.IsFinite(height) ||
                !double.IsFinite(right) || !double.IsFinite(bottom) ||
                width <= 0 || height <= 0 || left < int.MinValue || top < int.MinValue ||
                right > int.MaxValue || bottom > int.MaxValue)
                return false;

            bounds = new()
            {
                Left = (int)Math.Floor(left),
                Top = (int)Math.Floor(top),
                Right = (int)Math.Ceiling(right),
                Bottom = (int)Math.Ceiling(bottom)
            };
            return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
        }

        private IUIAutomationElement? LastChild(IUIAutomationTreeWalker walker, IUIAutomationElement parent)
        {
            BeforeCall();
            int hr = walker.GetLastChildElement(parent, out IUIAutomationElement? child);
            Own(child);
            Check(hr);
            return child;
        }

        private IUIAutomationElement? PreviousSibling(IUIAutomationTreeWalker walker, IUIAutomationElement element)
        {
            BeforeCall();
            int hr = walker.GetPreviousSiblingElement(element, out IUIAutomationElement? sibling);
            Own(sibling);
            Check(hr);
            return sibling;
        }

        private bool HasClockStructure(
            IUIAutomationTreeWalker walker, IUIAutomationElement button, TaskbarInterop.RECT buttonBounds, int depth)
        {
            if (depth >= MaximumDepth)
                throw new QueryFailure("uia-query-limit");

            bool timeFound = false;
            for (var child = LastChild(walker, button); child is not null; child = PreviousSibling(walker, child))
            {
                Visit();
                if (!IsOwner(child))
                    continue;

                bool time = HasId(child, "TimeInnerTextBlock");
                if (!time && !HasId(child, "DateInnerTextBlock"))
                    continue;

                if (!HasClass(child, "TextBlock") || !HasType(child, TextControlType))
                    return false;

                // A date element may remain in the tree while the system hides the date.
                if (!Visible(child))
                {
                    if (time)
                        return false;
                    continue;
                }

                if (!TryBounds(child, out var bounds) ||
                    bounds.Left < buttonBounds.Left || bounds.Top < buttonBounds.Top ||
                    bounds.Right > buttonBounds.Right || bounds.Bottom > buttonBounds.Bottom)
                    return false;
                timeFound |= time;
            }
            return timeFound;
        }

        internal Result Search(IUIAutomationElement element, IUIAutomationTreeWalker walker, int depth)
        {
            Visit();
            // Do not descend into Pagurian (or any other process), even if its geometry
            // and structural IDs happen to resemble the Explorer clock.
            if (!IsOwner(element))
                return new(null, _missingReason);

            if (HasId(element, "SystemTrayIcon") &&
                HasClass(element, "SystemTray.OmniButton") && HasType(element, ButtonControlType))
            {
                if (!Visible(element))
                    _missingReason = "uia-clock-offscreen";
                else if (!TryBounds(element, out var bounds) ||
                    !TaskbarSystemArea.IsValidBounds(bounds, content))
                    _missingReason = "uia-invalid-bounds";
                else if (!HasClockStructure(walker, element, bounds, depth))
                    _missingReason = "uia-clock-structure";
                else
                {
                    BeforeCall();
                    return new(bounds.Left, "uia-clock");
                }
                return new(null, _missingReason);
            }

            var child = LastChild(walker, element);
            if (child is not null && depth >= MaximumDepth)
                throw new QueryFailure("uia-query-limit");

            // The clock is normally at the end of the taskbar tree. Walking backwards
            // avoids enumerating every task button before reaching the right cluster.
            for (; child is not null; child = PreviousSibling(walker, child))
            {
                Result result = Search(child, walker, depth + 1);
                if (result.LeftPx.HasValue)
                    return result;
            }
            return new(null, _missingReason);
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        in Guid classId, nint outer, uint context, in Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IUIAutomation2? instance);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassName(nint window, StringBuilder className, int maximum);

    // ABI source: Windows SDK 10.0.26100.0 um\UIAutomationClient.h:
    // IUIAutomation (18848-19133), IUIAutomation2 (19666-19695),
    // IUIAutomationElement (1591 onward), IUIAutomationTreeWalker (3449 onward),
    // and CUIAutomation8 (23189). UIA_HWND is void*, BOOL/int/enum/LONG are 32-bit,
    // DWORD is uint32, POINT/RECT contain 32-bit LONGs on BOTH x64 and ARM64.
    // IUIAutomation2 is flattened explicitly, including every inherited method in
    // header order. The other interfaces expose exact prefixes only; no slot gaps.
    // Unused interface/SAFEARRAY/native-array pointers are opaque nint, not RCWs.
    // VARIANT parameters use the runtime's Struct marshaller (not a pointer-sized
    // stand-in). All HRESULTs are preserved and every called out-interface is owned.
    [ComImport, Guid("34723aff-0c9d-49d0-9896-7ab52df8cd8a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation2
    {
        [PreserveSig] int CompareElements(IUIAutomationElement first, IUIAutomationElement second, out int same);
        [PreserveSig] int CompareRuntimeIds(nint first, nint second, out int same);
        [PreserveSig] int GetRootElement(out IUIAutomationElement? root);
        [PreserveSig] int ElementFromHandle(nint window, out IUIAutomationElement? element);
        [PreserveSig] int ElementFromPoint(NativePoint point, out IUIAutomationElement? element);
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement? element);
        [PreserveSig] int GetRootElementBuildCache(nint request, out IUIAutomationElement? root);
        [PreserveSig] int ElementFromHandleBuildCache(nint window, nint request, out IUIAutomationElement? element);
        [PreserveSig] int ElementFromPointBuildCache(NativePoint point, nint request, out IUIAutomationElement? element);
        [PreserveSig] int GetFocusedElementBuildCache(nint request, out IUIAutomationElement? element);
        [PreserveSig] int CreateTreeWalker(nint condition, out IUIAutomationTreeWalker? walker);
        [PreserveSig] int get_ControlViewWalker(out IUIAutomationTreeWalker? walker);
        [PreserveSig] int get_ContentViewWalker(out IUIAutomationTreeWalker? walker);
        [PreserveSig] int get_RawViewWalker(out IUIAutomationTreeWalker? walker);
        [PreserveSig] int get_RawViewCondition(out nint condition);
        [PreserveSig] int get_ControlViewCondition(out nint condition);
        [PreserveSig] int get_ContentViewCondition(out nint condition);
        [PreserveSig] int CreateCacheRequest(out nint request);
        [PreserveSig] int CreateTrueCondition(out nint condition);
        [PreserveSig] int CreateFalseCondition(out nint condition);
        [PreserveSig] int CreatePropertyCondition(int property, [MarshalAs(UnmanagedType.Struct)] object value, out nint condition);
        [PreserveSig] int CreatePropertyConditionEx(int property, [MarshalAs(UnmanagedType.Struct)] object value, int flags, out nint condition);
        [PreserveSig] int CreateAndCondition(nint first, nint second, out nint condition);
        [PreserveSig] int CreateAndConditionFromArray(nint conditions, out nint condition);
        [PreserveSig] int CreateAndConditionFromNativeArray(nint conditions, int count, out nint condition);
        [PreserveSig] int CreateOrCondition(nint first, nint second, out nint condition);
        [PreserveSig] int CreateOrConditionFromArray(nint conditions, out nint condition);
        [PreserveSig] int CreateOrConditionFromNativeArray(nint conditions, int count, out nint condition);
        [PreserveSig] int CreateNotCondition(nint condition, out nint result);
        [PreserveSig] int AddAutomationEventHandler(int eventId, IUIAutomationElement element, int scope, nint request, nint handler);
        [PreserveSig] int RemoveAutomationEventHandler(int eventId, IUIAutomationElement element, nint handler);
        [PreserveSig] int AddPropertyChangedEventHandlerNativeArray(IUIAutomationElement element, int scope, nint request, nint handler, nint properties, int count);
        [PreserveSig] int AddPropertyChangedEventHandler(IUIAutomationElement element, int scope, nint request, nint handler, nint properties);
        [PreserveSig] int RemovePropertyChangedEventHandler(IUIAutomationElement element, nint handler);
        [PreserveSig] int AddStructureChangedEventHandler(IUIAutomationElement element, int scope, nint request, nint handler);
        [PreserveSig] int RemoveStructureChangedEventHandler(IUIAutomationElement element, nint handler);
        [PreserveSig] int AddFocusChangedEventHandler(nint request, nint handler);
        [PreserveSig] int RemoveFocusChangedEventHandler(nint handler);
        [PreserveSig] int RemoveAllEventHandlers();
        [PreserveSig] int IntNativeArrayToSafeArray(nint array, int count, out nint safeArray);
        [PreserveSig] int IntSafeArrayToNativeArray(nint array, out nint nativeArray, out int count);
        [PreserveSig] int RectToVariant(TaskbarInterop.RECT rectangle, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int VariantToRect([MarshalAs(UnmanagedType.Struct)] object value, out TaskbarInterop.RECT rectangle);
        [PreserveSig] int SafeArrayToRectNativeArray(nint rectangles, out nint nativeArray, out int count);
        [PreserveSig] int CreateProxyFactoryEntry(nint factory, out nint entry);
        [PreserveSig] int get_ProxyFactoryMapping(out nint mapping);
        [PreserveSig] int GetPropertyProgrammaticName(int property, [MarshalAs(UnmanagedType.BStr)] out string name);
        [PreserveSig] int GetPatternProgrammaticName(int pattern, [MarshalAs(UnmanagedType.BStr)] out string name);
        [PreserveSig] int PollForPotentialSupportedPatterns(IUIAutomationElement element, out nint ids, out nint names);
        [PreserveSig] int PollForPotentialSupportedProperties(IUIAutomationElement element, out nint ids, out nint names);
        [PreserveSig] int CheckNotSupported([MarshalAs(UnmanagedType.Struct)] object value, out int notSupported);
        [PreserveSig] int get_ReservedNotSupportedValue(out nint value);
        [PreserveSig] int get_ReservedMixedAttributeValue(out nint value);
        [PreserveSig] int ElementFromIAccessible(nint accessible, int childId, out IUIAutomationElement? element);
        [PreserveSig] int ElementFromIAccessibleBuildCache(nint accessible, int childId, nint request, out IUIAutomationElement? element);
        [PreserveSig] int get_AutoSetFocus(out int autoSetFocus);
        [PreserveSig] int put_AutoSetFocus(int autoSetFocus);
        [PreserveSig] int get_ConnectionTimeout(out uint timeout);
        [PreserveSig] int put_ConnectionTimeout(uint timeout);
        [PreserveSig] int get_TransactionTimeout(out uint timeout);
        [PreserveSig] int put_TransactionTimeout(uint timeout);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        [PreserveSig] int SetFocus();
        [PreserveSig] int GetRuntimeId(out nint runtimeId);
        [PreserveSig] int FindFirst(int scope, nint condition, out IUIAutomationElement? found);
        [PreserveSig] int FindAll(int scope, nint condition, out nint found);
        [PreserveSig] int FindFirstBuildCache(int scope, nint condition, nint request, out IUIAutomationElement? found);
        [PreserveSig] int FindAllBuildCache(int scope, nint condition, nint request, out nint found);
        [PreserveSig] int BuildUpdatedCache(nint request, out IUIAutomationElement? updated);
        [PreserveSig] int GetCurrentPropertyValue(int property, [MarshalAs(UnmanagedType.Struct)] out object? value);
        [PreserveSig] int GetCurrentPropertyValueEx(int property, int ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object? value);
    }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTreeWalker
    {
        [PreserveSig] int GetParentElement(IUIAutomationElement element, out IUIAutomationElement? parent);
        [PreserveSig] int GetFirstChildElement(IUIAutomationElement element, out IUIAutomationElement? first);
        [PreserveSig] int GetLastChildElement(IUIAutomationElement element, out IUIAutomationElement? last);
        [PreserveSig] int GetNextSiblingElement(IUIAutomationElement element, out IUIAutomationElement? next);
        [PreserveSig] int GetPreviousSiblingElement(IUIAutomationElement element, out IUIAutomationElement? previous);
    }
}
