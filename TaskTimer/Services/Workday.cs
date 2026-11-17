using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace TaskTimer.Services;

/// <summary>
/// Follows the Windows session: whether the user is away (workstation locked or the remote
/// desktop connection dropped) and whether the session is currently driven over Remote Desktop.
/// </summary>
public sealed class SessionMonitor : IDisposable
{
    private volatile bool _away;

    public SessionMonitor() => SystemEvents.SessionSwitch += OnSessionSwitch;

    /// <summary>True while nobody can be working in this session.</summary>
    public bool IsAway => _away;

    /// <summary>True while the session is used through a Remote Desktop connection.</summary>
    public static bool IsRemote => SystemInformation.TerminalServerSession;

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.RemoteDisconnect:
            case SessionSwitchReason.ConsoleDisconnect:
                _away = true;
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.RemoteConnect:
            case SessionSwitchReason.ConsoleConnect:
                _away = false;
                break;
        }
    }

    // SystemEvents is static: without this the form would stay referenced after it closes.
    public void Dispose() => SystemEvents.SessionSwitch -= OnSessionSwitch;
}

public sealed class DayActivity
{
    /// <summary>Seconds the computer was actively used.</summary>
    public double ActiveSeconds { get; set; }
    /// <summary>The part of <see cref="ActiveSeconds"/> spent in a Remote Desktop session.</summary>
    public double RemoteSeconds { get; set; }
}

/// <summary>Working time on the computer per day, independent of which task (if any) was timed.</summary>
public sealed class WorkdayTracker
{
    private const string FileName = "activity.json";

    public Dictionary<string, DayActivity> Days { get; set; } = new();

    [JsonIgnore]
    public DayActivity Today
    {
        get
        {
            var key = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!Days.TryGetValue(key, out var day)) Days[key] = day = new DayActivity();
            return day;
        }
    }

    public static WorkdayTracker Load()
    {
        var tracker = SettingsStore.LoadJson<WorkdayTracker>(FileName);
        var cutoff = DateTime.Now.AddDays(-90).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var old in tracker.Days.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList())
            tracker.Days.Remove(old);
        // Repair days saved by older versions, which could subtract time after a clock change.
        foreach (var day in tracker.Days.Values)
        {
            day.ActiveSeconds = Math.Max(day.ActiveSeconds, 0);
            day.RemoteSeconds = Math.Clamp(day.RemoteSeconds, 0, day.ActiveSeconds);
        }
        return tracker;
    }

    public void Add(double seconds, bool remote)
    {
        if (!(seconds > 0)) return; // also rejects NaN
        var day = Today;
        day.ActiveSeconds += seconds;
        if (remote) day.RemoteSeconds += seconds;
    }

    public void Save() => SettingsStore.SaveJson(FileName, this);
}
