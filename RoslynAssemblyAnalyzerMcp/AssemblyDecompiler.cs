using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;
using System.Reflection.Metadata;

namespace RoslynAssemblyAnalyzerMcp;

public sealed class AssemblyDecompiler
{
    private const int DefaultMaxTypes = 32;
    private const int DefaultMaxCodeLengthPerItem = 200_000;

    public DecompileResult Decompile(
        AssemblyAnalysisInfo? assemblyInfo,
        string assemblyPath,
        IReadOnlyList<string>? typeNames,
        IReadOnlyList<string>? methodNames,
        string language,
        bool? includeXmlDocumentation,
        bool? includeMemberBodies,
        bool? useDebugSymbols,
        bool? useLambdaSyntax,
        bool? anonymousMethods,
        bool? anonymousTypes,
        bool? alwaysQualifyMemberReferences,
        bool? alwaysUseBraces,
        bool? alwaysShowEnumMemberValues,
        bool? useImplicitMethodGroupConversion,
        bool includeAssemblyAttributes,
        bool includeModuleAttributes,
        bool detectControlStructure,
        bool showSequencePoints,
        bool showMetadataTokens,
        bool showMetadataTokensInBase10,
        bool showRawRvaAndBytes,
        bool expandMemberDefinitions,
        bool decodeCustomAttributeBlobs,
        int maxTypes,
        int maxCodeLengthPerItem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        var fullPath = Path.GetFullPath(assemblyPath);
        var assemblyImage = assemblyInfo?.AssemblyImage;
        var normalizedLanguage = NormalizeLanguage(language);
        var options = BuildOptions(
            normalizedLanguage,
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
            maxCodeLengthPerItem);

        var requestedTypes = NormalizeNames(typeNames);
        var requestedMethods = NormalizeNames(methodNames);
        maxTypes = maxTypes <= 0 ? DefaultMaxTypes : maxTypes;
        var failures = new List<DecompileFailureDto>();
        var notes = new List<string>();

        if (requestedTypes.Count > maxTypes)
        {
            notes.Add($"请求的类型数量超过 maxTypes={maxTypes}, 只处理前 {maxTypes} 个类型。");
            requestedTypes = [.. requestedTypes.Take(maxTypes)];
        }

        var items = normalizedLanguage switch
        {
            "C#" => DecompileCSharp(fullPath, assemblyImage, assemblyInfo, requestedTypes, requestedMethods, options, failures),
            "IL" => DecompileIL(fullPath, assemblyImage, assemblyInfo, requestedTypes, requestedMethods, options, failures),
            _ => throw new InvalidOperationException($"不支持的反编译语言: {normalizedLanguage}")
        };

        string? attributeCode = null;
        if (normalizedLanguage == "C#" && (includeAssemblyAttributes || includeModuleAttributes))
        {
            attributeCode = DecompileModuleAndAssemblyAttributes(fullPath, assemblyImage, options);
        }

        var truncatedItems = items.Where(item => item.Truncated).ToList();
        if (truncatedItems.Count != 0)
        {
            notes.Add($"有 {truncatedItems.Count} 个输出超过 maxCodeLengthPerItem 并已截断。");
        }

        return new DecompileResult(
            assemblyInfo is null ? null : ToAssemblyIdentityDto(assemblyInfo),
            fullPath,
            normalizedLanguage,
            requestedTypes.Count == 0 && requestedMethods.Count == 0,
            requestedTypes,
            requestedMethods,
            items,
            failures,
            attributeCode,
            options,
            items.Sum(item => item.OriginalCodeLength),
            truncatedItems.Count != 0,
            notes);
    }

