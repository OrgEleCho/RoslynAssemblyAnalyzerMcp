using Microsoft.CodeAnalysis;
using NuGet.Protocol.Core.Types;

namespace RoslynAssemblyAnalyzerMcp;

public enum AssemblySourceKind
{
    NuGetPackage,
    LocalFile
}

public enum PackageAssemblyGroupKind
{
    None,
    Lib,
    Ref,
    NetFrameworkReferenceAssemblies
}

public sealed class PackageInfo
{
    public required string PackageId { get; init; }
    public required string PackageVersion { get; init; }
    public required IPackageSearchMetadata Metadata { get; init; }
    public PackageAssemblyGroupKind AssemblyGroupKind { get; init; }
    public List<PackageTargetFrameworkInfo> TargetFrameworks { get; } = [];

    public IReadOnlyList<AssemblyAnalysisInfo> Assemblies =>
        [.. TargetFrameworks.SelectMany(framework => framework.Assemblies)];
}

public sealed class PackageTargetFrameworkInfo
{
    public required string TargetFramework { get; init; }
    public List<PackageAssemblyFileInfo> AssemblyFiles { get; } = [];
    public List<AssemblyAnalysisInfo> Assemblies { get; } = [];
}

public sealed class PackageAssemblyFileInfo
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
}

public sealed class PackageAssemblySelection
{
    public required PackageInfo Package { get; init; }
    public required IPackageSearchMetadata Metadata { get; init; }
    public required PackageAssemblyGroupKind AssemblyGroupKind { get; init; }
    public required PackageTargetFrameworkInfo TargetFrameworkInfo { get; init; }
    public required string TargetFramework { get; init; }
    public required PackageAssemblyFileInfo AssemblyFile { get; init; }
}

public sealed class AssemblyAnalysisInfo
{
    public required AssemblySourceKind SourceKind { get; init; }
    public string? PackageId { get; init; }
    public string? PackageVersion { get; init; }
    public string? TargetFramework { get; init; }
    public required string AssemblyName { get; init; }
    public required string AssemblyVersion { get; init; }
    public required string AssemblyPath { get; init; }
    public byte[]? AssemblyImage { get; init; }

    public bool IsRefAssembly { get; set; }

    public required IAssemblySymbol AssemblySymbol { get; init; }

    public List<INamedTypeSymbol> AllTypes { get; set; } = [];
    public List<string> Namespaces { get; set; } = [];
    public int AllTypesCount => AllTypes.Count;
    public int PublicTypesCount { get; set; }
    public int ClassesCount { get; set; }
    public int StaticClassesCount { get; set; }
    public int InterfacesCount { get; set; }
    public int EnumsCount { get; set; }
    public int StructsCount { get; set; }
    public int DelegatesCount { get; set; }
    public List<string> References { get; set; } = [];
}

public readonly record struct AssemblyAnalysisCacheKey(
    AssemblySourceKind SourceKind,
    string? PackageId,
    string? PackageVersion,
    string? TargetFramework,
    string AssemblyName,
    string AssemblyPath,
    long? AssemblyFileLength = null,
    long? AssemblyLastWriteTimeUtcTicks = null);
