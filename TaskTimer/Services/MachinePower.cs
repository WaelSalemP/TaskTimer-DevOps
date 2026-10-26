using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TaskTimer.Models;

namespace TaskTimer.Services;

public enum PowerState { Unknown, Running, Stopped, Busy }

/// <param name="Key">String-table key for a known state; otherwise null and <paramref name="Raw"/> is shown.</param>
public sealed record MachineStatus(PowerState State, string? Key, string Raw = "");

/// <summary>
/// Reads and changes the power state of dev machines:
/// cloud machines are Azure VMs (Azure Resource Manager API) or LCS environments (LCS API), both
/// reached with the user's own Microsoft sign-in (<see cref="AzureAuth"/>); nothing else needs
/// installing. On-premises machines are Hyper-V VMs, driven through the Hyper-V PowerShell module.
/// Stopping a cloud machine deallocates it (no compute charges); "stopping" an on-premises VM saves its state.
/// </summary>
public static partial class MachinePower
{
    public const string DefaultLcsApiUrl = "https://lcsapi.lcs.dynamics.com";
    // The token audience is the same for every regional LCS API address.
    private const string LcsResource = "https://lcsapi.lcs.dynamics.com";
    private const string ArmResource = "https://management.azure.com";
    private const string ArmComputeApi = "api-version=2024-07-01";
    private static readonly HttpClient Http = RetryHandler.CreateClient(TimeSpan.FromSeconds(60));

    /// <summary>Subscription found for a resource group, so the search runs once per session.</summary>
    private static readonly ConcurrentDictionary<string, string> SubscriptionByGroup = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\d{1,12}$")]
    private static partial Regex LcsProject();

    [GeneratedRegex(@"^[0-9A-Fa-f]{8}-?[0-9A-Fa-f]{4}-?[0-9A-Fa-f]{4}-?[0-9A-Fa-f]{4}-?[0-9A-Fa-f]{12}$")]
    private static partial Regex Guid();

