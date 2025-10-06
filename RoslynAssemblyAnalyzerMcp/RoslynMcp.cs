using ModelContextProtocol.Server;
using System.ComponentModel;
using NuGet.Protocol.Core.Types;
using System.Text;
using Microsoft.CodeAnalysis;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using NuGet.Packaging;

namespace RoslynAssemblyAnalyzerMcp;

[McpServerToolType]
public static class RoslynMcp
{
    private static readonly RoslynService _service = new();

    private const string PackageIdDescription = "NuGet PackageId (例如: 'Newtonsoft.Json', 'Microsoft.EntityFrameworkCore')";

    public static Task Initialize() => _service.Initialize();

    private static bool TryGetAnalyzeAssemblyCache(string packageId, string packageVersion, string assemblyName, string? targetFramework, [NotNullWhen(true)] out AssemblyAnalysisInfo? assemblyAnalysisInfo, [NotNullWhen(false)] out string? errorMessage)
    {
        var assemblyInfo = _service.GetAnalyzeAssemblyCache(packageId, packageVersion, assemblyName, targetFramework);
        if (assemblyInfo is null)
        {
            assemblyAnalysisInfo = null;
            errorMessage = $"错误: 程序集未解析或缓存已过期, 请先调用{nameof(AnalyzeAssembly)}解析程序集";
            if (_service.GetAnalyzeAssemblyCache(packageId) is [_, ..] assemblies)
            {
                errorMessage += $"只找到了以下程序集:\n{string.Join(", ", assemblies
                    .Select(v => $"PackageVersion:{v.PackageVersion}, AssemblyName:{v.AssemblyName}{(string.IsNullOrWhiteSpace(v.TargetFramework) ? "" : $", TargetFramework:{v.TargetFramework}")}"))})";
            }
            return false;
        }
        else
        {
            assemblyAnalysisInfo = assemblyInfo;
            errorMessage = null;
            return true;
        }
    }

    private static string EnsureAssemblyName(string assemblyName)
    {
        if (!assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
             !assemblyName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            assemblyName += ".dll";
        }
        return assemblyName;
    }

    [McpServerTool, Description("在NuGet上搜索包, 返回NuGet包基本信息")]
    public static async Task<string> SearchNuGetPackage(
        [Description("要搜索的包名或关键词 (例如：'json' 'entityframework')")] string searchText,
        [Description("返回的最大结果数量, 默认为 10")] int maxResults = 10)
    {
        try
        {
            var packages = await _service.SearchNuGetAsync(searchText, maxResults);

            if (packages.Count == 0)
            {
                return "没有找到匹配的包";
            }

            var result = new StringBuilder();

            result.AppendLine($"一共搜索了 {packages.Count} 个包");
            result.AppendLine();

            var jsonArrayPackages = new JsonArray();
            foreach (var package in packages)
            {
                result.AppendLine($"- {package.Identity.Id} ({package.Identity.Version})");
                result.AppendLine($"  描述: {package.Description}");
                result.AppendLine($"  下载量: {package.DownloadCount:N0}");
                result.AppendLine($"  标签: {package.Tags}");
                result.AppendLine();
            }

            return result.ToString();
        }
        catch (Exception)
        {
            return "搜索NuGet包异常";
        }
    }


    [McpServerTool, Description("获取指定 NuGet 包的详细元数据，包括最新版本、所有历史版本列表、作者、项目 URL、依赖项等完整信息")]
    public static async Task<string> GetNuGetPackageDetails(
        [Description(PackageIdDescription)] string packageId)
    {
        try
        {
            var versions = await _service.GetPackageMetadataAsync(packageId);

            if (versions is [])
            {
                return "没有找到该包";
            }

            // 获取最新版本的详细信息
            var latest = versions.OrderByDescending(m => m.Identity.Version).First();

            StringBuilder result = new();

            result.AppendLine($"PackageId: {latest.Identity.Id}");
            result.AppendLine($"最新版本: {latest.Identity.Version}");
            result.AppendLine($"作者: {latest.Authors}");
            result.AppendLine($"描述: {latest.Description}");
            result.AppendLine($"项目 URL: {latest.ProjectUrl}");
            result.AppendLine($"许可证: {latest.LicenseUrl}");
            result.AppendLine($"下载量: {latest.DownloadCount:N0}");
            result.AppendLine($"发布日期: {latest.Published?.DateTime:yyyy-MM-dd}");
            result.AppendLine($"标签: {latest.Tags}");

            // 依赖项
            if (latest.DependencySets.Any())
            {
                result.AppendLine("依赖项:");
                foreach (var dependencySet in latest.DependencySets)
                {
                    var framework = dependencySet.TargetFramework?.GetShortFolderName() ?? "所有框架";
                    result.AppendLine($"  [{framework}]");

                    if (dependencySet.Packages != null && dependencySet.Packages.Any())
                    {
                        foreach (var dep in dependencySet.Packages)
                        {
                            result.AppendLine($"    - {dep.Id} {dep.VersionRange}");
                        }
                    }
                    else
                    {
                        result.AppendLine("    无依赖项");
                    }
                }
            }

            // 所有版本
            result.AppendLine($"所有版本 (共 {versions.Count} 个):");

            foreach (var version in versions.OrderByDescending(m => m.Identity.Version).Take(20))
            {
                result.AppendLine($"  {version.Identity.Version} - 发布于 {version.Published?.DateTime:yyyy-MM-dd}");
            }

            if (versions.Count > 20)
            {
                result.AppendLine($"  还有另外{versions.Count - 20}个版本 ...");
            }

            return result.ToString();
        }
        catch (Exception)
        {
            return "获取包信息异常";
        }
    }


    [McpServerTool, Description("获取NuGet包中所有程序集文件信息")]
    public static async Task<string> GetPackageAssemblies(
        [Description(PackageIdDescription)] string packageId,
        [Description("包的版本号, 如果不指定则使用最新版本")] string? packageVersion = null)
    {
        try
        {
            var result = new StringBuilder();

            var versions = await _service.GetPackageMetadataAsync(packageId, includePrerelease: true);
            if (versions is [])
            {
                return $"未找到包: {packageId}";
            }

            result.AppendLine($"Package Id: {packageId}");

            // 确定要使用的版本
            IPackageSearchMetadata? targetMetadata = versions.GetVersion(packageVersion);
            if (targetMetadata is null)
            {
                result.AppendLine("无法获取指定版本的包");
                return result.ToString();
            }

            result.AppendLine();

            var downloadResult = await _service.DownloadPackageAsync(targetMetadata.Identity);
            if (downloadResult.Status != DownloadResourceResultStatus.Available)
            {
                return $"错误：无法下载包，状态: {downloadResult.Status}";
            }

            // 读取包内容
            using var packageReader = downloadResult.PackageReader;
            // 获取所有 lib 文件
            var libItems = packageReader.GetLibItems().ToList();

            bool isFindRefAssembly = false;

            if (libItems.Count == 0)
            {
                isFindRefAssembly = true;

                var items = await packageReader.GetItemsAsync(PackagingConstants.Folders.Ref, default);
                libItems = items.ToList();

                if (libItems.Count == 0)
                {
                    result.AppendLine("该包不包含任何程序集文件。");
                    return result.ToString();
                }
            }

            if (isFindRefAssembly)
            {
                result.AppendLine($"没有找到Lib程序集, 只找到了Ref程序集");
            }
            result.AppendLine($"找到 {libItems.Count} 个目标框架:");
            foreach (var libItem in libItems)
            {
                var framework = libItem.TargetFramework.GetShortFolderName();
                result.AppendLine($"[{framework}]");

                var assemblies = libItem.Items.Where(item =>
                    item.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    item.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();

                if (assemblies.Count == 0)
                {
                    result.AppendLine("   (无程序集文件)");
                }
                else
                {
                    foreach (var assembly in assemblies)
                    {
                        var fileName = Path.GetFileName(assembly);
                        result.AppendLine($"- {fileName}");

                    }
                }

                result.AppendLine();
            }

            return result.ToString();
        }
        catch (Exception)
        {
            return "获取包程序集异常";
        }
    }

    private static async Task<(AssemblyAnalysisInfo? AssemblyAnalysisInfo, string? ErrorMessage)> TryGetAssemblyAnalysisInfo(string packageId, string assemblyName, string? packageVersion, string? targetFramework)
    {
        assemblyName = EnsureAssemblyName(assemblyName);

        var versions = await _service.GetPackageMetadataAsync(packageId, includePrerelease: true);
        if (versions.Count == 0)
        {
            return (null, $"未找到包: {packageId}");
        }

        // 确定要使用的版本
        IPackageSearchMetadata? targetMetadata = versions.GetVersion(packageVersion);
        if (targetMetadata is null)
        {
            return (null, "无法获取指定版本的包");
        }

        AssemblyAnalysisInfo? assemblyInfo;
        if (!TryGetAnalyzeAssemblyCache(packageId, packageVersion ?? "latest", assemblyName, targetFramework, out assemblyInfo, out var error))
        {
            // 下载包
            var downloadResult = await _service.DownloadPackageAsync(targetMetadata.Identity);
            if (downloadResult.Status != DownloadResourceResultStatus.Available)
            {
                return (null, $"错误：无法下载包，状态: {downloadResult.Status}");
            }

            // 查找程序集文件
            string? assemblyPath = null;
            string? selectedFramework = null;

            using (var packageReader = downloadResult.PackageReader)
            {
                var libItems = (await packageReader.GetLibItemsAsync(default)).ToList();

                if (libItems.Count == 0)
                {
                    // 如果找不到lib, 则找refs
                    var items = await packageReader.GetItemsAsync(PackagingConstants.Folders.Ref, default);
                    libItems = items.ToList();
                    if (libItems.Count == 0)
                    {
                        return (null, "错误：该包不包含任何程序集文件");
                    }
                }

                // 查找指定的程序集
                foreach (var libItem in libItems)
                {
                    var framework = libItem.TargetFramework.GetShortFolderName();

                    // 如果指定了目标框架，只查找匹配的
                    if (!string.IsNullOrWhiteSpace(targetFramework) && framework != targetFramework)
                    {
                        continue;
                    }

                    var assembly = libItem.Items.FirstOrDefault(item =>
                        Path.GetFileName(item).Equals(assemblyName, StringComparison.OrdinalIgnoreCase));

                    if (assembly != null)
                    {
                        // {GlobalPackagePath}\{packageId}\{version}\{assembly}
                        assemblyPath = Path.Combine(
                            _service.GlobalPackagePath,
                            targetMetadata.Identity.Id.ToLowerInvariant(),
                            targetMetadata.Identity.Version.ToString(),
                            assembly);

                        selectedFramework = framework;
                        break;
                    }
                }
            }

            if (assemblyPath == null)
            {
                return (null, $"错误：在包中未找到程序集 '{assemblyName}'");
            }

            assemblyInfo = _service.AnalyzeAssembly(packageId, targetMetadata.Identity.Version.ToString(), assemblyName, targetFramework, assemblyPath, packageVersion is null);
        }

        if(assemblyInfo is null)
        {
            return (null, "程序集信息获取失败");
        }

        return (assemblyInfo, null);
    }


    [McpServerTool, Description("分析并获取dotnet程序集的基本信息 (获取程序集的所有的类型个数, 命名空间, 引用信息等)")]
    public static async Task<string> AnalyzeAssembly(
        [Description(PackageIdDescription)] string packageId,
        [Description("要分析的程序集文件名, 通常是包名.dll(例如：'Newtonsoft.Json' 或 'Newtonsoft.Json.dll')")] string assemblyName,
        [Description("包的版本号，如果不指定则使用最新版本")] string? packageVersion = null,
        [Description("目标框架(例如 net6.0 net8.0) 如果不指定则使用默认可用的框架")] string? targetFramework = null)
    {
        try
        {
            var result = new StringBuilder();

            var (assemblyInfo, errorMessage) = await TryGetAssemblyAnalysisInfo(packageId, assemblyName, packageVersion, targetFramework);

            if(errorMessage != null)
            {
                return errorMessage;
            }

            result.AppendLine($"目标框架: {assemblyInfo!.TargetFramework}");
            result.AppendLine("程序集分析:");
            result.AppendLine();


            // 收集程序集信息
            var assemblyInfoBuilder = new StringBuilder();
            assemblyInfoBuilder.AppendLine($"程序集名称: {assemblyInfo.AssemblyName}");
            assemblyInfoBuilder.AppendLine($"版本: {assemblyInfo.AssemblyVersion}");
            if (assemblyInfo.IsRefAssembly)
            {
                assemblyInfoBuilder.AppendLine("这是一个RefsAssembly");
            }

            assemblyInfoBuilder.AppendLine();
            assemblyInfoBuilder.AppendLine($"类型统计:");
            assemblyInfoBuilder.AppendLine($"  总类型数: {assemblyInfo.AllTypesCount}");
            assemblyInfoBuilder.AppendLine($"  公共类型: {assemblyInfo.PublicTypesCount}");
            assemblyInfoBuilder.AppendLine($"  - 公共类: {assemblyInfo.ClassesCount}");
            assemblyInfoBuilder.AppendLine($"  - 公共静态类: {assemblyInfo.StaticClassesCount}");
            assemblyInfoBuilder.AppendLine($"  - 公共接口: {assemblyInfo.InterfacesCount}");
            assemblyInfoBuilder.AppendLine($"  - 公共枚举: {assemblyInfo.EnumsCount}");
            assemblyInfoBuilder.AppendLine($"  - 公共结构: {assemblyInfo.StructsCount}");
            assemblyInfoBuilder.AppendLine($"  - 公共委托: {assemblyInfo.DelegatesCount}");
            assemblyInfoBuilder.AppendLine();

            assemblyInfoBuilder.AppendLine($"所有的命名空间 ({assemblyInfo.Namespaces.Count} 个):");
            foreach (var ns in assemblyInfo.Namespaces.Take(50))
            {
                var nsTypes = assemblyInfo.AllTypes.Count(t => t.ContainingNamespace?.ToDisplayString() == ns);
                assemblyInfoBuilder.AppendLine($"   - {ns} ({nsTypes} 个类型)");
            }
            if (assemblyInfo.Namespaces.Count > 20)
            {
                assemblyInfoBuilder.AppendLine($"   ... 还有 {assemblyInfo.Namespaces.Count - 50} 个命名空间");
            }


            assemblyInfoBuilder.AppendLine();
            assemblyInfoBuilder.AppendLine($"引用的程序集 ({assemblyInfo.References.Count} 个):");
            foreach (var refAsm in assemblyInfo.References.Take(30))
            {
                assemblyInfoBuilder.AppendLine($"   - {refAsm}");
            }
            if (assemblyInfo.References.Count > 30)
            {
                assemblyInfoBuilder.AppendLine($"   ... 还有 {assemblyInfo.References.Count - 30} 个引用");
            }

            result.Append(assemblyInfoBuilder);

            // 缓存结果
            var finalResult = result.ToString();

            return finalResult;
        }
        catch (Exception)
        {
            return "分析程序集异常";
        }
    }


    [McpServerTool, Description("获取dotnet程序集中指定类型的所有成员详细信息(方法 属性 字段 事件 以及对应的注释等)")]
    public static async Task<string> GetTypeMembers(
        [Description(PackageIdDescription)] string packageId,
        [Description("程序集文件名")] string assemblyName,
        [Description("完整的类型名称，包括命名空间")] string typeName,
        [Description("包的版本号 (可选)")] string? packageVersion = null,
        [Description("目标框架 (可选)")] string? targetFramework = null,
        [Description("过滤特定成员名称 (可选)，例如：'WriteLine' 只显示名为 WriteLine 的所有重载")] string? memberNameFilterText = null,
        [Description("成员类型过滤：method property field event constructor *, 默认*")] string memberType = "*",
        [Description("是否仅显示公共成员，默认true, false选项显示所有成员")] bool publicOnly = true,
        [Description("是否获取注释, 默认true")] bool comment = true,
        [Description("是否也获取基类的成员, 默认false")] bool includeBaseMembers = false)
    {
        try
        {
            var result = new StringBuilder();

            assemblyName = EnsureAssemblyName(assemblyName);

            var (assemblyInfo, errorMessage) = await TryGetAssemblyAnalysisInfo(packageId, assemblyName, packageVersion, targetFramework);

            if (errorMessage != null)
            {
                return errorMessage;
            }

            result.AppendLine($"程序集: {assemblyInfo!.AssemblyName}");
            result.AppendLine($"类型: {typeName}");
            result.AppendLine();

            // 查找指定的类型
            var allTypes = assemblyInfo.AllTypes;
            var targetType = allTypes.FirstOrDefault(t =>
                t.ToFullName() == typeName ||
                t.ToDisplayString() == typeName ||
                t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) == typeName ||
                t.Name == typeName);

            if (targetType == null)
            {
                result.AppendLine($"错误：未找到类型 '{typeName}'");
                return result.ToString();
            }

            result.AppendLine($"找到类型: {targetType.ToDisplayString()}");
            result.AppendLine($"  类型类别: {targetType.TypeKind}");
            result.AppendLine($"  访问级别: {targetType.DeclaredAccessibility}");

            if (targetType.GetBaseTypes() is { Count: > 0 } baseTypes)
            {
                result.AppendLine($"  基类: {string.Join(" -> ", baseTypes.Select(v => v.ToFullName()))}");
            }
            
            if (targetType.GetInterfaces(includeBaseMembers) is { Count: > 0} interfaces)
            {
                result.AppendLine($"  接口: {string.Join(", ", targetType.GetInterfaces(includeBaseMembers).Select(i => i.ToFullName()))}");
            }

            result.AppendLine();

            // 注释过滤
            if(comment)
                result.AppendLine(targetType.GetCommentText(2));

            result.AppendLine();
            result.AppendLine("成员列表:");
            result.AppendLine();

            // 获取所有成员
            var membersQueryable = targetType.GetMembers(includeBaseMembers).AsEnumerable();

            // public过滤
            if (publicOnly)
            {
                membersQueryable = membersQueryable.Where(m => m.DeclaredAccessibility == Accessibility.Public);
            }

            if (!string.IsNullOrWhiteSpace(memberNameFilterText))
            {
                Regex regex = new Regex(Regex.Escape(memberNameFilterText).Replace(@"\*", ".*"), RegexOptions.IgnoreCase);
                membersQueryable = membersQueryable.Where(m => regex.IsMatch(m.Name));
            }

            var members = membersQueryable.ToList();

            // 按成员类型分组
            var methods = members.OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary).ToList();
            var constructors = members.OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Constructor).ToList();
            var properties = members.OfType<IPropertySymbol>().ToList();
            var fields = members.OfType<IFieldSymbol>().ToList();
            var events = members.OfType<IEventSymbol>().ToList();

            // 根据类型过滤
            bool showMethods = memberType is "*" or "method";
            bool showConstructors = memberType is "*" or "constructor";
            bool showProperties = memberType is "*" or "property";
            bool showFields = memberType is "*" or "field";
            bool showEvents = memberType is "*" or "event";

            int totalShown = 0;

            // 显示构造函数
            if (showConstructors && constructors.Count != 0)
            {
                result.AppendLine($"构造函数 ({constructors.Count}):");

                foreach (var ctor in constructors.OrderBy(c => c.Parameters.Length))
                {
                    var constructorMethodDisplayText = ctor.ToConstructorFullDisplayText();
                    if (comment)
                        result.AppendLine(ctor.GetCommentText(2));
                    result.AppendLine($"    {constructorMethodDisplayText};");

                    totalShown++;
                }
                result.AppendLine();
            }

            // 显示方法
            if (showMethods && methods.Count != 0)
            {
                result.AppendLine($"方法 ({methods.Count}):");

                foreach (var method in methods)
                {
                    var methodDisplayText = method.ToFullDisplay();
                    if (comment)
                        result.AppendLine(method.GetCommentText(2));
                    result.AppendLine($"  {methodDisplayText};");

                    totalShown++;
                }

                result.AppendLine();
            }

            // 显示属性
            if (showProperties && properties.Count != 0)
            {
                result.AppendLine($"属性 ({properties.Count}):");

                foreach (var prop in properties.OrderBy(p => p.Name))
                {
                    var propertyDisplayText = prop.ToFullDisplayText();
                    if (comment)
                        result.AppendLine(prop.GetCommentText(2));
                    result.AppendLine($"  {propertyDisplayText}");

                    totalShown++;
                }

                result.AppendLine();
            }

            // 显示字段
            if (showFields && fields.Count != 0)
            {
                result.AppendLine($"字段 ({fields.Count}):");

                foreach (var field in fields.OrderBy(f => f.Name))
                {
                    var fieldDisplayText = field.ToFullDisplayText();
                    if (comment)
                        result.AppendLine(field.GetCommentText(2));
                    result.AppendLine($"  {fieldDisplayText};");
                    totalShown++;
                }

                result.AppendLine($"字段 ({fields.Count}):");
            }

            // 显示事件
            if (showEvents && events.Count != 0)
            {
                result.AppendLine($"事件 ({events.Count}):");

                foreach (var evt in events.OrderBy(e => e.Name))
                {
                    var eventDisplayText = evt.ToFullDisplayText();
                    if (comment)
                        result.AppendLine(evt.GetCommentText(2));
                    result.AppendLine($"  {eventDisplayText};");

                    totalShown++;
                }

                result.AppendLine();
            }

            if (totalShown == 0)
            {
                result.AppendLine("未找到匹配的成员。");
            }
            else
            {
                result.AppendLine($"一共: {totalShown} 个成员");
            }

            return result.ToString();
        }
        catch (Exception)
        {
            return "获取类型成员异常";
        }
    }


    [McpServerTool, Description("在已解析的dotnet程序集中按模式搜索类型(支持通配符 *), 例如: 'Newtonsoft.*'查找所有以Newtonsoft开头的类型, '*Stream*'查找所有包含Stream的类型, 并获取类型的注释")]
    public static async Task<string> SearchTypes(
        [Description(PackageIdDescription)] string packageId,
        [Description("程序集文件名")] string assemblyName,
        [Description("名称过滤器, 例如'Newtonsoft.Json'匹配类型全名包含'Newtonsoft.Json'的类型, 'Stream'匹配所有包含Stream的类型, 'Stream'相当于'*Stream*', 如果不填或填*则搜索所有类型")] string typeFullNameFilterText = "*",
        [Description("包的版本号（可选）")] string? packageVersion = null,
        [Description("目标框架（可选）")] string? targetFramework = null,
        [Description("类型过滤器：class interface enum struct *, 默认 *")] string typeFilter = "*",
        [Description("是否仅显示公共类型")] bool publicOnly = true,
        [Description("最大返回结果数，默认 50")] int maxResults = 50,
        [Description("是否获取注释, 默认true")] bool comment = true)
    {
        try
        {
            var result = new StringBuilder();

            assemblyName = EnsureAssemblyName(assemblyName);

            var (assemblyInfo, errorMessage) = await TryGetAssemblyAnalysisInfo(packageId, assemblyName, packageVersion, targetFramework);

            if (errorMessage != null)
            {
                return errorMessage;
            }

            result.AppendLine($"程序集: {assemblyInfo!.AssemblyName}");
            result.AppendLine($"搜索模式: {typeFullNameFilterText}");

            // 获取所有类型
            var allTypesQueryable = assemblyInfo.AllTypes.AsEnumerable();

            // public过滤
            if (publicOnly)
            {
                allTypesQueryable = allTypesQueryable.Where(t => t.DeclaredAccessibility == Accessibility.Public);
            }

            // 类型过滤
            if (typeFilter != "*")
            {
                allTypesQueryable = typeFilter.ToLower() switch
                {
                    "class" => allTypesQueryable.Where(t => t.TypeKind == TypeKind.Class && !t.IsStatic),
                    "interface" => allTypesQueryable.Where(t => t.TypeKind == TypeKind.Interface),
                    "enum" => allTypesQueryable.Where(t => t.TypeKind == TypeKind.Enum),
                    "struct" => allTypesQueryable.Where(t => t.TypeKind == TypeKind.Struct),
                    _ => allTypesQueryable
                };
            }

            // 模糊搜索
            if (!string.IsNullOrWhiteSpace(typeFullNameFilterText) && typeFullNameFilterText != "*")
            {
                Regex regex = new Regex(Regex.Escape(typeFullNameFilterText).Replace(@"\*", ".*"), RegexOptions.IgnoreCase);
                allTypesQueryable = allTypesQueryable.Where(v => regex.IsMatch(v.ToFullName()));
            }

            var allTypes = allTypesQueryable.ToList();

            // 限制结果数量
            var totalMatches = allTypes.Count;

            if (allTypes.Count == 0)
            {
                result.AppendLine("未找到匹配的类型");
                return result.ToString();
            }

            result.AppendLine($"找到 {totalMatches} 个匹配的类型{(totalMatches > maxResults ? $"，显示前 {maxResults} 个" : "")}");
            foreach (var type in allTypesQueryable.Take(maxResults).OrderBy(t => t.Name))
            {
                var typeDisplayText = type.ToFullDisplayText();
                if (comment)
                    result.AppendLine(type.GetCommentText(2));
                result.AppendLine($"  {typeDisplayText};");
            }

            result.AppendLine("".PadRight(80, '-'));
            result.AppendLine($"匹配: {totalMatches} 个类型{(totalMatches > maxResults ? $"，已显示: {maxResults} 个" : "")}");

            return result.ToString();
        }
        catch (Exception)
        {
            return "搜索类型异常";
        }
    }


}