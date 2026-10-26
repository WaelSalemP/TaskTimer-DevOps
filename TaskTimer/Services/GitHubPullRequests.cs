using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using TaskTimer.Models;

namespace TaskTimer.Services;

/// <summary>
/// GitHub pull requests, branches and repositories through the GitHub REST API, with the token from the
/// browser sign-in. Pull requests and repositories are identified by "owner/name".
/// </summary>
public sealed class GitHubPullRequestService : HttpProviderBase, IPullRequestService
{
    private const int MaxPages = 10;
    private readonly string _api;
    private string? _login;

    public GitHubPullRequestService(AppSettings settings)
    {
        _api = GitHubProvider.ApiUrl(settings);
        GitHubProvider.Configure(Http, settings);
    }

    private Task<JsonNode> GetAsync(string url, CancellationToken ct) => SendAsync(HttpMethod.Get, url, null, "application/json", ct);

    private Task<JsonNode> SendJsonAsync(HttpMethod method, string url, object? body, CancellationToken ct) =>
        SendAsync(method, url, body, "application/json", ct);

    /// <summary>Every page of a list endpoint (100 per page), up to <see cref="MaxPages"/>.</summary>
    private async Task<List<JsonNode>> GetAllAsync(string url, CancellationToken ct, Func<JsonNode, JsonArray?>? items = null)
    {
        var result = new List<JsonNode>();
        var separator = url.Contains('?') ? '&' : '?';
        for (var page = 1; page <= MaxPages; page++)
        {
            var json = await GetAsync($"{url}{separator}per_page=100&page={page}", ct);
            var array = items != null ? items(json) : json as JsonArray;
            if (array == null) break;
            result.AddRange(array.Where(n => n != null)!);
            if (array.Count < 100) break;
        }
        return result;
    }

    /// <summary>A branch or file path for a URL: each part escaped, the slashes kept.</summary>
    private static string EscapePath(string path) => string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    private string RepoUrl(string fullName) => $"{_api}/repos/{EscapePath(fullName)}";

    private string PullUrl(PullRequest pr) => $"{RepoUrl(pr.RepositoryId)}/pulls/{pr.Id}";

    public async Task<string> GetMyIdAsync(CancellationToken ct = default) =>
        _login ??= (await GetAsync($"{_api}/user", ct))["login"]?.ToString() ?? "";

    // ---------- the list ----------

    /// <summary>
    /// Without chosen repositories: the open pull requests the user created, is asked to review, or takes
    /// part in, across all of GitHub. With chosen repositories: every open pull request in them.
    /// </summary>
    public async Task<List<PullRequest>> GetActiveAsync(IEnumerable<string> projects, IReadOnlyCollection<string> repositoryIds,
        CancellationToken ct = default)
    {
        var me = await GetMyIdAsync(ct);
        var found = new List<(string Repo, int Number)>();
        if (repositoryIds.Count == 0)
        {
            // "involves" covers author, assignee, mentions and comments, but not a review request.
            foreach (var qualifier in new[] { "involves", "review-requested" })
            {
                var query = Uri.EscapeDataString($"is:pr is:open archived:false {qualifier}:{me}");
                var hits = await GetAllAsync($"{_api}/search/issues?q={query}", ct, json => json["items"] as JsonArray);
                found.AddRange(hits.Select(h => (RepoFromUrl(h["repository_url"]?.ToString()), h["number"]!.GetValue<int>())));
            }
        }
        else
        {
            foreach (var repo in repositoryIds)
                found.AddRange((await GetAllAsync($"{RepoUrl(repo)}/pulls?state=open", ct))
                    .Select(p => (repo, p["number"]!.GetValue<int>())));
        }

        // The lists lack the merge state and reviews, so each pull request is read in full, a few at a time.
        using var gate = new SemaphoreSlim(6);
        var details = await Task.WhenAll(found.Distinct().Select(async f =>
        {
            await gate.WaitAsync(ct);
            try { return await GetPullRequestAsync(f.Repo, f.Number, me, ct); }
            finally { gate.Release(); }
        }));
        return details.OrderByDescending(p => p.Created).ToList();
    }

