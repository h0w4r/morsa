using Morsa.Domain.Common;

namespace Morsa.Domain.Correlation;

/// <summary>Normalized node in the investigation graph.</summary>
public sealed class EntityNode : Entity
{
    public Guid ProjectId { get; set; }

    public required string Type { get; set; }

    public required string Value { get; set; }

    public required string NormalizedValue { get; set; }

    public double Confidence { get; set; }
}

/// <summary>Evidence-backed directed relationship between two graph nodes.</summary>
public sealed class EntityRelation : Entity
{
    public Guid ProjectId { get; set; }

    public Guid FromEntityId { get; set; }

    public Guid ToEntityId { get; set; }

    public required string Type { get; set; }

    public Guid EvidenceId { get; set; }

    public double Confidence { get; set; }
}

