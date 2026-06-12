using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using ICSharpCode.Decompiler.Metadata;
using NuGet.Protocol.Core.Types;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace RoslynAssemblyAnalyzerMcp;

[McpServerToolType]
public sealed partial class RoslynMcp(
    RoslynService roslynService,
    AssemblyDecompiler decompiler,
    AssemblyResourceService resourceService,
    ILogger<RoslynMcp> logger)
{
    private readonly RoslynService _service = roslynService;
    private readonly AssemblyDecompiler _decompiler = decompiler;
    private readonly AssemblyResourceService _resourceService = resourceService;
    private readonly ILogger<RoslynMcp> _logger = logger;

    private const string PackageIdDescription = "NuGet PackageId (例如: 'Newtonsoft.Json', 'Microsoft.EntityFrameworkCore')";
    private const string AssemblyNameDescription = "程序集名(例如: 'System.Runtime.dll' 'Newtonsoft.Json.dll', 如果不填则使用和包名相同的程序集名)";
    private const string AssemblyPathDescription = "本地 .NET 程序集 DLL 的完整路径";
    private const int DefaultDetailsVersionLimit = 20;
    private const int DefaultNamespaceLimit = 50;
    private const int DefaultReferenceLimit = 50;
    private const int DefaultMaxBatchTypes = 32;
    private const int DefaultMaxMembersPerGroup = 200;

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("在 NuGet 上搜索包, 返回 NuGet 包基本信息")]
    public async Task<ToolResponse<NuGetPackageSearchResult>> SearchNuGetPackage(
        [Description("要搜索的包名或关键词 (例如：'json' 'entityframework'), 如果要获取标准库的程序集, 请使用 Microsoft.NETCore.App.Ref, Microsoft.AspNetCore.App.Ref, Microsoft.WindowsDesktop.App.Ref, 或 Microsoft.NETFramework.ReferenceAssemblies")] string searchText,
        [Description("返回的最大结果数量, 默认为 10")] int maxResults = 10)
    {
        try
        {
            var packages = await _service.SearchNuGetAsync(searchText, maxResults);
            return ToolResponse<NuGetPackageSearchResult>.Ok(new NuGetPackageSearchResult(
                packages.Count,
                [.. packages.Select(ToSearchItem)]));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "搜索 NuGet 包异常");
            return ToolResponse<NuGetPackageSearchResult>.Fail("搜索 NuGet 包异常");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("获取指定 NuGet 包的详细元数据，包括最新版本、所有历史版本列表、作者、项目 URL、依赖项等完整信息")]
    public async Task<ToolResponse<NuGetPackageDetailsResult>> GetNuGetPackageDetails(
        [Description(PackageIdDescription)] string packageId)
    {
        try
        {
            var versions = await _service.GetPackageMetadataAsync(packageId);
            if (versions is [])
            {
                return ToolResponse<NuGetPackageDetailsResult>.Fail("没有找到该包");
            }

            var latest = versions.OrderByDescending(m => m.Identity.Version).First();
            var dependencyGroups = latest.DependencySets.Select(dependencySet =>
            {
                var framework = dependencySet.TargetFramework?.GetShortFolderName() ?? "所有框架";
                var dependencies = dependencySet.Packages is null
                    ? []
                    : dependencySet.Packages
                        .Select(dep => new PackageDependencyDto(dep.Id, dep.VersionRange.ToString()))
                        .ToList();
                return new PackageDependencyGroupDto(framework, dependencies);
            }).ToList();

            var returnedVersions = versions
                .OrderByDescending(m => m.Identity.Version)
                .Take(DefaultDetailsVersionLimit)
                .Select(version => new PackageVersionDto(version.Identity.Version.ToString(), version.Published))
                .ToList();

            return ToolResponse<NuGetPackageDetailsResult>.Ok(new NuGetPackageDetailsResult(
                latest.Identity.Id,
                latest.Identity.Version.ToString(),
                latest.Authors,
                latest.Description,
                latest.ProjectUrl?.ToString(),
                latest.LicenseUrl?.ToString(),
                latest.DownloadCount,
                latest.Published,
                latest.Tags,
                dependencyGroups,
                returnedVersions,
                versions.Count,
                versions.Count > DefaultDetailsVersionLimit));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取包信息异常");
            return ToolResponse<NuGetPackageDetailsResult>.Fail("获取包信息异常");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("获取 NuGet 包中所有程序集文件信息")]
    public async Task<ToolResponse<PackageAssembliesResult>> GetPackageAssemblies(
        [Description(PackageIdDescription)] string packageId,
        [Description("包的版本号, 如果不指定则使用最新版本")] string? packageVersion = null)
    {
        try
        {
            var packageInfo = await _service.GetPackageInfoAsync(packageId, packageVersion);
            if (packageInfo is null)
            {
                return ToolResponse<PackageAssembliesResult>.Fail($"未找到包或无法下载包: {packageId}");
            }

            return ToolResponse<PackageAssembliesResult>.Ok(ToPackageAssembliesResult(packageInfo));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取包程序集异常");
            return ToolResponse<PackageAssembliesResult>.Fail("获取包程序集异常");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("分析并获取 NuGet 包的所有 dotnet 程序集的基本信息 (类型个数、命名空间、引用信息等)")]
    public async Task<ToolResponse<AnalyzeAllAssembliesResult>> AnalyzeAllAssembly(
        [Description(PackageIdDescription)] string packageId,
        [Description("包的版本号，如果不指定则使用最新版本")] string? packageVersion = null,
        [Description("目标框架(例如 net6.0 net8.0) 如果不指定则使用所有可用框架")] string? targetFramework = null)
    {
        try
        {
            var packageInfo = await _service.GetPackageInfoAsync(packageId, packageVersion);
            if (packageInfo is null)
            {
                return ToolResponse<AnalyzeAllAssembliesResult>.Fail($"未找到包或无法下载包: {packageId}");
            }

            var assemblies = new List<AssemblyAnalysisDto>();
            var skippedAssemblies = new List<SkippedAssemblyDto>();
            var availableTargetFrameworks = new List<string>();

            foreach (var framework in packageInfo.TargetFrameworks)
            {
                if (!string.IsNullOrWhiteSpace(targetFramework)
                    && targetFramework != framework.TargetFramework
                    && !packageInfo.PackageId.StartsWith("Microsoft.NETFramework.ReferenceAssemblies", StringComparison.OrdinalIgnoreCase))
                {
                    availableTargetFrameworks.Add(framework.TargetFramework);
                    continue;
                }

                foreach (var assemblyFile in framework.AssemblyFiles)
                {
                    var assemblyInfo = await _service.AnalyzePackageAssemblyAsync(
                        packageId,
                        assemblyFile.Name,
                        packageVersion,
                        framework.TargetFramework);
                    if (assemblyInfo is null)
                    {
                        skippedAssemblies.Add(new SkippedAssemblyDto(
                            assemblyFile.Name,
                            framework.TargetFramework,
                            "不是可解析的 .NET 程序集或读取失败"));
                        continue;
                    }

                    assemblies.Add(ToAssemblyAnalysisDto(assemblyInfo));
                }
            }

            var result = new AnalyzeAllAssembliesResult(
                packageInfo.PackageId,
                packageInfo.PackageVersion,
                targetFramework,
                packageInfo.AssemblyGroupKind,
                GetAssemblyGroupNotes(packageInfo.AssemblyGroupKind),
                assemblies,
                skippedAssemblies,
                availableTargetFrameworks);

            return ToolResponse<AnalyzeAllAssembliesResult>.Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "分析所有程序集异常");
            return ToolResponse<AnalyzeAllAssembliesResult>.Fail("分析所有程序集异常");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("分析并获取 NuGet 包的 dotnet 程序集的基本信息 (类型个数、命名空间、引用信息等)")]
    public async Task<ToolResponse<AssemblyAnalysisDto>> AnalyzeAssembly(
        [Description(PackageIdDescription)] string packageId,
        [Description(AssemblyNameDescription)] string? assemblyName = null,
        [Description("包的版本号，如果不指定则使用最新版本")] string? packageVersion = null,
        [Description("目标框架(例如 net6.0 net8.0) 如果不指定则使用默认可用的框架")] string? targetFramework = null)
    {
        try
        {
            var (assemblyInfo, errorMessage) = await TryGetPackageAssemblyAnalysisInfo(packageId, assemblyName, packageVersion, targetFramework);
            return errorMessage is null
                ? ToolResponse<AssemblyAnalysisDto>.Ok(ToAssemblyAnalysisDto(assemblyInfo!))
                : ToolResponse<AssemblyAnalysisDto>.Fail(errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "分析程序集异常");
            return ToolResponse<AssemblyAnalysisDto>.Fail("分析程序集异常");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("分析并获取本地 dotnet DLL 程序集的基本信息 (类型个数、命名空间、引用信息等)")]
    public ToolResponse<AssemblyAnalysisDto> AnalyzeLocalAssembly(
        [Description(AssemblyPathDescription)] string assemblyPath)
    {
        try
        {
            var (assemblyInfo, errorMessage) = TryGetLocalAssemblyAnalysisInfo(assemblyPath);
            return errorMessage is null
                ? ToolResponse<AssemblyAnalysisDto>.Ok(ToAssemblyAnalysisDto(assemblyInfo!))
                : ToolResponse<AssemblyAnalysisDto>.Fail(errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "分析本地程序集异常");
            return ToolResponse<AssemblyAnalysisDto>.Fail("分析本地程序集异常");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("获取 NuGet 包内程序集的结构化元数据，包括程序集特性、模块特性、引用程序集和目标框架信息")]
    public async Task<ToolResponse<AssemblyMetadataResult>> GetAssemblyMetadata(
        [Description(PackageIdDescription)] string packageId,
        [Description(AssemblyNameDescription)] string? assemblyName = null,
        [Description("包的版本号，如果不指定则使用最新版本")] string? packageVersion = null,
        [Description("目标框架(例如 net6.0 net8.0) 如果不指定则使用默认可用的框架")] string? targetFramework = null,
        [Description("是否包含程序集特性, 默认 true")] bool includeAssemblyAttributes = true,
        [Description("是否包含模块特性, 默认 true")] bool includeModuleAttributes = true)
    {
        try
        {
            var (assemblyInfo, errorMessage) = await TryGetPackageAssemblyAnalysisInfo(packageId, assemblyName, packageVersion, targetFramework);
            return errorMessage is null
                ? ToolResponse<AssemblyMetadataResult>.Ok(ToAssemblyMetadataResult(assemblyInfo!, includeAssemblyAttributes, includeModuleAttributes))
                : ToolResponse<AssemblyMetadataResult>.Fail(errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取程序集元数据异常");
            return ToolResponse<AssemblyMetadataResult>.Fail("获取程序集元数据异常");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("获取本地 dotnet DLL 的结构化元数据，包括程序集特性、模块特性、引用程序集和目标框架信息")]
    public ToolResponse<AssemblyMetadataResult> GetLocalAssemblyMetadata(
        [Description(AssemblyPathDescription)] string assemblyPath,
        [Description("是否包含程序集特性, 默认 true")] bool includeAssemblyAttributes = true,
        [Description("是否包含模块特性, 默认 true")] bool includeModuleAttributes = true)
    {
        try
        {
            var (assemblyInfo, errorMessage) = TryGetLocalAssemblyAnalysisInfo(assemblyPath);
            return errorMessage is null
                ? ToolResponse<AssemblyMetadataResult>.Ok(ToAssemblyMetadataResult(assemblyInfo!, includeAssemblyAttributes, includeModuleAttributes))
                : ToolResponse<AssemblyMetadataResult>.Fail(errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取本地程序集元数据异常");
            return ToolResponse<AssemblyMetadataResult>.Fail("获取本地程序集元数据异常");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("获取 dotnet 程序集中指定类型的所有成员详细信息(方法、属性、字段、事件以及对应注释等)")]
    public async Task<ToolResponse<TypeMembersResult>> GetTypeMembers(
        [Description(PackageIdDescription)] string packageId,
        [Description("完整的类型名称，包括命名空间")] string typeName,
        [Description(AssemblyNameDescription)] string? assemblyName = null,
        [Description("包的版本号 (可选)")] string? packageVersion = null,
        [Description("目标框架 (可选)")] string? targetFramework = null,
        [Description("过滤特定成员名称 (可选)，例如：'WriteLine' 只显示名为 WriteLine 的所有重载")] string? memberNameFilterText = null,
        [Description("成员类型过滤：method property field event constructor *, 默认 *")] string memberType = "*",
        [Description("是否仅显示公共成员，默认 true, false 选项显示所有成员")] bool publicOnly = true,
        [Description("是否获取注释, 默认 true")] bool comment = true,
        [Description("是否也获取基类的成员, 默认 false")] bool includeBaseMembers = false,
        [Description("是否包含特性, 默认 false")] bool includeAttributes = false,
        [Description("每个成员分组最大返回数量, 默认 200")] int maxMembersPerGroup = DefaultMaxMembersPerGroup)
    {
        try
        {
            var (assemblyInfo, errorMessage) = await TryGetPackageAssemblyAnalysisInfo(packageId, assemblyName, packageVersion, targetFramework);
            if (errorMessage is not null)
            {
                return ToolResponse<TypeMembersResult>.Fail(errorMessage);
            }

            return ToTypeMembersResponse(assemblyInfo!, typeName, memberNameFilterText, memberType, publicOnly, comment, includeBaseMembers, includeAttributes, maxMembersPerGroup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取类型成员异常");
            return ToolResponse<TypeMembersResult>.Fail("获取类型成员异常");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("获取本地 dotnet DLL 程序集中指定类型的所有成员详细信息(方法、属性、字段、事件以及对应注释等)")]
    public ToolResponse<TypeMembersResult> GetLocalTypeMembers(
        [Description(AssemblyPathDescription)] string assemblyPath,
        [Description("完整的类型名称，包括命名空间")] string typeName,
        [Description("过滤特定成员名称 (可选)，例如：'WriteLine' 只显示名为 WriteLine 的所有重载")] string? memberNameFilterText = null,
        [Description("成员类型过滤：method property field event constructor *, 默认 *")] string memberType = "*",
        [Description("是否仅显示公共成员，默认 true, false 选项显示所有成员")] bool publicOnly = true,
        [Description("是否获取注释, 默认 true")] bool comment = true,
        [Description("是否也获取基类的成员, 默认 false")] bool includeBaseMembers = false,
        [Description("是否包含特性, 默认 false")] bool includeAttributes = false,
        [Description("每个成员分组最大返回数量, 默认 200")] int maxMembersPerGroup = DefaultMaxMembersPerGroup)
    {
        try
        {
            var (assemblyInfo, errorMessage) = TryGetLocalAssemblyAnalysisInfo(assemblyPath);
            if (errorMessage is not null)
            {
                return ToolResponse<TypeMembersResult>.Fail(errorMessage);
            }

            return ToTypeMembersResponse(assemblyInfo!, typeName, memberNameFilterText, memberType, publicOnly, comment, includeBaseMembers, includeAttributes, maxMembersPerGroup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取本地类型成员异常");
            return ToolResponse<TypeMembersResult>.Fail("获取本地类型成员异常");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("批量获取 NuGet 包内程序集多个类型的成员信息，适合 AI 一次请求多个类型避免连续工具调用")]
    public async Task<ToolResponse<TypeMembersBatchResult>> GetTypeMembersBatch(
        [Description(PackageIdDescription)] string packageId,
        [Description("完整类型名称数组。嵌套类型可以使用 A.B.C 或 A.B+C")] IReadOnlyList<string> typeNames,
        [Description(AssemblyNameDescription)] string? assemblyName = null,
        [Description("包的版本号 (可选)")] string? packageVersion = null,
        [Description("目标框架 (可选)")] string? targetFramework = null,
        [Description("过滤特定成员名称 (可选)，支持 * 通配符")] string? memberNameFilterText = null,
        [Description("成员类型过滤：method property field event constructor *, 默认 *")] string memberType = "*",
        [Description("是否仅显示公共成员，默认 true, false 选项显示所有成员")] bool publicOnly = true,
        [Description("是否获取注释, 默认 true")] bool comment = true,
        [Description("是否也获取基类的成员, 默认 false")] bool includeBaseMembers = false,
        [Description("是否包含特性, 默认 false")] bool includeAttributes = false,
        [Description("每个成员分组最大返回数量, 默认 200")] int maxMembersPerGroup = DefaultMaxMembersPerGroup,
        [Description("最大处理类型数量, 默认 32")] int maxTypes = DefaultMaxBatchTypes)
    {
        try
        {
            var (assemblyInfo, errorMessage) = await TryGetPackageAssemblyAnalysisInfo(packageId, assemblyName, packageVersion, targetFramework);
            return errorMessage is not null
                ? ToolResponse<TypeMembersBatchResult>.Fail(errorMessage)
                : ToTypeMembersBatchResponse(assemblyInfo!, typeNames, memberNameFilterText, memberType, publicOnly, comment, includeBaseMembers, includeAttributes, maxMembersPerGroup, maxTypes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量获取类型成员异常");
            return ToolResponse<TypeMembersBatchResult>.Fail("批量获取类型成员异常");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("批量获取本地 dotnet DLL 多个类型的成员信息，适合 AI 一次请求多个类型避免连续工具调用")]
    public ToolResponse<TypeMembersBatchResult> GetLocalTypeMembersBatch(
        [Description(AssemblyPathDescription)] string assemblyPath,
        [Description("完整类型名称数组。嵌套类型可以使用 A.B.C 或 A.B+C")] IReadOnlyList<string> typeNames,
        [Description("过滤特定成员名称 (可选)，支持 * 通配符")] string? memberNameFilterText = null,
        [Description("成员类型过滤：method property field event constructor *, 默认 *")] string memberType = "*",
        [Description("是否仅显示公共成员，默认 true, false 选项显示所有成员")] bool publicOnly = true,
        [Description("是否获取注释, 默认 true")] bool comment = true,
        [Description("是否也获取基类的成员, 默认 false")] bool includeBaseMembers = false,
        [Description("是否包含特性, 默认 false")] bool includeAttributes = false,
        [Description("每个成员分组最大返回数量, 默认 200")] int maxMembersPerGroup = DefaultMaxMembersPerGroup,
        [Description("最大处理类型数量, 默认 32")] int maxTypes = DefaultMaxBatchTypes)
    {
        try
        {
            var (assemblyInfo, errorMessage) = TryGetLocalAssemblyAnalysisInfo(assemblyPath);
            return errorMessage is not null
                ? ToolResponse<TypeMembersBatchResult>.Fail(errorMessage)
                : ToTypeMembersBatchResponse(assemblyInfo!, typeNames, memberNameFilterText, memberType, publicOnly, comment, includeBaseMembers, includeAttributes, maxMembersPerGroup, maxTypes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量获取本地类型成员异常");
            return ToolResponse<TypeMembersBatchResult>.Fail("批量获取本地类型成员异常");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("在已解析的 dotnet 程序集中按模式搜索类型(支持通配符 *), 例如: 'Newtonsoft.*', '*Stream*', 并获取类型注释")]
    public async Task<ToolResponse<TypeSearchResult>> SearchTypes(
        [Description(PackageIdDescription)] string packageId,
        [Description(AssemblyNameDescription)] string? assemblyName = null,
        [Description("名称过滤器, 例如 'Newtonsoft.Json' 匹配类型全名包含 'Newtonsoft.Json' 的类型, 'Stream' 相当于 '*Stream*', 如果不填或填 * 则搜索所有类型")] string typeFullNameFilterText = "*",
        [Description("包的版本号(可选)")] string? packageVersion = null,
        [Description("目标框架(可选)")] string? targetFramework = null,
        [Description("类型过滤器：class interface enum struct *, 默认 *")] string typeFilter = "*",
        [Description("是否仅显示公共类型")] bool publicOnly = true,
        [Description("最大返回结果数，默认 50")] int maxResults = 50,
        [Description("是否获取注释, 默认 true")] bool comment = true,
        [Description("是否包含类型特性, 默认 false")] bool includeAttributes = false,
        [Description("命名空间过滤器, 支持 * 通配符, 可选")] string? namespaceFilter = null,
        [Description("是否排除编译器生成类型, 默认 true")] bool excludeCompilerGenerated = true)
    {
        try
        {
            var (assemblyInfo, errorMessage) = await TryGetPackageAssemblyAnalysisInfo(packageId, assemblyName, packageVersion, targetFramework);
            return errorMessage is null
                ? ToTypeSearchResponse(assemblyInfo!, typeFullNameFilterText, typeFilter, publicOnly, maxResults, comment, includeAttributes, namespaceFilter, excludeCompilerGenerated)
                : ToolResponse<TypeSearchResult>.Fail(errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "搜索类型异常");
            return ToolResponse<TypeSearchResult>.Fail("搜索类型异常");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("在本地 dotnet DLL 程序集中按模式搜索类型(支持通配符 *), 例如: '*Stream*', 并获取类型注释")]
    public ToolResponse<TypeSearchResult> SearchLocalTypes(
        [Description(AssemblyPathDescription)] string assemblyPath,
        [Description("名称过滤器, 例如 'Newtonsoft.Json' 匹配类型全名包含 'Newtonsoft.Json' 的类型, 'Stream' 相当于 '*Stream*', 如果不填或填 * 则搜索所有类型")] string typeFullNameFilterText = "*",
        [Description("类型过滤器：class interface enum struct *, 默认 *")] string typeFilter = "*",
        [Description("是否仅显示公共类型")] bool publicOnly = true,
        [Description("最大返回结果数，默认 50")] int maxResults = 50,
        [Description("是否获取注释, 默认 true")] bool comment = true,
        [Description("是否包含类型特性, 默认 false")] bool includeAttributes = false,
        [Description("命名空间过滤器, 支持 * 通配符, 可选")] string? namespaceFilter = null,
        [Description("是否排除编译器生成类型, 默认 true")] bool excludeCompilerGenerated = true)
    {
        try
        {
            var (assemblyInfo, errorMessage) = TryGetLocalAssemblyAnalysisInfo(assemblyPath);
            return errorMessage is null
                ? ToTypeSearchResponse(assemblyInfo!, typeFullNameFilterText, typeFilter, publicOnly, maxResults, comment, includeAttributes, namespaceFilter, excludeCompilerGenerated)
                : ToolResponse<TypeSearchResult>.Fail(errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "搜索本地类型异常");
            return ToolResponse<TypeSearchResult>.Fail("搜索本地类型异常");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("搜索 NuGet 包内某个类型的方法/构造器列表，可包含私有方法，用于先定位再按方法反编译")]
    public async Task<ToolResponse<MethodSearchResult>> SearchMethods(
        [Description(PackageIdDescription)] string packageId,
        [Description("完整类型名称，包括命名空间。嵌套类型可以使用 A.B.C 或 A.B+C")] string typeName,
        [Description(AssemblyNameDescription)] string? assemblyName = null,
        [Description("包的版本号 (可选)")] string? packageVersion = null,
        [Description("目标框架 (可选)")] string? targetFramework = null,
        [Description("方法名或签名片段过滤, 支持 * 通配符。为空返回全部方法")] string? methodNameFilterText = null,
        [Description("成员类型过滤：method constructor staticconstructor *, 默认 *")] string memberType = "*",
        [Description("是否仅显示公共方法，默认 false，便于看到私有/编译器生成方法")] bool publicOnly = false,
        [Description("是否包含基类方法, 默认 false")] bool includeBaseMembers = false,
        [Description("是否包含编译器生成方法, 默认 true")] bool includeCompilerGenerated = true,
        [Description("是否包含特性, 默认 false")] bool includeAttributes = false,
        [Description("最大返回方法数, 默认 200")] int maxResults = DefaultMaxMembersPerGroup)
    {
        try
        {
            var (assemblyInfo, errorMessage) = await TryGetPackageAssemblyAnalysisInfo(packageId, assemblyName, packageVersion, targetFramework);
            return errorMessage is not null
                ? ToolResponse<MethodSearchResult>.Fail(errorMessage)
                : ToMethodSearchResponse(assemblyInfo!, typeName, methodNameFilterText, memberType, publicOnly, includeBaseMembers, includeCompilerGenerated, includeAttributes, maxResults);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "搜索方法异常");
            return ToolResponse<MethodSearchResult>.Fail("搜索方法异常");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("搜索本地 dotnet DLL 某个类型的方法/构造器列表，可包含私有方法，用于先定位再按方法反编译")]
    public ToolResponse<MethodSearchResult> SearchLocalMethods(
        [Description(AssemblyPathDescription)] string assemblyPath,
        [Description("完整类型名称，包括命名空间。嵌套类型可以使用 A.B.C 或 A.B+C")] string typeName,
        [Description("方法名或签名片段过滤, 支持 * 通配符。为空返回全部方法")] string? methodNameFilterText = null,
        [Description("成员类型过滤：method constructor staticconstructor *, 默认 *")] string memberType = "*",
        [Description("是否仅显示公共方法，默认 false，便于看到私有/编译器生成方法")] bool publicOnly = false,
        [Description("是否包含基类方法, 默认 false")] bool includeBaseMembers = false,
        [Description("是否包含编译器生成方法, 默认 true")] bool includeCompilerGenerated = true,
        [Description("是否包含特性, 默认 false")] bool includeAttributes = false,
        [Description("最大返回方法数, 默认 200")] int maxResults = DefaultMaxMembersPerGroup)
    {
        try
        {
            var (assemblyInfo, errorMessage) = TryGetLocalAssemblyAnalysisInfo(assemblyPath);
            return errorMessage is not null
                ? ToolResponse<MethodSearchResult>.Fail(errorMessage)
                : ToMethodSearchResponse(assemblyInfo!, typeName, methodNameFilterText, memberType, publicOnly, includeBaseMembers, includeCompilerGenerated, includeAttributes, maxResults);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "搜索本地方法异常");
            return ToolResponse<MethodSearchResult>.Fail("搜索本地方法异常");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("列出 NuGet 包内 dotnet 程序集的 manifest resources 资源")]
    public async Task<ToolResponse<AssemblyResourcesResult>> ListPackageAssemblyResources(
        [Description(PackageIdDescription)] string packageId,
        [Description(AssemblyNameDescription)] string? assemblyName = null,
        [Description("包的版本号，如果不指定则使用最新版本")] string? packageVersion = null,
        [Description("目标框架(例如 net6.0 net8.0) 如果不指定则使用默认可用的框架")] string? targetFramework = null)
    {
        try
        {
            var (assemblyInfo, errorMessage) = await TryGetPackageAssemblyAnalysisInfo(packageId, assemblyName, packageVersion, targetFramework);
            return errorMessage is null
                ? ToolResponse<AssemblyResourcesResult>.Ok(_resourceService.ListResources(assemblyInfo!))
                : ToolResponse<AssemblyResourcesResult>.Fail(errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "列出 NuGet 程序集资源异常");
            return ToolResponse<AssemblyResourcesResult>.Fail($"列出 NuGet 程序集资源异常: {ex.Message}");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("列出本地 dotnet DLL 的 manifest resources 资源")]
    public ToolResponse<AssemblyResourcesResult> ListLocalAssemblyResources(
        [Description(AssemblyPathDescription)] string assemblyPath)
    {
        try
        {
            var (assemblyInfo, errorMessage) = TryGetLocalAssemblyAnalysisInfo(assemblyPath);
            return errorMessage is null
                ? ToolResponse<AssemblyResourcesResult>.Ok(_resourceService.ListResources(assemblyInfo!))
                : ToolResponse<AssemblyResourcesResult>.Fail(errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "列出本地程序集资源异常");
            return ToolResponse<AssemblyResourcesResult>.Fail($"列出本地程序集资源异常: {ex.Message}");
        }
    }

    [McpServerTool(UseStructuredContent = true), Description("提取 NuGet 包内 dotnet 程序集的 manifest resources 资源到指定绝对目录")]
    public async Task<ToolResponse<ExtractAssemblyResourcesResult>> ExtractPackageAssemblyResources(
        [Description(PackageIdDescription)] string packageId,
        [Description("资源输出目录，必须是绝对路径；不存在时会自动创建")] string outputDirectory,
        [Description("要提取的资源名数组；为空则提取全部可提取的内嵌资源")] IReadOnlyList<string>? resourceNames = null,
        [Description(AssemblyNameDescription)] string? assemblyName = null,
        [Description("包的版本号，如果不指定则使用最新版本")] string? packageVersion = null,
        [Description("目标框架(例如 net6.0 net8.0) 如果不指定则使用默认可用的框架")] string? targetFramework = null)
    {
        try
        {
            var (assemblyInfo, errorMessage) = await TryGetPackageAssemblyAnalysisInfo(packageId, assemblyName, packageVersion, targetFramework);
            return errorMessage is null
                ? ToolResponse<ExtractAssemblyResourcesResult>.Ok(_resourceService.ExtractResources(assemblyInfo!, outputDirectory, resourceNames))
                : ToolResponse<ExtractAssemblyResourcesResult>.Fail(errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "提取 NuGet 程序集资源异常");
            return ToolResponse<ExtractAssemblyResourcesResult>.Fail($"提取 NuGet 程序集资源异常: {ex.Message}");
        }
    }

    [McpServerTool(UseStructuredContent = true), Description("提取本地 dotnet DLL 的 manifest resources 资源到指定绝对目录")]
    public ToolResponse<ExtractAssemblyResourcesResult> ExtractLocalAssemblyResources(
        [Description(AssemblyPathDescription)] string assemblyPath,
        [Description("资源输出目录，必须是绝对路径；不存在时会自动创建")] string outputDirectory,
        [Description("要提取的资源名数组；为空则提取全部可提取的内嵌资源")] IReadOnlyList<string>? resourceNames = null)
    {
        try
        {
            var (assemblyInfo, errorMessage) = TryGetLocalAssemblyAnalysisInfo(assemblyPath);
            return errorMessage is null
                ? ToolResponse<ExtractAssemblyResourcesResult>.Ok(_resourceService.ExtractResources(assemblyInfo!, outputDirectory, resourceNames))
                : ToolResponse<ExtractAssemblyResourcesResult>.Fail(errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "提取本地程序集资源异常");
            return ToolResponse<ExtractAssemblyResourcesResult>.Fail($"提取本地程序集资源异常: {ex.Message}");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("反编译本地 dotnet DLL 中的指定类型为 C# 源码。兼容旧入口；高级场景请用 DecompileLocalAssembly")]
    public ToolResponse<DecompileResult> DecompileLocalType(
        [Description(AssemblyPathDescription)] string assemblyPath,
        [Description("完整类型名称，包括命名空间。嵌套类型可以使用 A.B.C 或 A.B+C")] string typeName)
    {
        try
        {
            var (assemblyInfo, errorMessage) = TryGetLocalAssemblyAnalysisInfo(assemblyPath);
            if (errorMessage is not null)
            {
                return ToolResponse<DecompileResult>.Fail(errorMessage);
            }

            return ToolResponse<DecompileResult>.Ok(_decompiler.Decompile(
                assemblyInfo,
                assemblyInfo!.AssemblyPath,
                [typeName],
                methodNames: null,
                language: "csharp",
                includeXmlDocumentation: true,
                includeMemberBodies: true,
                useDebugSymbols: true,
                useLambdaSyntax: null,
                anonymousMethods: null,
                anonymousTypes: null,
                alwaysQualifyMemberReferences: null,
                alwaysUseBraces: null,
                alwaysShowEnumMemberValues: null,
                useImplicitMethodGroupConversion: null,
                includeAssemblyAttributes: false,
                includeModuleAttributes: false,
                detectControlStructure: true,
                showSequencePoints: false,
                showMetadataTokens: false,
                showMetadataTokensInBase10: false,
                showRawRvaAndBytes: false,
                expandMemberDefinitions: false,
                decodeCustomAttributeBlobs: false,
                maxTypes: 1,
                maxCodeLengthPerItem: 200_000));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "反编译本地类型异常");
            return ToolResponse<DecompileResult>.Fail($"反编译本地类型异常: {ex.Message}");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("反编译本地 dotnet DLL 中一个或多个方法，适合类型太大时按方法下钻；支持 C#/IL 和私有方法")]
    public ToolResponse<DecompileResult> DecompileLocalMethods(
        [Description(AssemblyPathDescription)] string assemblyPath,
        [Description("方法搜索范围类型名称数组，不会输出整个类型。强烈建议传入一个类型名称以消歧；嵌套类型可以使用 A.B.C 或 A.B+C")] IReadOnlyList<string> typeNames,
        [Description("要反编译的方法名称或签名片段数组。支持私有方法、构造器 .ctor、静态构造器 .cctor")] IReadOnlyList<string> methodNames,
        [Description("输出语言: csharp/c#/cs 或 il/msil, 默认 csharp")] string language = "csharp",
        [Description("C# 是否包含 XML 文档注释, 默认 true")] bool? includeXmlDocumentation = null,
        [Description("C# 是否使用 PDB 调试符号里的变量名, 默认 true")] bool? useDebugSymbols = null,
        [Description("C# 是否使用 lambda 语法；false 会更偏向匿名方法/显式委托形式")] bool? useLambdaSyntax = null,
        [Description("C# 是否始终限定成员引用, 默认 true")] bool? alwaysQualifyMemberReferences = null,
        [Description("C# 是否使用方法组简写；false 会输出 new Delegate(Target) 这种更显式形式")] bool? useImplicitMethodGroupConversion = null,
        [Description("IL 是否识别控制流结构, 默认 true")] bool detectControlStructure = true,
        [Description("IL 是否显示 metadata token, 默认 false")] bool showMetadataTokens = false,
        [Description("IL 是否展开成员定义, 默认 false")] bool expandMemberDefinitions = false,
        [Description("每个输出项最大字符数, 默认 60000；超出会截断并标记 Truncated")] int maxCodeLengthPerItem = 60_000)
    {
        try
        {
            var (assemblyInfo, errorMessage) = TryGetLocalAssemblyAnalysisInfo(assemblyPath);
            if (errorMessage is not null)
            {
                return ToolResponse<DecompileResult>.Fail(errorMessage);
            }

            return ToolResponse<DecompileResult>.Ok(_decompiler.Decompile(
                assemblyInfo,
                assemblyInfo!.AssemblyPath,
                typeNames,
                methodNames,
                language,
                includeXmlDocumentation,
                includeMemberBodies: true,
                useDebugSymbols,
                useLambdaSyntax,
                anonymousMethods: null,
                anonymousTypes: null,
                alwaysQualifyMemberReferences,
                alwaysUseBraces: null,
                alwaysShowEnumMemberValues: null,
                useImplicitMethodGroupConversion,
                includeAssemblyAttributes: false,
                includeModuleAttributes: false,
                detectControlStructure,
                showSequencePoints: false,
                showMetadataTokens,
                showMetadataTokensInBase10: false,
                showRawRvaAndBytes: false,
                expandMemberDefinitions,
                decodeCustomAttributeBlobs: false,
                maxTypes: DefaultMaxBatchTypes,
                maxCodeLengthPerItem));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "反编译本地方法异常");
            return ToolResponse<DecompileResult>.Fail($"反编译本地方法异常: {ex.Message}");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("反编译本地 dotnet DLL，支持 C#/IL、批量类型、整程序集、属性文本和高级反编译参数")]
    public ToolResponse<DecompileResult> DecompileLocalAssembly(
        [Description(AssemblyPathDescription)] string assemblyPath,
        [Description("要反编译的完整类型名称数组。为空则反编译整程序集；嵌套类型可以使用 A.B.C 或 A.B+C")] IReadOnlyList<string>? typeNames = null,
        [Description("要反编译的方法名称或签名片段数组。建议同时传 typeNames 缩小范围；支持私有方法、构造器 .ctor、静态构造器 .cctor")] IReadOnlyList<string>? methodNames = null,
        [Description("输出语言: csharp/c#/cs 或 il/msil, 默认 csharp")] string language = "csharp",
        [Description("C# 是否包含 XML 文档注释, 默认 true")] bool? includeXmlDocumentation = null,
        [Description("C# 是否包含方法体, 默认 true；false 适合只看 API 形状")] bool? includeMemberBodies = null,
        [Description("C# 是否使用 PDB 调试符号里的变量名, 默认 true")] bool? useDebugSymbols = null,
        [Description("C# 是否使用 lambda 语法；false 会更偏向匿名方法/显式委托形式")] bool? useLambdaSyntax = null,
        [Description("C# 是否反编译匿名方法/lambda, 默认 true")] bool? anonymousMethods = null,
        [Description("C# 是否反编译匿名类型, 默认 false，保持输出更显式")] bool? anonymousTypes = null,
        [Description("C# 是否始终限定成员引用, 默认 true")] bool? alwaysQualifyMemberReferences = null,
        [Description("C# 是否始终使用花括号, 默认 true")] bool? alwaysUseBraces = null,
        [Description("C# 是否始终显示枚举成员值, 默认 true")] bool? alwaysShowEnumMemberValues = null,
        [Description("C# 是否使用方法组简写；false 会输出 new Delegate(Target) 这种更显式形式")] bool? useImplicitMethodGroupConversion = null,
        [Description("是否附带程序集特性源码文本, 默认 false")] bool includeAssemblyAttributes = false,
        [Description("是否附带模块特性源码文本, 默认 false")] bool includeModuleAttributes = false,
        [Description("IL 是否识别控制流结构, 默认 true")] bool detectControlStructure = true,
        [Description("IL 是否显示 sequence points, 默认 false")] bool showSequencePoints = false,
        [Description("IL 是否显示 metadata token, 默认 false")] bool showMetadataTokens = false,
        [Description("IL metadata token 是否用十进制显示, 默认 false")] bool showMetadataTokensInBase10 = false,
        [Description("IL 是否显示原始 RVA/文件偏移/字节, 默认 false")] bool showRawRvaAndBytes = false,
        [Description("IL 是否展开成员定义, 默认 false")] bool expandMemberDefinitions = false,
        [Description("IL 是否解码 attribute blob, 默认 false")] bool decodeCustomAttributeBlobs = false,
        [Description("最大处理类型数量, 默认 32")] int maxTypes = DefaultMaxBatchTypes,
        [Description("每个输出项最大字符数, 默认 200000；超出会截断并标记 Truncated")] int maxCodeLengthPerItem = 200_000)
    {
        try
        {
            var (assemblyInfo, errorMessage) = TryGetLocalAssemblyAnalysisInfo(assemblyPath);
            if (errorMessage is not null)
            {
                return ToolResponse<DecompileResult>.Fail(errorMessage);
            }

            return ToolResponse<DecompileResult>.Ok(_decompiler.Decompile(
                assemblyInfo,
                assemblyInfo!.AssemblyPath,
                typeNames,
                methodNames,
                language,
                includeXmlDocumentation,
                includeMemberBodies,
                useDebugSymbols,
                useLambdaSyntax,
                anonymousMethods,
                anonymousTypes,
                alwaysQualifyMemberReferences,
                alwaysUseBraces,
                alwaysShowEnumMemberValues,
                useImplicitMethodGroupConversion,
                includeAssemblyAttributes,
                includeModuleAttributes,
                detectControlStructure,
                showSequencePoints,
                showMetadataTokens,
                showMetadataTokensInBase10,
                showRawRvaAndBytes,
                expandMemberDefinitions,
                decodeCustomAttributeBlobs,
                maxTypes,
                maxCodeLengthPerItem));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "反编译本地程序集异常");
            return ToolResponse<DecompileResult>.Fail($"反编译本地程序集异常: {ex.Message}");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("反编译 NuGet 包内程序集，支持 C#/IL、批量类型、整程序集、属性文本和高级反编译参数")]
    public async Task<ToolResponse<DecompileResult>> DecompileAssembly(
        [Description(PackageIdDescription)] string packageId,
        [Description(AssemblyNameDescription)] string? assemblyName = null,
        [Description("要反编译的完整类型名称数组。为空则反编译整程序集；嵌套类型可以使用 A.B.C 或 A.B+C")] IReadOnlyList<string>? typeNames = null,
        [Description("要反编译的方法名称或签名片段数组。建议同时传 typeNames 缩小范围；支持私有方法、构造器 .ctor、静态构造器 .cctor")] IReadOnlyList<string>? methodNames = null,
        [Description("包的版本号，如果不指定则使用最新版本")] string? packageVersion = null,
        [Description("目标框架(例如 net6.0 net8.0) 如果不指定则使用默认可用的框架")] string? targetFramework = null,
        [Description("输出语言: csharp/c#/cs 或 il/msil, 默认 csharp")] string language = "csharp",
        [Description("C# 是否包含 XML 文档注释, 默认 true")] bool? includeXmlDocumentation = null,
        [Description("C# 是否包含方法体, 默认 true；false 适合只看 API 形状")] bool? includeMemberBodies = null,
        [Description("C# 是否使用 PDB 调试符号里的变量名, 默认 true")] bool? useDebugSymbols = null,
        [Description("C# 是否使用 lambda 语法；false 会更偏向匿名方法/显式委托形式")] bool? useLambdaSyntax = null,
        [Description("C# 是否反编译匿名方法/lambda, 默认 true")] bool? anonymousMethods = null,
        [Description("C# 是否反编译匿名类型, 默认 false，保持输出更显式")] bool? anonymousTypes = null,
        [Description("C# 是否始终限定成员引用, 默认 true")] bool? alwaysQualifyMemberReferences = null,
        [Description("C# 是否始终使用花括号, 默认 true")] bool? alwaysUseBraces = null,
        [Description("C# 是否始终显示枚举成员值, 默认 true")] bool? alwaysShowEnumMemberValues = null,
        [Description("C# 是否使用方法组简写；false 会输出 new Delegate(Target) 这种更显式形式")] bool? useImplicitMethodGroupConversion = null,
        [Description("是否附带程序集特性源码文本, 默认 false")] bool includeAssemblyAttributes = false,
        [Description("是否附带模块特性源码文本, 默认 false")] bool includeModuleAttributes = false,
        [Description("IL 是否识别控制流结构, 默认 true")] bool detectControlStructure = true,
        [Description("IL 是否显示 sequence points, 默认 false")] bool showSequencePoints = false,
        [Description("IL 是否显示 metadata token, 默认 false")] bool showMetadataTokens = false,
        [Description("IL metadata token 是否用十进制显示, 默认 false")] bool showMetadataTokensInBase10 = false,
        [Description("IL 是否显示原始 RVA/文件偏移/字节, 默认 false")] bool showRawRvaAndBytes = false,
        [Description("IL 是否展开成员定义, 默认 false")] bool expandMemberDefinitions = false,
        [Description("IL 是否解码 attribute blob, 默认 false")] bool decodeCustomAttributeBlobs = false,
        [Description("最大处理类型数量, 默认 32")] int maxTypes = DefaultMaxBatchTypes,
        [Description("每个输出项最大字符数, 默认 200000；超出会截断并标记 Truncated")] int maxCodeLengthPerItem = 200_000)
    {
        try
        {
            var (assemblyInfo, errorMessage) = await TryGetPackageAssemblyAnalysisInfo(packageId, assemblyName, packageVersion, targetFramework);
            if (errorMessage is not null)
            {
                return ToolResponse<DecompileResult>.Fail(errorMessage);
            }

            return ToolResponse<DecompileResult>.Ok(_decompiler.Decompile(
                assemblyInfo,
                assemblyInfo!.AssemblyPath,
                typeNames,
                methodNames,
                language,
                includeXmlDocumentation,
                includeMemberBodies,
                useDebugSymbols,
                useLambdaSyntax,
                anonymousMethods,
                anonymousTypes,
                alwaysQualifyMemberReferences,
                alwaysUseBraces,
                alwaysShowEnumMemberValues,
                useImplicitMethodGroupConversion,
                includeAssemblyAttributes,
                includeModuleAttributes,
                detectControlStructure,
                showSequencePoints,
                showMetadataTokens,
                showMetadataTokensInBase10,
                showRawRvaAndBytes,
                expandMemberDefinitions,
                decodeCustomAttributeBlobs,
                maxTypes,
                maxCodeLengthPerItem));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "反编译 NuGet 程序集异常");
            return ToolResponse<DecompileResult>.Fail($"反编译 NuGet 程序集异常: {ex.Message}");
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true), Description("反编译 NuGet 包内程序集的一个或多个方法，适合类型太大时按方法下钻；支持 C#/IL 和私有方法")]
    public async Task<ToolResponse<DecompileResult>> DecompileMethods(
        [Description(PackageIdDescription)] string packageId,
        [Description("方法搜索范围类型名称数组，不会输出整个类型。强烈建议传入一个类型名称以消歧；嵌套类型可以使用 A.B.C 或 A.B+C")] IReadOnlyList<string> typeNames,
        [Description("要反编译的方法名称或签名片段数组。支持私有方法、构造器 .ctor、静态构造器 .cctor")] IReadOnlyList<string> methodNames,
        [Description(AssemblyNameDescription)] string? assemblyName = null,
        [Description("包的版本号，如果不指定则使用最新版本")] string? packageVersion = null,
        [Description("目标框架(例如 net6.0 net8.0) 如果不指定则使用默认可用的框架")] string? targetFramework = null,
        [Description("输出语言: csharp/c#/cs 或 il/msil, 默认 csharp")] string language = "csharp",
        [Description("C# 是否包含 XML 文档注释, 默认 true")] bool? includeXmlDocumentation = null,
        [Description("C# 是否使用 PDB 调试符号里的变量名, 默认 true")] bool? useDebugSymbols = null,
        [Description("C# 是否使用 lambda 语法；false 会更偏向匿名方法/显式委托形式")] bool? useLambdaSyntax = null,
        [Description("C# 是否始终限定成员引用, 默认 true")] bool? alwaysQualifyMemberReferences = null,
        [Description("C# 是否使用方法组简写；false 会输出 new Delegate(Target) 这种更显式形式")] bool? useImplicitMethodGroupConversion = null,
        [Description("IL 是否识别控制流结构, 默认 true")] bool detectControlStructure = true,
        [Description("IL 是否显示 metadata token, 默认 false")] bool showMetadataTokens = false,
        [Description("IL 是否展开成员定义, 默认 false")] bool expandMemberDefinitions = false,
        [Description("每个输出项最大字符数, 默认 60000；超出会截断并标记 Truncated")] int maxCodeLengthPerItem = 60_000)
    {
        try
        {
            var (assemblyInfo, errorMessage) = await TryGetPackageAssemblyAnalysisInfo(packageId, assemblyName, packageVersion, targetFramework);
            if (errorMessage is not null)
            {
                return ToolResponse<DecompileResult>.Fail(errorMessage);
            }

            return ToolResponse<DecompileResult>.Ok(_decompiler.Decompile(
                assemblyInfo,
                assemblyInfo!.AssemblyPath,
                typeNames,
                methodNames,
                language,
                includeXmlDocumentation,
                includeMemberBodies: true,
                useDebugSymbols,
                useLambdaSyntax,
                anonymousMethods: null,
                anonymousTypes: null,
                alwaysQualifyMemberReferences,
                alwaysUseBraces: null,
                alwaysShowEnumMemberValues: null,
                useImplicitMethodGroupConversion,
                includeAssemblyAttributes: false,
                includeModuleAttributes: false,
                detectControlStructure,
                showSequencePoints: false,
                showMetadataTokens,
                showMetadataTokensInBase10: false,
                showRawRvaAndBytes: false,
                expandMemberDefinitions,
                decodeCustomAttributeBlobs: false,
                maxTypes: DefaultMaxBatchTypes,
                maxCodeLengthPerItem));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "反编译 NuGet 方法异常");
            return ToolResponse<DecompileResult>.Fail($"反编译 NuGet 方法异常: {ex.Message}");
        }
    }

    private async Task<(AssemblyAnalysisInfo? AssemblyAnalysisInfo, string? ErrorMessage)> TryGetPackageAssemblyAnalysisInfo(
        string packageId,
        string? assemblyName,
        string? packageVersion,
        string? targetFramework)
    {
        assemblyName = EnsureAssemblyName(string.IsNullOrWhiteSpace(assemblyName) ? packageId : assemblyName);

        var versions = await _service.GetPackageMetadataAsync(packageId, includePrerelease: true);
        if (versions.Count == 0)
        {
            return (null, $"未找到包: {packageId}");
        }

        IPackageSearchMetadata? targetMetadata = versions.GetVersion(packageVersion);
        if (targetMetadata is null)
        {
            return (null, "无法获取指定版本的包");
        }

        var requestedVersion = packageVersion ?? "latest";
        if (_service.GetAnalyzeAssemblyCache(packageId, requestedVersion, assemblyName, targetFramework) is { } cachedAssemblyInfo)
        {
            return (cachedAssemblyInfo, null);
        }

        var selection = await _service.FindPackageAssemblyAsync(packageId, assemblyName, packageVersion, targetFramework);
        if (selection is null)
        {
            return (null, $"错误：在包中未找到程序集 '{assemblyName}'");
        }

        if (!Utils.IsDotnetAssembly(selection.AssemblyFile.FullPath))
        {
            return (null, "分析错误, 这是一个本机库");
        }

        var assemblyInfo = await _service.AnalyzePackageAssemblyAsync(packageId, assemblyName, packageVersion, targetFramework);
        if (assemblyInfo is null)
        {
            return (null, "程序集信息获取失败");
        }

        return (assemblyInfo, null);
    }

    private (AssemblyAnalysisInfo? AssemblyAnalysisInfo, string? ErrorMessage) TryGetLocalAssemblyAnalysisInfo(string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            return (null, "程序集路径不能为空");
        }

        var fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
        {
            return (null, $"文件不存在: {assemblyPath}");
        }

        if (!Utils.IsDotnetAssembly(fullPath))
        {
            return (null, "分析错误, 这不是可读取的 .NET 程序集");
        }

        var assemblyInfo = _service.AnalyzeLocalAssembly(fullPath);
        if (assemblyInfo is null)
        {
            return (null, "程序集信息获取失败");
        }

        return (assemblyInfo, null);
    }

    private static ToolResponse<TypeMembersResult> ToTypeMembersResponse(
        AssemblyAnalysisInfo assemblyInfo,
        string typeName,
        string? memberNameFilterText,
        string memberType,
        bool publicOnly,
        bool comment,
        bool includeBaseMembers,
        bool includeAttributes,
        int maxMembersPerGroup)
    {
        var result = CreateTypeMembersResult(
            assemblyInfo,
            typeName,
            memberNameFilterText,
            memberType,
            publicOnly,
            comment,
            includeBaseMembers,
            includeAttributes,
            maxMembersPerGroup);

        return result is null
            ? ToolResponse<TypeMembersResult>.Fail($"错误：未找到类型 '{typeName}'")
            : ToolResponse<TypeMembersResult>.Ok(result);
    }

    private static ToolResponse<TypeMembersBatchResult> ToTypeMembersBatchResponse(
        AssemblyAnalysisInfo assemblyInfo,
        IReadOnlyList<string> typeNames,
        string? memberNameFilterText,
        string memberType,
        bool publicOnly,
        bool comment,
        bool includeBaseMembers,
        bool includeAttributes,
        int maxMembersPerGroup,
        int maxTypes)
    {
        var normalizedTypeNames = NormalizeTypeNames(typeNames, maxTypes);
        if (normalizedTypeNames.Count == 0)
        {
            return ToolResponse<TypeMembersBatchResult>.Fail("typeNames 不能为空");
        }

        var results = new List<TypeMembersResult>();
        var failures = new List<TypeLookupFailureDto>();
        foreach (var typeName in normalizedTypeNames)
        {
            var result = CreateTypeMembersResult(
                assemblyInfo,
                typeName,
                memberNameFilterText,
                memberType,
                publicOnly,
                comment,
                includeBaseMembers,
                includeAttributes,
                maxMembersPerGroup);
            if (result is null)
            {
                failures.Add(new TypeLookupFailureDto(typeName, "未找到类型"));
                continue;
            }

            results.Add(result);
        }

        return ToolResponse<TypeMembersBatchResult>.Ok(new TypeMembersBatchResult(
            ToAssemblyIdentityDto(assemblyInfo),
            results,
            failures,
            results.Sum(result => result.TotalShown)));
    }

    private static TypeMembersResult? CreateTypeMembersResult(
        AssemblyAnalysisInfo assemblyInfo,
        string typeName,
        string? memberNameFilterText,
        string memberType,
        bool publicOnly,
        bool comment,
        bool includeBaseMembers,
        bool includeAttributes,
        int maxMembersPerGroup)
    {
        var targetType = FindType(assemblyInfo, typeName);
        if (targetType is null)
        {
            return null;
        }

        maxMembersPerGroup = maxMembersPerGroup <= 0 ? DefaultMaxMembersPerGroup : maxMembersPerGroup;
        var membersQueryable = targetType.GetMembers(includeBaseMembers).AsEnumerable();

        if (publicOnly)
        {
            membersQueryable = membersQueryable.Where(m => m.DeclaredAccessibility == Accessibility.Public);
        }

        if (!string.IsNullOrWhiteSpace(memberNameFilterText))
        {
            Regex regex = new(Regex.Escape(memberNameFilterText).Replace(@"\*", ".*"), RegexOptions.IgnoreCase);
            membersQueryable = membersQueryable.Where(m => regex.IsMatch(m.Name));
        }

        var members = membersQueryable.ToList();
        var normalizedMemberType = memberType.ToLowerInvariant();
        var memberGroups = new List<MemberGroupDto>();

        AddMemberGroup(memberGroups, "constructor", normalizedMemberType, members.OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Constructor), comment, includeAttributes, maxMembersPerGroup, targetType);
        AddMemberGroup(memberGroups, "method", normalizedMemberType, members.OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary), comment, includeAttributes, maxMembersPerGroup, targetType);
        AddMemberGroup(memberGroups, "property", normalizedMemberType, members.OfType<IPropertySymbol>(), comment, includeAttributes, maxMembersPerGroup, targetType);
        AddMemberGroup(memberGroups, "field", normalizedMemberType, members.OfType<IFieldSymbol>(), comment, includeAttributes, maxMembersPerGroup, targetType);
        AddMemberGroup(memberGroups, "event", normalizedMemberType, members.OfType<IEventSymbol>(), comment, includeAttributes, maxMembersPerGroup, targetType);

        var totalShown = memberGroups.Sum(group => group.Members.Count);
        return new TypeMembersResult(
            ToAssemblyIdentityDto(assemblyInfo),
            typeName,
            ToTypeDto(targetType, comment, includeAttributes),
            [.. targetType.GetBaseTypes().Select(v => v.ToFullName())],
            [.. targetType.GetInterfaces(includeBaseMembers).Select(v => v.ToFullName())],
            memberGroups,
            totalShown);
    }

    private static ToolResponse<TypeSearchResult> ToTypeSearchResponse(
        AssemblyAnalysisInfo assemblyInfo,
        string typeFullNameFilterText,
        string typeFilter,
        bool publicOnly,
        int maxResults,
        bool comment,
        bool includeAttributes,
        string? namespaceFilter,
        bool excludeCompilerGenerated)
    {
        if (assemblyInfo.AllTypes.Count == 1 && assemblyInfo.AllTypes[0].Name == "<Module>")
        {
            return ToolResponse<TypeSearchResult>.Fail("搜索失败, 这个程序集里没有任何类型");
        }

        var allTypesQueryable = assemblyInfo.AllTypes.AsEnumerable();

        if (publicOnly)
        {
            allTypesQueryable = allTypesQueryable.Where(t => t.DeclaredAccessibility == Accessibility.Public);
        }

        if (excludeCompilerGenerated)
        {
            allTypesQueryable = allTypesQueryable.Where(t => !IsCompilerGenerated(t));
        }

        if (typeFilter != "*")
        {
            allTypesQueryable = typeFilter.ToLowerInvariant() switch
            {
                "class" => allTypesQueryable.Where(t => t.TypeKind == TypeKind.Class && !t.IsStatic),
                "interface" => allTypesQueryable.Where(t => t.TypeKind == TypeKind.Interface),
                "enum" => allTypesQueryable.Where(t => t.TypeKind == TypeKind.Enum),
                "struct" => allTypesQueryable.Where(t => t.TypeKind == TypeKind.Struct),
                _ => allTypesQueryable
            };
        }

        if (!string.IsNullOrWhiteSpace(namespaceFilter) && namespaceFilter != "*")
        {
            var namespacePattern = namespaceFilter.Contains('*', StringComparison.Ordinal)
                ? namespaceFilter
                : $"*{namespaceFilter}*";
            Regex namespaceRegex = new(Regex.Escape(namespacePattern).Replace(@"\*", ".*"), RegexOptions.IgnoreCase);
            allTypesQueryable = allTypesQueryable.Where(v => namespaceRegex.IsMatch(v.ContainingNamespace.ToDisplayStringOrEmpty()));
        }

        if (!string.IsNullOrWhiteSpace(typeFullNameFilterText) && typeFullNameFilterText != "*")
        {
            var searchPattern = typeFullNameFilterText.Contains('*', StringComparison.Ordinal)
                ? typeFullNameFilterText
                : $"*{typeFullNameFilterText}*";
            Regex regex = new(Regex.Escape(searchPattern).Replace(@"\*", ".*"), RegexOptions.IgnoreCase);
            allTypesQueryable = allTypesQueryable.Where(v => regex.IsMatch(v.ToFullName()));
        }

        var allTypes = allTypesQueryable.ToList();
        var result = new TypeSearchResult(
            ToAssemblyIdentityDto(assemblyInfo),
            typeFullNameFilterText,
            typeFilter,
            publicOnly,
            allTypes.Count,
            Math.Min(allTypes.Count, maxResults),
            [.. allTypes.Take(maxResults).OrderBy(t => t.ToFullName()).Select(type => ToTypeDto(type, comment, includeAttributes))]);

        return ToolResponse<TypeSearchResult>.Ok(result);
    }

    private static ToolResponse<MethodSearchResult> ToMethodSearchResponse(
        AssemblyAnalysisInfo assemblyInfo,
        string typeName,
        string? methodNameFilterText,
        string memberType,
        bool publicOnly,
        bool includeBaseMembers,
        bool includeCompilerGenerated,
        bool includeAttributes,
        int maxResults)
    {
        using var peFile = AssemblyImageUtilities.OpenPeFile(assemblyInfo.AssemblyPath, assemblyInfo.AssemblyImage);
        if (!MetadataMethodUtilities.TryGetTypeDefinitionHandle(peFile.Metadata, FindType(assemblyInfo, typeName)?.ToMetadataFullName() ?? typeName, out _))
        {
            return ToolResponse<MethodSearchResult>.Fail($"错误：未找到类型 '{typeName}'");
        }

        maxResults = maxResults <= 0 ? DefaultMaxMembersPerGroup : maxResults;
        var methods = MetadataMethodUtilities.FindMethods(
            peFile.Metadata,
            assemblyInfo,
            [typeName],
            string.IsNullOrWhiteSpace(methodNameFilterText) ? [] : [methodNameFilterText],
            memberType,
            publicOnly,
            includeCompilerGenerated,
            includeAttributes,
            maxResults,
            exactSimpleMethodName: false,
            new List<string>(),
            includeBaseTypes: includeBaseMembers);
        var totalMatches = methods.Count;

        return ToolResponse<MethodSearchResult>.Ok(new MethodSearchResult(
            ToAssemblyIdentityDto(assemblyInfo),
            typeName,
            memberType,
            methodNameFilterText,
            publicOnly,
            includeBaseMembers,
            includeAttributes,
            totalMatches,
            [.. methods.Select(method => new MemberDto(
                method.MethodName,
                method.DeclaringTypeName,
                method.IsInherited,
                method.Kind,
                method.Accessibility,
                method.Signature,
                method.IsStatic,
                null,
                method.ReturnType,
                method.Parameters,
                method.Attributes))]));
    }

    private static void AddMemberGroup<TSymbol>(
        List<MemberGroupDto> groups,
        string groupKind,
        string requestedKind,
        IEnumerable<TSymbol> symbols,
        bool includeDocumentation,
        bool includeAttributes,
        int maxMembersPerGroup,
        INamedTypeSymbol requestedType)
        where TSymbol : ISymbol
    {
        if (requestedKind is not "*" && requestedKind != groupKind)
        {
            return;
        }

        var orderedSymbols = symbols
            .OrderBy(symbol => symbol.Name)
            .ToList();
        if (orderedSymbols.Count == 0)
        {
            return;
        }

        groups.Add(new MemberGroupDto(
            groupKind,
            orderedSymbols.Count,
            [.. orderedSymbols.Take(maxMembersPerGroup).Select(symbol => ToMemberDto(symbol, groupKind, includeDocumentation, includeAttributes, requestedType))]));
    }

    private static NuGetPackageSearchItem ToSearchItem(IPackageSearchMetadata package)
    {
        return new NuGetPackageSearchItem(
            package.Identity.Id,
            package.Identity.Version.ToString(),
            package.Description,
            package.DownloadCount,
            package.Tags);
    }

    private static PackageAssembliesResult ToPackageAssembliesResult(PackageInfo packageInfo)
    {
        return new PackageAssembliesResult(
            packageInfo.PackageId,
            packageInfo.PackageVersion,
            packageInfo.AssemblyGroupKind,
            GetAssemblyGroupNotes(packageInfo.AssemblyGroupKind),
            [.. packageInfo.TargetFrameworks.Select(framework => new PackageTargetFrameworkAssembliesDto(
                framework.TargetFramework,
                [.. framework.AssemblyFiles.Select(file => new AssemblyFileDto(file.Name, file.RelativePath, file.FullPath))]))]);
    }

    private static AssemblyAnalysisDto ToAssemblyAnalysisDto(AssemblyAnalysisInfo assemblyInfo)
    {
        var namespaces = assemblyInfo.Namespaces
            .Take(DefaultNamespaceLimit)
            .Select(ns => new NamespaceInfoDto(ns, assemblyInfo.AllTypes.Count(t => t.ContainingNamespace?.ToDisplayStringOrEmpty() == ns)))
            .ToList();

        var references = assemblyInfo.References.Take(DefaultReferenceLimit).ToList();

        return new AssemblyAnalysisDto(
            assemblyInfo.SourceKind,
            assemblyInfo.PackageId,
            assemblyInfo.PackageVersion,
            assemblyInfo.TargetFramework,
            assemblyInfo.AssemblyName,
            assemblyInfo.AssemblyVersion,
            assemblyInfo.AssemblyPath,
            assemblyInfo.IsRefAssembly,
            new TypeStatisticsDto(
                assemblyInfo.AllTypesCount,
                assemblyInfo.PublicTypesCount,
                assemblyInfo.ClassesCount,
                assemblyInfo.StaticClassesCount,
                assemblyInfo.InterfacesCount,
                assemblyInfo.EnumsCount,
                assemblyInfo.StructsCount,
                assemblyInfo.DelegatesCount),
            namespaces,
            assemblyInfo.Namespaces.Count,
            assemblyInfo.Namespaces.Count > DefaultNamespaceLimit,
            references,
            assemblyInfo.References.Count,
            assemblyInfo.References.Count > DefaultReferenceLimit);
    }

    private static AssemblyIdentityDto ToAssemblyIdentityDto(AssemblyAnalysisInfo assemblyInfo)
    {
        return new AssemblyIdentityDto(
            assemblyInfo.SourceKind,
            assemblyInfo.PackageId,
            assemblyInfo.PackageVersion,
            assemblyInfo.TargetFramework,
            assemblyInfo.AssemblyName,
            assemblyInfo.AssemblyPath);
    }

    private static AssemblyMetadataResult ToAssemblyMetadataResult(
        AssemblyAnalysisInfo assemblyInfo,
        bool includeAssemblyAttributes,
        bool includeModuleAttributes)
    {
        var assemblySymbol = assemblyInfo.AssemblySymbol;
        var targetFrameworkAttribute = assemblySymbol.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Runtime.Versioning.TargetFrameworkAttribute");
        var informationalVersionAttribute = assemblySymbol.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyInformationalVersionAttribute");

        return new AssemblyMetadataResult(
            ToAssemblyIdentityDto(assemblyInfo),
            assemblySymbol.Identity.GetDisplayName(),
            assemblyInfo.AssemblyVersion,
            targetFrameworkAttribute?.ConstructorArguments.FirstOrDefault().Value?.ToString(),
            informationalVersionAttribute?.ConstructorArguments.FirstOrDefault().Value?.ToString(),
            assemblyInfo.IsRefAssembly,
            includeAssemblyAttributes ? [.. assemblySymbol.GetAttributes().Select(ToAttributeDto)] : [],
            [.. assemblySymbol.Modules.Select(module => new ModuleMetadataDto(
                module.Name,
            includeModuleAttributes ? [.. module.GetAttributes().Select(ToAttributeDto)] : []))],
            [.. assemblySymbol.Modules
                .SelectMany(module => module.ReferencedAssemblySymbols)
                .GroupBy(symbol => symbol.Identity.GetDisplayName())
                .Select(group => group.First())
                .Select(symbol => new AssemblyReferenceDto(
                    symbol.Identity.Name,
                    symbol.Identity.Version.ToString(),
                    symbol.Identity.CultureName,
                    symbol.Identity.PublicKeyToken.IsDefaultOrEmpty
                        ? null
                        : string.Concat(symbol.Identity.PublicKeyToken.Select(value => value.ToString("x2")))))
                .OrderBy(reference => reference.Name)]);
    }

    private static TypeDto ToTypeDto(INamedTypeSymbol type, bool includeDocumentation, bool includeAttributes)
    {
        return new TypeDto(
            type.Name,
            type.ToFullName(),
            type.ToMetadataFullName(),
            type.ContainingNamespace.ToDisplayStringOrEmpty(),
            type.TypeKind.ToString(),
            type.DeclaredAccessibility.ToString(),
            type.IsStatic,
            type.ToFullDisplayText(),
            includeDocumentation ? type.GetCommentText(0) : null,
            includeAttributes ? [.. type.GetAttributes().Select(ToAttributeDto)] : []);
    }

    private static MemberDto ToMemberDto(ISymbol symbol, string kind, bool includeDocumentation, bool includeAttributes, INamedTypeSymbol? requestedType = null)
    {
        var declaringTypeName = symbol.ContainingType?.ToFullName();
        var isInherited = requestedType is not null
            && symbol.ContainingType is not null
            && !SymbolEqualityComparer.Default.Equals(symbol.ContainingType, requestedType);

        return new MemberDto(
            symbol.Name,
            declaringTypeName,
            isInherited,
            kind,
            symbol.DeclaredAccessibility.ToString(),
            ToMemberSignature(symbol),
            symbol.IsStatic,
            includeDocumentation ? symbol.GetCommentText(0) : null,
            ToReturnType(symbol),
            ToParameters(symbol),
            includeAttributes ? [.. symbol.GetAttributes().Select(ToAttributeDto)] : []);
    }

    private static string? ToReturnType(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method when method.MethodKind == MethodKind.Constructor => null,
            IMethodSymbol method => method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(),
            IPropertySymbol property => property.Type.ToDisplayString(),
            IFieldSymbol field => field.Type.ToDisplayString(),
            IEventSymbol evt => evt.Type.ToDisplayString(),
            _ => null
        };
    }

    private static IReadOnlyList<MemberParameterDto> ToParameters(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method => [.. method.Parameters.Select(ToParameterDto)],
            IPropertySymbol property when property.IsIndexer => [.. property.Parameters.Select(ToParameterDto)],
            _ => []
        };
    }

    private static MemberParameterDto ToParameterDto(IParameterSymbol parameter)
    {
        return new MemberParameterDto(
            parameter.Name,
            parameter.Type.ToDisplayString(),
            parameter.RefKind.ToString(),
            parameter.IsOptional,
            parameter.HasExplicitDefaultValue ? FormatValue(parameter.ExplicitDefaultValue) : null);
    }

    private static AttributeDto ToAttributeDto(AttributeData attribute)
    {
        return new AttributeDto(
            attribute.AttributeClass?.ToDisplayString() ?? "<unknown>",
            attribute.ToString() ?? string.Empty,
            [.. attribute.ConstructorArguments.Select(ToAttributeArgumentDto)],
            [.. attribute.NamedArguments.Select(argument => new NamedAttributeArgumentDto(argument.Key, ToAttributeArgumentDto(argument.Value)))]);
    }

    private static AttributeArgumentDto ToAttributeArgumentDto(TypedConstant constant)
    {
        if (constant.Kind == TypedConstantKind.Array)
        {
        return new AttributeArgumentDto(
            constant.Kind.ToString(),
            constant.Type?.ToDisplayString() ?? "<unknown>",
            null,
            [.. constant.Values.Select(v => FormatTypedConstantValue(v))]);
    }

        return new AttributeArgumentDto(
            constant.Kind.ToString(),
            constant.Type?.ToDisplayString() ?? "<unknown>",
            FormatTypedConstantValue(constant),
            []);
    }

    private static string? FormatTypedConstantValue(TypedConstant constant)
    {
        return constant.Kind switch
        {
            TypedConstantKind.Error => null,
            TypedConstantKind.Type when constant.Value is ITypeSymbol type => type.ToDisplayString(),
            _ => FormatValue(constant.Value)
        };
    }

    private static string? FormatValue(object? value)
    {
        return value switch
        {
            null => null,
            string text => text,
            char c => c.ToString(),
            bool b => b.ToString(),
            _ => value.ToString()
        };
    }

    private static string ToMemberSignature(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method when method.MethodKind == MethodKind.Constructor => method.ToConstructorFullDisplayText(),
            IMethodSymbol method => method.ToFullDisplay(),
            IPropertySymbol property => property.ToFullDisplayText(),
            IFieldSymbol field => field.ToFullDisplayText(),
            IEventSymbol evt => evt.ToFullDisplayText(),
            _ => symbol.ToDisplayString()
        };
    }

    private static INamedTypeSymbol? FindType(AssemblyAnalysisInfo assemblyInfo, string typeName)
    {
        return assemblyInfo.AllTypes.FirstOrDefault(t =>
            t.ToFullName().Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
            t.ToMetadataFullName().Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
            t.ToDisplayString().Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
            t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
            t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCompilerGenerated(ISymbol symbol)
    {
        return symbol.Name.Contains('<', StringComparison.Ordinal)
            || symbol.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
    }

    private static IReadOnlyList<string> NormalizeTypeNames(IReadOnlyList<string> typeNames, int maxTypes)
    {
        maxTypes = maxTypes <= 0 ? DefaultMaxBatchTypes : maxTypes;
        return [.. typeNames
            .Where(typeName => !string.IsNullOrWhiteSpace(typeName))
            .Select(typeName => typeName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxTypes)];
    }

    private static IReadOnlyList<string> GetAssemblyGroupNotes(PackageAssemblyGroupKind groupKind)
    {
        return groupKind switch
        {
            PackageAssemblyGroupKind.Ref => ["没有找到 Lib 程序集, 只找到了 Ref 程序集"],
            PackageAssemblyGroupKind.NetFrameworkReferenceAssemblies => ["找到了 .NET Framework 参考程序集"],
            PackageAssemblyGroupKind.None => ["没有找到可用程序集"],
            _ => []
        };
    }

    private static string EnsureAssemblyName(string assemblyName)
    {
        return assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? assemblyName
            : $"{assemblyName}.dll";
    }
}
