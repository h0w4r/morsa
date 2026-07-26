namespace Morsa.Domain.Common;

/// <summary>Declares how intrusive an operation is allowed to be.</summary>
public enum ActivityMode
{
    Passive,
    Active,
    Aggressive,
}

/// <summary>Durable lifecycle shared by runs and tasks.</summary>
public enum ExecutionStatus
{
    Pending,
    Running,
    Completed,
    CompletedWithFallbacks,
    PartiallyFailed,
    Failed,
    Cancelled,
}

/// <summary>High-level artifact family selected after content inspection.</summary>
public enum ArtifactKind
{
    Unknown,
    OleCompound,
    OpenXml,
    OpenDocument,
    Pdf,
    Image,
    Svg,
    InDesign,
    Rdp,
    Ica,
    WordPerfect,
    Text,
    Zip,
}

/// <summary>Normalized severity used in reports and machine contracts.</summary>
public enum FindingSeverity
{
    Informational,
    Low,
    Medium,
    High,
    Critical,
}

