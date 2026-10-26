using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace TaskTimer.Services;

/// <summary>A code the user types on GitHub to approve this app (step one of the device flow).</summary>
public sealed record GitHubDeviceCode(string DeviceCode, string UserCode, string VerificationUri, int IntervalSeconds,
    DateTime ExpiresUtc);

/// <summary>
/// Signs the user in to GitHub in the browser with the OAuth device flow, so no token has to be created
/// and pasted: the user signs in on github.com with their own email and password (and two-factor code),
/// enters the code shown by the app, and the app receives an access token. GitHub's API no longer accepts
/// an email and password directly.
/// </summary>
public static class GitHubAuth
{
    // The GitHub CLI's public OAuth app, which has the device flow enabled, so no app registration is needed.
    private const string ClientId = "178c6fc778ccc68e1d6a";
    // Issues, pull requests, branches and repositories, private ones too; read:org lists organization
    // repositories, and delete_repo is needed to delete a repository from the Repositories window.
    private const string Scopes = "repo read:user read:org delete_repo";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = RetryHandler.CreateClient(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TaskTimer/1.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    /// <summary>The sign-in site for an API URL: api.github.com belongs to github.com, a server's /api/v3 to the server.</summary>
    public static string WebUrlFor(string apiUrl)
    {
        var api = string.IsNullOrWhiteSpace(apiUrl) ? "https://api.github.com" : apiUrl.Trim().TrimEnd('/');
        if (api.Equals("https://api.github.com", StringComparison.OrdinalIgnoreCase)) return "https://github.com";
        return api.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase) ? api[..^"/api/v3".Length] : api;
    }

    public static async Task<GitHubDeviceCode> RequestCodeAsync(string apiUrl, CancellationToken ct = default)
    {
        var json = await PostAsync($"{WebUrlFor(apiUrl)}/login/device/code",
            new Dictionary<string, string> { ["client_id"] = ClientId, ["scope"] = Scopes }, ct);
        if (json["error"] != null) throw Failure(json);
        return new GitHubDeviceCode(
            json["device_code"]!.ToString(), json["user_code"]!.ToString(), json["verification_uri"]!.ToString(),
            (int?)json["interval"] ?? 5, DateTime.UtcNow.AddSeconds((int?)json["expires_in"] ?? 900));
    }

    /// <summary>Waits until the user has approved the code on GitHub and returns the access token.</summary>
    public static async Task<string> WaitForTokenAsync(string apiUrl, GitHubDeviceCode code, CancellationToken ct = default)
    {
        var interval = code.IntervalSeconds;
        while (DateTime.UtcNow < code.ExpiresUtc)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var json = await PostAsync($"{WebUrlFor(apiUrl)}/login/oauth/access_token", new Dictionary<string, string>
            {
                ["client_id"] = ClientId, ["device_code"] = code.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            }, ct);

            if (json["access_token"]?.ToString() is { Length: > 0 } token) return token;
            switch (json["error"]?.ToString())
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval = (int?)json["interval"] ?? interval + 5;
                    continue;
                default:
                    throw Failure(json);
            }
        }
        throw new InvalidOperationException(Loc.T("GitHubCodeExpired"));
    }

    /// <summary>The login name of the account a token belongs to.</summary>
    public static async Task<string> GetLoginAsync(string apiUrl, string token, CancellationToken ct = default)
    {
        var api = string.IsNullOrWhiteSpace(apiUrl) ? "https://api.github.com" : apiUrl.Trim().TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{api}/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["login"]?.ToString() ?? "";
    }

    private static async Task<JsonNode> PostAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await Http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            if (JsonNode.Parse(text) is { } json) return json;
        }
        catch (System.Text.Json.JsonException) { }
        throw new InvalidOperationException($"GitHub sign-in failed: {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    private static InvalidOperationException Failure(JsonNode json) => new(json["error"]?.ToString() switch
    {
        "access_denied" => Loc.T("GitHubDenied"),
        "expired_token" => Loc.T("GitHubCodeExpired"),
        _ => "GitHub sign-in failed: " + (json["error_description"] ?? json["error"])
    });
}
