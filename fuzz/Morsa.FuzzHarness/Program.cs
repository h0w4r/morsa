namespace Morsa.FuzzHarness;

/// <summary>Punto de entrada del fuzzing mutacional reproducible de parsers.</summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = FuzzOptions.Parse(args);
            return options.WorkerMode
                ? await ParserWorker.ExecuteAsync(options).ConfigureAwait(false)
                : await FuzzController.ExecuteAsync(options).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"argument_error: {exception.Message}");
            FuzzOptions.PrintUsage();
            return ExitCodes.InvalidArguments;
        }
        catch (Exception exception)
        {
            // El controlador nunca debe ocultar una avería propia como si fuera un hallazgo del parser.
            Console.Error.WriteLine($"harness_error: {exception.GetType().Name}: {exception.Message}");
            return ExitCodes.HarnessFailure;
        }
    }
}

/// <summary>Códigos estables consumidos por scripts y automatizaciones.</summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int FindingDetected = 1;
    public const int InvalidArguments = 2;
    public const int InputRejected = 3;
    public const int HarnessFailure = 4;
    public const int Timeout = 124;
    public const int ParserCrash = 100;
    public const int InvariantViolation = 101;
}