    /// <summary>"owner/name" from https://api.github.com/repos/owner/name.</summary>
    private static string RepoFromUrl(string? url)
    {
        var marker = url?.IndexOf("/repos/", StringComparison.Ordinal) ?? -1;
        return marker < 0 ? "" : Uri.UnescapeDataString(url![(marker + "/repos/".Length)..]);
    }

    private async Task<PullRequest> GetPullRequestAsync(string repo, int number, string me, CancellationToken ct)
    {
        var node = await GetAsync($"{RepoUrl(repo)}/pulls/{number}", ct);
        var reviews = await GetAllAsync($"{RepoUrl(repo)}/pulls/{number}/reviews", ct);

        // Each person's latest decision counts; plain comments do not change it.
        var decisions = reviews
            .Where(r => r["state"]?.ToString() is "APPROVED" or "CHANGES_REQUESTED" or "DISMISSED")
            .GroupBy(r => r["user"]?["login"]?.ToString() ?? "")
            .ToDictionary(g => g.Key, g => g.Last()["state"]!.ToString());
        var myVote = decisions.GetValueOrDefault(me) switch
        {
            "APPROVED" => PullRequestService.VoteApproved,
            "CHANGES_REQUESTED" => -10,
            _ => 0
        };
        var requested = node["requested_reviewers"]?.AsArray().Select(r => r?["login"]?.ToString() ?? "").ToList() ?? new List<string>();
        var reviewers = requested.Concat(reviews.Select(r => r["user"]?["login"]?.ToString() ?? ""))
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var author = node["user"]?["login"]?.ToString() ?? "";
        reviewers.Remove(author);

        var baseRepo = node["base"]?["repo"]?["full_name"]?.ToString() ?? repo;
        var headRepo = node["head"]?["repo"]?["full_name"]?.ToString() ?? baseRepo;
        var headRef = node["head"]?["ref"]?.ToString() ?? "";
        return new PullRequest(
            number,
            node["title"]?.ToString() ?? "",
            node["base"]?["repo"]?["owner"]?["login"]?.ToString() ?? repo.Split('/')[0],
            baseRepo,
            baseRepo,
            // A branch in a fork is shown with its owner, as GitHub does.
            headRepo == baseRepo ? headRef : $"{headRepo.Split('/')[0]}:{headRef}",
            node["base"]?["ref"]?.ToString() ?? "",
            author,
            author,
            ParseDate(node["created_at"]),
            node["draft"]?.GetValue<bool>() ?? false,
            node["mergeable_state"]?.ToString() == "dirty" ? "conflicts" : node["mergeable_state"]?.ToString() ?? "",
            myVote,
            requested.Contains(me, StringComparer.OrdinalIgnoreCase) || reviewers.Contains(me, StringComparer.OrdinalIgnoreCase),
            string.Join(", ", reviewers),
            node["html_url"]?.ToString() ?? "",
            "",
            node["head"]?["sha"]?.ToString() ?? "");
    }

    // ---------- votes and checks ----------

    public Task ApproveAsync(PullRequest pr, CancellationToken ct = default) => VoteAsync(pr, PullRequestService.VoteApproved, null, ct);

    /// <summary>GitHub knows approve and request changes; a reset (0) leaves the earlier review as it is.</summary>
    public async Task VoteAsync(PullRequest pr, int vote, string? comment = null, CancellationToken ct = default)
    {
        if (vote == 0) return;
        var body = new JsonObject { ["event"] = vote > 0 ? "APPROVE" : "REQUEST_CHANGES" };
        if (!string.IsNullOrWhiteSpace(comment)) body["body"] = comment;
        await SendJsonAsync(HttpMethod.Post, $"{PullUrl(pr)}/reviews", body, ct);
    }