    private static IReadOnlyList<DecompileItemDto> DecompileCSharp(
        string assemblyPath,
        byte[]? assemblyImage,
        AssemblyAnalysisInfo? assemblyInfo,
        IReadOnlyList<string> requestedTypes,
        IReadOnlyList<string> requestedMethods,
        DecompileOptionsDto options,
        List<DecompileFailureDto> failures)
    {
        using var peFile = AssemblyImageUtilities.OpenPeFile(assemblyPath, assemblyImage);
        var decompiler = CreateCSharpDecompiler(assemblyPath, options, peFile);

        if (requestedTypes.Count == 0 && requestedMethods.Count == 0)
        {
            return [CreateItem("assembly", null, null, null, null, options.Language, decompiler.DecompileWholeModuleAsString(), options.MaxCodeLengthPerItem)];
        }

        var items = new List<DecompileItemDto>();
        if (requestedMethods.Count == 0)
        {
            foreach (var requestedType in requestedTypes)
            {
                var match = FindType(assemblyInfo, requestedType);
                var metadataName = match?.ToMetadataFullName() ?? requestedType;
                try
                {
                    var code = decompiler.DecompileTypeAsString(new FullTypeName(metadataName));
                    items.Add(CreateItem("type", requestedType, null, match?.ToFullName() ?? metadataName, null, options.Language, code, options.MaxCodeLengthPerItem));
                }
                catch (Exception ex)
                {
                    failures.Add(new DecompileFailureDto(requestedType, ex.Message));
                }
            }
        }

        items.AddRange(DecompileCSharpMethods(
            assemblyInfo,
            requestedTypes,
            requestedMethods,
            options,
            peFile,
            decompiler,
            failures));

        return items;
    }

    private static IReadOnlyList<DecompileItemDto> DecompileIL(
        string assemblyPath,
        byte[]? assemblyImage,
        AssemblyAnalysisInfo? assemblyInfo,
        IReadOnlyList<string> requestedTypes,
        IReadOnlyList<string> requestedMethods,
        DecompileOptionsDto options,
        List<DecompileFailureDto> failures)
    {
        using var peFile = AssemblyImageUtilities.OpenPeFile(assemblyPath, assemblyImage);
        var output = new PlainTextOutput();
        var disassembler = CreateIlDisassembler(assemblyPath, output, options);

        if (requestedTypes.Count == 0 && requestedMethods.Count == 0)
        {
            disassembler.WriteAssemblyHeader(peFile);
            disassembler.WriteAssemblyReferences(peFile.Metadata);
            disassembler.WriteModuleHeader(peFile, skipMVID: false);
            disassembler.WriteModuleContents(peFile);
            return [CreateItem("assembly", null, null, null, null, options.Language, output.ToString(), options.MaxCodeLengthPerItem)];
        }

        var items = new List<DecompileItemDto>();
        if (requestedMethods.Count == 0)
        {
            foreach (var requestedType in requestedTypes)
            {
                output = new PlainTextOutput();
                disassembler = CreateIlDisassembler(assemblyPath, output, options);
                var match = FindType(assemblyInfo, requestedType);
                var metadataName = match?.ToMetadataFullName() ?? requestedType;
                if (!MetadataMethodUtilities.TryGetTypeDefinitionHandle(peFile.Metadata, metadataName, out var handle))
                {
                    failures.Add(new DecompileFailureDto(requestedType, $"未找到类型: {requestedType}"));
                    continue;
                }

                try
                {
                    disassembler.DisassembleType(peFile, handle);
                    items.Add(CreateItem("type", requestedType, null, match?.ToFullName() ?? metadataName, null, options.Language, output.ToString(), options.MaxCodeLengthPerItem));
                }
                catch (Exception ex)
                {
                    failures.Add(new DecompileFailureDto(requestedType, ex.Message));
                }
            }
        }

        items.AddRange(DecompileILMethods(
            assemblyPath,
            assemblyInfo,
            requestedTypes,
            requestedMethods,
            options,
            peFile,
            failures));

        return items;
    }

