using Morsa.Application.Abstractions;
using Morsa.Domain.Correlation;

namespace Morsa.Application.Services;

/// <summary>Builds normalized project entities while preserving observation provenance.</summary>
public sealed class CorrelationService(IMorsaStore store)
{
    private static readonly HashSet<string> EntityCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "author", "last_saved_by", "email", "username", "hostname", "server", "domain",
        "url", "application", "company", "manager", "printer", "path", "unc_path", "gps",
    };

    public async Task<int> CorrelateAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var observations = (
                from observation in store.MetadataObservations
                join artifact in store.Artifacts on observation.ArtifactId equals artifact.Id
                join run in store.Runs on artifact.RunId equals run.Id
                where run.ProjectId == projectId && EntityCategories.Contains(observation.Category)
                select observation)
            .ToList();

        var existing = store.Entities
            .Where(entity => entity.ProjectId == projectId)
            .ToList();
        var keys = existing.Select(entity => (entity.Type, entity.NormalizedValue)).ToHashSet();
        var added = 0;

        foreach (var observation in observations)
        {
            var key = (observation.Category, observation.NormalizedValue);
            if (!keys.Add(key))
            {
                continue;
            }

            store.Add(new EntityNode
            {
                ProjectId = projectId,
                Type = observation.Category,
                Value = observation.OriginalValue,
                NormalizedValue = observation.NormalizedValue,
                Confidence = observation.Confidence,
            });
            added++;
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return added;
    }
}