    // Azure resource group and VM names, and subscription names or IDs (the characters Azure allows).
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._()-]{0,89}$")]
    private static partial Regex AzureName();

    // Hyper-V names are quoted for PowerShell, so only control characters need refusing.
    [GeneratedRegex(@"^[^\x00-\x1F]{1,100}$")]
    private static partial Regex HyperVName();

    private static bool IsLcs(DevMachine m) => m.Location == MachineLocation.Cloud && m.CloudSource == CloudSource.Lcs;

    public static bool IsManaged(DevMachine m) => m.Location == MachineLocation.Cloud
        ? IsLcs(m)
            ? m.LcsProjectId.Length > 0 && m.LcsEnvironmentId.Length > 0
            : m.AzureResourceGroup.Length > 0 && m.AzureVmName.Length > 0
        : m.HyperVVmName.Length > 0;

    /// <summary>Null when the VM details are usable (or not filled in); otherwise a string-table key.</summary>
    public static string? Validate(DevMachine m)
    {
        if (IsLcs(m))
        {
            if (m.LcsProjectId.Length == 0 && m.LcsEnvironmentId.Length == 0) return null;
            if (m.LcsProjectId.Length == 0 || m.LcsEnvironmentId.Length == 0) return "LcsDetailsIncomplete";
            var validApi = Uri.TryCreate(LcsApiBase(m), UriKind.Absolute, out var api) && api.Scheme == Uri.UriSchemeHttps &&
                           api.Host.EndsWith(".dynamics.com", StringComparison.OrdinalIgnoreCase);
            return LcsProject().IsMatch(m.LcsProjectId) && Guid().IsMatch(m.LcsEnvironmentId) && validApi
                ? null
                : "InvalidVmDetails";
        }
        if (m.Location == MachineLocation.Cloud)
        {
            if (m.AzureResourceGroup.Length == 0 && m.AzureVmName.Length == 0) return null;
            if (m.AzureResourceGroup.Length == 0 || m.AzureVmName.Length == 0) return "VmDetailsIncomplete";
            var all = new[] { m.AzureSubscription, m.AzureResourceGroup, m.AzureVmName };
            return all.Where(v => v.Length > 0).All(v => AzureName().IsMatch(v)) ? null : "InvalidVmDetails";
        }
        if (m.HyperVVmName.Length == 0) return null;
        return HyperVName().IsMatch(m.HyperVVmName) && (m.HyperVHost.Length == 0 || HyperVName().IsMatch(m.HyperVHost))
            ? null
            : "InvalidVmDetails";
    }

    /// <param name="interactive">
    /// False for background checks: they never open a browser sign-in, and throw
    /// <see cref="SignInRequiredException"/> instead when one is needed.
    /// </param>
    public static async Task<MachineStatus> GetStatusAsync(DevMachine m, bool interactive = false, CancellationToken ct = default)
    {
        if (IsLcs(m)) return await GetLcsStatusAsync(m, interactive, ct);
        if (m.Location == MachineLocation.Cloud) return await GetAzureStatusAsync(m, interactive, ct);

        var state = await RunHyperVAsync(m, "(Get-VM @vm).State.ToString()", ct);
        return state switch
        {
            "Running" => new(PowerState.Running, "PowerRunning"),
            "Saved" => new(PowerState.Stopped, "PowerSaved"),
            "Off" => new(PowerState.Stopped, "PowerOff"),
            "Starting" => new(PowerState.Busy, "PowerStarting"),
            "Saving" => new(PowerState.Busy, "PowerSaving"),
            _ => new(PowerState.Unknown, null, state)
        };
    }

    // Start and stop are always the user's own action, so they may show the sign-in.
    public static Task StartAsync(DevMachine m, CancellationToken ct = default) =>
        IsLcs(m) ? CallLcsAsync(m, HttpMethod.Post, LcsActionPath(m, "start"), true, ct) :
        m.Location == MachineLocation.Cloud ? AzureVmActionAsync(m, "start", ct) :
        RunHyperVAsync(m, "Start-VM @vm", ct);

    /// <summary>Cloud: shut down and deallocate. On-premises: save the VM's state.</summary>
    public static Task StopAsync(DevMachine m, CancellationToken ct = default) =>
        IsLcs(m) ? CallLcsAsync(m, HttpMethod.Post, LcsActionPath(m, "stop"), true, ct) :
        m.Location == MachineLocation.Cloud ? AzureVmActionAsync(m, "deallocate", ct) :
        RunHyperVAsync(m, "Save-VM @vm", ct);

    // ---------- Azure VM (Azure Resource Manager REST API) ----------

    private static async Task<MachineStatus> GetAzureStatusAsync(DevMachine m, bool interactive, CancellationToken ct)
    {
        var view = await CallArmAsync(HttpMethod.Get, await VmUrlAsync(m, interactive, ct) + "/instanceView?" + ArmComputeApi, interactive, ct);
        var power = view?["statuses"]?.AsArray()
            .Select(s => s?["code"]?.ToString() ?? "")
            .FirstOrDefault(c => c.StartsWith("PowerState/", StringComparison.OrdinalIgnoreCase))?["PowerState/".Length..] ?? "";
        return power.ToLowerInvariant() switch
        {
            "running" => new(PowerState.Running, "PowerRunning"),
            "deallocated" or "stopped" => new(PowerState.Stopped, "PowerStopped"),
            "starting" => new(PowerState.Busy, "PowerStarting"),
            "deallocating" or "stopping" => new(PowerState.Busy, "PowerStopping"),
            _ => new(PowerState.Unknown, null, power)
        };
    }

    /// <summary>Azure accepts start/deallocate at once (202) and finishes them in the background.</summary>
    private static async Task AzureVmActionAsync(DevMachine m, string action, CancellationToken ct) =>
        await CallArmAsync(HttpMethod.Post, await VmUrlAsync(m, true, ct) + "/" + action + "?" + ArmComputeApi, true, ct);

    private static async Task<string> VmUrlAsync(DevMachine m, bool interactive, CancellationToken ct)
    {
        if (Validate(m) != null || !IsManaged(m)) throw new InvalidOperationException("The Azure VM details are not valid.");
        var subscription = await ResolveSubscriptionAsync(m, interactive, ct);
        return $"{ArmResource}/subscriptions/{subscription}/resourceGroups/{Uri.EscapeDataString(m.AzureResourceGroup)}" +
               $"/providers/Microsoft.Compute/virtualMachines/{Uri.EscapeDataString(m.AzureVmName)}";
    }

    /// <summary>
    /// The subscription ID to use: the one entered (as an ID or a name), or, when the field is empty,
    /// the subscription that contains the resource group.
    /// </summary>
    private static async Task<string> ResolveSubscriptionAsync(DevMachine m, bool interactive, CancellationToken ct)
    {
        var entered = m.AzureSubscription.Trim();
        if (Guid().IsMatch(entered)) return entered;
        var cacheKey = entered + "|" + m.AzureResourceGroup;
        if (SubscriptionByGroup.TryGetValue(cacheKey, out var known)) return known;

        var list = await CallArmAsync(HttpMethod.Get, $"{ArmResource}/subscriptions?api-version=2022-12-01", interactive, ct);
        var subscriptions = list?["value"]?.AsArray()
            .Select(s => (Id: s?["subscriptionId"]?.ToString() ?? "", Name: s?["displayName"]?.ToString() ?? ""))
            .Where(s => s.Id.Length > 0).ToList() ?? new();

        if (entered.Length > 0)
        {
            var named = subscriptions.FirstOrDefault(s => string.Equals(s.Name, entered, StringComparison.OrdinalIgnoreCase));
            if (named.Id == null) throw new InvalidOperationException($"No Azure subscription named \"{entered}\" is available to your account.");
            return SubscriptionByGroup[cacheKey] = named.Id;
        }

        foreach (var (id, _) in subscriptions)
        {
            var url = $"{ArmResource}/subscriptions/{id}/resourcegroups/{Uri.EscapeDataString(m.AzureResourceGroup)}?api-version=2021-04-01";
            if (await ExistsAsync(url, interactive, ct)) return SubscriptionByGroup[cacheKey] = id;
        }
        throw new InvalidOperationException(
            $"The resource group \"{m.AzureResourceGroup}\" was not found in any Azure subscription your account can see. Enter the subscription in the machine's details.");
    }

    private static async Task<bool> ExistsAsync(string url, bool interactive, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new("Bearer", await AzureAuth.GetTokenAsync(ArmResource, interactive, ct));
        using var response = await Http.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    private static async Task<JsonNode?> CallArmAsync(HttpMethod method, string url, bool interactive, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new("Bearer", await AzureAuth.GetTokenAsync(ArmResource, interactive, ct));
        if (method == HttpMethod.Post) request.Content = new StringContent("", Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string? message = null;
            try { message = JsonNode.Parse(text)?["error"]?["message"]?.ToString(); }
            catch (System.Text.Json.JsonException) { }
            throw new InvalidOperationException($"Azure: {message ?? $"{(int)response.StatusCode} {response.ReasonPhrase}"}");
        }
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    // ---------- LCS (Lifecycle Services) environment API ----------

    private static string LcsApiBase(DevMachine m) =>
        (m.LcsApiUrl.Trim().Length == 0 ? DefaultLcsApiUrl : m.LcsApiUrl.Trim()).TrimEnd('/');

    private static string LcsActionPath(DevMachine m, string action) =>
        $"/environment/v1/{action}/project/{m.LcsProjectId}/environment/{m.LcsEnvironmentId}";

    private static async Task<MachineStatus> GetLcsStatusAsync(DevMachine m, bool interactive, CancellationToken ct)
    {
        var json = await CallLcsAsync(m, HttpMethod.Get,
            $"/environmentinfo/v1/detail/project/{m.LcsProjectId}/?environmentId={m.LcsEnvironmentId}", interactive, ct);
        var environment = json["Data"]?.AsArray().FirstOrDefault()
                          ?? throw new InvalidOperationException("LCS did not return this environment. Check the project and environment IDs.");
        var display = environment["DeploymentStatusDisplay"]?.ToString() ?? "";
        return display.ToLowerInvariant() switch
        {
            "deployed" => new(PowerState.Running, "PowerRunning"),
            "stopped" => new(PowerState.Stopped, "PowerStopped"),
            "starting" => new(PowerState.Busy, "PowerStarting"),
            "stopping" => new(PowerState.Busy, "PowerStopping"),
            _ => new(PowerState.Unknown, null, display)
        };
    }

    /// <summary>
    /// Calls the LCS API as the signed-in user. LCS answers 200 even for failures, with IsSuccess and
    /// ErrorMessage in the body; a start or stop that succeeds has only been triggered.
    /// </summary>
    private static async Task<JsonNode> CallLcsAsync(DevMachine m, HttpMethod method, string path, bool interactive, CancellationToken ct)
    {
        if (Validate(m) != null || !IsManaged(m)) throw new InvalidOperationException("The LCS environment details are not valid.");
        var token = await AzureAuth.GetTokenAsync(LcsResource, interactive, ct);

        using var request = new HttpRequestMessage(method, LcsApiBase(m) + path);
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("x-ms-version", "2017-09-15");
        if (method == HttpMethod.Post) request.Content = new StringContent("", Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if ((int)response.StatusCode == 429)
            throw new InvalidOperationException("LCS allows only one start or stop per environment every 5 minutes, and a few status checks per minute. Try again shortly.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"LCS answered {(int)response.StatusCode} {response.ReasonPhrase}. " +
                                                "Check that you are an owner or environment administrator in the LCS project.");

        var json = JsonNode.Parse(text) ?? throw new InvalidOperationException("LCS returned an empty answer.");
        if (json["IsSuccess"]?.GetValue<bool>() == false)
            throw new InvalidOperationException("LCS: " + (json["ErrorMessage"]?.ToString() ?? "the request failed."));
        return json;
    }

    // ---------- Hyper-V ----------

    private static Task<string> RunHyperVAsync(DevMachine m, string action, CancellationToken ct)
    {
        if (Validate(m) != null || !IsManaged(m)) throw new InvalidOperationException("The Hyper-V VM details are not valid.");
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

        // Errors are written to standard output with a marker: redirected PowerShell error streams
        // can arrive as CLIXML instead of readable text.
        var script = new StringBuilder()
            .AppendLine("$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'")
            .AppendLine($"$vm = @{{ Name = {Quote(m.HyperVVmName)} }}")
            .AppendLine(m.HyperVHost.Length > 0 ? $"$vm.ComputerName = {Quote(m.HyperVHost)}" : "")
            .AppendLine($"try {{ {action} }} catch {{ Write-Output ('ERROR: ' + $_.Exception.Message); exit 1 }}")
            .ToString();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded }, ct,
            notInstalledHint: "The Hyper-V PowerShell module is not installed on this computer. Turn on the Windows feature \"Hyper-V Module for Windows PowerShell\" (under Hyper-V > Hyper-V Management Tools).");
    }

    private static async Task<string> RunAsync(string file, IEnumerable<string> args, CancellationToken ct, string? notInstalledHint = null)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{file} could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var text = (await output).Trim();
        if (process.ExitCode == 0) return text;

        var problem = (await error).Trim();
        var message = text.StartsWith("ERROR: ") ? text[7..] : problem.Length > 0 ? problem : text;
        if (notInstalledHint != null && message.Contains("is not recognized", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(notInstalledHint);
        // Keep dialogs readable: PowerShell can print long error records.
        throw new InvalidOperationException(string.Join(Environment.NewLine, message.Split('\n').Take(6)).Trim());
    }
}