    private static IReadOnlyList<DecompileItemDto> DecompileCSharpMethods(
        AssemblyAnalysisInfo? assemblyInfo,
        IReadOnlyList<string> requestedTypes,
        IReadOnlyList<string> requestedMethods,
        DecompileOptionsDto options,
        PEFile peFile,
        CSharpDecompiler decompiler,
        List<DecompileFailureDto> failures)
    {
        if (requestedMethods.Count == 0)
        {
            return [];
        }

        var methodFailures = new List<string>();
        var candidates = MetadataMethodUtilities.FindMethods(
            peFile.Metadata,
            assemblyInfo,
            requestedTypes,
            requestedMethods,
            memberType: "*",
            publicOnly: false,
            includeCompilerGenerated: true,
            includeAttributes: false,
            maxResults: int.MaxValue,
            exactSimpleMethodName: true,
            methodFailures);
        failures.AddRange(methodFailures.Select(failure => new DecompileFailureDto(null, failure)));
        var items = new List<DecompileItemDto>();

        foreach (var candidate in candidates)
        {
            try
            {
                var code = decompiler.DecompileAsString([candidate.Handle]);
                items.Add(CreateItem(
                    "method",
                    candidate.RequestedTypeName,
                    candidate.RequestedMethodName,
                    candidate.MatchedTypeName,
                    candidate.Signature,
                    options.Language,
                    code,
                    options.MaxCodeLengthPerItem));
            }
            catch (Exception ex)
            {
                failures.Add(new DecompileFailureDto(candidate.RequestedMethodName, ex.Message));
            }
        }

        return items;
    }

    private static IReadOnlyList<DecompileItemDto> DecompileILMethods(
        string assemblyPath,
        AssemblyAnalysisInfo? assemblyInfo,
        IReadOnlyList<string> requestedTypes,
        IReadOnlyList<string> requestedMethods,
        DecompileOptionsDto options,
        PEFile peFile,
        List<DecompileFailureDto> failures)
    {
        if (requestedMethods.Count == 0)
        {
            return [];
        }

        var methodFailures = new List<string>();
        var candidates = MetadataMethodUtilities.FindMethods(
            peFile.Metadata,
            assemblyInfo,
            requestedTypes,
            requestedMethods,
            memberType: "*",
            publicOnly: false,
            includeCompilerGenerated: true,
            includeAttributes: false,
            maxResults: int.MaxValue,
            exactSimpleMethodName: true,
            methodFailures);
        failures.AddRange(methodFailures.Select(failure => new DecompileFailureDto(null, failure)));
        var items = new List<DecompileItemDto>();

        foreach (var candidate in candidates)
        {
            try
            {
                var output = new PlainTextOutput();
                var disassembler = CreateIlDisassembler(assemblyPath, output, options);
                disassembler.DisassembleMethod(peFile, candidate.Handle);
                items.Add(CreateItem(
                    "method",
                    candidate.RequestedTypeName,
                    candidate.RequestedMethodName,
                    candidate.MatchedTypeName,
                    candidate.Signature,
                    options.Language,
                    output.ToString(),
                    options.MaxCodeLengthPerItem));
            }
            catch (Exception ex)
            {
                failures.Add(new DecompileFailureDto(candidate.RequestedMethodName, ex.Message));
            }
        }

        return items;
    }

    private static string DecompileModuleAndAssemblyAttributes(
        string assemblyPath,
        byte[]? assemblyImage,
        DecompileOptionsDto options)
    {
        using var peFile = AssemblyImageUtilities.OpenPeFile(assemblyPath, assemblyImage);
        return CreateCSharpDecompiler(assemblyPath, options, peFile).DecompileModuleAndAssemblyAttributesToString();
    }

    private static CSharpDecompiler CreateCSharpDecompiler(
        string assemblyPath,
        DecompileOptionsDto options,
        MetadataFile metadataFile)
    {
        var settings = new DecompilerSettings
        {
            ThrowOnAssemblyResolveErrors = false,
            DecompileMemberBodies = options.IncludeMemberBodies,
            ShowXmlDocumentation = options.IncludeXmlDocumentation,
            UseDebugSymbols = options.UseDebugSymbols,
            UseLambdaSyntax = options.UseLambdaSyntax,
            AnonymousMethods = options.AnonymousMethods,
            AnonymousTypes = options.AnonymousTypes,
            AlwaysQualifyMemberReferences = options.AlwaysQualifyMemberReferences,
            AlwaysUseBraces = options.AlwaysUseBraces,
            AlwaysShowEnumMemberValues = options.AlwaysShowEnumMemberValues,
            UseImplicitMethodGroupConversion = options.UseImplicitMethodGroupConversion
        };

        var resolver = new UniversalAssemblyResolver(assemblyPath, throwOnError: false, targetFramework: null);
        return new CSharpDecompiler(metadataFile, resolver, settings);
    }

