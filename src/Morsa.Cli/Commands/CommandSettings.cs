using Spectre.Console.Cli;

namespace Morsa.Cli.Commands;

/// <summary>Common options accepted by workspace commands.</summary>
public class WorkspaceSettings : CommandSettings
{
    [CommandOption("--project <PATH>")]
    public string? ProjectPath { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}


