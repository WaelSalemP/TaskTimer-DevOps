using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using TaskTimer.Models;

namespace TaskTimer.Services;

/// <summary>A record to choose: a customer (account or contact) or a user.</summary>
/// <param name="Kind">"account", "contact" or "systemuser".</param>
public sealed record CrmRef(string Id, string Name, string Kind)
{
    public override string ToString() => Name;
}

/// <summary>A Dynamics 365 case.</summary>
/// <param name="State">0 active, 1 resolved, 2 cancelled.</param>
/// <param name="Priority">1 high, 2 normal, 3 low.</param>
public sealed record Ticket(string Id, string Number, string Title, string Description, string Customer, CrmRef? Owner,
    int Priority, string PriorityText, int State, string Status, DateTime? Created, DateTime? Modified, string WebUrl);

public sealed record TicketNote(string Subject, string Text, string Author, DateTime? Date);

/// <summary>Tickets (cases) in Dynamics 365 through the Dataverse Web API, as the signed-in user.</summary>
public sealed class CrmService : HttpProviderBase
{
    public const int Active = 0;
    public const int Resolved = 1;
    public const int Cancelled = 2;
    private const string FormattedValue = "@OData.Community.Display.V1.FormattedValue";
    private const string Fields = "incidentid,ticketnumber,title,description,prioritycode,statecode,statuscode,createdon,modifiedon,_customerid_value,_ownerid_value";

    private readonly string _url;
    private readonly string _api;

    private CrmService(string url, string token)
    {
        _url = url;
        _api = url + "/api/data/v9.2";
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        Http.DefaultRequestHeaders.Add("OData-Version", "4.0");
        Http.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
        // Names for lookups and choices, and the created record in the answer.
        Http.DefaultRequestHeaders.Add("Prefer", "odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\",return=representation");
    }

    public static bool IsConfigured(AppSettings s) => s.CrmUrl.Length > 0 && s.CrmUserName.Length > 0;

    /// <summary>The Dynamics 365 address in one form; null unless it is an https://....dynamics.com address.</summary>
    public static string? NormalizeUrl(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.EndsWith(".dynamics.com", StringComparison.OrdinalIgnoreCase)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;

    /// <summary>Connects with a cached sign-in; throws <see cref="SignInRequiredException"/> when a new one is needed.</summary>
    public static async Task<CrmService> ConnectAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (!IsConfigured(settings)) throw new SignInRequiredException();
        return new CrmService(settings.CrmUrl, await CrmAuth.GetTokenAsync(settings.CrmUrl, settings.CrmUserName, ct));
    }

