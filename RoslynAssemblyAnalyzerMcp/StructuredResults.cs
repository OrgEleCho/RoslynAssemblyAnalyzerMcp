namespace RoslynAssemblyAnalyzerMcp;

public sealed record ToolResponse<T>
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public T? Data { get; init; }

    public static ToolResponse<T> Ok(T data) => new()
    {
        Success = true,
        Data = data
    };

    public static ToolResponse<T> Fail(string error) => new()
    {
        Success = false,
        Error = error
    };
}

public sealed record NuGetPackageSearchResult(int Count, IReadOnlyList<NuGetPackageSearchItem> Packages);

public sealed record NuGetPackageSearchItem(
    string PackageId,
    string Version,
    string? Description,
    long? DownloadCount,
    string? Tags);

public sealed record NuGetPackageDetailsResult(
    string PackageId,
    string LatestVersion,
    string? Authors,
    string? Description,
    string? ProjectUrl,
    string? LicenseUrl,
    long? DownloadCount,
    DateTimeOffset? Published,
    string? Tags,
    IReadOnlyList<PackageDependencyGroupDto> DependencyGroups,
    IReadOnlyList<PackageVersionDto> Versions,
    int TotalVersionCount,
    bool VersionsTruncated);

public sealed record PackageDependencyGroupDto(
    string TargetFramework,
    IReadOnlyList<PackageDependencyDto> Dependencies);

public sealed record PackageDependencyDto(string Id, string VersionRange);

public sealed record PackageVersionDto(string Version, DateTimeOffset? Published);

public sealed record PackageAssembliesResult(
    string PackageId,
    string PackageVersion,
    PackageAssemblyGroupKind AssemblyGroupKind,
    IReadOnlyList<string> Notes,
    IReadOnlyList<PackageTargetFrameworkAssembliesDto> TargetFrameworks);

public sealed record PackageTargetFrameworkAssembliesDto(
    string TargetFramework,
    IReadOnlyList<AssemblyFileDto> Assemblies);

public sealed record AssemblyFileDto(string Name, string RelativePath, string FullPath);

public sealed record AnalyzeAllAssembliesResult(
    string PackageId,
    string PackageVersion,
    string? RequestedTargetFramework,
    PackageAssemblyGroupKind AssemblyGroupKind,
    IReadOnlyList<string> Notes,
    IReadOnlyList<AssemblyAnalysisDto> Assemblies,
    IReadOnlyList<SkippedAssemblyDto> SkippedAssemblies,
    IReadOnlyList<string> AvailableTargetFrameworks);

public sealed record SkippedAssemblyDto(
    string AssemblyName,
    string? TargetFramework,
    string Reason);

public sealed record AssemblyAnalysisDto(
    AssemblySourceKind SourceKind,
    string? PackageId,
    string? PackageVersion,
    string? TargetFramework,
    string AssemblyName,
    string AssemblyVersion,
    string AssemblyPath,
    bool IsRefAssembly,
    TypeStatisticsDto TypeStatistics,
    IReadOnlyList<NamespaceInfoDto> Namespaces,
    int TotalNamespaceCount,
    bool NamespacesTruncated,
    IReadOnlyList<string> References,
    int TotalReferenceCount,
    bool ReferencesTruncated);

public sealed record AssemblyMetadataResult(
    AssemblyIdentityDto Assembly,
    string AssemblyFullName,
    string AssemblyVersion,
    string? TargetFramework,
    string? InformationalVersion,
    bool IsReferenceAssembly,
    IReadOnlyList<AttributeDto> AssemblyAttributes,
    IReadOnlyList<ModuleMetadataDto> Modules,
    IReadOnlyList<AssemblyReferenceDto> References);

public sealed record ModuleMetadataDto(
    string Name,
    IReadOnlyList<AttributeDto> Attributes);

public sealed record AssemblyReferenceDto(
    string Name,
    string Version,
    string? CultureName,
    string? PublicKeyToken);

public sealed record AssemblyResourcesResult(
    AssemblyIdentityDto Assembly,
    int TotalResources,
    int EmbeddedResourceCount,
    IReadOnlyList<AssemblyResourceDto> Resources);

public sealed record AssemblyResourceDto(
    string Name,
    string Visibility,
    string Implementation,
    long? Size,
    bool CanExtract);

public sealed record ExtractAssemblyResourcesResult(
    AssemblyIdentityDto Assembly,
    string OutputDirectory,
    int TotalResources,
    int ExtractedCount,
    IReadOnlyList<ExtractedAssemblyResourceDto> ExtractedResources,
    IReadOnlyList<SkippedAssemblyResourceDto> SkippedResources);

public sealed record ExtractedAssemblyResourceDto(
    string Name,
    string OutputPath,
    long Size);

public sealed record SkippedAssemblyResourceDto(
    string Name,
    string Reason);

