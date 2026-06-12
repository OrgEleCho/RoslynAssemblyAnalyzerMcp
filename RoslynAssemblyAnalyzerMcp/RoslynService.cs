using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace RoslynAssemblyAnalyzerMcp;

public sealed class RoslynService
{
    private const string NETFrameworkReferenceAssemblies = "Microsoft.NETFramework.ReferenceAssemblies";

    private List<PackageSource> _packageSources = null!;
    public SourceRepository SourceRepository { get; private set; } = null!;
    public PackageSearchResource SearchResource { get; private set; } = null!;
    public PackageMetadataResource MetadataResource { get; private set; } = null!;
    public DownloadResource DownloadResource { get; private set; } = null!;

    public string GlobalPackagePath { get; private set; } = null!;

    private readonly MemoryCache _metadataCache = new(new MemoryCacheOptions());
    private readonly MemoryCache _packageInfoCache = new(new MemoryCacheOptions());
    private readonly MemoryCache _assemblyAnalysisInfoCache = new(new MemoryCacheOptions());
    private readonly MemoryCache _assemblyAnalysisInfoBySourceCache = new(new MemoryCacheOptions());

    private readonly ILogger<RoslynService> _logger;

    public RoslynService(ILogger<RoslynService> logger)
    {
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        GlobalPackagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        var settings = Settings.LoadDefaultSettings(root: null);
        var packageSourceProvider = new PackageSourceProvider(settings);
        _packageSources = packageSourceProvider.LoadPackageSources().Where(s => s.IsEnabled).ToList();
        if (_packageSources.Count == 0)
        {
            throw new InvalidOperationException("没有找到启用的 NuGet 源");
        }

        var primarySource = _packageSources.First();

        SourceRepository = Repository.Factory.GetCoreV3(primarySource.Source);
        SearchResource = await SourceRepository.GetResourceAsync<PackageSearchResource>(cancellationToken);
        MetadataResource = await SourceRepository.GetResourceAsync<PackageMetadataResource>(cancellationToken);
        DownloadResource = await SourceRepository.GetResourceAsync<DownloadResource>(cancellationToken);
    }

    public async Task<IReadOnlyList<IPackageSearchMetadata>> SearchNuGetAsync(
        string text,
        int maxResult = 10,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default)
    {
        var searchFilter = new SearchFilter(includePrerelease);

        var searchResults = await SearchResource.SearchAsync(
            text,
            searchFilter,
            skip: 0,
            take: maxResult,
            NullLogger.Instance,
            cancellationToken);

        return searchResults.ToList();
    }

    public async ValueTask<IReadOnlyList<IPackageSearchMetadata>> GetPackageMetadataAsync(
        string packageId,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{packageId}|{includePrerelease}";
        if (_metadataCache.TryGetValue<IReadOnlyList<IPackageSearchMetadata>>(cacheKey, out var cachedMetadata))
        {
            return cachedMetadata!;
        }

        var result = await MetadataResource.GetMetadataAsync(
            packageId,
            includePrerelease,
            includeUnlisted: false,
            new SourceCacheContext(),
            NullLogger.Instance,
            cancellationToken);
        var list = result.ToList();
        _metadataCache.Set(cacheKey, list, TimeSpan.FromMinutes(30));
        return list;
    }

    public async Task<DownloadResourceResult> DownloadPackageAsync(PackageIdentity id, CancellationToken cancellationToken = default)
    {
        var downloadResult = await DownloadResource.GetDownloadResourceResultAsync(
            id,
            new PackageDownloadContext(new SourceCacheContext()),
            GlobalPackagePath,
            NullLogger.Instance,
            cancellationToken);
        _logger.LogInformation("下载 NuGet 包 {Status} {NuGetPackage}", downloadResult.Status, id.Id);
        return downloadResult;
    }