    // A PATCH must not quietly create a record someone else has just deleted.
    protected override void Prepare(HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Patch) request.Headers.IfMatch.Add(EntityTagHeaderValue.Any);
    }

    private Task<JsonNode> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, $"{_api}/{path}", null, "application/json", ct);

    private Task<JsonNode> SendAsync(HttpMethod method, string path, JsonObject body, CancellationToken ct) =>
        SendAsync(method, $"{_api}/{path}", body, "application/json", ct);

    private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

    private static string Name(JsonNode node, string field) => node[field + FormattedValue]?.ToString() ?? "";

    public async Task<string> GetMyIdAsync(CancellationToken ct = default) =>
        (await GetAsync("WhoAmI", ct))["UserId"]?.ToString() ?? "";

    /// <param name="state">Only tickets in this state; null = all.</param>
    /// <param name="mineOnly">Only tickets owned by the signed-in user.</param>
    public async Task<List<Ticket>> GetTicketsAsync(int? state, bool mineOnly, CancellationToken ct = default)
    {
        var filters = new List<string>();
        if (state != null) filters.Add($"statecode eq {state}");
        if (mineOnly) filters.Add($"_ownerid_value eq {await GetMyIdAsync(ct)}");
        var filter = filters.Count > 0 ? "&$filter=" + Uri.EscapeDataString(string.Join(" and ", filters)) : "";
        var json = await GetAsync($"incidents?$select={Fields}{filter}&$orderby=modifiedon desc&$top=500", ct);
        return (json["value"]?.AsArray() ?? new JsonArray()).Where(n => n != null).Select(n => Parse(n!)).ToList();
    }

    private Ticket Parse(JsonNode n)
    {
        var id = n["incidentid"]!.ToString();
        var ownerId = n["_ownerid_value"]?.ToString();
        return new Ticket(id, n["ticketnumber"]?.ToString() ?? "", n["title"]?.ToString() ?? "", n["description"]?.ToString() ?? "",
            Name(n, "_customerid_value"),
            ownerId == null ? null : new CrmRef(ownerId, Name(n, "_ownerid_value"), "systemuser"),
            n["prioritycode"]?.GetValue<int>() ?? 2, Name(n, "prioritycode"),
            n["statecode"]?.GetValue<int>() ?? Active, Name(n, "statuscode"),
            ParseDate(n["createdon"]), ParseDate(n["modifiedon"]),
            $"{_url}/main.aspx?pagetype=entityrecord&etn=incident&id={id}");
    }

    public async Task<Ticket> CreateTicketAsync(string title, string description, CrmRef customer, int priority, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["title"] = title, ["description"] = description, ["prioritycode"] = priority,
            [$"customerid_{customer.Kind}@odata.bind"] = customer.Kind == "account" ? $"/accounts({customer.Id})" : $"/contacts({customer.Id})"
        };
        return Parse(await SendAsync(HttpMethod.Post, $"incidents?$select={Fields}", body, ct));
    }

    /// <param name="owner">The new owner, or null to leave it as it is.</param>
    public async Task UpdateTicketAsync(Ticket ticket, string title, string description, int priority, CrmRef? owner, CancellationToken ct = default)
    {
        var body = new JsonObject { ["title"] = title, ["description"] = description, ["prioritycode"] = priority };
        if (owner != null) body["ownerid@odata.bind"] = $"/systemusers({owner.Id})";
        await SendAsync(HttpMethod.Patch, $"incidents({ticket.Id})", body, ct);
    }

    public async Task ResolveAsync(Ticket ticket, string resolution, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["Status"] = 5, // Problem Solved
            ["IncidentResolution"] = new JsonObject
            {
                ["@odata.type"] = "Microsoft.Dynamics.CRM.incidentresolution",
                ["subject"] = resolution,
                ["incidentid@odata.bind"] = $"/incidents({ticket.Id})"
            }
        };
        await SendAsync(HttpMethod.Post, "CloseIncident", body, ct);
    }

    public Task CancelAsync(Ticket ticket, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Patch, $"incidents({ticket.Id})", new JsonObject { ["statecode"] = Cancelled, ["statuscode"] = 6 }, ct);

    public Task ReopenAsync(Ticket ticket, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Patch, $"incidents({ticket.Id})", new JsonObject { ["statecode"] = Active, ["statuscode"] = 1 }, ct);

    public async Task<List<TicketNote>> GetNotesAsync(string ticketId, CancellationToken ct = default)
    {
        var filter = Uri.EscapeDataString($"_objectid_value eq {ticketId}");
        var json = await GetAsync($"annotations?$select=subject,notetext,createdon,_createdby_value&$filter={filter}&$orderby=createdon desc", ct);
        return (json["value"]?.AsArray() ?? new JsonArray()).Where(n => n != null).Select(n => new TicketNote(
            n!["subject"]?.ToString() ?? "", n["notetext"]?.ToString() ?? "", Name(n, "_createdby_value"), ParseDate(n["createdon"]))).ToList();
    }

    public Task AddNoteAsync(string ticketId, string text, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "annotations", new JsonObject
        {
            ["subject"] = "Note from Task Timer", ["notetext"] = text, ["objectid_incident@odata.bind"] = $"/incidents({ticketId})"
        }, ct);

    /// <summary>Accounts and contacts whose name contains the text.</summary>
    public async Task<List<CrmRef>> SearchCustomersAsync(string text, CancellationToken ct = default)
    {
        var name = Uri.EscapeDataString($"contains(name,{Quote(text)})");
        var fullName = Uri.EscapeDataString($"contains(fullname,{Quote(text)})");
        var accounts = await GetAsync($"accounts?$select=accountid,name&$filter={name}&$orderby=name&$top=25", ct);
        var contacts = await GetAsync($"contacts?$select=contactid,fullname&$filter={fullName}&$orderby=fullname&$top=25", ct);
        return (accounts["value"]?.AsArray() ?? new JsonArray()).Select(a => new CrmRef(a!["accountid"]!.ToString(), a["name"]?.ToString() ?? "", "account"))
            .Concat((contacts["value"]?.AsArray() ?? new JsonArray()).Select(c => new CrmRef(c!["contactid"]!.ToString(), c["fullname"]?.ToString() ?? "", "contact")))
            .ToList();
    }

    public async Task<List<CrmRef>> SearchUsersAsync(string text, CancellationToken ct = default)
    {
        var filter = Uri.EscapeDataString($"isdisabled eq false and contains(fullname,{Quote(text)})");
        var users = await GetAsync($"systemusers?$select=systemuserid,fullname&$filter={filter}&$orderby=fullname&$top=25", ct);
        return (users["value"]?.AsArray() ?? new JsonArray())
            .Select(u => new CrmRef(u!["systemuserid"]!.ToString(), u["fullname"]?.ToString() ?? "", "systemuser")).ToList();
    }

    /// <summary>
    /// Records time as a completed task activity on the ticket, which is where Dynamics 365 counts
    /// the time spent when a case is resolved.
    /// </summary>
    public async Task LogTimeAsync(string ticketId, double hours, CancellationToken ct = default)
    {
        var created = await SendAsync(HttpMethod.Post, "tasks?$select=activityid", new JsonObject
        {
            ["subject"] = "Time logged via Task Timer",
            ["actualdurationminutes"] = Math.Max(1, (int)Math.Round(hours * 60)),
            ["regardingobjectid_incident_task@odata.bind"] = $"/incidents({ticketId})"
        }, ct);
        await SendAsync(HttpMethod.Patch, $"tasks({created["activityid"]})", new JsonObject { ["statecode"] = 1, ["statuscode"] = 5 }, ct);
    }
}

