using System.Globalization;
using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Security;
using Merit.Domain.Text;

namespace Merit.Application.Services;

/// <summary>
/// Writes the audit ledger: every view, change, merge, search, export and permission denial. Scoped per request so
/// duplicate reads of the same record within one request are collapsed to a single row (#333), and every event carries
/// the request's client address and W3C trace id (#326).
/// </summary>
public sealed class AuditService(IWorkData data, IRequestAuditContext? request = null)
{
    private readonly IRequestAuditContext context = request ?? NullRequestAuditContext.Instance;
    private readonly HashSet<(string EntityType, Guid EntityId)> viewedThisRequest = [];
    private bool permissionDeniedRecorded;
    private bool notFoundRecorded;

    public Task ViewedAsync(string entityType, Guid id, CancellationToken ct = default, string viewKind = "direct")
    {
        if (!viewedThisRequest.Add((entityType, id)))
        {
            return Task.CompletedTask;
        }

        return data.RecordAuditAsync("view", entityType, id, null, context.ClientAddress, ct, viewKind: viewKind, traceId: context.TraceId);
    }

    public Task ViewedIncidentalAsync(string entityType, Guid id, CancellationToken ct = default)
        => ViewedAsync(entityType, id, ct, viewKind: "incidental");

    public Task ChangedAsync(string entityType, Guid id, CancellationToken ct = default)
        => data.RecordAuditAsync("update", entityType, id, null, context.ClientAddress, ct, traceId: context.TraceId);

    public Task CreatedAsync(string entityType, Guid id, CancellationToken ct = default)
        => data.RecordAuditAsync("create", entityType, id, null, context.ClientAddress, ct, traceId: context.TraceId);

    public Task SearchedAsync(string entityType, int rowCount, CancellationToken ct = default, object? filter = null,
                              IEnumerable<AuditManifestEntry>? manifest = null)
        => data.RecordAuditAsync("search", entityType, null, rowCount, context.ClientAddress, ct, viewKind: "direct",
            filterJson: SerializeFilter(filter), recordIdsJson: SerializeManifest(manifest), traceId: context.TraceId);

    public Task ExportedAsync(string entityType, Guid? id, int rowCount, CancellationToken ct = default, object? filter = null,
                              IEnumerable<AuditManifestEntry>? manifest = null)
        => data.RecordAuditAsync("export", entityType, id, rowCount, context.ClientAddress, ct,
            filterJson: SerializeFilter(filter), recordIdsJson: SerializeManifest(manifest), traceId: context.TraceId);

    public Task ListedAsync(string entityType, int rowCount, CancellationToken ct = default, object? filter = null,
                            IEnumerable<AuditManifestEntry>? manifest = null)
        => data.RecordAuditAsync("view", entityType, null, rowCount, context.ClientAddress, ct, viewKind: "direct",
            filterJson: SerializeFilter(filter), recordIdsJson: SerializeManifest(manifest), traceId: context.TraceId);

    /// <summary>Records a free-text search in logs.searchLog and a matching search event (+ optional manifest) in logs.auditEvent (#331).</summary>
    public Task RecordSearchAsync(string searchText, int rowCount, string searchSource, string entityType, CancellationToken ct = default,
                                  object? filter = null, IEnumerable<AuditManifestEntry>? manifest = null)
        => data.RecordSearchLogAsync(searchText, rowCount, searchSource, entityType, SerializeFilter(filter), SerializeManifest(manifest), ct);

    /// <summary>Records permissionDenied once per request (#325), even when caught by a controller or policy handler.</summary>
    public Task PermissionDeniedAsync(string entityType, Guid? entityId = null, CancellationToken ct = default)
    {
        if (permissionDeniedRecorded)
        {
            return Task.CompletedTask;
        }

        permissionDeniedRecorded = true;
        return data.RecordAuditAsync("permissionDenied", entityType, entityId, null, context.ClientAddress, ct, traceId: context.TraceId);
    }

    /// <summary>Records an authenticated record-lookup 404 once per request for probing detection (#327).</summary>
    public Task NotFoundAsync(string entityType, Guid? entityId = null, CancellationToken ct = default)
    {
        if (notFoundRecorded)
        {
            return Task.CompletedTask;
        }

        notFoundRecorded = true;
        return data.RecordAuditAsync("notFound", entityType, entityId, null, context.ClientAddress, ct, traceId: context.TraceId);
    }

    public Task<IReadOnlyList<AuditEventRow>> SearchAsync(ICurrentSession session, AuditQuery query, CancellationToken ct = default)
    {
        session.Require(Permissions.AuditView);
        return data.SearchAuditAsync(query, ct);
    }

    /// <summary>Record manifest for one list, export or batch audit event, scoped per record (#333, #341).</summary>
    public Task<AuditEventRecordsResult> RecordsAsync(ICurrentSession session, long auditEventId, CancellationToken ct = default)
    {
        session.Require(Permissions.AuditView);
        return data.GetAuditEventRecordsAsync(auditEventId, ct);
    }

