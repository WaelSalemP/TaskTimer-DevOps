using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using TaskTimer.Models;

namespace TaskTimer.Services;

/// <param name="HeadCommit">GitHub: the source branch's latest commit, which checks and line comments refer to.</param>
public sealed record PullRequest(
    int Id, string Title, string Project, string RepositoryId, string RepositoryName,
    string SourceBranch, string TargetBranch, string CreatedBy, string CreatedById, DateTime? Created,
    bool IsDraft, string MergeStatus, int MyVote, bool IAmReviewer, string Reviewers, string WebUrl, string ProjectId,
    string HeadCommit = "")
{
    /// <summary>Unique across repositories: GitHub numbers pull requests per repository.</summary>
    public string Key => $"{RepositoryId}#{Id}";
}

/// <summary>One branch policy evaluated on a pull request (build, reviewers, linked work items, comments...).</summary>
/// <param name="Status">"approved" (passed), "rejected" (failed), "running", "queued", "broken" or "notApplicable".</param>
/// <param name="IsBlocking">Required: the pull request cannot be completed until it passes.</param>
public sealed record PolicyCheck(string Name, string Status, bool IsBlocking)
{
    public bool Passed => Status == "approved";
    public bool Failed => Status is "rejected" or "broken";
    public bool Pending => Status is "running" or "queued";
}

/// <param name="ChangeType">Azure DevOps change type, e.g. "add", "edit", "delete", "rename" or "edit, rename".</param>
public sealed record PullRequestFileChange(string Path, string OriginalPath, string ChangeType)
{
    public bool IsAdd => ChangeType.Contains("add", StringComparison.OrdinalIgnoreCase);
    public bool IsDelete => ChangeType.Contains("delete", StringComparison.OrdinalIgnoreCase);

    public override string ToString()
    {
        var mark = IsAdd ? "A" : IsDelete ? "D" : ChangeType.Contains("rename", StringComparison.OrdinalIgnoreCase) ? "R" : "M";
        // File name first: paths are long and share most of their beginning.
        var slash = Path.LastIndexOf('/');
        // A file at the root ("/README") has no folder to show.
        return slash <= 0 ? $"{mark}  {Path.TrimStart('/')}" : $"{mark}  {Path[(slash + 1)..]}   ({Path[..slash]})";
    }
}

/// <summary>A comment from a pull request thread. <see cref="FilePath"/> is empty for general comments.</summary>
/// <param name="LeftSide">True when anchored to a line of the old (target) version, e.g. a removed line.</param>
public sealed record PullRequestComment(string FilePath, int Line, bool LeftSide, string Author, string Text, DateTime? Date, string Status);

/// <summary>A work item as shown in link lists: enough to recognise it and open it.</summary>
public sealed record LinkedWorkItem(string Id, string Type, string Title, string State, string AssignedTo, string WebUrl)
{
    public override string ToString() => $"{Type} {Id} - {Title}";
}

/// <param name="Id">Azure DevOps: the repository's GUID. GitHub: "owner/name".</param>
/// <param name="Project">Azure DevOps: the team project. GitHub: the owner (user or organization).</param>
/// <param name="Visibility">GitHub: "public", "private" or "internal". Empty for Azure DevOps.</param>
/// <param name="CanDelete">The signed-in user may delete it; true when that could not be checked (the server decides).</param>
/// <param name="CanRename">The signed-in user may rename it; true when that could not be checked.</param>
/// <param name="DeleteNeedsSignIn">GitHub: the user is an admin, but signed in before the app asked for permission to delete.</param>
public sealed record GitRepository(string Id, string Name, string DefaultBranch, string Project = "", string Visibility = "",
    string WebUrl = "", bool CanDelete = true, bool CanRename = true, bool DeleteNeedsSignIn = false)
{
    public override string ToString() => Name;
}

/// <summary>How a pull request is completed.</summary>
/// <param name="MergeStrategy">"noFastForward", "squash", "rebase" or "rebaseMerge" (Azure DevOps names).</param>
public sealed record PullRequestCompletion(string MergeStrategy, bool DeleteSourceBranch, bool CompleteWorkItems, string MergeMessage);

