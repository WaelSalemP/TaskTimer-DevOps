using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskTimer.Models;

namespace TaskTimer.Services;

public interface IWorkItemProvider : IDisposable
{
    Task TestAsync(CancellationToken ct = default);
    /// <summary>
    /// Items assigned to the user (or to the configured team), plus their ancestors (IsListed = false) so the caller can
    /// show the hierarchy. Without a sprint only open items are returned; with a sprint, every item
    /// of that sprint including finished ones, so ended sprints are not empty.
    /// </summary>
    Task<List<WorkItem>> GetMyItemsAsync(string sprint, CancellationToken ct = default);
    /// <summary>All sprints on the server, ended ones included. Empty when the provider cannot list them.</summary>
    Task<List<SprintInfo>> GetSprintsAsync(CancellationToken ct = default);
    /// <summary>
    /// Adds <paramref name="hours"/> of completed time to the item on the server and, when
    /// <paramref name="newState"/> is given, changes its status in the same update.
    /// <paramref name="reduceRemaining"/> also lowers the remaining work by the same hours.
    /// </summary>
    Task LogTimeAsync(WorkItem item, double hours, string? newState, bool reduceRemaining, CancellationToken ct = default);
    /// <summary>Creates a task assigned to the signed-in user and returns it as loaded from the server.</summary>
    Task<WorkItem> CreateTaskAsync(NewTask task, CancellationToken ct = default);
    /// <summary>Applies the given changes on the server and to <paramref name="item"/>.</summary>
    Task UpdateTaskAsync(WorkItem item, TaskChanges changes, CancellationToken ct = default);
    /// <summary>The statuses an item of this type can be set to.</summary>
    Task<List<string>> GetStatesAsync(WorkItem item, CancellationToken ct = default);
}

public static class ProviderFactory
{
    public static bool IsConfigured(AppSettings s) =>
        !string.IsNullOrWhiteSpace(s.PatProtected) &&
        (s.Provider == ProviderKind.GitHub || !string.IsNullOrWhiteSpace(s.Url));

    public static IWorkItemProvider Create(AppSettings s) => s.Provider switch
    {
        ProviderKind.GitHub => new GitHubProvider(s),
        _ => new AzureDevOpsProvider(s)
    };
}

public abstract class HttpProviderBase : IDisposable
{
    protected readonly HttpClient Http = RetryHandler.CreateClient(TimeSpan.FromSeconds(30));

    protected async Task<JsonNode> SendAsync(HttpMethod method, string url, object? body, string contentType,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        // Azure DevOps answers 203 + sign-in HTML when the PAT is invalid or expired.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation)
            throw new InvalidOperationException(
                "Authentication failed. Check the personal access token and its scopes, or for GitHub sign in again in the settings.");

        if (!response.IsSuccessStatusCode)
        {
            string? message = null;
            try
            {
                var error = JsonNode.Parse(text);
                message = error?["message"]?.ToString();
                // GitHub explains a "Validation Failed" in a list of errors.
                var details = error?["errors"]?.AsArray()
                    .Select(e => e is JsonObject o ? o["message"]?.ToString() ?? o["code"]?.ToString() : e?.ToString())
                    .Where(d => !string.IsNullOrEmpty(d)).ToList();
                if (details is { Count: > 0 }) message += " - " + string.Join("; ", details);
            }
            catch (JsonException) { }
            throw new InvalidOperationException(
                $"{(int)response.StatusCode} {response.ReasonPhrase}: {message ?? text}");
        }

        // Deletes and some updates answer with no content at all.
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) ?? new JsonObject();
    }

    /// <summary>Parses an ISO date from the API into local time.</summary>
    protected static DateTime? ParseDate(JsonNode? value) =>
        DateTime.TryParse(value?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var date)
            ? date.ToLocalTime()
            : null;

    public void Dispose() => Http.Dispose();
}

public sealed class AzureDevOpsProvider : HttpProviderBase, IWorkItemProvider
{
    private const string Api = "api-version=7.0";
    private const string CompletedField = "Microsoft.VSTS.Scheduling.CompletedWork";
    private const string RemainingField = "Microsoft.VSTS.Scheduling.RemainingWork";
    private const int MaxParentDepth = 6;