    public async Task<PackageInfo?> GetPackageInfoAsync(
        string packageId,
        string? packageVersion = null,
        bool includePrerelease = true,
        CancellationToken cancellationToken = default)
    {
        var versions = await GetPackageMetadataAsync(packageId, includePrerelease, cancellationToken);
        var metadata = versions.GetVersion(packageVersion);
        if (metadata is null)
        {
            return null;
        }

        var cacheKey = $"{metadata.Identity.Id}|{metadata.Identity.Version}|package-info";
        if (_packageInfoCache.TryGetValue<PackageInfo>(cacheKey, out var cachedPackageInfo))
        {
            return cachedPackageInfo!;
        }

        var downloadResult = await DownloadPackageAsync(metadata.Identity, cancellationToken);
        if (downloadResult.Status != DownloadResourceResultStatus.Available)
        {
            return null;
        }

        var (groups, groupKind) = await GetAssemblyGroupsAsync(metadata.Identity.Id, downloadResult.PackageReader, cancellationToken);
        var packageInfo = new PackageInfo
        {
            PackageId = metadata.Identity.Id,
            PackageVersion = metadata.Identity.Version.ToString(),
            Metadata = metadata,
            AssemblyGroupKind = groupKind
        };

        foreach (var group in groups)
        {
            var framework = group.TargetFramework.GetShortFolderName();
            var targetFrameworkInfo = new PackageTargetFrameworkInfo
            {
                TargetFramework = framework
            };

            foreach (var item in group.Items.Where(item => item.EqualsFileExtension(".dll")))
            {
                targetFrameworkInfo.AssemblyFiles.Add(new PackageAssemblyFileInfo
                {
                    Name = Path.GetFileName(item),
                    RelativePath = item,
                    FullPath = BuildPackageAssemblyPath(metadata.Identity, item)
                });
            }

            packageInfo.TargetFrameworks.Add(targetFrameworkInfo);
        }

        _packageInfoCache.Set(cacheKey, packageInfo, TimeSpan.FromMinutes(30));
        return packageInfo;
    }

    public async Task<PackageAssemblySelection?> FindPackageAssemblyAsync(
        string packageId,
        string? assemblyName,
        string? packageVersion = null,
        string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            assemblyName = packageId;
        }

        assemblyName = EnsureAssemblyFileName(assemblyName);
        var packageInfo = await GetPackageInfoAsync(packageId, packageVersion, includePrerelease: true, cancellationToken);
        if (packageInfo is null)
        {
            return null;
        }

        foreach (var framework in packageInfo.TargetFrameworks)
        {
            if (!string.IsNullOrWhiteSpace(targetFramework)
                && framework.TargetFramework != targetFramework
                && !packageId.StartsWith(NETFrameworkReferenceAssemblies, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var assembly = framework.AssemblyFiles.FirstOrDefault(file =>
                file.Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase));
            if (assembly is null)
            {
                continue;
            }

            return new PackageAssemblySelection
            {
                Package = packageInfo,
                Metadata = packageInfo.Metadata,
                AssemblyGroupKind = packageInfo.AssemblyGroupKind,
                TargetFrameworkInfo = framework,
                TargetFramework = framework.TargetFramework,
                AssemblyFile = assembly
            };
        }

        return null;
    }

    public async Task<AssemblyAnalysisInfo?> AnalyzePackageAssemblyAsync(
        string packageId,
        string? assemblyName,
        string? packageVersion = null,
        string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        var selection = await FindPackageAssemblyAsync(packageId, assemblyName, packageVersion, targetFramework, cancellationToken);
        if (selection is null)
        {
            return null;
        }

        if (!Utils.IsDotnetAssembly(selection.AssemblyFile.FullPath))
        {
            return null;
        }

        var version = selection.Metadata.Identity.Version.ToString();
        var cacheKey = new AssemblyAnalysisCacheKey(
            AssemblySourceKind.NuGetPackage,
            selection.Metadata.Identity.Id,
            version,
            selection.TargetFramework,
            selection.AssemblyFile.Name,
            Path.GetFullPath(selection.AssemblyFile.FullPath));

        if (TryGetAnalyzeAssemblyCache(cacheKey, out var cachedInfo))
        {
            return cachedInfo;
        }

        var assemblyInfo = AnalyzeAssemblyFile(
            AssemblySourceKind.NuGetPackage,
            selection.AssemblyFile.FullPath,
            selection.AssemblyFile.Name,
            packageId: selection.Metadata.Identity.Id,
            packageVersion: version,
            targetFramework: selection.TargetFramework);
        CacheAssemblyAnalysis(cacheKey, assemblyInfo);
        if (!selection.TargetFrameworkInfo.Assemblies.Any(info =>
            info.AssemblyPath.Equals(assemblyInfo.AssemblyPath, StringComparison.OrdinalIgnoreCase)))
        {
            selection.TargetFrameworkInfo.Assemblies.Add(assemblyInfo);
        }

        if (string.IsNullOrWhiteSpace(packageVersion))
        {
            var latestCacheKey = cacheKey with { PackageVersion = "latest" };
            CacheAssemblyAnalysis(latestCacheKey, assemblyInfo);
        }

        return assemblyInfo;
    }

    public AssemblyAnalysisInfo? AnalyzeLocalAssembly(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            return null;
        }

        var assemblyName = Path.GetFileName(fullPath);
        var cacheKey = CreateLocalAssemblyCacheKey(fullPath, assemblyName, fileInfo);

        if (TryGetAnalyzeAssemblyCache(cacheKey, out var cachedInfo))
        {
            return cachedInfo;
        }

