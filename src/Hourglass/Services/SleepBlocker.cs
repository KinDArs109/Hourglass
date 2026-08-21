using System.Runtime.InteropServices;
using Hourglass.Services.Interfaces;

namespace Hourglass.Services;

/// <summary>
/// Holds sleep off while the app is actually boosting.
///
/// A machine that dozes off counts no hours, so idling through the night on a laptop is
/// pointless unless something tells Windows the work is real. The display is left alone:
/// the screen may go dark, only the machine itself has to stay awake.
/// </summary>
public sealed class SleepBlocker
{
    private const uint ContinuousState = 0x80000000;
    private const uint SystemRequired = 0x00000001;

    private readonly IAppLogger _logger;
    private bool _isHolding;

    public SleepBlocker(IAppLogger logger) => _logger = logger;

    /// <summary>
    /// Must be called from the same thread every time: Windows tracks the request per
    /// thread, so a hold taken on one thread cannot be released from another.
    /// </summary>
    public void Hold(bool keepAwake)
    {
        if (_isHolding == keepAwake)
            return;

        var state = keepAwake ? ContinuousState | SystemRequired : ContinuousState;
        if (SetThreadExecutionState(state) == 0)
        {
            _logger.Warn(AppLogScopes.App, "Windows не принял просьбу не засыпать");
            return;
        }

        _isHolding = keepAwake;
        _logger.Info(AppLogScopes.App, keepAwake
            ? "Компьютеру не даём уснуть, пока идёт накрутка"
            : "Отпускаем — компьютер снова может уснуть");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
