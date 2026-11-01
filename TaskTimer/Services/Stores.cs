using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaskTimer.Models;

namespace TaskTimer.Services;

public static class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskTimer");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string PathFor(string file) => Path.Combine(Dir, file);

    public static T LoadJson<T>(string file) where T : new()
    {
        try
        {
            var path = PathFor(file);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable file: start from defaults rather than refusing to launch.
        }
        return new T();
    }

    public static void SaveJson<T>(string file, T value)
    {
        Directory.CreateDirectory(Dir);
        var path = PathFor(file);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public static AppSettings Load()
    {
        var settings = LoadJson<AppSettings>("settings.json");
        var hadAccounts = settings.Connections.Count > 0;
        settings.EnsureConnections();
        // Time entries are tied to the account's ID, so a newly created first account is saved at once.
        if (!hadAccounts) Save(settings);
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        settings.StoreActiveConnection();
        SaveJson("settings.json", settings);
    }

    public static string GetPat(AppSettings settings)
    {
        if (string.IsNullOrEmpty(settings.PatProtected)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(settings.PatProtected), null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }

    public static void SetPat(AppSettings settings, string pat)
    {
        settings.PatProtected = string.IsNullOrEmpty(pat)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(pat), null,
                DataProtectionScope.CurrentUser));
    }
}

/// <summary>Locally recorded time. Unsynced entries are what still has to be pushed to the server.</summary>
public sealed class TimeLog
{
    private const string FileName = "timelog.json";

    public List<TimeEntry> Entries { get; set; } = new();

    public static TimeLog Load()
    {
        var log = SettingsStore.LoadJson<TimeLog>(FileName);
        var cutoff = DateTime.UtcNow.AddDays(-30);
        log.Entries.RemoveAll(e => e.Synced && e.StartUtc < cutoff);
        return log;
    }

    public void Save() => SettingsStore.SaveJson(FileName, this);

    public void Add(WorkItem item, EntryKind kind, DateTime startUtc, double seconds)
    {
        if (seconds < 1) return;
        Entries.Add(new TimeEntry
        {
            Key = item.Key, Title = item.Title, Kind = kind, StartUtc = startUtc, Seconds = seconds,
            ConnectionId = IsLocalKey(item.Key) ? "" : ActiveConnection
        });
        Save();
    }

    /// <summary>Seconds recorded on any task, call or meeting that started on the given local day.</summary>
    public double SecondsOn(DateTime localDay) =>
        Entries.Where(e => e.StartUtc.ToLocalTime().Date == localDay.Date).Sum(e => e.Seconds);

    /// <summary>The account whose time is shown and sent; set by the main window.</summary>
    public static string ActiveConnection { get; set; } = "";

    // Local tasks and CRM tickets stay in the list whichever account is active.
    private static bool IsLocalKey(string key) =>
        key.StartsWith("local:", StringComparison.Ordinal) || key.StartsWith(TicketStore.KeyPrefix, StringComparison.Ordinal);

    private static bool InActiveAccount(TimeEntry e) => IsLocalKey(e.Key) || e.ConnectionId == ActiveConnection;

    /// <summary>Entries saved before accounts existed belong to the first account.</summary>
    public void AssignUnownedEntries(string connectionId)
    {
        var unowned = Entries.Where(e => e.ConnectionId.Length == 0 && !IsLocalKey(e.Key)).ToList();
        if (unowned.Count == 0) return;
        foreach (var e in unowned) e.ConnectionId = connectionId;
        Save();
    }

    public List<TimeEntry> Pending(string key) =>
        Entries.Where(e => !e.Synced && e.Key == key && InActiveAccount(e)).ToList();

    public double PendingSeconds(string key) => Pending(key).Sum(e => e.Seconds);

    /// <summary>Applies manual corrections: new durations for some entries, and entries to delete.</summary>
    public void Update(IReadOnlyDictionary<TimeEntry, double> durations, IEnumerable<TimeEntry> removed)
    {
        foreach (var (entry, seconds) in durations) entry.Seconds = seconds;
        var drop = removed.ToHashSet();
        // An entry corrected down to nothing is the same as a deleted one.
        Entries.RemoveAll(e => drop.Contains(e) || (durations.ContainsKey(e) && e.Seconds < 1));
        Save();
    }

    public void MarkSynced(IEnumerable<TimeEntry> entries)
    {
        foreach (var e in entries) e.Synced = true;
        Save();
    }
}