/// <summary>What a provider's pull requests and repositories support, so the windows only offer what works.</summary>
/// <param name="UsesProjects">Repositories live in team projects (Azure DevOps), not under owners (GitHub).</param>
/// <param name="WorkItems">Pull requests can be linked to work items.</param>
/// <param name="VoteLevels">Votes beyond approve and reject: "with suggestions" and "waiting for author".</param>
/// <param name="MergeStrategies">Merge types offered when completing, by their Azure DevOps names.</param>
/// <param name="PrivateRepositories">New repositories can be made public or private.</param>
public sealed record PullRequestFeatures(bool UsesProjects, bool WorkItems, bool VoteLevels, IReadOnlyList<string> MergeStrategies,
    bool PrivateRepositories);

/// <summary>Pull requests and repositories of one provider (Azure Repos or GitHub).</summary>
public interface IPullRequestService : IDisposable
{
    /// <summary>The signed-in user's ID (Azure DevOps) or login name (GitHub), used for votes and "mine".</summary>
    Task<string> GetMyIdAsync(CancellationToken ct = default);
    /// <summary>Open pull requests, newest first; only in the given repositories when <paramref name="repositoryIds"/> is not empty.</summary>
    Task<List<PullRequest>> GetActiveAsync(IEnumerable<string> projects, IReadOnlyCollection<string> repositoryIds, CancellationToken ct = default);
    Task ApproveAsync(PullRequest pr, CancellationToken ct = default);
    /// <param name="vote">10 approved, 5 approved with suggestions, 0 reset, -5 waiting for author, -10 rejected.</param>
    /// <param name="comment">Sent with the vote; GitHub needs one to request changes.</param>
    Task VoteAsync(PullRequest pr, int vote, string? comment = null, CancellationToken ct = default);
    Task<List<PolicyCheck>> GetChecksAsync(PullRequest pr, CancellationToken ct = default);
    Task<List<LinkedWorkItem>> GetLinkedWorkItemsAsync(PullRequest pr, CancellationToken ct = default);
    Task<List<LinkedWorkItem>> SearchWorkItemsAsync(string project, string text, CancellationToken ct = default);
    Task<(List<PullRequestFileChange> Files, string NewCommit, string BaseCommit)> GetChangesAsync(PullRequest pr, CancellationToken ct = default);
    Task<string?> GetFileTextAsync(PullRequest pr, string path, string commit, CancellationToken ct = default);
    Task<List<PullRequestComment>> GetCommentsAsync(PullRequest pr, CancellationToken ct = default);
    Task AddCommentAsync(PullRequest pr, string text, string? filePath, int line, bool leftSide, CancellationToken ct = default);
    Task CompleteAsync(PullRequest pr, PullRequestCompletion options, CancellationToken ct = default);
    /// <param name="project">Azure DevOps: the team project. GitHub: ignored, every repository the user can reach is listed.</param>
    Task<List<GitRepository>> GetRepositoriesAsync(string project, CancellationToken ct = default);
    Task<List<string>> GetBranchesAsync(string project, string repositoryId, CancellationToken ct = default);
    /// <returns>The new pull request's ID and web address.</returns>
    Task<(int Id, string WebUrl)> CreateAsync(string project, GitRepository repository, string sourceBranch, string targetBranch,
        string title, string description, bool isDraft, IReadOnlyCollection<string> workItemIds, CancellationToken ct = default);

    // ---------- repository management ----------

    /// <param name="project">Azure DevOps: the team project. GitHub: the owner; empty or the user's own login = a personal repository.</param>
    Task<GitRepository> CreateRepositoryAsync(string project, string name, bool isPrivate, CancellationToken ct = default);
    Task RenameRepositoryAsync(GitRepository repository, string newName, CancellationToken ct = default);
    /// <summary>Azure DevOps moves the repository to its recycle bin; GitHub deletes it for good.</summary>
    Task DeleteRepositoryAsync(GitRepository repository, CancellationToken ct = default);
    Task CreateBranchAsync(GitRepository repository, string name, string fromBranch, CancellationToken ct = default);
    Task DeleteBranchAsync(GitRepository repository, string name, CancellationToken ct = default);
}