    private readonly AppSettings _settings;
    private readonly string _org;
    private List<JsonNode?>? _fieldDefinitions;

    public AzureDevOpsProvider(AppSettings settings)
    {
        _settings = settings;
        _org = settings.Url.Trim().TrimEnd('/');
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + SettingsStore.GetPat(settings)));
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task TestAsync(CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, WiqlUrl + "&$top=1", new { query = BuildQuery("") }, "application/json", ct);

    private string WiqlUrl
    {
        get
        {
            var project = _settings.Project.Trim();
            var scope = project.Length == 0 ? "" : "/" + Uri.EscapeDataString(project);
            return $"{_org}{scope}/_apis/wit/wiql?{Api}";
        }
    }

    /// <summary>Me, plus the members of the configured team, plus the individually listed team accounts.</summary>
    private string AssigneeClause()
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var conditions = new List<string> { "[System.AssignedTo] = @Me" };

        var team = _settings.Team.Trim();
        if (team.Length > 0)
        {
            // WIQL addresses a team as the group "[Project]\Team".
            if (!team.StartsWith('['))
            {
                var project = _settings.Project.Trim();
                if (project.Length == 0)
                    throw new InvalidOperationException(
                        "To load the team's items, set the project in the connection settings or enter the team as [Project]\\Team.");
                team = $"[{project}]\\{team}";
            }
            conditions.Add("[System.AssignedTo] IN GROUP " + Quote(team));
        }

        var emails = _settings.TeamEmails.Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
        if (emails.Count > 0)
            conditions.Add($"[System.AssignedTo] IN ({string.Join(", ", emails.Select(Quote))})");

        return conditions.Count == 1 ? conditions[0] + " " : "(" + string.Join(" OR ", conditions) + ") ";
    }

    private string BuildQuery(string sprint)
    {
        var projectClause = _settings.Project.Trim().Length == 0 ? "" : "AND [System.TeamProject] = @project ";
        var scopeClause = sprint.Length == 0
            ? "AND [System.State] NOT IN ('Closed', 'Done', 'Removed', 'Completed') "
            : $"AND [System.IterationPath] = '{sprint.Replace("'", "''")}' AND [System.State] <> 'Removed' ";
        return "SELECT [System.Id] FROM WorkItems WHERE " + AssigneeClause() + projectClause + scopeClause +
               "ORDER BY [System.ChangedDate] DESC";
    }

    public async Task<List<WorkItem>> GetMyItemsAsync(string sprint, CancellationToken ct = default)
    {
        var wiql = await SendAsync(HttpMethod.Post, WiqlUrl, new { query = BuildQuery(sprint) }, "application/json", ct);
        var ids = wiql["workItems"]?.AsArray().Select(w => w!["id"]!.GetValue<int>()).ToList() ?? new List<int>();

        var result = await LoadAsync(ids, isListed: true, ct);

        // Walk up the parent links (Task -> Story -> Feature -> Epic) one level per round trip.
        var known = result.Select(i => i.Key).ToHashSet();
        var level = result;
        for (var depth = 0; depth < MaxParentDepth; depth++)
        {
            var missing = level.Select(i => i.ParentKey).Where(k => k.Length > 0 && known.Add(k))
                .Select(k => int.Parse(k["ado:".Length..], CultureInfo.InvariantCulture)).ToList();
            if (missing.Count == 0) break;
            level = await LoadAsync(missing, isListed: false, ct);
            result.AddRange(level);
        }
        return result;
    }

    /// <summary>
    /// Turns a configured field into its reference name. A setting may hold either the display name
    /// ("Customer") or the reference name ("Custom.Customer"), but work item values are only ever
    /// keyed by reference name. Returns "" when nothing is configured and null when no field matches.
    /// </summary>
    private async Task<string?> ResolveFieldAsync(string configured, CancellationToken ct)
    {
        configured = configured.Trim();
        if (configured.Length == 0) return "";

        if (_fieldDefinitions == null)
        {
            var list = await SendAsync(HttpMethod.Get, $"{_org}/_apis/wit/fields?{Api}", null, "application/json", ct);
            _fieldDefinitions = list["value"]?.AsArray().Where(f => f != null).ToList() ?? new List<JsonNode?>();
        }
        bool Is(JsonNode? field, string property) =>
            string.Equals(field![property]?.ToString(), configured, StringComparison.OrdinalIgnoreCase);

        var match = _fieldDefinitions.FirstOrDefault(f => Is(f, "referenceName")) ??
                    _fieldDefinitions.FirstOrDefault(f => Is(f, "name"));
        return match?["referenceName"]?.ToString();
    }

    private async Task<string> ResolveCustomerFieldAsync(CancellationToken ct) =>
        await ResolveFieldAsync(_settings.CustomerField, ct) ??
        throw new InvalidOperationException(
            $"The customer field \"{_settings.CustomerField.Trim()}\" was not found in Azure DevOps. Check its name in the connection settings.");

    /// <summary>Text of a field value; identity fields come back as an object with a display name.</summary>
    private static string FieldText(JsonNode? value) =>
        value is JsonObject person ? person["displayName"]?.ToString() ?? "" : value?.ToString() ?? "";

    private async Task<List<WorkItem>> LoadAsync(IReadOnlyCollection<int> ids, bool isListed, CancellationToken ct)
    {
        var fields = new List<string>
        {
            "System.Id", "System.Title", "System.WorkItemType", "System.State", "System.TeamProject",
            "System.IterationPath", "System.Parent", "System.AssignedTo", "System.CreatedDate", CompletedField, RemainingField
        };
        var customerField = await ResolveCustomerFieldAsync(ct);
        if (customerField.Length > 0 && !fields.Contains(customerField, StringComparer.OrdinalIgnoreCase))
            fields.Add(customerField);

        // Not every organization has a delivery date field; without one the column simply stays empty.
        var deliveryField = await ResolveFieldAsync(_settings.DeliveryDateField, ct) ?? "";
        if (deliveryField.Length > 0 && !fields.Contains(deliveryField, StringComparer.OrdinalIgnoreCase))
            fields.Add(deliveryField);

        var result = new List<WorkItem>();
        foreach (var chunk in ids.Chunk(200)) // workitemsbatch limit
        {
            var body = new
            {
                ids = chunk,
                fields,
                errorPolicy = "omit"
            };
            var batch = await SendAsync(HttpMethod.Post, $"{_org}/_apis/wit/workitemsbatch?{Api}", body,
                "application/json", ct);
            foreach (var node in batch["value"]?.AsArray() ?? new JsonArray())
            {
                var f = node?["fields"];
                if (f == null) continue;
                var id = node!["id"]!.ToString();
                var parent = f["System.Parent"]?.ToString();
                result.Add(new WorkItem
                {
                    ParentKey = string.IsNullOrEmpty(parent) ? "" : "ado:" + parent,
                    IsListed = isListed,
                    Key = "ado:" + id,
                    Url = $"{_org}/{Uri.EscapeDataString(f["System.TeamProject"]?.ToString() ?? "")}/_workitems/edit/{id}",
                    Id = id,
                    Title = f["System.Title"]?.ToString() ?? "",
                    Type = f["System.WorkItemType"]?.ToString() ?? "",
                    State = f["System.State"]?.ToString() ?? "",
                    Project = f["System.TeamProject"]?.ToString() ?? "",
                    Sprint = f["System.IterationPath"]?.ToString() ?? "",
                    AssignedTo = f["System.AssignedTo"]?["displayName"]?.ToString() ?? "",
                    AssignedToAccount = f["System.AssignedTo"]?["uniqueName"]?.ToString() ?? "",
                    Customer = customerField.Length == 0 ? "" : FieldText(f[customerField]),
                    CreatedDate = ParseDate(f["System.CreatedDate"]),
                    DeliveryDate = deliveryField.Length == 0 ? null : ParseDate(f[deliveryField]),
                    CompletedWork = f[CompletedField]?.GetValue<double>(),
                    RemainingWork = f[RemainingField]?.GetValue<double>()
                });
            }
        }
        return result;
    }

    public async Task<List<SprintInfo>> GetSprintsAsync(CancellationToken ct = default)
    {
        var projects = new List<string>();
        var configured = _settings.Project.Trim();
        if (configured.Length > 0)
            projects.Add(configured);
        else
        {
            try
            {
                var list = await SendAsync(HttpMethod.Get, $"{_org}/_apis/projects?$top=200&{Api}", null, "application/json", ct);
                projects.AddRange(list["value"]?.AsArray().Select(p => p!["name"]!.ToString()) ?? []);
            }
            catch (InvalidOperationException)
            {
                // A token limited to work items cannot list projects; the caller then falls back to
                // the sprints of the loaded items instead of failing the whole sync.
                return new List<SprintInfo>();
            }
        }

        // One request per project: run them a few at a time, or a large organization takes minutes.
        var result = new List<SprintInfo>();
        foreach (var batch in projects.Chunk(8))
        {
            var roots = await Task.WhenAll(batch.Select(project => GetIterationTreeAsync(project, ct)));
            foreach (var root in roots)
                if (root != null) AddIterations(root, "", result);
        }
        return result;
    }

    private async Task<JsonNode?> GetIterationTreeAsync(string project, CancellationToken ct)
    {
        try
        {
            var url = $"{_org}/{Uri.EscapeDataString(project)}/_apis/wit/classificationnodes/iterations?$depth=10&{Api}";
            return await SendAsync(HttpMethod.Get, url, null, "application/json", ct);
        }
        catch (InvalidOperationException)
        {
            return null; // a project the token cannot read must not hide the sprints of the others
        }
    }

    /// <summary>Flattens the iteration tree into iteration paths as work items store them ("Project\Sprint 1").</summary>
    private static void AddIterations(JsonNode node, string parentPath, List<SprintInfo> result)
    {
        var name = node["name"]?.ToString() ?? "";
        var path = parentPath.Length == 0 ? name : parentPath + "\\" + name;

        var today = DateTime.UtcNow.Date;
        var isCurrent =
            DateTime.TryParse(node["attributes"]?["startDate"]?.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var start) &&
            DateTime.TryParse(node["attributes"]?["finishDate"]?.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var finish) &&
            start.Date <= today && today <= finish.Date;
        result.Add(new SprintInfo(path, isCurrent));

        foreach (var child in node["children"]?.AsArray() ?? new JsonArray())
            if (child != null) AddIterations(child, path, result);
    }

    private string? _myAccount;

    /// <summary>The signed-in user's account name, to assign new tasks to them.</summary>
    private async Task<string> GetMyAccountAsync(CancellationToken ct)
    {
        if (_myAccount != null) return _myAccount;
        var data = await SendAsync(HttpMethod.Get, $"{_org}/_apis/connectionData", null, "application/json", ct);
        var user = data["authenticatedUser"];
        return _myAccount = user?["properties"]?["Account"]?["$value"]?.ToString()
                            ?? user?["providerDisplayName"]?.ToString() ?? "";
    }

    public async Task<WorkItem> CreateTaskAsync(NewTask task, CancellationToken ct = default)
    {
        var ops = new List<object> { new { op = "add", path = "/fields/System.Title", value = task.Title } };
        var assignee = task.AssignedTo ?? await GetMyAccountAsync(ct);
        if (assignee.Length > 0) ops.Add(new { op = "add", path = "/fields/System.AssignedTo", value = assignee });
        if (task.Sprint.Length > 0) ops.Add(new { op = "add", path = "/fields/System.IterationPath", value = task.Sprint });
        if (task.Description.Length > 0)
        {
            // The description is HTML; keep the user's line breaks and show their text literally.
            var html = System.Net.WebUtility.HtmlEncode(task.Description).Replace("\r\n", "\n").Replace("\n", "<br>");
            ops.Add(new { op = "add", path = "/fields/System.Description", value = html });
        }
        if (task.RemainingWork is > 0) ops.Add(new { op = "add", path = "/fields/" + RemainingField, value = task.RemainingWork });
        if (task.ParentId != null)
            ops.Add(new
            {
                op = "add", path = "/relations/-",
                value = new { rel = "System.LinkTypes.Hierarchy-Reverse", url = $"{_org}/_apis/wit/workItems/{task.ParentId}" }
            });

        var url = $"{_org}/{Uri.EscapeDataString(task.Project)}/_apis/wit/workitems/${Uri.EscapeDataString(task.Type)}?{Api}";
        var created = await SendAsync(HttpMethod.Post, url, ops, "application/json-patch+json", ct);
        var id = created["id"]!.GetValue<int>();
        return (await LoadAsync(new[] { id }, isListed: true, ct)).First();
    }

    public async Task UpdateTaskAsync(WorkItem item, TaskChanges changes, CancellationToken ct = default)
    {
        var ops = new List<object>();
        if (changes.Title != null) ops.Add(new { op = "add", path = "/fields/System.Title", value = changes.Title });
        if (changes.State != null) ops.Add(new { op = "add", path = "/fields/System.State", value = changes.State });
        if (changes.RemainingWork != null) ops.Add(new { op = "add", path = "/fields/" + RemainingField, value = changes.RemainingWork });
        if (changes.AssignedTo != null) ops.Add(new { op = "add", path = "/fields/System.AssignedTo", value = changes.AssignedTo });
        if (ops.Count == 0) return;

        await SendAsync(HttpMethod.Patch, $"{_org}/_apis/wit/workitems/{item.Id}?{Api}", ops, "application/json-patch+json", ct);

        // Re-read: the server resolves the assignee to a person and may adjust other fields by rule.
        var fresh = (await LoadAsync(new[] { int.Parse(item.Id, CultureInfo.InvariantCulture) }, item.IsListed, ct)).FirstOrDefault();
        if (fresh == null) return;
        item.Title = fresh.Title;
        item.State = fresh.State;
        item.RemainingWork = fresh.RemainingWork;
        item.CompletedWork = fresh.CompletedWork;
        item.AssignedTo = fresh.AssignedTo;
        item.AssignedToAccount = fresh.AssignedToAccount;
    }

    public async Task<List<string>> GetStatesAsync(WorkItem item, CancellationToken ct = default)
    {
        var url = $"{_org}/{Uri.EscapeDataString(item.Project)}/_apis/wit/workitemtypes/{Uri.EscapeDataString(item.Type)}/states?{Api}";
        var result = await SendAsync(HttpMethod.Get, url, null, "application/json", ct);
        return result["value"]?.AsArray().Select(s => s?["name"]?.ToString() ?? "").Where(n => n.Length > 0).ToList()
               ?? new List<string>();
    }

    public async Task LogTimeAsync(WorkItem item, double hours, string? newState, bool reduceRemaining, CancellationToken ct = default)
    {
        // Re-read so we add to the server's current value, and guard the write with the revision.
        var url = $"{_org}/_apis/wit/workitems/{item.Id}?{Api}";
        var current = await SendAsync(HttpMethod.Get, url, null, "application/json", ct);
        var fields = current["fields"];
        var completed = fields?[CompletedField]?.GetValue<double>() ?? 0;
        var remaining = fields?[RemainingField]?.GetValue<double>();

        var newCompleted = Math.Round(completed + hours, 2);
        var ops = new List<object>
        {
            new { op = "test", path = "/rev", value = current["rev"]!.GetValue<int>() },
            new { op = "add", path = "/fields/" + CompletedField, value = newCompleted }
        };

        double? newRemaining = remaining;
        if (reduceRemaining && remaining.HasValue)
        {
            newRemaining = Math.Round(Math.Max(0, remaining.Value - hours), 2);
            ops.Add(new { op = "add", path = "/fields/" + RemainingField, value = newRemaining });
        }

        if (_settings.AddComment)
            ops.Add(new
            {
                op = "add", path = "/fields/System.History",
                value = string.Format(CultureInfo.InvariantCulture, "Logged {0:0.00} h via Task Timer.", hours)
            });

        // Same patch as the hours: if the server rejects the status change, no time is added either.
        if (newState != null)
            ops.Add(new { op = "add", path = "/fields/System.State", value = newState });

        await SendAsync(HttpMethod.Patch, url, ops, "application/json-patch+json", ct);
        item.CompletedWork = newCompleted;
        item.RemainingWork = newRemaining;
        if (newState != null) item.State = newState;
    }
}

