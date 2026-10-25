using System.Globalization;
using Merit.Application.Data;
using Merit.Application.Security;
using Merit.Domain.Text;

namespace Merit.Application.Services;

/// <summary>Writes the audit ledger: every view, change, merge and export of a person, result or case.</summary>
public sealed class AuditService(IWorkData data)
{
    public Task ViewedAsync(string entityType, Guid id, CancellationToken ct = default)
        => data.RecordAuditAsync("view", entityType, id, null, null, ct);

    public Task ChangedAsync(string entityType, Guid id, CancellationToken ct = default)
        => data.RecordAuditAsync("update", entityType, id, null, null, ct);

    public Task CreatedAsync(string entityType, Guid id, CancellationToken ct = default)
        => data.RecordAuditAsync("create", entityType, id, null, null, ct);

    public Task ExportedAsync(string entityType, Guid? id, int rowCount, CancellationToken ct = default)
        => data.RecordAuditAsync("export", entityType, id, rowCount, null, ct);

    public Task ListedAsync(string entityType, int rowCount, CancellationToken ct = default)
        => data.RecordAuditAsync("view", entityType, null, rowCount, null, ct);

    public Task<IReadOnlyList<AuditEventRow>> SearchAsync(ICurrentSession session, Guid? appUserId, Guid? entityId, DateTime? fromUtc, CancellationToken ct = default)
    {
        session.Require(Permissions.AuditView);
        return data.SearchAuditAsync(appUserId, entityId, fromUtc, 200, ct);
    }
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

        return await data.SearchAsync(query.Raw, query.NameKey1, query.NameKey2, query.Dob, query.AddressKey, 50, ct)
            .ConfigureAwait(false);
    }

    public sealed record ParsedQuery(string Raw, string? NameKey1, string? NameKey2, DateOnly? Dob, string? AddressKey)
    {
        public bool IsEmpty => string.IsNullOrWhiteSpace(Raw);
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
        foreach (var token in raw.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (dob is null && DateOnly.TryParseExact(token, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                dob = d;
            }
            else if (!token.Any(char.IsDigit))
            {
                nameTokens.Add(token);
            }
        }

        string? addressKey = char.IsDigit(raw[0]) && raw.Any(char.IsLetter) ? MatchKeys.AddressSearchPrefix(raw) : null;
        string? k1 = nameTokens.Count > 0 ? MatchKeys.NameKey(nameTokens[0]) : null;
        string? k2 = nameTokens.Count > 1 ? MatchKeys.NameKey(nameTokens[1]) : null;
        if (addressKey is not null)
        {
            k1 = null;
            k2 = null;
        }

        return new ParsedQuery(raw, k1, k2, dob, addressKey);
    }
}
