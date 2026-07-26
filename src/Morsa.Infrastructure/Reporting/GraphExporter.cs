using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;

namespace Morsa.Infrastructure.Reporting;

/// <summary>Exports graph data using deterministic ordering and XML-safe values.</summary>
public sealed class GraphExporter(IMorsaStore store)
{
    public async Task ExportAsync(Guid projectId, string format, string path, CancellationToken cancellationToken)
    {
        var nodes = await store.Entities.Where(item => item.ProjectId == projectId).OrderBy(item => item.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var edges = await store.Relations.Where(item => item.ProjectId == projectId).OrderBy(item => item.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var content = format.ToLowerInvariant() switch
        {
            "dot" => ToDot(nodes, edges),
            "graphml" => ToGraphMl(nodes, edges),
            "gexf" => ToGexf(nodes, edges),
            "csv" => ToCsv(nodes, edges),
            _ => throw new ArgumentException("Format must be dot, graphml, gexf or csv.", nameof(format)),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    private static string ToDot(IEnumerable<Domain.Correlation.EntityNode> nodes, IEnumerable<Domain.Correlation.EntityRelation> edges)
    {
        var builder = new StringBuilder("digraph morsa {\n");
        foreach (var node in nodes) builder.Append("  \"").Append(node.Id).Append("\" [label=\"").Append(EscapeDot($"{node.Type}: {node.Value}")).Append("\"];\n");
        foreach (var edge in edges) builder.Append("  \"").Append(edge.FromEntityId).Append("\" -> \"").Append(edge.ToEntityId).Append("\" [label=\"").Append(EscapeDot(edge.Type)).Append("\"];\n");
        return builder.Append("}\n").ToString();
    }

    private static string ToGraphMl(IEnumerable<Domain.Correlation.EntityNode> nodes, IEnumerable<Domain.Correlation.EntityRelation> edges)
    {
        var builder = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><graphml xmlns=\"http://graphml.graphdrawing.org/xmlns\"><graph edgedefault=\"directed\">");
        foreach (var node in nodes) builder.Append("<node id=\"").Append(node.Id).Append("\"><data key=\"type\">").Append(WebUtility.HtmlEncode(node.Type)).Append("</data><data key=\"value\">").Append(WebUtility.HtmlEncode(node.Value)).Append("</data></node>");
        foreach (var edge in edges) builder.Append("<edge source=\"").Append(edge.FromEntityId).Append("\" target=\"").Append(edge.ToEntityId).Append("\"><data key=\"type\">").Append(WebUtility.HtmlEncode(edge.Type)).Append("</data></edge>");
        return builder.Append("</graph></graphml>").ToString();
    }

    private static string ToGexf(IEnumerable<Domain.Correlation.EntityNode> nodes, IEnumerable<Domain.Correlation.EntityRelation> edges)
    {
        var builder = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><gexf xmlns=\"http://gexf.net/1.3\" version=\"1.3\"><graph defaultedgetype=\"directed\"><nodes>");
        foreach (var node in nodes) builder.Append("<node id=\"").Append(node.Id).Append("\" label=\"").Append(WebUtility.HtmlEncode($"{node.Type}: {node.Value}")).Append("\"/>");
        builder.Append("</nodes><edges>");
        foreach (var edge in edges) builder.Append("<edge id=\"").Append(edge.Id).Append("\" source=\"").Append(edge.FromEntityId).Append("\" target=\"").Append(edge.ToEntityId).Append("\" label=\"").Append(WebUtility.HtmlEncode(edge.Type)).Append("\"/>");
        return builder.Append("</edges></graph></gexf>").ToString();
    }

    private static string ToCsv(IEnumerable<Domain.Correlation.EntityNode> nodes, IEnumerable<Domain.Correlation.EntityRelation> edges)
    {
        var builder = new StringBuilder("record,id,type,value,from,to\n");
        foreach (var node in nodes) builder.Append("node,").Append(node.Id).Append(',').Append(Csv(node.Type)).Append(',').Append(Csv(node.Value)).Append(",,\n");
        foreach (var edge in edges) builder.Append("edge,").Append(edge.Id).Append(',').Append(Csv(edge.Type)).Append(",,").Append(edge.FromEntityId).Append(',').Append(edge.ToEntityId).Append('\n');
        return builder.ToString();
    }

    private static string EscapeDot(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

