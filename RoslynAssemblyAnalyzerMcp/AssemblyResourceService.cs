using ICSharpCode.Decompiler.Metadata;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace RoslynAssemblyAnalyzerMcp;

public sealed class AssemblyResourceService
{
    public AssemblyResourcesResult ListResources(AssemblyAnalysisInfo assemblyInfo)
    {
        using var peFile = AssemblyImageUtilities.OpenPeFile(assemblyInfo.AssemblyPath, assemblyInfo.AssemblyImage);
        var resources = ReadResources(peFile, includeData: false);

        return new AssemblyResourcesResult(
            ToAssemblyIdentityDto(assemblyInfo),
            resources.Count,
            resources.Count(resource => resource.CanExtract),
            [.. resources.Select(ToResourceDto)]);
    }

    public ExtractAssemblyResourcesResult ExtractResources(
        AssemblyAnalysisInfo assemblyInfo,
        string outputDirectory,
        IReadOnlyList<string>? resourceNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (!Path.IsPathFullyQualified(outputDirectory))
        {
            throw new ArgumentException("输出目录必须是绝对路径。", nameof(outputDirectory));
        }

        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);

        using var peFile = AssemblyImageUtilities.OpenPeFile(assemblyInfo.AssemblyPath, assemblyInfo.AssemblyImage);
        var resources = ReadResources(peFile, includeData: true);
        var selectedResourceNames = NormalizeResourceNames(resourceNames);
        var extracted = new List<ExtractedAssemblyResourceDto>();
        var skipped = new List<SkippedAssemblyResourceDto>();
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var resource in resources)
        {
            if (selectedResourceNames.Count != 0 && !selectedResourceNames.Contains(resource.Name))
            {
                continue;
            }

            if (!resource.CanExtract)
            {
                skipped.Add(new SkippedAssemblyResourceDto(resource.Name, $"资源实现不是当前程序集内嵌数据: {resource.Implementation}"));
                continue;
            }

            if (resource.Data is null)
            {
                skipped.Add(new SkippedAssemblyResourceDto(resource.Name, "资源数据为空或无法读取"));
                continue;
            }

            var outputPath = CreateResourceOutputPath(fullOutputDirectory, resource.Name, usedFileNames);
            File.WriteAllBytes(outputPath, resource.Data);
            extracted.Add(new ExtractedAssemblyResourceDto(resource.Name, outputPath, resource.Data.LongLength));
        }

        if (selectedResourceNames.Count != 0)
        {
            var availableNames = resources.Select(resource => resource.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var missingName in selectedResourceNames.Where(name => !availableNames.Contains(name)))
            {
                skipped.Add(new SkippedAssemblyResourceDto(missingName, "未找到指定资源"));
            }
        }

        return new ExtractAssemblyResourcesResult(
            ToAssemblyIdentityDto(assemblyInfo),
            fullOutputDirectory,
            resources.Count,
            extracted.Count,
            extracted,
            skipped);
    }

    private static IReadOnlyList<AssemblyResourceInfo> ReadResources(PEFile peFile, bool includeData)
    {
        var metadataReader = peFile.Metadata;
        var peReader = peFile.Reader;
        var resources = new List<AssemblyResourceInfo>();

        foreach (var handle in metadataReader.ManifestResources)
        {
            var resource = metadataReader.GetManifestResource(handle);
            var name = metadataReader.GetString(resource.Name);
            var implementation = GetImplementation(metadataReader, resource.Implementation);
            var isEmbedded = resource.Implementation.IsNil;
            byte[]? data = null;
            long? size = null;

            if (isEmbedded && TryReadEmbeddedResource(peReader, resource.Offset, includeData, out data, out size))
            {
                resources.Add(new AssemblyResourceInfo(
                    name,
                    GetVisibility(resource.Attributes),
                    implementation,
                    size,
                    CanExtract: true,
                    data));
                continue;
            }

            resources.Add(new AssemblyResourceInfo(
                name,
                GetVisibility(resource.Attributes),
                implementation,
                size,
                CanExtract: false,
                data));
        }

        return resources;
    }

    private static bool TryReadEmbeddedResource(
        PEReader peReader,
        long offset,
        bool includeData,
        out byte[]? data,
        out long? size)
    {
        data = null;
        size = null;

        if (peReader.PEHeaders.CorHeader is not { } corHeader)
        {
            return false;
        }

        var resourcesDirectory = corHeader.ResourcesDirectory;
        if (resourcesDirectory.RelativeVirtualAddress == 0)
        {
            return false;
        }

        var resourcesBlock = peReader.GetSectionData(resourcesDirectory.RelativeVirtualAddress);
        var resourceLengthOffset = checked((int)offset);
        var resourceBlock = resourcesBlock.GetReader(resourceLengthOffset, sizeof(int));
        var resourceLength = resourceBlock.ReadInt32();
        if (resourceLength < 0)
        {
            return false;
        }

        size = resourceLength;
        if (!includeData)
        {
            return true;
        }

        var resourceDataOffset = checked(resourceLengthOffset + sizeof(int));
        var dataReader = resourcesBlock.GetReader(resourceDataOffset, resourceLength);
        data = dataReader.ReadBytes(resourceLength);
        return true;
    }

    private static string GetImplementation(MetadataReader metadataReader, EntityHandle implementation)
    {
        if (implementation.IsNil)
        {
            return "current-assembly";
        }

        return implementation.Kind switch
        {
            HandleKind.AssemblyFile => $"file:{metadataReader.GetString(metadataReader.GetAssemblyFile((AssemblyFileHandle)implementation).Name)}",
            HandleKind.AssemblyReference => $"assembly:{metadataReader.GetString(metadataReader.GetAssemblyReference((AssemblyReferenceHandle)implementation).Name)}",
            HandleKind.ExportedType => $"exported-type:{metadataReader.GetString(metadataReader.GetExportedType((ExportedTypeHandle)implementation).Name)}",
            HandleKind.ModuleReference => $"module:{metadataReader.GetString(metadataReader.GetModuleReference((ModuleReferenceHandle)implementation).Name)}",
            HandleKind.NamespaceDefinition => "current-assembly",
            HandleKind.TypeReference => "type-reference",
            HandleKind.TypeDefinition => "type-definition",
            HandleKind.TypeSpecification => "type-specification",
            HandleKind.ManifestResource => "manifest-resource",
            HandleKind.CustomAttribute => "custom-attribute",
            HandleKind.MemberReference => "member-reference",
            HandleKind.MethodDefinition => "method-definition",
            HandleKind.FieldDefinition => "field-definition",
            HandleKind.PropertyDefinition => "property-definition",
            HandleKind.EventDefinition => "event-definition",
            HandleKind.Parameter => "parameter",
            HandleKind.GenericParameter => "generic-parameter",
            HandleKind.GenericParameterConstraint => "generic-parameter-constraint",
            HandleKind.MethodSpecification => "method-specification",
            HandleKind.AssemblyDefinition => "assembly-definition",
            HandleKind.ModuleDefinition => "module-definition",
            HandleKind.StandaloneSignature => "standalone-signature",
            HandleKind.MethodImplementation => "method-implementation",
            HandleKind.MethodDebugInformation => "method-debug-information",
            HandleKind.Document => "document",
            HandleKind.LocalScope => "local-scope",
            HandleKind.LocalVariable => "local-variable",
            HandleKind.LocalConstant => "local-constant",
            HandleKind.ImportScope => "import-scope",
            HandleKind.CustomDebugInformation => "custom-debug-information",
            HandleKind.DeclarativeSecurityAttribute => "declarative-security-attribute",
            HandleKind.String => "string",
            HandleKind.Blob => "blob",
            HandleKind.Guid => "guid",
            HandleKind.UserString => "user-string",
            _ => implementation.Kind.ToString()
        };
    }

    private static string GetVisibility(ManifestResourceAttributes attributes)
    {
        return attributes.HasFlag(ManifestResourceAttributes.Public)
            ? "Public"
            : "Private";
    }

    private static IReadOnlySet<string> NormalizeResourceNames(IReadOnlyList<string>? resourceNames)
    {
        return resourceNames is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : resourceNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .ToHashSet(StringComparer.Ordinal);
    }

    private static string CreateResourceOutputPath(
        string outputDirectory,
        string resourceName,
        HashSet<string> usedFileNames)
    {
        var fileName = SanitizeFileName(resourceName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "resource";
        }

        var uniqueFileName = fileName;
        for (var i = 2; !usedFileNames.Add(uniqueFileName); i++)
        {
            uniqueFileName = string.IsNullOrWhiteSpace(extension)
                ? $"{baseName}_{i}"
                : $"{baseName}_{i}{extension}";
        }

        var outputPath = Path.GetFullPath(Path.Combine(outputDirectory, uniqueFileName));
        if (!IsPathInDirectory(outputPath, outputDirectory))
        {
            throw new InvalidOperationException($"资源输出路径越界: {outputPath}");
        }

        return outputPath;
    }

    private static string SanitizeFileName(string resourceName)
    {
        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
        StringBuilder builder = new(resourceName.Length);
        foreach (var ch in resourceName)
        {
            builder.Append(invalidChars.Contains(ch) || ch is '/' or '\\' or ':' ? '_' : ch);
        }

        var fileName = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(fileName) ? "resource" : fileName;
    }

    private static bool IsPathInDirectory(string path, string directory)
    {
        var normalizedDirectory = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static AssemblyResourceDto ToResourceDto(AssemblyResourceInfo resource)
    {
        return new AssemblyResourceDto(
            resource.Name,
            resource.Visibility,
            resource.Implementation,
            resource.Size,
            resource.CanExtract);
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

    private sealed record AssemblyResourceInfo(
        string Name,
        string Visibility,
        string Implementation,
        long? Size,
        bool CanExtract,
        byte[]? Data);
}