    /// <summary>The users who appear in the ledger, for its user filter.</summary>
    public Task<IReadOnlyList<AuditUserRow>> UsersAsync(ICurrentSession session, CancellationToken ct = default)
    {
        session.Require(Permissions.AuditView);
        return data.ListAuditUsersAsync(ct);
    }

    private static string? SerializeFilter(object? filter)
        => filter is null ? null : JsonSerializer.Serialize(filter, JsonOpts);

    private static string? SerializeManifest(IEnumerable<AuditManifestEntry>? manifest)
    {
        if (manifest is null) return null;
        var list = manifest.Distinct().ToList();
        return list.Count == 0 ? null : JsonSerializer.Serialize(list, JsonOpts);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// Global search by name, date of birth, address, organization, accession number or case number. The text is parsed
/// here into match keys; the stored procedure searches within the caller's scope and reports only that a record exists
/// elsewhere on an exact name and DOB match outside it.
/// </summary>
public sealed class SearchService(IPersonData data, ICurrentSession session)
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "M-d-yyyy", "MMddyyyy", "yyyyMMdd"];

    public async Task<SearchResults> SearchAsync(string? text, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonView);
        var query = Parse(text);
        if (query.IsEmpty)
        {
            return new SearchResults([], [], [], false);
        }

        return await data.SearchAsync(query.Raw, query.NameKey1, query.NameKey2, query.Dob, query.AddressKey, 50, ct, JsonSerializer.Serialize(query.NamePairs.Select(p => new { key1 = p.Key1, key2 = p.Key2 })))
            .ConfigureAwait(false);
    }

    public sealed record ParsedQuery(string Raw, string? NameKey1, string? NameKey2, DateOnly? Dob, string? AddressKey)
    {
        public IReadOnlyList<(string Key1, string Key2)> NamePairs { get; init; } = [];
        public bool IsEmpty => string.IsNullOrWhiteSpace(Raw);
    }

    /// <summary>
    /// The person a name search describes, as typed, to start "Add a person" from (issue #147): "Rivera, Mia 2023-05-14" or
    /// "Rivera Mia 2023-05-14" is last name Rivera, first name Mia. A search for an address, case or accession number
    /// describes nobody.
    /// </summary>
    public static (string? LastName, string? FirstName, DateOnly? Dob) PersonFrom(string? text)
    {
        var query = Parse(text);
        if (query.IsEmpty || query.AddressKey is not null)
        {
            return (null, null, null);
        }

        var tokens = query.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !DateOnly.TryParseExact(t.TrimEnd(','), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .ToList();
        if (tokens.Count == 0 || tokens.Any(t => t.Any(char.IsDigit)))
        {
            return (null, null, query.Dob);
        }

        string names = string.Join(' ', tokens);
        int comma = names.IndexOf(',', StringComparison.Ordinal);
        string last = (comma >= 0 ? names[..comma] : tokens[0]).Trim();
        string first = (comma >= 0 ? names[(comma + 1)..] : string.Join(' ', tokens.Skip(1))).Replace(",", " ", StringComparison.Ordinal).Trim();
        return (last.Length == 0 ? null : last, first.Length == 0 ? null : first, query.Dob);
    }

    /// <summary>"Rivera, Mia 2023-05-14" gives name keys RIVERA and MIA and a date of birth.</summary>
    public static ParsedQuery Parse(string? text)
    {
        string raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return new ParsedQuery(string.Empty, null, null, null, null);
        }

        DateOnly? dob = null;
        var nameTokens = new List<string>();
        var remaining = new List<string>();
        foreach (var token in raw.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (dob is null && DateOnly.TryParseExact(token, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                dob = d;
            }
            else
            {
                remaining.Add(token);
                if (!token.Any(char.IsDigit)) nameTokens.Add(token);
            }
        }

        string rest = string.Join(" ", remaining);
        string? addressKey = remaining.Count > 1 && char.IsDigit(rest[0]) && rest.Any(char.IsLetter) ? MatchKeys.AddressSearchPrefix(rest) : null;
        string? k1 = nameTokens.Count > 0 ? MatchKeys.NameKey(nameTokens[0]) : null;
        string? k2 = nameTokens.Count > 1 ? MatchKeys.NameKey(nameTokens[^1]) : null;
        if (addressKey is not null)
        {
            k1 = null;
            k2 = null;
        }

        var pairs = new HashSet<(string Key1, string Key2)>();
        if (addressKey is null && nameTokens.Count > 2)
        {
            for (int split = 1; split < nameTokens.Count; split++)
            {
                string left = MatchKeys.NameKey(string.Join(" ", nameTokens.Take(split)))!;
                string right = MatchKeys.NameKey(string.Join(" ", nameTokens.Skip(split)))!;
                pairs.Add((left, right));
            }
        }
        return new ParsedQuery(raw, k1, k2, dob, addressKey) { NamePairs = pairs.ToList() };
    }
}
