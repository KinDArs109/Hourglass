using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Hourglass.Services.Interfaces;

namespace Hourglass.Services;

/// <summary>
/// Turns down the request an installer sends when it wants every running program out of
/// the way.
///
/// Windows delivers that request as the same message it uses for a real shutdown, and it
/// sends it to every window a program owns — not just the one on screen. A program with
/// one window answering "no" and a hidden service window answering "yes" still ends up
/// closed, because the "yes" is enough. So the answer has to be given by all of them,
/// which is what this does: it steps in front of each window's own message handling and
/// replies for it.
///
/// A genuine shutdown or sign-out is left alone — those come without the mark that
/// singles out an installer, and standing in their way would only hang the machine.
/// </summary>
public sealed class SessionEndGuard
{
    /// <summary>Windows asking whether this window may close.</summary>
    private const int QueryEndSession = 0x0011;

    /// <summary>The follow-up telling the window what was decided.</summary>
    private const int EndSession = 0x0016;

    /// <summary>The last message a window ever gets; the handle is free after it.</summary>
    private const int NonClientDestroy = 0x0082;

    /// <summary>
    /// Set when the request comes from an installer clearing the way for its own files,
    /// rather than from Windows actually going down.
    /// </summary>
    private const long CloseAppFlag = 0x00000001;

    /// <summary>Where a window keeps the address of its message handling.</summary>
    private const int WindowProcSlot = -4;

    /// <summary>Only WPF's own windows are taken over; the rest belong to Windows.</summary>
    private const string GuardedClassPrefix = "HwndWrapper[";

    /// <summary>Windows come and go, so the sweep is repeated rather than done once.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    /// <summary>One request reaches every window, but it is worth saying once.</summary>
    private static readonly TimeSpan SameRequestWindow = TimeSpan.FromSeconds(5);

    private delegate IntPtr WindowProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private readonly IAppLogger _logger;
    private readonly Dictionary<IntPtr, IntPtr> _replaced = new();

    // The delegates handed to Windows are held here on purpose: nothing else refers to
    // them, and a collected one leaves a window calling into an address that is gone.
    private readonly List<WindowProc> _handlers = new();

    private DispatcherTimer? _sweep;
    private long _refusedStamp;

    public SessionEndGuard(IAppLogger logger) => _logger = logger;

    /// <summary>
    /// Whether an installer has just been turned down. Anything that survived the sweep
    /// and asks separately can lean on this instead of deciding on its own.
    /// </summary>
    public bool RefusedJustNow =>
        _refusedStamp != 0 && Stopwatch.GetElapsedTime(_refusedStamp) < SameRequestWindow;

    /// <summary>Covers the windows there are now and keeps covering the ones to come.</summary>
    public void Start()
    {
        Sweep();

        _sweep = new DispatcherTimer { Interval = SweepInterval };
        _sweep.Tick += (_, _) => Sweep();
        _sweep.Start();
    }

    private void Sweep()
    {
        var self = (uint)Environment.ProcessId;
        var covered = _replaced.Count;

        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);

            if (owner == self && IsOurs(window))
                Take(window);

            return true;
        }, IntPtr.Zero);

        // Silence is the normal outcome; an empty sweep is not, and the journal is the
        // only place a night-long problem can be traced back to afterwards.
        if (_replaced.Count == 0 && covered == 0)
            _logger.Warn(AppLogScopes.App, "Окна не удалось прикрыть — чужой установщик может закрыть программу");
    }

    private static bool IsOurs(IntPtr window)
    {
        var name = new StringBuilder(64);
        return GetClassName(window, name, name.Capacity) > 0
               && name.ToString().StartsWith(GuardedClassPrefix, StringComparison.Ordinal);
    }

    private void Take(IntPtr window)
    {
        if (_replaced.ContainsKey(window))
            return;

        WindowProc handler = OnMessage;

        var previous = SetWindowProc(window, Marshal.GetFunctionPointerForDelegate(handler));
        if (previous == IntPtr.Zero)
            return;

        _handlers.Add(handler);
        _replaced[window] = previous;
    }

    private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam)
    {
        if ((message == QueryEndSession || message == EndSession)
            && (lParam.ToInt64() & CloseAppFlag) != 0)
        {
            Refuse();

            // Zero means no: this window is staying, and with it the whole program.
            return IntPtr.Zero;
        }

        if (!_replaced.TryGetValue(window, out var previous))
            return DefWindowProc(window, message, wParam, lParam);

        var result = CallWindowProc(previous, window, message, wParam, lParam);

        // Handles get reused. Forgetting a window as it dies keeps the next one that
        // lands on the same handle from being sent to a handler that no longer exists.
        if (message == NonClientDestroy)
            _replaced.Remove(window);

        return result;
    }

    private void Refuse()
    {
        var repeated = RefusedJustNow;
        _refusedStamp = Stopwatch.GetTimestamp();

        if (repeated)
            return;

        _logger.Info(AppLogScopes.App,
            "Установщик другой программы просил закрыться — отказались, накрутка продолжается");
    }

    /// <summary>
    /// The wide handle only exists on 64-bit Windows; on 32-bit the narrow one is the
    /// whole of it, and both hand back what was there before.
    /// </summary>
    private static IntPtr SetWindowProc(IntPtr window, IntPtr handler) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr(window, WindowProcSlot, handler)
            : new IntPtr(SetWindowLong(window, WindowProcSlot, handler.ToInt32()));

    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int slot, IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int slot, int value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(
        IntPtr previous, IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
