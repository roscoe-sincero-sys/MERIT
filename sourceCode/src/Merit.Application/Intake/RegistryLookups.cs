using Merit.Application.Data;

namespace Merit.Application.Intake;

/// <summary>
/// Registry lookups for one intake batch. A file usually comes from one laboratory and a few ordering providers, so each
/// distinct CLIA number or NPI is looked up once and the answer, found or not, is reused for the rest of the batch
/// (issue #14). Not shared between batches, so a registry change is seen by the next batch.
/// </summary>
public sealed class RegistryLookups(IPlaceData places)
{
    private readonly Dictionary<string, Guid?> laboratories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid?> providers = new(StringComparer.Ordinal);

    /// <summary>The laboratory with this CLIA number, or null when the number is blank or not in the registry.</summary>
    public async Task<Guid?> LaboratoryAsync(string? clia, CancellationToken ct)
    {
        if (clia is null)
        {
            return null;
        }

        if (!laboratories.TryGetValue(clia, out var id))
        {
            id = (await places.ResolveOrganizationAsync(clia, null, null, ct).ConfigureAwait(false))?.OrganizationId;
            laboratories[clia] = id;
        }

        return id;
    }

    /// <summary>The provider with exactly this NPI, or null when it is not in the registry.</summary>
    public async Task<Guid?> ProviderAsync(string npi, CancellationToken ct)
    {
        if (!providers.TryGetValue(npi, out var id))
        {
            id = (await places.GetProviderByNpiAsync(npi, ct).ConfigureAwait(false))?.ProviderId;
            providers[npi] = id;
        }

        return id;
    }
}