/// <summary>A ticket in the task list, so it can be timed. Saved on this computer, not per account.</summary>
public sealed class TrackedTicket
{
    public string Id { get; set; } = "";
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public string Customer { get; set; } = "";
    public string WebUrl { get; set; } = "";
    public DateTime? Created { get; set; }
}

public sealed class TicketStore
{
    private const string FileName = "tickets.json";
    public const string KeyPrefix = "crm:";

    public List<TrackedTicket> Tickets { get; set; } = new();

    public static TicketStore Load() => SettingsStore.LoadJson<TicketStore>(FileName);

    public void Save() => SettingsStore.SaveJson(FileName, this);

    public static bool IsTicket(WorkItem item) => item.Key.StartsWith(KeyPrefix, StringComparison.Ordinal);

    public static string IdOf(WorkItem item) => item.Key[KeyPrefix.Length..];

    /// <summary>Adds the ticket to the list, or refreshes it when it is already there.</summary>
    public TrackedTicket Track(Ticket ticket)
    {
        var tracked = Tickets.FirstOrDefault(t => t.Id == ticket.Id);
        if (tracked == null) Tickets.Add(tracked = new TrackedTicket { Id = ticket.Id });
        tracked.Number = ticket.Number;
        tracked.Title = ticket.Title;
        tracked.Status = ticket.Status;
        tracked.Customer = ticket.Customer;
        tracked.WebUrl = ticket.WebUrl;
        tracked.Created = ticket.Created;
        Save();
        return tracked;
    }

    public void Untrack(WorkItem item)
    {
        Tickets.RemoveAll(t => t.Id == IdOf(item));
        Save();
    }

    public static WorkItem ToWorkItem(TrackedTicket t) => new()
    {
        Key = KeyPrefix + t.Id,
        Id = t.Number,
        Title = t.Title,
        Url = t.WebUrl,
        Type = "Ticket",
        State = t.Status,
        Project = "CRM",
        Customer = t.Customer,
        CreatedDate = t.Created
    };

    public IEnumerable<WorkItem> ToWorkItems() => Tickets.Select(ToWorkItem);
}
