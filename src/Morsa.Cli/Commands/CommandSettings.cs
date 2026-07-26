using Spectre.Console.Cli;

namespace Morsa.Cli.Commands;

/// <summary>Common options accepted by workspace commands.</summary>
public class WorkspaceSettings : CommandSettings
{
    private bool _json;

    [CommandOption("--project <PATH>")]
    public string? ProjectPath { get; init; }

    [CommandOption("--json")]
    public bool Json { get => _json || Ndjson; init => _json = value; }

    [CommandOption("--ndjson")]
    public bool Ndjson { get; init; }
}