    /// <summary>
    /// The check runs and commit statuses on the source branch's latest commit. Those the target
    /// branch's protection requires are marked as blocking.
    /// </summary>
    public async Task<List<PolicyCheck>> GetChecksAsync(PullRequest pr, CancellationToken ct = default)
    {
        var commit = pr.HeadCommit.Length > 0 ? pr.HeadCommit : (await GetAsync(PullUrl(pr), ct))["head"]?["sha"]?.ToString() ?? "";
        var required = await RequiredChecksAsync(pr, ct);
        var checks = new List<PolicyCheck>();

        var runs = await GetAllAsync($"{RepoUrl(pr.RepositoryId)}/commits/{commit}/check-runs", ct, json => json["check_runs"] as JsonArray);
        foreach (var run in runs)
        {
            var name = run["name"]?.ToString() ?? "";
            var status = run["status"]?.ToString() != "completed" ? (run["status"]?.ToString() == "queued" ? "queued" : "running")
                : run["conclusion"]?.ToString() is "success" or "neutral" or "skipped" ? "approved"
                : "rejected";
            checks.Add(new PolicyCheck(name, status, required.Contains(name)));
        }

        var combined = await GetAsync($"{RepoUrl(pr.RepositoryId)}/commits/{commit}/status", ct);
        foreach (var status in combined["statuses"]?.AsArray() ?? new JsonArray())
        {
            var name = status?["context"]?.ToString() ?? "";
            var state = status?["state"]?.ToString() switch { "success" => "approved", "pending" => "running", _ => "rejected" };
            checks.Add(new PolicyCheck(name, state, required.Contains(name)));
        }
        return checks;
    }