        var assemblyImage = AssemblyImageUtilities.ReadFileImage(fullPath);
        if (!Utils.IsDotnetAssembly(assemblyImage))
        {
            return null;
        }

        var assemblyInfo = AnalyzeAssemblyFile(
            AssemblySourceKind.LocalFile,
            fullPath,
            assemblyName,
            packageId: null,
            packageVersion: null,
            targetFramework: null,
            assemblyImage);
        CacheAssemblyAnalysis(cacheKey, assemblyInfo);
        return assemblyInfo;
    }

    public AssemblyAnalysisInfo? GetAnalyzeAssemblyCache(
        string packageId,
        string packageVersion,
        string assemblyName,
        string? targetFramework)
    {
        assemblyName = EnsureAssemblyFileName(assemblyName);
        var packageKeys = GetSourceCacheKeys(AssemblySourceKind.NuGetPackage, packageId);
        return packageKeys
            .Select(key => GetAnalyzeAssemblyCache(key))
            .FirstOrDefault(info =>
                info is not null
                && info.PackageVersion == packageVersion
                && info.AssemblyName.Equals(assemblyName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(info.TargetFramework, targetFramework, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<AssemblyAnalysisInfo> GetAnalyzeAssemblyCache(string packageId)
    {
        return [.. GetSourceCacheKeys(AssemblySourceKind.NuGetPackage, packageId)
            .Select(key => GetAnalyzeAssemblyCache(key))
            .Where(info => info is not null)
            .Select(info => info!)];
    }

    public AssemblyAnalysisInfo? GetAnalyzeLocalAssemblyCache(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            return null;
        }

        var assemblyName = Path.GetFileName(fullPath);
        var cacheKey = CreateLocalAssemblyCacheKey(fullPath, assemblyName, fileInfo);

        return GetAnalyzeAssemblyCache(cacheKey);
    }

    private AssemblyAnalysisInfo AnalyzeAssemblyFile(
        AssemblySourceKind sourceKind,
        string assemblyPath,
        string assemblyName,
        string? packageId,
        string? packageVersion,
        string? targetFramework,
        byte[]? assemblyImage = null)
    {
        DocumentationProvider documentationProvider = DocumentationProvider.Default;
        var xmlDocumentFilePath = Path.ChangeExtension(assemblyPath, "xml");
        if (File.Exists(xmlDocumentFilePath))
        {
            documentationProvider = assemblyImage is null
                ? XmlDocumentationProvider.CreateFromFile(xmlDocumentFilePath)
                : InMemoryXmlDocumentationProvider.CreateFromFile(xmlDocumentFilePath);
        }

        var reference = assemblyImage is null
            ? MetadataReference.CreateFromFile(assemblyPath, documentation: documentationProvider)
            : MetadataReference.CreateFromImage(assemblyImage, documentation: documentationProvider, filePath: assemblyPath);
        var compilation = CSharpCompilation.Create("_")
            .AddReferences(reference);

        var assemblySymbol = (IAssemblySymbol?)compilation.GetAssemblyOrModuleSymbol(reference)
            ?? throw new InvalidOperationException($"无法读取程序集元数据: {assemblyPath}");

        var assemblyAttributes = assemblySymbol.GetAttributes();
        bool isRefAssembly = assemblyAttributes.Any(v => v.AttributeClass?.ToString() == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute");

        AssemblyAnalysisInfo result = new()
        {
            SourceKind = sourceKind,
            PackageId = packageId,
            PackageVersion = packageVersion,
            TargetFramework = targetFramework,
            AssemblyName = assemblyName,
            AssemblyVersion = assemblySymbol.Identity.Version.ToString(),
            AssemblyPath = assemblyPath,
            AssemblyImage = assemblyImage,
            AssemblySymbol = assemblySymbol,
            IsRefAssembly = isRefAssembly
        };

        var allTypes = GetAllTypes(assemblySymbol.GlobalNamespace).ToList();
        result.AllTypes = allTypes;

        result.PublicTypesCount = allTypes.Count(t => t.DeclaredAccessibility == Accessibility.Public);
        result.ClassesCount = allTypes.Count(t => t.TypeKind == TypeKind.Class && !t.IsStatic);
        result.StaticClassesCount = allTypes.Count(t => t.TypeKind == TypeKind.Class && t.IsStatic);
        result.InterfacesCount = allTypes.Count(t => t.TypeKind == TypeKind.Interface);
        result.EnumsCount = allTypes.Count(t => t.TypeKind == TypeKind.Enum);
        result.StructsCount = allTypes.Count(t => t.TypeKind == TypeKind.Struct);
        result.DelegatesCount = allTypes.Count(t => t.TypeKind == TypeKind.Delegate);

        result.Namespaces = [.. allTypes
            .Where(t => t.ContainingNamespace != null)
            .Select(t => t.ContainingNamespace.ToDisplayStringOrEmpty())
            .Distinct()
            .OrderBy(n => n)];

        result.References = [.. assemblySymbol.Modules.First().ReferencedAssemblySymbols
            .Select(r => $"{r.Name} ({r.Identity.Version})")
            .OrderBy(r => r)];

        return result;
    }

    private async Task<(IReadOnlyList<FrameworkSpecificGroup> Groups, PackageAssemblyGroupKind GroupKind)> GetAssemblyGroupsAsync(
        string packageId,
        PackageReaderBase packageReader,
        CancellationToken cancellationToken)
    {
        var libItems = (await packageReader.GetLibItemsAsync(cancellationToken)).ToList();
        if (libItems.Count != 0)
        {
            return (libItems, PackageAssemblyGroupKind.Lib);
        }

        var refItems = (await packageReader.GetItemsAsync(PackagingConstants.Folders.Ref, cancellationToken)).ToList();
        if (refItems.Count != 0)
        {
            return (refItems, PackageAssemblyGroupKind.Ref);
        }

        if (!packageId.StartsWith(NETFrameworkReferenceAssemblies, StringComparison.OrdinalIgnoreCase))
        {
            return ([], PackageAssemblyGroupKind.None);
        }

        var bclItems = (await packageReader.GetItemsAsync(@"build/.NETFramework/", cancellationToken)).ToList();
        return bclItems.Count == 0
            ? ([], PackageAssemblyGroupKind.None)
            : (bclItems, PackageAssemblyGroupKind.NetFrameworkReferenceAssemblies);
    }

    private string BuildPackageAssemblyPath(PackageIdentity packageIdentity, string relativePath)
    {
        return Path.Combine(
            GlobalPackagePath,
            packageIdentity.Id.ToLowerInvariant(),
            packageIdentity.Version.ToString(),
            relativePath);
    }

    private void CacheAssemblyAnalysis(AssemblyAnalysisCacheKey cacheKey, AssemblyAnalysisInfo assemblyInfo)
    {
        _assemblyAnalysisInfoCache.Set(cacheKey, assemblyInfo, TimeSpan.FromMinutes(30));

        var sourceKey = GetSourceCacheKey(cacheKey.SourceKind, cacheKey.PackageId ?? cacheKey.AssemblyPath);
        var cacheKeys = _assemblyAnalysisInfoBySourceCache.GetOrCreate(sourceKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            return new HashSet<AssemblyAnalysisCacheKey>();
        })!;
        cacheKeys.Add(cacheKey);
        _assemblyAnalysisInfoBySourceCache.Set(sourceKey, cacheKeys, TimeSpan.FromMinutes(30));
    }

    private bool TryGetAnalyzeAssemblyCache(AssemblyAnalysisCacheKey cacheKey, out AssemblyAnalysisInfo? assemblyInfo)
    {
        assemblyInfo = GetAnalyzeAssemblyCache(cacheKey);
        return assemblyInfo is not null;
    }

    private AssemblyAnalysisInfo? GetAnalyzeAssemblyCache(AssemblyAnalysisCacheKey cacheKey)
    {
        return _assemblyAnalysisInfoCache.TryGetValue(cacheKey, out AssemblyAnalysisInfo? result)
            ? result
            : null;
    }

    private IEnumerable<AssemblyAnalysisCacheKey> GetSourceCacheKeys(AssemblySourceKind sourceKind, string sourceId)
    {
        var sourceKey = GetSourceCacheKey(sourceKind, sourceId);
        if (_assemblyAnalysisInfoBySourceCache.TryGetValue<HashSet<AssemblyAnalysisCacheKey>>(sourceKey, out var cacheKeys))
        {
            return cacheKeys!;
        }

        return [];
    }

    private static string GetSourceCacheKey(AssemblySourceKind sourceKind, string sourceId)
    {
        return $"{sourceKind}|{sourceId}";
    }

    private static AssemblyAnalysisCacheKey CreateLocalAssemblyCacheKey(
        string fullPath,
        string assemblyName,
        FileInfo fileInfo)
    {
        return new AssemblyAnalysisCacheKey(
            AssemblySourceKind.LocalFile,
            PackageId: null,
            PackageVersion: null,
            TargetFramework: null,
            assemblyName,
            fullPath,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc.Ticks);
    }

    private static string EnsureAssemblyFileName(string assemblyName)
    {
        return assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? assemblyName
            : $"{assemblyName}.dll";
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol namespaceSymbol)
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

    private static IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol type)
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
