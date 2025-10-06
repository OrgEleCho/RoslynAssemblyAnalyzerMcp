using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Common;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Caching.Memory;
using System.Globalization;

namespace RoslynAssemblyAnalyzerMcp;

public class RoslynService
{
    private List<PackageSource> _packageSources = null!;
    public SourceRepository SourceRepository { get; private set; } = null!;
    public PackageSearchResource SearchResource { get; private set; } = null!;
    public PackageMetadataResource MetadataResource { get; private set; } = null!;
    public DownloadResource DownloadResource { get; private set; } = null!;

    public string GlobalPackagePath { get; private set; } = null!;

    private readonly MemoryCache _metadataCache = new(new MemoryCacheOptions());

    private readonly MemoryCache _assemblyAnalysisInfoCache = new(new MemoryCacheOptions());
    private readonly MemoryCache _assemblyAnalysisInfoPackageIdCache = new(new MemoryCacheOptions());

    public async Task Initialize()
    {
        if (OperatingSystem.IsWindows())
        {
            GlobalPackagePath = Environment.ExpandEnvironmentVariables(@"%UserProfile%\.nuget\packages");
        }
        else
        {
            GlobalPackagePath = "~/.nuget/packages";
        }

        var settings = Settings.LoadDefaultSettings(root: null);
        var packageSourceProvider = new PackageSourceProvider(settings);
        _packageSources = packageSourceProvider.LoadPackageSources().Where(s => s.IsEnabled).ToList();

        // 使用第一个启用的源进行搜索 (通常是 nuget.org)
        var primarySource = _packageSources.First();

        SourceRepository = Repository.Factory.GetCoreV3(primarySource.Source);
        SearchResource = await SourceRepository.GetResourceAsync<PackageSearchResource>();
        MetadataResource = await SourceRepository.GetResourceAsync<PackageMetadataResource>();
        DownloadResource = await SourceRepository.GetResourceAsync<DownloadResource>();
    }

    public async Task<IReadOnlyList<IPackageSearchMetadata>> SearchNuGetAsync(string text, int maxResult = 10, bool includePrerelease = false, CancellationToken cancellationToken = default)
    {
        var searchFilter = new SearchFilter(includePrerelease);
        var logger = NullLogger.Instance;

        var searchResults = await SearchResource.SearchAsync(
            text,
            searchFilter,
            skip: 0,
            take: maxResult,
            logger,
            cancellationToken);

        var packages = searchResults.ToList();

        return packages;
    }

    public async ValueTask<IReadOnlyList<IPackageSearchMetadata>> GetPackageMetadataAsync(string packageId, bool includePrerelease = false, CancellationToken cancellationToken = default)
    {
        if (_metadataCache.TryGetValue<IReadOnlyList<IPackageSearchMetadata>>(packageId, out var cachedMetadata))
        {
            return cachedMetadata!;
        }
        var result = await MetadataResource.GetMetadataAsync(packageId, includePrerelease, includeUnlisted: false, new SourceCacheContext(), NullLogger.Instance, cancellationToken);
        var list = result.ToList();
        _metadataCache.Set(packageId, list, TimeSpan.FromMinutes(10));
        return list;
    }

    public async Task<DownloadResourceResult> DownloadPackageAsync(NuGet.Packaging.Core.PackageIdentity id, CancellationToken cancellationToken = default)
    {
        var downloadResult = await DownloadResource.GetDownloadResourceResultAsync(
            id,
            new PackageDownloadContext(new SourceCacheContext()),
            GlobalPackagePath, NullLogger.Instance, default);
        return downloadResult;
    }