public sealed class GitHubProvider : HttpProviderBase, IWorkItemProvider
{
    private readonly string _api;

    public GitHubProvider(AppSettings settings)
    {
        _api = ApiUrl(settings);
        Configure(Http, settings);
    }

    public static string ApiUrl(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Url) ? "https://api.github.com" : settings.Url.Trim().TrimEnd('/');

    /// <summary>The headers every GitHub API call needs, with the signed-in user's token.</summary>
    public static void Configure(HttpClient http, AppSettings settings)
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TaskTimer/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SettingsStore.GetPat(settings));
    }

    public async Task TestAsync(CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Get, $"{_api}/user", null, "application/json", ct);

    // Milestones stand in for sprints; they are filtered on the client, so the argument is unused here.
    public async Task<List<WorkItem>> GetMyItemsAsync(string sprint, CancellationToken ct = default)
    {
        var result = new List<WorkItem>();
        for (var page = 1; page <= 10; page++)
        {
            var issues = (await SendAsync(HttpMethod.Get,
                $"{_api}/issues?filter=assigned&state=open&per_page=100&page={page}", null, "application/json",
                ct)).AsArray();

            foreach (var issue in issues)
            {
                if (issue == null || issue["pull_request"] != null) continue; // the endpoint also returns PRs
                var repo = issue["repository"]?["full_name"]?.ToString() ?? "";
                var number = issue["number"]!.ToString();
                result.Add(new WorkItem
                {
                    Key = $"gh:{repo}#{number}",
                    Url = issue["html_url"]?.ToString() ?? "",
                    Id = number,
                    Title = issue["title"]?.ToString() ?? "",
                    Type = "Issue",
                    State = issue["state"]?.ToString() ?? "",
                    Project = repo,
                    Customer = repo.Split('/')[0],
                    CreatedDate = ParseDate(issue["created_at"]),
                    DeliveryDate = ParseDate(issue["milestone"]?["due_on"]),
                    AssignedTo = string.Join(", ", issue["assignees"]?.AsArray().Select(a => a?["login"]?.ToString()) ?? []),
                    Sprint = issue["milestone"]?["title"]?.ToString() ?? ""
                });
            }
            if (issues.Count < 100) break;
        }
        return result;
    }

    public Task<List<SprintInfo>> GetSprintsAsync(CancellationToken ct = default) => Task.FromResult(new List<SprintInfo>());

    // GitHub issues have no time-tracking field, so the time is recorded as an issue comment.
    // Issues have no remaining work, so reduceRemaining does not apply here.
    public async Task LogTimeAsync(WorkItem item, double hours, string? newState, bool reduceRemaining, CancellationToken ct = default)
    {
        var body = string.Format(CultureInfo.InvariantCulture, "⏱ Logged {0:0.00} h via Task Timer.", hours);
        await SendAsync(HttpMethod.Post, $"{_api}/repos/{item.Project}/issues/{item.Id}/comments", new { body },
            "application/json", ct);

        if (newState == null) return;
        await SendAsync(HttpMethod.Patch, $"{_api}/repos/{item.Project}/issues/{item.Id}", new { state = newState },
            "application/json", ct);
        item.State = newState;
    }

    public Task<List<string>> GetStatesAsync(WorkItem item, CancellationToken ct = default) =>
        Task.FromResult(new List<string> { "open", "closed" });

    // Creating issues needs a repository to put them in; the app only offers it for Azure DevOps.
    public Task<WorkItem> CreateTaskAsync(NewTask task, CancellationToken ct = default) =>
        throw new NotSupportedException("New tasks can be created in Azure DevOps or locally, not in GitHub.");

    // GitHub issues have a title and an open/closed state, but no remaining work.
    public async Task UpdateTaskAsync(WorkItem item, TaskChanges changes, CancellationToken ct = default)
    {
        if (changes.Title == null && changes.State == null) return;
        var body = new Dictionary<string, string>();
        if (changes.Title != null) body["title"] = changes.Title;
        if (changes.State != null) body["state"] = changes.State;
        await SendAsync(HttpMethod.Patch, $"{_api}/repos/{item.Project}/issues/{item.Id}", body, "application/json", ct);
        item.Title = changes.Title ?? item.Title;
        item.State = changes.State ?? item.State;
    }
}
