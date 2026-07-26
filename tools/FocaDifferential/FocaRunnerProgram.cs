// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using MetadataExtractCore.Extractors;

/// <summary>Emits a stable category-count contract from the pinned FOCA extractor.</summary>
internal static class FocaRunnerProgram
{
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        var path = Path.GetFullPath(args[0]);
        try
        {
            using (var stream = File.OpenRead(path))
            using (var extractor = DocumentExtractor.Create(Path.GetExtension(path), stream))
            {
                var metadata = extractor.AnalyzeFile();
                Write("users", metadata.Users.Count);
                Write("applications", metadata.Applications.Count);
                Write("emails", metadata.Emails.Count);
                Write("paths", metadata.Paths.Count);
                Write("servers", metadata.Servers.Count);
                Write("printers", metadata.Printers.Count);
                Write("password_indicators", metadata.Passwords.Count);
                Write("history", metadata.History.Count);
                Write("old_versions", metadata.OldVersions.Count);
                Write("title", String.IsNullOrWhiteSpace(metadata.Title) ? 0 : 1);
                Write("company", String.IsNullOrWhiteSpace(metadata.Company) ? 0 : 1);
                Write("operating_system", String.IsNullOrWhiteSpace(metadata.OperatingSystem) ? 0 : 1);
                Write("gps", metadata.GPS == null ? 0 : 1);
                return 0;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
    }

    private static void Write(string category, int count) => Console.WriteLine(category + "=" + count);
}
