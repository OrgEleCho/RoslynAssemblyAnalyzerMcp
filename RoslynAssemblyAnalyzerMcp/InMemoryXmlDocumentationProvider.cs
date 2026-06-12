using Microsoft.CodeAnalysis;
using System.Globalization;
using System.Xml.Linq;

namespace RoslynAssemblyAnalyzerMcp;

internal sealed class InMemoryXmlDocumentationProvider : DocumentationProvider
{
    private readonly string _path;
    private readonly IReadOnlyDictionary<string, string> _documentation;
    private readonly int _hashCode;

    private InMemoryXmlDocumentationProvider(string path, IReadOnlyDictionary<string, string> documentation, int hashCode)
    {
        _path = path;
        _documentation = documentation;
        _hashCode = hashCode;
    }

    public static InMemoryXmlDocumentationProvider CreateFromFile(string path)
    {
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan
        });

        var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        var members = document
            .Descendants("member")
            .Select(member => new
            {
                Name = (string?)member.Attribute("name"),
                Documentation = string.Concat(member.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)))
            })
            .Where(member => !string.IsNullOrWhiteSpace(member.Name))
            .GroupBy(member => member.Name!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Documentation, StringComparer.Ordinal);

        var fileInfo = new FileInfo(path);
        return new InMemoryXmlDocumentationProvider(
            Path.GetFullPath(path),
            members,
            HashCode.Combine(Path.GetFullPath(path).ToUpperInvariant(), fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks));
    }

    protected override string GetDocumentationForSymbol(
        string documentationMemberID,
        CultureInfo preferredCulture,
        CancellationToken cancellationToken)
    {
        return _documentation.TryGetValue(documentationMemberID, out var documentation)
            ? documentation
            : string.Empty;
    }

    public override bool Equals(object? obj)
    {
        return obj is InMemoryXmlDocumentationProvider other
            && _hashCode == other._hashCode
            && string.Equals(_path, other._path, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return _hashCode;
    }
}
