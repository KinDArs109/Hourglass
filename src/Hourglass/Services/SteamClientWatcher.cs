using System.Diagnostics;
using Hourglass.Utilities;
using Microsoft.Win32;

namespace Hourglass.Services;

/// <summary>
/// Reports whether the local Steam client is running, and whether it is signed in
/// with a particular account. Used to stand down instead of fighting the user for
/// their own session.
/// </summary>
public sealed class SteamClientWatcher
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private DateTime _sampledAtUtc = DateTime.MinValue;
    private bool _isRunning;
    private string? _signedInAccount;
    private uint _runningAppId;

    public bool IsClientRunning
    {
        get
        {
            Sample();
            return _isRunning;
        }
    }

    /// <summary>Login name the local client is signed in with, or null when unknown.</summary>
    public string? SignedInAccount
    {
        get
        {
            Sample();
            return _signedInAccount;
        }
    }

    /// <summary>App id the local client currently has running, or 0 when idle.</summary>
    public uint RunningAppId
    {
        get
        {
            Sample();
            return _runningAppId;
        }
    }

    public bool IsSignedInAs(string username)
    {
        Sample();
        return _signedInAccount is not null &&
               string.Equals(_signedInAccount, username, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True only when the owner is actually playing something under this account.
    /// Merely being signed in is not a conflict: Steam is happy to hold both sessions,
    /// and standing down for an idle client would stop boosting for no reason.
    /// </summary>
    public bool IsPlayingAs(string username)
    {
        Sample();
        return _runningAppId != 0 && IsSignedInAs(username);
    }

    private void Sample()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _sampledAtUtc < CacheLifetime)
                return;

            _sampledAtUtc = DateTime.UtcNow;
            _isRunning = IsProcessRunning("steam");
            _signedInAccount = _isRunning ? ReadSignedInAccount() : null;
            _runningAppId = _isRunning ? ReadRunningAppId() : 0;
        }
    }

    private static bool IsProcessRunning(string processName)
    {
        var processes = Array.Empty<Process>();
        try
        {
            processes = Process.GetProcessesByName(processName);
            return processes.Length > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>The game the client launched, straight from Steam's own registry value.</summary>
    private static uint ReadRunningAppId()
    {
        uint appId = 0;

        SafeExec.Try(() =>
        {
            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steam?.GetValue("RunningAppID") is int value && value > 0)
                appId = (uint)value;
        });

        return appId;
    }

    /// <summary>
    /// Returns the account only when the client actually has a user signed in.
    /// AutoLoginUser alone is stale after sign-out, so ActiveUser gates it.
    /// </summary>
    private static string? ReadSignedInAccount()
    {
        string? account = null;

        SafeExec.Try(() =>
        {
            using var activeProcess = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (activeProcess?.GetValue("ActiveUser") is not int activeUser || activeUser == 0)
                return;

            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steam?.GetValue("AutoLoginUser") is string login && !string.IsNullOrWhiteSpace(login))
                account = login;
        });

        return account;
    }
}