    public AssemblyAnalysisInfo? AnalyzeAssembly(string packageId, string packageVersion, string assemblyName, string? targetFramework, string assemblyPath, bool isVersionLatest)
    {
        DocumentationProvider documentationProvider = DocumentationProvider.Default;
        var xmlDocumentFilePath = Path.ChangeExtension(assemblyPath, "xml");
        if (File.Exists(xmlDocumentFilePath))
        {
            documentationProvider = XmlDocumentationProvider.CreateFromFile(xmlDocumentFilePath);
        }

        // 使用 Roslyn 分析程序集
        var reference = MetadataReference.CreateFromFile(assemblyPath, documentation: documentationProvider);
        var compilation = CSharpCompilation.Create("_")
            .AddReferences(reference);

        var assemblySymbol = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(reference)!;
        if (assemblySymbol == null)
        {
            return null;
        }

        var attributes = assemblySymbol.GetAttributes();
        bool isRefAssembly = attributes.Any(v => v?.AttributeClass?.ToString() == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute");

        AssemblyAnalysisInfo result = new()
        {
            PackageId = packageId,
            PackageVersion = packageVersion,
            TargetFramework = targetFramework,
            AssemblyName = assemblyName,
            AssemblyVersion = assemblySymbol.Identity.Version.ToString(),
            AssemblyPath = assemblyPath,
            AssemblySymbol = assemblySymbol,
            IsRefAssembly = isRefAssembly,
        };

        // 统计类型
        List<INamedTypeSymbol> allTypes = GetAllTypes(assemblySymbol.GlobalNamespace).ToList();
        result.AllTypes = allTypes;

        var publicTypes = allTypes.Where(t => t.DeclaredAccessibility == Accessibility.Public).ToList();
        var classes = allTypes.Where(t => t.TypeKind == TypeKind.Class && !t.IsStatic).ToList();
        var staticClasses = allTypes.Where(t => t.TypeKind == TypeKind.Class && t.IsStatic).ToList();
        var interfaces = allTypes.Where(t => t.TypeKind == TypeKind.Interface).ToList();
        var enums = allTypes.Where(t => t.TypeKind == TypeKind.Enum).ToList();
        var structs = allTypes.Where(t => t.TypeKind == TypeKind.Struct).ToList();
        var delegates = allTypes.Where(t => t.TypeKind == TypeKind.Delegate).ToList();

        result.PublicTypesCount = publicTypes.Count;
        result.ClassesCount = classes.Count;
        result.StaticClassesCount = staticClasses.Count;
        result.InterfacesCount = interfaces.Count;
        result.EnumsCount = enums.Count;
        result.StructsCount = structs.Count;
        result.DelegatesCount = delegates.Count;

        // 命名空间
        var namespaces = allTypes
            .Where(t => t.ContainingNamespace != null)
            .Select(t => t.ContainingNamespace.ToDisplayString())
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        result.Namespaces = namespaces;

        var references = assemblySymbol.Modules.First().ReferencedAssemblySymbols
            .Select(r => $"{r.Name} ({r.Identity.Version})")
            .OrderBy(r => r)
            .ToList();

        result.References = references;

        var cacheKey = $"{packageId}|{packageVersion}|{assemblyName}|{targetFramework}";
        _assemblyAnalysisInfoCache.Set(cacheKey, result, TimeSpan.FromMinutes(30));
        var cacheList = _assemblyAnalysisInfoCache.GetOrCreate(packageId, v =>
        {
            v.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            return new HashSet<AssemblyAnalysisInfo>();
        })!;
        _assemblyAnalysisInfoCache.Set(packageId, new HashSet<AssemblyAnalysisInfo>(cacheList.Append(result)), TimeSpan.FromMinutes(30));

        if (isVersionLatest)
        {
            var latestVersionCacheKey = $"{packageId}|latest|{assemblyName}|{targetFramework}";
            _assemblyAnalysisInfoCache.Set(latestVersionCacheKey, result, TimeSpan.FromMinutes(10));
        }

        return result;
    }

    public AssemblyAnalysisInfo? GetAnalyzeAssemblyCache(string packageId, string packageVersion, string assemblyName, string? targetFramework)
    {
        var cacheKey = $"{packageId}|{packageVersion}|{assemblyName}|{targetFramework}";
        if (_assemblyAnalysisInfoCache.TryGetValue(cacheKey, out AssemblyAnalysisInfo? result))
        {
            return result!;
        }
        return null;
    }

    public IReadOnlyList<AssemblyAnalysisInfo>? GetAnalyzeAssemblyCache(string packageId)
    {
        if (_assemblyAnalysisInfoPackageIdCache.TryGetValue(packageId, out HashSet<AssemblyAnalysisInfo>? result))
        {
            return [.. result!];
        }
        return null;
    }


    private IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol namespaceSymbol)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            yield return type;

            foreach (var nestedType in GetNestedTypes(type))
            {
                yield return nestedType;
            }
        }

        foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var type in GetAllTypes(childNamespace))
            {
                yield return type;
            }
        }
    }

    private IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol type)
    {
        foreach (var nestedType in type.GetTypeMembers())
        {
            yield return nestedType;

            foreach (var deeplyNestedType in GetNestedTypes(nestedType))
            {
                yield return deeplyNestedType;
            }
        }
    }

}



public class AssemblyAnalysisInfo
{
    public required string PackageId { get; set; }
    public required string PackageVersion { get; set; }
    public required string? TargetFramework { get; set; }
    public required string AssemblyName { get; set; }
    public required string AssemblyVersion { get; set; }
    public required string AssemblyPath { get; set; }

    public bool IsRefAssembly { get; set; }

    public required IAssemblySymbol AssemblySymbol { get; set; }

    public List<INamedTypeSymbol> AllTypes { get; set; } = new();
    public List<string> Namespaces { get; set; } = new();
    public int AllTypesCount => AllTypes.Count;
    public int PublicTypesCount { get; set; }
    public int ClassesCount { get; set; }
    public int StaticClassesCount { get; set; }
    public int InterfacesCount { get; set; }
    public int EnumsCount { get; set; }
    public int StructsCount { get; set; }
    public int DelegatesCount { get; set; }
    public List<string> References { get; set; } = new();

}