    /// <summary>Checks the target branch's protection requires; empty when it has none or it cannot be read.</summary>
    private async Task<HashSet<string>> RequiredChecksAsync(PullRequest pr, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var branch = await GetAsync($"{RepoUrl(pr.RepositoryId)}/branches/{EscapePath(pr.TargetBranch)}", ct);
            var rules = branch["protection"]?["required_status_checks"];
            foreach (var context in rules?["contexts"]?.AsArray() ?? new JsonArray())
                if (context?.ToString() is { Length: > 0 } name) names.Add(name);
            foreach (var check in rules?["checks"]?.AsArray() ?? new JsonArray())
                if (check?["context"]?.ToString() is { Length: > 0 } name) names.Add(name);
        }
        catch (InvalidOperationException)
        {
            // Without access to the protection rules nothing is treated as required.
        }
        return names;
    }

    // ---------- work items: GitHub pull requests are not linked to work items ----------

    public Task<List<LinkedWorkItem>> GetLinkedWorkItemsAsync(PullRequest pr, CancellationToken ct = default) =>
        Task.FromResult(new List<LinkedWorkItem>());

    public Task<List<LinkedWorkItem>> SearchWorkItemsAsync(string project, string text, CancellationToken ct = default) =>
        Task.FromResult(new List<LinkedWorkItem>());

    // ---------- code review ----------

    /// <summary>The changed files, compared with the commit the branches share, as on GitHub's "Files changed" tab.</summary>
    public async Task<(List<PullRequestFileChange> Files, string NewCommit, string BaseCommit)> GetChangesAsync(
        PullRequest pr, CancellationToken ct = default)
    {
        var current = await GetAsync(PullUrl(pr), ct);
        var head = current["head"]?["sha"]?.ToString() ?? "";
        var baseSha = current["base"]?["sha"]?.ToString() ?? "";
        var compare = await GetAsync($"{RepoUrl(pr.RepositoryId)}/compare/{baseSha}...{head}?per_page=1", ct);
        var mergeBase = compare["merge_base_commit"]?["sha"]?.ToString() ?? baseSha;

        // GitHub lists at most 3000 files.
        var files = (await GetAllAsync($"{PullUrl(pr)}/files", ct)).Select(f =>
        {
            var path = "/" + (f["filename"]?.ToString() ?? "");
            var original = f["previous_filename"]?.ToString() is { Length: > 0 } previous ? "/" + previous : path;
            var change = f["status"]?.ToString() switch
            {
                "added" => "add",
                "removed" => "delete",
                "renamed" => "rename",
                _ => "edit"
            };
            return new PullRequestFileChange(path, original, change);
        });
        return (files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(), head, mergeBase);
    }

    /// <summary>A file's text at a commit; null when it is binary. Paths start with "/", as in Azure DevOps.</summary>
    public async Task<string?> GetFileTextAsync(PullRequest pr, string path, string commit, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{RepoUrl(pr.RepositoryId)}/contents/{EscapePath(path.TrimStart('/'))}?ref={commit}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {path}");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return Array.IndexOf(bytes, (byte)0) >= 0 ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Conversation comments, review summaries and line comments.</summary>
    public async Task<List<PullRequestComment>> GetCommentsAsync(PullRequest pr, CancellationToken ct = default)
    {
        static PullRequestComment General(JsonNode c, string status) =>
            new("", 0, false, c["user"]?["login"]?.ToString() ?? "", c["body"]?.ToString() ?? "",
                ParseDate(c["created_at"] ?? c["submitted_at"]), status);

        var result = new List<PullRequestComment>();
        result.AddRange((await GetAllAsync($"{RepoUrl(pr.RepositoryId)}/issues/{pr.Id}/comments", ct)).Select(c => General(c, "")));
        result.AddRange((await GetAllAsync($"{PullUrl(pr)}/reviews", ct))
            .Where(r => !string.IsNullOrWhiteSpace(r["body"]?.ToString()))
            .Select(r => General(r, (r["state"]?.ToString() ?? "").ToLowerInvariant().Replace('_', ' '))));
        foreach (var c in await GetAllAsync($"{PullUrl(pr)}/comments", ct))
        {
            // A comment on code that has changed since keeps only its original line.
            var line = (c["line"] ?? c["original_line"])?.GetValue<int>() ?? 0;
            result.Add(new PullRequestComment("/" + (c["path"]?.ToString() ?? ""), line, c["side"]?.ToString() == "LEFT",
                c["user"]?["login"]?.ToString() ?? "", c["body"]?.ToString() ?? "", ParseDate(c["created_at"]),
                c["line"] == null ? "outdated" : ""));
        }
        return result.OrderBy(c => c.Date).ToList();
    }

    /// <summary>A conversation comment, or a comment on a line. GitHub only accepts lines that are part of the changes.</summary>
    public async Task AddCommentAsync(PullRequest pr, string text, string? filePath, int line, bool leftSide, CancellationToken ct = default)
    {
        if (filePath == null)
        {
            await SendJsonAsync(HttpMethod.Post, $"{RepoUrl(pr.RepositoryId)}/issues/{pr.Id}/comments", new { body = text }, ct);
            return;
        }
        var head = (await GetAsync(PullUrl(pr), ct))["head"]?["sha"]?.ToString() ?? pr.HeadCommit;
        var body = new { body = text, commit_id = head, path = filePath.TrimStart('/'), line, side = leftSide ? "LEFT" : "RIGHT" };
        await SendJsonAsync(HttpMethod.Post, $"{PullUrl(pr)}/comments", body, ct);
    }

    /// <summary>Merges the pull request; GitHub refuses when the branch protection is not satisfied, and its reason is passed on.</summary>
    public async Task CompleteAsync(PullRequest pr, PullRequestCompletion options, CancellationToken ct = default)
    {
        var current = await GetAsync(PullUrl(pr), ct);
        var method = options.MergeStrategy switch { "squash" => "squash", "rebase" => "rebase", _ => "merge" };
        var body = new JsonObject { ["merge_method"] = method, ["sha"] = current["head"]?["sha"]?.ToString() };
        if (options.MergeMessage.Length > 0 && method != "rebase") body["commit_title"] = options.MergeMessage;
        await SendJsonAsync(HttpMethod.Put, $"{PullUrl(pr)}/merge", body, ct);

        // A branch in someone else's fork is not ours to delete.
        var headRepo = current["head"]?["repo"]?["full_name"]?.ToString();
        if (options.DeleteSourceBranch && string.Equals(headRepo, pr.RepositoryId, StringComparison.OrdinalIgnoreCase))
            await SendJsonAsync(HttpMethod.Delete, $"{RepoUrl(pr.RepositoryId)}/git/refs/heads/{EscapePath(current["head"]!["ref"]!.ToString())}", null, ct);
    }

    // ---------- repositories and branches ----------

    public async Task<List<GitRepository>> GetRepositoriesAsync(string project, CancellationToken ct = default)
    {
        var repos = await GetAllAsync($"{_api}/user/repos?affiliation=owner,collaborator,organization_member&sort=full_name", ct);
        var mayDelete = await HasScopeAsync("delete_repo", ct);
        return repos.Where(r => r["archived"]?.GetValue<bool>() != true)
            .Select(r => ParseRepository(r, mayDelete))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Deleting and renaming need admin rights on the repository; deleting also needs the sign-in to
    /// include GitHub's "delete_repo" permission.
    /// </summary>
    private static GitRepository ParseRepository(JsonNode r, bool mayDelete)
    {
        var admin = r["permissions"]?["admin"]?.GetValue<bool>() ?? false;
        var visibility = r["visibility"]?.ToString() ?? (r["private"]?.GetValue<bool>() == true ? "private" : "public");
        return new(r["full_name"]!.ToString(), r["full_name"]!.ToString(), r["default_branch"]?.ToString() ?? "",
            r["owner"]?["login"]?.ToString() ?? "", visibility, r["html_url"]?.ToString() ?? "",
            CanDelete: admin && mayDelete, CanRename: admin, DeleteNeedsSignIn: admin && !mayDelete);
    }

    /// <summary>Whether the signed-in token carries an OAuth scope, from the header GitHub adds to every answer.</summary>
    private async Task<bool> HasScopeAsync(string scope, CancellationToken ct)
    {
        using var response = await Http.GetAsync($"{_api}/user", ct);
        if (!response.Headers.TryGetValues("X-OAuth-Scopes", out var values)) return true; // not an OAuth token: GitHub decides
        return values.SelectMany(v => v.Split(',')).Any(s => s.Trim() == scope);
    }

    public async Task<List<string>> GetBranchesAsync(string project, string repositoryId, CancellationToken ct = default) =>
        (await GetAllAsync($"{RepoUrl(repositoryId)}/branches", ct))
        .Select(b => b["name"]?.ToString() ?? "")
        .Where(n => n.Length > 0)
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Work items cannot be linked on GitHub, so <paramref name="workItemIds"/> is not used.</summary>
    public async Task<(int Id, string WebUrl)> CreateAsync(string project, GitRepository repository, string sourceBranch,
        string targetBranch, string title, string description, bool isDraft, IReadOnlyCollection<string> workItemIds, CancellationToken ct = default)
    {
        var body = new { title, head = sourceBranch, @base = targetBranch, body = description, draft = isDraft };
        var created = await SendJsonAsync(HttpMethod.Post, $"{RepoUrl(repository.Id)}/pulls", body, ct);
        return (created["number"]!.GetValue<int>(), created["html_url"]?.ToString() ?? "");
    }

    /// <summary>Starts the repository with a README, so it has a default branch to work from.</summary>
    public async Task<GitRepository> CreateRepositoryAsync(string project, string name, bool isPrivate, CancellationToken ct = default)
    {
        var owner = project.Trim();
        var me = await GetMyIdAsync(ct);
        var url = owner.Length == 0 || string.Equals(owner, me, StringComparison.OrdinalIgnoreCase)
            ? $"{_api}/user/repos"
            : $"{_api}/orgs/{Uri.EscapeDataString(owner)}/repos";
        var created = await SendJsonAsync(HttpMethod.Post, url, new { name, @private = isPrivate, auto_init = true }, ct);
        return ParseRepository(created, await HasScopeAsync("delete_repo", ct));
    }

    public async Task RenameRepositoryAsync(GitRepository repository, string newName, CancellationToken ct = default) =>
        await SendJsonAsync(HttpMethod.Patch, RepoUrl(repository.Id), new { name = newName }, ct);

    public async Task DeleteRepositoryAsync(GitRepository repository, CancellationToken ct = default) =>
        await SendJsonAsync(HttpMethod.Delete, RepoUrl(repository.Id), null, ct);

    public async Task CreateBranchAsync(GitRepository repository, string name, string fromBranch, CancellationToken ct = default)
    {
        var from = await GetAsync($"{RepoUrl(repository.Id)}/git/ref/heads/{EscapePath(fromBranch)}", ct);
        var sha = from["object"]?["sha"]?.ToString() ?? throw new InvalidOperationException($"The branch \"{fromBranch}\" was not found.");
        await SendJsonAsync(HttpMethod.Post, $"{RepoUrl(repository.Id)}/git/refs", new { @ref = "refs/heads/" + name, sha }, ct);
    }

    public async Task DeleteBranchAsync(GitRepository repository, string name, CancellationToken ct = default) =>
        await SendJsonAsync(HttpMethod.Delete, $"{RepoUrl(repository.Id)}/git/refs/heads/{EscapePath(name)}", null, ct);
}