    private static ReflectionDisassembler CreateIlDisassembler(
        string assemblyPath,
        PlainTextOutput output,
        DecompileOptionsDto options)
    {
        return new ReflectionDisassembler(output, CancellationToken.None)
        {
            AssemblyResolver = new UniversalAssemblyResolver(assemblyPath, throwOnError: false, targetFramework: null),
            DetectControlStructure = options.DetectControlStructure,
            ShowSequencePoints = options.ShowSequencePoints,
            ShowMetadataTokens = options.ShowMetadataTokens,
            ShowMetadataTokensInBase10 = options.ShowMetadataTokensInBase10,
            ShowRawRVAOffsetAndBytes = options.ShowRawRvaAndBytes,
            ExpandMemberDefinitions = options.ExpandMemberDefinitions,
            DecodeCustomAttributeBlobs = options.DecodeCustomAttributeBlobs
        };
    }

    private static DecompileOptionsDto BuildOptions(
        string language,
        bool? includeXmlDocumentation,
        bool? includeMemberBodies,
        bool? useDebugSymbols,
        bool? useLambdaSyntax,
        bool? anonymousMethods,
        bool? anonymousTypes,
        bool? alwaysQualifyMemberReferences,
        bool? alwaysUseBraces,
        bool? alwaysShowEnumMemberValues,
        bool? useImplicitMethodGroupConversion,
        bool includeAssemblyAttributes,
        bool includeModuleAttributes,
        bool detectControlStructure,
        bool showSequencePoints,
        bool showMetadataTokens,
        bool showMetadataTokensInBase10,
        bool showRawRvaAndBytes,
        bool expandMemberDefinitions,
        bool decodeCustomAttributeBlobs,
        int maxCodeLengthPerItem)
    {
        return new DecompileOptionsDto(
            language,
            includeXmlDocumentation ?? true,
            includeMemberBodies ?? true,
            useDebugSymbols ?? true,
            useLambdaSyntax ?? false,
            anonymousMethods ?? true,
            anonymousTypes ?? false,
            alwaysQualifyMemberReferences ?? true,
            alwaysUseBraces ?? true,
            alwaysShowEnumMemberValues ?? true,
            useImplicitMethodGroupConversion ?? false,
            includeAssemblyAttributes,
            includeModuleAttributes,
            detectControlStructure,
            showSequencePoints,
            showMetadataTokens,
            showMetadataTokensInBase10,
            showRawRvaAndBytes,
            expandMemberDefinitions,
            decodeCustomAttributeBlobs,
            maxCodeLengthPerItem <= 0 ? DefaultMaxCodeLengthPerItem : maxCodeLengthPerItem);
    }

    private static string NormalizeLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "C#";
        }

        return language.Trim().ToLowerInvariant() switch
        {
            "c#" or "csharp" or "cs" => "C#",
            "il" or "msil" => "IL",
            _ => throw new ArgumentException("language 只支持 csharp/c# 或 il/msil。", nameof(language))
        };
    }

    private static IReadOnlyList<string> NormalizeNames(IReadOnlyList<string>? names)
    {
        return names is null
            ? []
            : [.. names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static INamedTypeSymbol? FindType(AssemblyAnalysisInfo? assemblyInfo, string typeName)
    {
        return assemblyInfo?.AllTypes.FirstOrDefault(t =>
            t.ToFullName().Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
            t.ToMetadataFullName().Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
            t.ToDisplayString().Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
            t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
            t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
    }

    private static DecompileItemDto CreateItem(
        string kind,
        string? requestedTypeName,
        string? requestedMethodName,
        string? matchedTypeName,
        string? matchedMethodSignature,
        string language,
        string code,
        int maxCodeLength)
    {
        var originalLength = code.Length;
        var truncated = maxCodeLength > 0 && originalLength > maxCodeLength;
        var returnedCode = truncated ? code[..maxCodeLength] : code;
        return new DecompileItemDto(
            kind,
            requestedTypeName,
            requestedMethodName,
            matchedTypeName,
            matchedMethodSignature,
            language,
            returnedCode,
            originalLength,
            returnedCode.Length,
            truncated);
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

}
