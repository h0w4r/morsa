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
                select new { Observation = observation, Artifact = artifact })
            .ToList();

        var existing = store.Entities
            .Where(entity => entity.ProjectId == projectId)
            .ToList();
        var keys = existing.Select(entity => (entity.Type, entity.NormalizedValue)).ToHashSet();
        var evidence = store.Evidence.ToList();
        var relations = store.Relations.Where(item => item.ProjectId == projectId).ToList();
        var added = 0;

        foreach (var item in observations)
        {
            var observation = item.Observation;
            var key = (observation.Category, observation.NormalizedValue);
            var target = existing.FirstOrDefault(entity => entity.Type == key.Category && entity.NormalizedValue == key.NormalizedValue);
            if (target is null)
            {
                target = new EntityNode
                {
                    ProjectId = projectId,
                    Type = observation.Category,
                    Value = observation.OriginalValue,
                    NormalizedValue = observation.NormalizedValue,
                    Confidence = observation.Confidence,
                };
                store.Add(target);
                existing.Add(target);
                keys.Add(key);
                added++;
            }

            var artifactKey = ("artifact", item.Artifact.Sha256);
            var artifactNode = existing.FirstOrDefault(entity => entity.Type == artifactKey.Item1 && entity.NormalizedValue == artifactKey.Sha256);
            if (artifactNode is null)
            {
                artifactNode = new EntityNode
                {
                    ProjectId = projectId,
                    Type = "artifact",
                    Value = item.Artifact.OriginalPath ?? item.Artifact.SourceUri ?? item.Artifact.Sha256,
                    NormalizedValue = item.Artifact.Sha256,
                    Confidence = 1,
                };
                store.Add(artifactNode);
                existing.Add(artifactNode);
                keys.Add(artifactKey);
                added++;
            }

            var proof = evidence.FirstOrDefault(value => value.ObservationId == observation.Id);
            if (proof is null)
            {
                proof = new Domain.Artifacts.Evidence
                {
                    ArtifactId = item.Artifact.Id,
                    ObservationId = observation.Id,
                    Source = observation.Extractor,
                    Value = observation.OriginalValue,
                    Location = observation.Location,
                    ArtifactSha256 = item.Artifact.Sha256,
                };
                store.Add(proof);
                evidence.Add(proof);
            }

            if (!relations.Any(relation => relation.FromEntityId == artifactNode.Id && relation.ToEntityId == target.Id && relation.Type == "contains"))
            {
                var relation = new EntityRelation
                {
                    ProjectId = projectId,
                    FromEntityId = artifactNode.Id,
                    ToEntityId = target.Id,
                    Type = "contains",
                    EvidenceId = proof.Id,
                    Confidence = observation.Confidence,
                };
                store.Add(relation);
                relations.Add(relation);
            }
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return added;
    }
}
