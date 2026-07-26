namespace Morsa.Application.Models;

/// <summary>Stable machine-readable envelope emitted by CLI and MCP adapters.</summary>
public sealed record OutputEnvelope<T>(
    string SchemaVersion,
    bool Success,
    T? Data,
    IReadOnlyList<OperationError> Errors,
    string? RunId = null,
    string? Coverage = null);

/// <summary>Sanitized error returned to callers while full diagnostics remain in logs.</summary>
public sealed record OperationError(string Code, string Message, bool Retryable = false);

/// <summary>Exception carrying an exit code and safe error identifier.</summary>
public sealed class MorsaException : Exception
{
    public MorsaException(string code, string message, int exitCode, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        ExitCode = exitCode;
    }

    public string Code { get; }

    public int ExitCode { get; }
}