public sealed record TypeStatisticsDto(
    int TotalTypes,
    int PublicTypes,
    int PublicClasses,
    int PublicStaticClasses,
    int PublicInterfaces,
    int PublicEnums,
    int PublicStructs,
    int PublicDelegates);

public sealed record NamespaceInfoDto(string Name, int TypeCount);

public sealed record TypeSearchResult(
    AssemblyIdentityDto Assembly,
    string Query,
    string TypeFilter,
    bool PublicOnly,
    int TotalMatches,
    int ReturnedCount,
    IReadOnlyList<TypeDto> Types);

public sealed record AssemblyIdentityDto(
    AssemblySourceKind SourceKind,
    string? PackageId,
    string? PackageVersion,
    string? TargetFramework,
    string AssemblyName,
    string AssemblyPath);

public sealed record TypeDto(
    string Name,
    string FullName,
    string MetadataName,
    string Namespace,
    string Kind,
    string Accessibility,
    bool IsStatic,
    string DisplayText,
    string? Documentation,
    IReadOnlyList<AttributeDto> Attributes);

public sealed record TypeMembersResult(
    AssemblyIdentityDto Assembly,
    string RequestedTypeName,
    TypeDto? Type,
    IReadOnlyList<string> BaseTypes,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<MemberGroupDto> MemberGroups,
    int TotalShown);

public sealed record MethodSearchResult(
    AssemblyIdentityDto Assembly,
    string RequestedTypeName,
    string? MemberTypeFilter,
    string? MemberNameFilter,
    bool PublicOnly,
    bool IncludeBaseMembers,
    bool IncludeAttributes,
    int TotalMatches,
    IReadOnlyList<MemberDto> Methods);

public sealed record TypeMembersBatchResult(
    AssemblyIdentityDto Assembly,
    IReadOnlyList<TypeMembersResult> Types,
    IReadOnlyList<TypeLookupFailureDto> Failures,
    int TotalShown);

public sealed record TypeLookupFailureDto(
    string TypeName,
    string Reason);

public sealed record MemberGroupDto(
    string Kind,
    int TotalCount,
    IReadOnlyList<MemberDto> Members);

public sealed record MemberDto(
    string Name,
    string? DeclaringTypeName,
    bool IsInherited,
    string Kind,
    string Accessibility,
    string Signature,
    bool IsStatic,
    string? Documentation,
    string? ReturnType,
    IReadOnlyList<MemberParameterDto> Parameters,
    IReadOnlyList<AttributeDto> Attributes);

public sealed record MemberParameterDto(
    string Name,
    string Type,
    string RefKind,
    bool IsOptional,
    string? DefaultValue);

public sealed record AttributeDto(
    string AttributeClass,
    string DisplayText,
    IReadOnlyList<AttributeArgumentDto> ConstructorArguments,
    IReadOnlyList<NamedAttributeArgumentDto> NamedArguments);

public sealed record AttributeArgumentDto(
    string Kind,
    string Type,
    string? Value,
    IReadOnlyList<string?> Values);

public sealed record NamedAttributeArgumentDto(
    string Name,
    AttributeArgumentDto Value);

public sealed record DecompileResult(
    AssemblyIdentityDto? Assembly,
    string AssemblyPath,
    string Language,
    bool WholeAssembly,
    IReadOnlyList<string> RequestedTypeNames,
    IReadOnlyList<string> RequestedMethodNames,
    IReadOnlyList<DecompileItemDto> Items,
    IReadOnlyList<DecompileFailureDto> Failures,
    string? AttributeCode,
    DecompileOptionsDto Options,
    int TotalCodeLength,
    bool AnyTruncated,
    IReadOnlyList<string> Notes);

public sealed record DecompileItemDto(
    string Kind,
    string? RequestedTypeName,
    string? RequestedMethodName,
    string? MatchedTypeName,
    string? MatchedMethodSignature,
    string Language,
    string Code,
    int OriginalCodeLength,
    int ReturnedCodeLength,
    bool Truncated);

public sealed record DecompileFailureDto(
    string? TypeName,
    string Reason);

public sealed record DecompileOptionsDto(
    string Language,
    bool IncludeXmlDocumentation,
    bool IncludeMemberBodies,
    bool UseDebugSymbols,
    bool UseLambdaSyntax,
    bool AnonymousMethods,
    bool AnonymousTypes,
    bool AlwaysQualifyMemberReferences,
    bool AlwaysUseBraces,
    bool AlwaysShowEnumMemberValues,
    bool UseImplicitMethodGroupConversion,
    bool IncludeAssemblyAttributes,
    bool IncludeModuleAttributes,
    bool DetectControlStructure,
    bool ShowSequencePoints,
    bool ShowMetadataTokens,
    bool ShowMetadataTokensInBase10,
    bool ShowRawRvaAndBytes,
    bool ExpandMemberDefinitions,
    bool DecodeCustomAttributeBlobs,
    int MaxCodeLengthPerItem);