public static class PullRequestServices
{
    private static readonly PullRequestFeatures AzureFeatures =
        new(true, true, true, new[] { "noFastForward", "squash", "rebase", "rebaseMerge" }, false);

    private static readonly PullRequestFeatures GitHubFeatures =
        new(false, false, false, new[] { "noFastForward", "squash", "rebase" }, true);

    public static PullRequestFeatures Features(AppSettings settings) =>
        settings.Provider == ProviderKind.GitHub ? GitHubFeatures : AzureFeatures;

    public static IPullRequestService Create(AppSettings settings) =>
        settings.Provider == ProviderKind.GitHub ? new GitHubPullRequestService(settings) : new PullRequestService(settings);
}

/// <summary>Azure Repos pull requests through the Azure DevOps REST API, with the same token as the work items.</summary>
public sealed class PullRequestService : HttpProviderBase, IPullRequestService
{
    private const string Api = "api-version=7.0";
    public const int VoteApproved = 10;

    private readonly string _org;
    private string? _myId;

    public PullRequestService(AppSettings settings)
    {
        _org = settings.Url.Trim().TrimEnd('/');
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + SettingsStore.GetPat(settings)));
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private string ProjectUrl(string project) => $"{_org}/{Uri.EscapeDataString(project)}";

    private string PullRequestUrl(PullRequest pr) =>
        $"{ProjectUrl(pr.Project)}/_apis/git/repositories/{pr.RepositoryId}/pullrequests/{pr.Id}";

    /// <summary>The signed-in user's Azure DevOps ID, used for votes and "mine".</summary>
    public async Task<string> GetMyIdAsync(CancellationToken ct = default)
    {
        if (_myId != null) return _myId;
        var data = await SendAsync(HttpMethod.Get, $"{_org}/_apis/connectionData", null, "application/json", ct);
        return _myId = data["authenticatedUser"]?["id"]?.ToString() ?? "";
    }

    /// <summary>Active (open) pull requests in the given projects, newest first.</summary>
    public async Task<List<PullRequest>> GetActiveAsync(IEnumerable<string> projects, IReadOnlyCollection<string> repositoryIds,
        CancellationToken ct = default)
    {
        var me = await GetMyIdAsync(ct);
        var result = new List<PullRequest>();
        foreach (var project in projects.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var url = $"{ProjectUrl(project)}/_apis/git/pullrequests?searchCriteria.status=active&$top=200&{Api}";
            var json = await SendAsync(HttpMethod.Get, url, null, "application/json", ct);
            foreach (var node in json["value"]?.AsArray() ?? new JsonArray())
                if (node != null) result.Add(Parse(node, project, me));
        }
        if (repositoryIds.Count > 0)
            result.RemoveAll(p => !repositoryIds.Contains(p.RepositoryId, StringComparer.OrdinalIgnoreCase));
        return result.OrderByDescending(p => p.Created).ToList();
    }

    private PullRequest Parse(JsonNode node, string project, string me)
    {
        var reviewers = node["reviewers"]?.AsArray().Where(r => r != null).ToList() ?? new List<JsonNode?>();
        var mine = reviewers.FirstOrDefault(r => r!["id"]?.ToString() == me);
        var repository = node["repository"];
        var repoName = repository?["name"]?.ToString() ?? "";
        var id = node["pullRequestId"]!.GetValue<int>();
        return new PullRequest(
            id,
            node["title"]?.ToString() ?? "",
            repository?["project"]?["name"]?.ToString() ?? project,
            repository?["id"]?.ToString() ?? "",
            repoName,
            ShortBranch(node["sourceRefName"]?.ToString()),
            ShortBranch(node["targetRefName"]?.ToString()),
            node["createdBy"]?["displayName"]?.ToString() ?? "",
            node["createdBy"]?["id"]?.ToString() ?? "",
            DateTime.TryParse(node["creationDate"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var created)
                ? created.ToLocalTime() : null,
            node["isDraft"]?.GetValue<bool>() ?? false,
            node["mergeStatus"]?.ToString() ?? "",
            mine?["vote"]?.GetValue<int>() ?? 0,
            mine != null,
            string.Join(", ", reviewers.Select(r => r!["displayName"]?.ToString()).Where(n => !string.IsNullOrEmpty(n))),
            $"{ProjectUrl(repository?["project"]?["name"]?.ToString() ?? project)}/_git/{Uri.EscapeDataString(repoName)}/pullrequest/{id}",
            repository?["project"]?["id"]?.ToString() ?? "");
    }

    private static string ShortBranch(string? reference) =>
        reference?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? reference["refs/heads/".Length..] : reference ?? "";

    public async Task VoteAsync(PullRequest pr, int vote, string? comment = null, CancellationToken ct = default)
    {
        var me = await GetMyIdAsync(ct);
        await SendAsync(HttpMethod.Put, $"{PullRequestUrl(pr)}/reviewers/{me}?{Api}", new { vote }, "application/json", ct);
        if (!string.IsNullOrWhiteSpace(comment)) await AddCommentAsync(pr, comment, null, 0, false, ct);
    }

    public Task ApproveAsync(PullRequest pr, CancellationToken ct = default) => VoteAsync(pr, VoteApproved, null, ct);

    /// <summary>The branch policies evaluated for the pull request, as on its Overview page; not-applicable ones left out.</summary>
    public async Task<List<PolicyCheck>> GetChecksAsync(PullRequest pr, CancellationToken ct = default)
    {
        var artifact = Uri.EscapeDataString($"vstfs:///CodeReview/CodeReviewId/{pr.ProjectId}/{pr.Id}");
        var url = $"{ProjectUrl(pr.Project)}/_apis/policy/evaluations?artifactId={artifact}&api-version=7.0-preview.1";
        var json = await SendAsync(HttpMethod.Get, url, null, "application/json", ct);
        return (json["value"]?.AsArray() ?? new JsonArray())
            .Where(e => e != null)
            .Select(e =>
            {
                var configuration = e!["configuration"];
                // Build policies carry their own name (e.g. "CI build"); others use the policy type's name.
                var name = configuration?["settings"]?["displayName"]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) name = configuration?["type"]?["displayName"]?.ToString() ?? "";
                return new PolicyCheck(name, e["status"]?.ToString() ?? "", configuration?["isBlocking"]?.GetValue<bool>() ?? false);
            })
            .Where(c => c.Status != "notApplicable")
            .ToList();
    }

    // ---------- linked work items ----------

    /// <summary>The work items linked to the pull request.</summary>
    public async Task<List<LinkedWorkItem>> GetLinkedWorkItemsAsync(PullRequest pr, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Get, $"{PullRequestUrl(pr)}/workitems?{Api}", null, "application/json", ct);
        var ids = (json["value"]?.AsArray() ?? new JsonArray())
            .Select(w => int.TryParse(w?["id"]?.ToString(), out var id) ? id : 0).Where(id => id > 0).ToList();
        return await LoadWorkItemsAsync(ids, ct);
    }

    /// <summary>
    /// Finds work items of any type (task, bug, user story, change request...) by ID anywhere, or by
    /// words in the title within the project; newest changes first.
    /// </summary>
    public async Task<List<LinkedWorkItem>> SearchWorkItemsAsync(string project, string text, CancellationToken ct = default)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        // Test cases, suites and plans would crowd out the tasks, bugs and stories people link.
        var byTitle = $"([System.TeamProject] = {Quote(project)} AND [System.Title] CONTAINS {Quote(text)} AND " +
                      "[System.WorkItemType] NOT IN ('Test Case', 'Test Suite', 'Test Plan', 'Shared Steps', 'Shared Parameter'))";
        var condition = int.TryParse(text, out var id) && id > 0 ? $"([System.Id] = {id} OR {byTitle})" : byTitle;
        var query = $"SELECT [System.Id] FROM WorkItems WHERE {condition} AND [System.State] <> 'Removed' ORDER BY [System.ChangedDate] DESC";
        var result = await SendAsync(HttpMethod.Post, $"{_org}/_apis/wit/wiql?$top=50&{Api}", new { query }, "application/json", ct);
        var ids = (result["workItems"]?.AsArray() ?? new JsonArray()).Select(w => w!["id"]!.GetValue<int>()).ToList();
        return await LoadWorkItemsAsync(ids, ct);
    }

    private async Task<List<LinkedWorkItem>> LoadWorkItemsAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        var items = new List<LinkedWorkItem>();
        foreach (var chunk in ids.Distinct().Chunk(200)) // workitemsbatch limit
        {
            var body = new
            {
                ids = chunk,
                fields = new[] { "System.Id", "System.Title", "System.WorkItemType", "System.State", "System.AssignedTo", "System.TeamProject" },
                errorPolicy = "omit"
            };
            var batch = await SendAsync(HttpMethod.Post, $"{_org}/_apis/wit/workitemsbatch?{Api}", body, "application/json", ct);
            foreach (var node in batch["value"]?.AsArray() ?? new JsonArray())
            {
                var f = node?["fields"];
                if (f == null) continue;
                var id = node!["id"]!.ToString();
                items.Add(new LinkedWorkItem(id, f["System.WorkItemType"]?.ToString() ?? "", f["System.Title"]?.ToString() ?? "",
                    f["System.State"]?.ToString() ?? "", f["System.AssignedTo"]?["displayName"]?.ToString() ?? "",
                    $"{ProjectUrl(f["System.TeamProject"]?.ToString() ?? "")}/_workitems/edit/{id}"));
            }
        }
        // Keep the order asked for (search results arrive newest first).
        var order = ids.Select((id, index) => (id, index)).GroupBy(p => p.id).ToDictionary(g => g.Key.ToString(), g => g.First().index);
        return items.OrderBy(i => order.GetValueOrDefault(i.Id, int.MaxValue)).ToList();
    }

    // ---------- code review ----------

    /// <summary>
    /// The files changed by the whole pull request (latest iteration compared with its base), plus the
    /// commits to read the new and old versions from.
    /// </summary>
    public async Task<(List<PullRequestFileChange> Files, string NewCommit, string BaseCommit)> GetChangesAsync(
        PullRequest pr, CancellationToken ct = default)
    {
        var iterations = await SendAsync(HttpMethod.Get, $"{PullRequestUrl(pr)}/iterations?{Api}", null, "application/json", ct);
        var last = iterations["value"]?.AsArray().LastOrDefault()
                   ?? throw new InvalidOperationException("This pull request has no changes yet.");
        var iterationId = last["id"]!.GetValue<int>();
        var newCommit = last["sourceRefCommit"]?["commitId"]?.ToString() ?? "";
        // The common commit is what the source branch is compared with, as on the Files page in Azure DevOps.
        var baseCommit = last["commonRefCommit"]?["commitId"]?.ToString() ?? last["targetRefCommit"]?["commitId"]?.ToString() ?? "";

        var files = new List<PullRequestFileChange>();
        var skip = 0;
        while (true)
        {
            var url = $"{PullRequestUrl(pr)}/iterations/{iterationId}/changes?$top=1000&$skip={skip}&$compareTo=0&{Api}";
            var page = await SendAsync(HttpMethod.Get, url, null, "application/json", ct);
            foreach (var entry in page["changeEntries"]?.AsArray() ?? new JsonArray())
            {
                var item = entry?["item"];
                // Folders are listed too; only files have content to review.
                if (item == null || item["isFolder"]?.GetValue<bool>() == true || item["gitObjectType"]?.ToString() == "tree") continue;
                var path = item["path"]?.ToString() ?? "";
                files.Add(new PullRequestFileChange(path, entry!["originalPath"]?.ToString() ?? path, entry["changeType"]?.ToString() ?? "edit"));
            }
            var nextSkip = page["nextSkip"]?.GetValue<int>() ?? 0;
            if (nextSkip <= skip || page["nextTop"]?.GetValue<int>() is null or 0) break;
            skip = nextSkip;
        }
        return (files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(), newCommit, baseCommit);
    }

    /// <summary>A file's text at a commit; null when it is binary.</summary>
    public async Task<string?> GetFileTextAsync(PullRequest pr, string path, string commit, CancellationToken ct = default)
    {
        var url = $"{ProjectUrl(pr.Project)}/_apis/git/repositories/{pr.RepositoryId}/items?path={Uri.EscapeDataString(path)}" +
                  $"&versionDescriptor.version={commit}&versionDescriptor.versionType=commit&includeContent=true&{Api}";
        var item = await SendAsync(HttpMethod.Get, url, null, "application/json", ct);
        return item["contentMetadata"]?["isBinary"]?.GetValue<bool>() == true ? null : item["content"]?.ToString() ?? "";
    }

    /// <summary>All people's comments on the pull request (system messages and deleted comments left out).</summary>
    public async Task<List<PullRequestComment>> GetCommentsAsync(PullRequest pr, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Get, $"{PullRequestUrl(pr)}/threads?{Api}", null, "application/json", ct);
        var result = new List<PullRequestComment>();
        foreach (var thread in json["value"]?.AsArray() ?? new JsonArray())
        {
            if (thread == null || thread["isDeleted"]?.GetValue<bool>() == true) continue;
            var context = thread["threadContext"];
            var path = context?["filePath"]?.ToString() ?? "";
            var right = context?["rightFileStart"]?["line"]?.GetValue<int>();
            var left = context?["leftFileStart"]?["line"]?.GetValue<int>();
            var status = thread["status"]?.ToString() ?? "";
            foreach (var comment in thread["comments"]?.AsArray() ?? new JsonArray())
            {
                if (comment == null || comment["commentType"]?.ToString() == "system" || comment["isDeleted"]?.GetValue<bool>() == true) continue;
                result.Add(new PullRequestComment(path, right ?? left ?? 0, right == null && left != null,
                    comment["author"]?["displayName"]?.ToString() ?? "",
                    comment["content"]?.ToString() ?? "",
                    DateTime.TryParse(comment["publishedDate"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var date)
                        ? date.ToLocalTime() : null,
                    status));
            }
        }
        return result;
    }

    /// <summary>Starts a new comment thread: on a file line when <paramref name="filePath"/> is given, otherwise a general one.</summary>
    /// <param name="leftSide">Anchor to the old version's line (for removed lines) instead of the new version's.</param>
    public async Task AddCommentAsync(PullRequest pr, string text, string? filePath, int line, bool leftSide, CancellationToken ct = default)
    {
        var thread = new JsonObject
        {
            ["comments"] = new JsonArray(new JsonObject { ["parentCommentId"] = 0, ["content"] = text, ["commentType"] = 1 }),
            ["status"] = 1 // active
        };
        if (filePath != null)
        {
            JsonObject Position() => new() { ["line"] = line, ["offset"] = 1 };
            thread["threadContext"] = leftSide
                ? new JsonObject { ["filePath"] = filePath, ["leftFileStart"] = Position(), ["leftFileEnd"] = Position() }
                : new JsonObject { ["filePath"] = filePath, ["rightFileStart"] = Position(), ["rightFileEnd"] = Position() };
        }
        await SendAsync(HttpMethod.Post, $"{PullRequestUrl(pr)}/threads?{Api}", thread, "application/json", ct);
    }

    /// <summary>
    /// Completes (merges) the pull request. Branch policies still apply: if a required reviewer, build
    /// or check is missing, Azure DevOps refuses and its reason is passed on.
    /// </summary>
    public async Task CompleteAsync(PullRequest pr, PullRequestCompletion options, CancellationToken ct = default)
    {
        // The merge must name the latest source commit; take it fresh rather than from the list.
        var current = await SendAsync(HttpMethod.Get, $"{PullRequestUrl(pr)}?{Api}", null, "application/json", ct);
        var commit = current["lastMergeSourceCommit"]?["commitId"]?.ToString()
                     ?? throw new InvalidOperationException("Azure DevOps did not report the pull request's latest commit.");

        var body = new
        {
            status = "completed",
            lastMergeSourceCommit = new { commitId = commit },
            completionOptions = new
            {
                mergeStrategy = options.MergeStrategy,
                deleteSourceBranch = options.DeleteSourceBranch,
                transitionWorkItems = options.CompleteWorkItems,
                mergeCommitMessage = options.MergeMessage
            }
        };
        // The merge itself may finish a moment later; the refreshed list then no longer shows the pull request.
        await SendAsync(HttpMethod.Patch, $"{PullRequestUrl(pr)}?{Api}", body, "application/json", ct);
    }

    public async Task<List<GitRepository>> GetRepositoriesAsync(string project, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Get, $"{ProjectUrl(project)}/_apis/git/repositories?{Api}", null, "application/json", ct);
        var nodes = (json["value"]?.AsArray() ?? new JsonArray())
            .Where(r => r != null && r["isDisabled"]?.GetValue<bool>() != true).Select(r => r!).ToList();

        // Permissions are checked per repository, on the token "repoV2/{project ID}/{repository ID}".
        var tokens = nodes.Select(r => $"repoV2/{r["project"]?["id"]}/{r["id"]}").ToList();
        var canDelete = await HasGitPermissionAsync(tokens, GitDeleteRepository, ct);
        var canRename = await HasGitPermissionAsync(tokens, GitRenameRepository, ct);
        return nodes
            .Select((r, i) => ParseRepository(r, project) with { CanDelete = canDelete[i], CanRename = canRename[i] })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // The "Git Repositories" security namespace and its permission bits.
    private const string GitSecurityNamespace = "2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87";
    private const int GitDeleteRepository = 512;
    private const int GitRenameRepository = 1024;

    /// <summary>
    /// Whether the signed-in user has a Git permission on each token. When the check itself fails (for
    /// example a token without access to the security API), everything counts as allowed and the
    /// server has the last word when the action is tried.
    /// </summary>
    private async Task<bool[]> HasGitPermissionAsync(IReadOnlyList<string> tokens, int permission, CancellationToken ct)
    {
        var result = Enumerable.Repeat(true, tokens.Count).ToArray();
        try
        {
            // Many tokens fit in one call; batches keep the address short.
            for (var start = 0; start < tokens.Count; start += 40)
            {
                var batch = tokens.Skip(start).Take(40).ToList();
                var url = $"{_org}/_apis/permissions/{GitSecurityNamespace}/{permission}?tokens={Uri.EscapeDataString(string.Join(",", batch))}" +
                          $"&delimiter=,&alwaysAllowAdministrators=false&{Api}";
                var answers = (await SendAsync(HttpMethod.Get, url, null, "application/json", ct))["value"]?.AsArray();
                for (var i = 0; i < batch.Count && answers != null && i < answers.Count; i++)
                    result[start + i] = answers[i]?.GetValue<bool>() ?? true;
            }
        }
        catch (InvalidOperationException)
        {
            // Not known: leave the actions available.
        }
        return result;
    }

    private static GitRepository ParseRepository(JsonNode r, string project) =>
        new(r["id"]!.ToString(), r["name"]?.ToString() ?? "", ShortBranch(r["defaultBranch"]?.ToString()),
            r["project"]?["name"]?.ToString() ?? project, "", r["webUrl"]?.ToString() ?? "");

    private string RepositoryUrl(GitRepository repository) =>
        $"{ProjectUrl(repository.Project)}/_apis/git/repositories/{repository.Id}";

    public async Task<GitRepository> CreateRepositoryAsync(string project, string name, bool isPrivate, CancellationToken ct = default)
    {
        // Azure DevOps repositories are always private to the organization; isPrivate does not apply.
        var info = await SendAsync(HttpMethod.Get, $"{_org}/_apis/projects/{Uri.EscapeDataString(project)}?{Api}", null, "application/json", ct);
        var body = new { name, project = new { id = info["id"]!.ToString() } };
        var created = await SendAsync(HttpMethod.Post, $"{ProjectUrl(project)}/_apis/git/repositories?{Api}", body, "application/json", ct);
        return ParseRepository(created, project);
    }

    public async Task RenameRepositoryAsync(GitRepository repository, string newName, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Patch, $"{RepositoryUrl(repository)}?{Api}", new { name = newName }, "application/json", ct);

    public async Task DeleteRepositoryAsync(GitRepository repository, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Delete, $"{RepositoryUrl(repository)}?{Api}", null, "application/json", ct);

    private const string NoCommit = "0000000000000000000000000000000000000000";

    /// <summary>The commit a branch points to.</summary>
    private async Task<string> BranchCommitAsync(GitRepository repository, string branch, CancellationToken ct)
    {
        var url = $"{RepositoryUrl(repository)}/refs?filter={Uri.EscapeDataString("heads/" + branch)}&{Api}";
        var json = await SendAsync(HttpMethod.Get, url, null, "application/json", ct);
        // The filter matches by prefix, so "main" would also find "main-old".
        return (json["value"]?.AsArray() ?? new JsonArray())
                   .FirstOrDefault(r => r?["name"]?.ToString() == "refs/heads/" + branch)?["objectId"]?.ToString()
               ?? throw new InvalidOperationException($"The branch \"{branch}\" was not found.");
    }

    /// <summary>Creates, moves or deletes a branch: a zero commit on one side means "does not exist".</summary>
    private async Task UpdateBranchAsync(GitRepository repository, string branch, string oldCommit, string newCommit, CancellationToken ct)
    {
        var body = new[] { new { name = "refs/heads/" + branch, oldObjectId = oldCommit, newObjectId = newCommit } };
        var result = await SendAsync(HttpMethod.Post, $"{RepositoryUrl(repository)}/refs?{Api}", body, "application/json", ct);
        var update = result["value"]?.AsArray().FirstOrDefault();
        if (update?["success"]?.GetValue<bool>() != true)
            throw new InvalidOperationException($"Azure DevOps refused the change to \"{branch}\": " +
                                                (update?["customMessage"]?.ToString() ?? update?["updateStatus"]?.ToString() ?? "unknown reason"));
    }

    public async Task CreateBranchAsync(GitRepository repository, string name, string fromBranch, CancellationToken ct = default) =>
        await UpdateBranchAsync(repository, name, NoCommit, await BranchCommitAsync(repository, fromBranch, ct), ct);

    public async Task DeleteBranchAsync(GitRepository repository, string name, CancellationToken ct = default) =>
        await UpdateBranchAsync(repository, name, await BranchCommitAsync(repository, name, ct), NoCommit, ct);

    public async Task<List<string>> GetBranchesAsync(string project, string repositoryId, CancellationToken ct = default)
    {
        var url = $"{ProjectUrl(project)}/_apis/git/repositories/{repositoryId}/refs?filter=heads/&$top=1000&{Api}";
        var json = await SendAsync(HttpMethod.Get, url, null, "application/json", ct);
        return (json["value"]?.AsArray() ?? new JsonArray())
            .Select(r => ShortBranch(r?["name"]?.ToString()))
            .Where(n => n.Length > 0)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <returns>The new pull request's ID and web address.</returns>
    public async Task<(int Id, string WebUrl)> CreateAsync(string project, GitRepository repository, string sourceBranch,
        string targetBranch, string title, string description, bool isDraft, IReadOnlyCollection<string> workItemIds, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["sourceRefName"] = "refs/heads/" + sourceBranch,
            ["targetRefName"] = "refs/heads/" + targetBranch,
            ["title"] = title,
            ["description"] = description,
            ["isDraft"] = isDraft
        };
        if (workItemIds.Count > 0)
            body["workItemRefs"] = new JsonArray(workItemIds.Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray());

        var created = await SendAsync(HttpMethod.Post,
            $"{ProjectUrl(project)}/_apis/git/repositories/{repository.Id}/pullrequests?{Api}", body, "application/json", ct);
        var id = created["pullRequestId"]!.GetValue<int>();
        return (id, $"{ProjectUrl(project)}/_git/{Uri.EscapeDataString(repository.Name)}/pullrequest/{id}");
    }
}
