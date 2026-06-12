using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;

namespace RoslynAssemblyAnalyzerMcp;

internal sealed record MetadataMethodInfo(
    string RequestedTypeName,
    string RequestedMethodName,
    string MatchedTypeName,
    string DeclaringTypeName,
    bool IsInherited,
    string MethodName,
    string Kind,
    string Accessibility,
    string Signature,
    bool IsStatic,
    string? ReturnType,
    IReadOnlyList<MemberParameterDto> Parameters,
    IReadOnlyList<AttributeDto> Attributes,
    MethodDefinitionHandle Handle);

internal static class MetadataMethodUtilities
{
    public static IReadOnlyList<MetadataMethodInfo> FindMethods(
        MetadataReader reader,
        AssemblyAnalysisInfo? assemblyInfo,
        IReadOnlyList<string> typeNames,
        IReadOnlyList<string> methodNames,
        string memberType,
        bool publicOnly,
        bool includeCompilerGenerated,
        bool includeAttributes,
        int maxResults,
        bool exactSimpleMethodName,
        ICollection<string>? failures = null,
        bool includeBaseTypes = false)
    {
        var typeScopes = ResolveTypeScopes(reader, assemblyInfo, typeNames, includeBaseTypes, failures);
        if (typeScopes.Count == 0)
        {
            return [];
        }

        maxResults = maxResults <= 0 ? int.MaxValue : maxResults;
        var normalizedMemberType = string.IsNullOrWhiteSpace(memberType) ? "*" : memberType.Trim().ToLowerInvariant();
        var results = new List<MetadataMethodInfo>();

        foreach (var typeScope in typeScopes)
        {
            var typeDefinition = reader.GetTypeDefinition(typeScope.Handle);
            foreach (var methodHandle in typeDefinition.GetMethods())
            {
                var methodDefinition = reader.GetMethodDefinition(methodHandle);
                var methodName = reader.GetString(methodDefinition.Name);
                var declaringTypeName = GetMetadataFullName(reader, reader.GetTypeDefinition(methodDefinition.GetDeclaringType())).Replace('+', '.');
                var kind = GetMethodKind(methodName);
                if (!MatchesMemberType(kind, normalizedMemberType))
                {
                    continue;
                }

                var accessibility = GetAccessibility(methodDefinition.Attributes);
                if (publicOnly && accessibility != "Public")
                {
                    continue;
                }

                if (!includeCompilerGenerated && IsCompilerGenerated(reader, methodDefinition, methodName))
                {
                    continue;
                }

                var signatureInfo = DecodeMethodSignature(reader, methodDefinition, methodName, accessibility);
                if (!MatchesRequestedMethods(typeScope.MatchedTypeName, methodName, signatureInfo.Signature, methodNames, exactSimpleMethodName, out var requestedMethodName))
                {
                    continue;
                }

                results.Add(new MetadataMethodInfo(
                    typeScope.RequestedTypeName,
                    requestedMethodName,
                    typeScope.MatchedTypeName,
                    declaringTypeName,
                    !declaringTypeName.Equals(typeScope.RequestedMatchedTypeName, StringComparison.OrdinalIgnoreCase),
                    methodName,
                    kind,
                    accessibility,
                    signatureInfo.Signature,
                    methodDefinition.Attributes.HasFlag(MethodAttributes.Static),
                    signatureInfo.ReturnType,
                    signatureInfo.Parameters,
                    includeAttributes ? GetAttributes(reader, methodDefinition.GetCustomAttributes()) : [],
                    methodHandle));

                if (results.Count >= maxResults)
                {
                    return results;
                }
            }
        }

        return results;
    }

    public static bool TryGetTypeDefinitionHandle(MetadataReader reader, string metadataFullName, out TypeDefinitionHandle handle)
    {
        foreach (var candidate in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(candidate);
            var fullName = GetMetadataFullName(reader, definition);
            if (fullName.Equals(metadataFullName, StringComparison.Ordinal)
                || fullName.Equals(metadataFullName.Replace('.', '+'), StringComparison.Ordinal)
                || fullName.Equals(metadataFullName.Replace('+', '.'), StringComparison.Ordinal))
            {
                handle = candidate;
                return true;
            }
        }

        handle = default;
        return false;
    }

    public static string GetMetadataFullName(MetadataReader reader, TypeDefinition definition)
    {
        var name = reader.GetString(definition.Name);
        if (!definition.GetDeclaringType().IsNil)
        {
            return $"{GetMetadataFullName(reader, reader.GetTypeDefinition(definition.GetDeclaringType()))}+{name}";
        }

        var ns = reader.GetString(definition.Namespace);
        return string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
    }

    private static IReadOnlyList<TypeScope> ResolveTypeScopes(
        MetadataReader reader,
        AssemblyAnalysisInfo? assemblyInfo,
        IReadOnlyList<string> typeNames,
        bool includeBaseTypes,
        ICollection<string>? failures)
    {
        if (typeNames.Count == 0)
        {
            return [.. reader.TypeDefinitions
                .Select(handle => new TypeScope(
                    RequestedTypeName: GetMetadataFullName(reader, reader.GetTypeDefinition(handle)).Replace('+', '.'),
                    RequestedMatchedTypeName: GetMetadataFullName(reader, reader.GetTypeDefinition(handle)).Replace('+', '.'),
                    MatchedTypeName: GetMetadataFullName(reader, reader.GetTypeDefinition(handle)).Replace('+', '.'),
                    Handle: handle))
                .Where(scope => !scope.MatchedTypeName.EndsWith(".<Module>", StringComparison.Ordinal) && scope.MatchedTypeName != "<Module>")];
        }

        var scopes = new List<TypeScope>();
        var seenHandles = new HashSet<TypeDefinitionHandle>();
        foreach (var typeName in typeNames)
        {
            var matchedSymbol = FindType(assemblyInfo, typeName);
            var metadataName = matchedSymbol?.ToMetadataFullName() ?? typeName;
            if (!TryGetTypeDefinitionHandle(reader, metadataName, out var handle))
            {
                failures?.Add($"未找到类型: {typeName}");
                continue;
            }

            var matchedTypeName = matchedSymbol?.ToFullName()
                ?? GetMetadataFullName(reader, reader.GetTypeDefinition(handle)).Replace('+', '.');
            AddScope(scopes, seenHandles, typeName, matchedTypeName, matchedTypeName, handle);

            if (!includeBaseTypes || matchedSymbol is null)
            {
                continue;
            }

            foreach (var baseType in matchedSymbol.GetBaseTypes())
            {
                if (!TryGetTypeDefinitionHandle(reader, baseType.ToMetadataFullName(), out var baseHandle))
                {
                    continue;
                }

                AddScope(scopes, seenHandles, typeName, matchedTypeName, baseType.ToFullName(), baseHandle);
            }
        }

        return scopes;
    }

    private static void AddScope(
        List<TypeScope> scopes,
        HashSet<TypeDefinitionHandle> seenHandles,
        string requestedTypeName,
        string requestedMatchedTypeName,
        string matchedTypeName,
        TypeDefinitionHandle handle)
    {
        if (!seenHandles.Add(handle))
        {
            return;
        }

        scopes.Add(new TypeScope(requestedTypeName, requestedMatchedTypeName, matchedTypeName, handle));
    }

    private static bool MatchesRequestedMethods(
        string matchedTypeName,
        string methodName,
        string signature,
        IReadOnlyList<string> requestedMethods,
        bool exactSimpleMethodName,
        out string requestedMethodName)
    {
        if (requestedMethods.Count == 0)
        {
            requestedMethodName = methodName;
            return true;
        }

        foreach (var requestedMethod in requestedMethods)
        {
            if (IsExactSimpleMethodRequest(requestedMethod, exactSimpleMethodName))
            {
                if (IsMethodMatch(methodName, requestedMethod))
                {
                    requestedMethodName = requestedMethod;
                    return true;
                }

                continue;
            }

            if (IsWildcardMatch(methodName, requestedMethod)
                || IsWildcardMatch(signature, requestedMethod)
                || IsWildcardMatch($"{matchedTypeName}.{methodName}", requestedMethod)
                || IsWildcardMatch($"{matchedTypeName}.{signature}", requestedMethod))
            {
                requestedMethodName = requestedMethod;
                return true;
            }
        }

        requestedMethodName = string.Empty;
        return false;
    }

    private static bool IsMethodMatch(
        string methodName,
        string requestedMethod)
    {
        return methodName.Equals(requestedMethod, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExactSimpleMethodRequest(string requestedMethod, bool exactSimpleMethodName)
    {
        return exactSimpleMethodName && IsSimpleMethodName(requestedMethod);
    }

    private static bool IsSimpleMethodName(string requestedMethod)
    {
        if (string.IsNullOrWhiteSpace(requestedMethod))
        {
            return false;
        }

        if (requestedMethod is ".ctor" or ".cctor")
        {
            return true;
        }

        return requestedMethod.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '`');
    }

    private static bool IsWildcardMatch(string value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return true;
        }

        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        var regex = new System.Text.RegularExpressions.Regex(
            System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*"),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return regex.IsMatch(value);
    }

    private static bool MatchesMemberType(string kind, string memberType)
    {
        return memberType switch
        {
            "*" => true,
            "method" => kind == "method",
            "constructor" or "ctor" => kind == "constructor",
            "staticconstructor" or "static_constructor" or "cctor" => kind == "staticconstructor",
            _ => true
        };
    }

    private static string GetMethodKind(string methodName)
    {
        return methodName switch
        {
            ".ctor" => "constructor",
            ".cctor" => "staticconstructor",
            _ => "method"
        };
    }

    private static string GetAccessibility(MethodAttributes attributes)
    {
        return (attributes & MethodAttributes.MemberAccessMask) switch
        {
            MethodAttributes.Private => "Private",
            MethodAttributes.FamANDAssem => "ProtectedAndInternal",
            MethodAttributes.Assembly => "Internal",
            MethodAttributes.Family => "Protected",
            MethodAttributes.FamORAssem => "ProtectedInternal",
            MethodAttributes.Public => "Public",
            _ => "PrivateScope"
        };
    }

    private static bool IsCompilerGenerated(MetadataReader reader, MethodDefinition methodDefinition, string methodName)
    {
        return methodName.Contains('<', StringComparison.Ordinal)
            || GetAttributes(reader, methodDefinition.GetCustomAttributes())
                .Any(attribute => attribute.AttributeClass == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
    }

    private static MethodSignatureInfo DecodeMethodSignature(
        MetadataReader reader,
        MethodDefinition methodDefinition,
        string methodName,
        string accessibility)
    {
        var provider = MetadataSignatureTypeProvider.Instance;
        var signature = methodDefinition.DecodeSignature(provider, genericContext: null);
        var parameterDefinitions = methodDefinition.GetParameters()
            .Select(handle => reader.GetParameter(handle))
            .Where(parameter => parameter.SequenceNumber > 0)
            .ToDictionary(parameter => parameter.SequenceNumber);

        var parameters = new List<MemberParameterDto>();
        for (var i = 0; i < signature.ParameterTypes.Length; i++)
        {
            parameterDefinitions.TryGetValue(i + 1, out var parameterDefinition);
            var type = signature.ParameterTypes[i];
            var isByRef = type.StartsWith("ref ", StringComparison.Ordinal);
            var parameterName = parameterDefinition.Name.IsNil
                ? $"arg{i}"
                : reader.GetString(parameterDefinition.Name);
            var refKind = parameterDefinition.Attributes.HasFlag(ParameterAttributes.Out)
                ? "Out"
                : isByRef
                    ? "Ref"
                    : parameterDefinition.Attributes.HasFlag(ParameterAttributes.In)
                        ? "In"
                        : "None";
            parameters.Add(new MemberParameterDto(
                parameterName,
                isByRef ? type[4..] : type,
                refKind,
                parameterDefinition.Attributes.HasFlag(ParameterAttributes.Optional),
                null));
        }

        var modifiers = new List<string>();
        if (methodDefinition.Attributes.HasFlag(MethodAttributes.Static))
            modifiers.Add("static");
        if (methodDefinition.Attributes.HasFlag(MethodAttributes.Abstract))
            modifiers.Add("abstract");
        if (methodDefinition.Attributes.HasFlag(MethodAttributes.Virtual))
            modifiers.Add("virtual");

        var displayName = methodName;
        var genericParameters = methodDefinition.GetGenericParameters()
            .Select(handle => reader.GetString(reader.GetGenericParameter(handle).Name))
            .ToList();
        if (genericParameters.Count != 0)
        {
            displayName = $"{displayName}<{string.Join(", ", genericParameters)}>";
        }

        var signatureText = $"{accessibility.ToLowerInvariant()} {string.Join(" ", modifiers)} {signature.ReturnType} {displayName}({string.Join(", ", parameters.Select(parameter => $"{FormatRefKind(parameter.RefKind)}{parameter.Type} {parameter.Name}"))})"
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();
        return new MethodSignatureInfo(signature.ReturnType, parameters, signatureText);
    }

    private static string FormatRefKind(string refKind)
    {
        return refKind switch
        {
            "Ref" => "ref ",
            "Out" => "out ",
            "In" => "in ",
            _ => string.Empty
        };
    }

    private static IReadOnlyList<AttributeDto> GetAttributes(MetadataReader reader, CustomAttributeHandleCollection handles)
    {
        return [.. handles.Select(handle =>
        {
            var attribute = reader.GetCustomAttribute(handle);
            var attributeClass = GetCustomAttributeTypeName(reader, attribute.Constructor);
            return new AttributeDto(attributeClass, attributeClass, [], []);
        })];
    }

    private static string GetCustomAttributeTypeName(MetadataReader reader, EntityHandle constructor)
    {
        EntityHandle declaringType = constructor.Kind switch
        {
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            _ => default
        };

        return declaringType.Kind switch
        {
            HandleKind.TypeDefinition => GetMetadataFullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)declaringType)).Replace('+', '.'),
            HandleKind.TypeReference => GetTypeReferenceFullName(reader, (TypeReferenceHandle)declaringType).Replace('+', '.'),
            _ => "<unknown>"
        };
    }

    private static string GetTypeReferenceFullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return $"{GetTypeReferenceFullName(reader, (TypeReferenceHandle)reference.ResolutionScope)}+{name}";
        }

        var ns = reader.GetString(reference.Namespace);
        return string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
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

    private sealed record TypeScope(
        string RequestedTypeName,
        string RequestedMatchedTypeName,
        string MatchedTypeName,
        TypeDefinitionHandle Handle);

    private sealed record MethodSignatureInfo(
        string ReturnType,
        IReadOnlyList<MemberParameterDto> Parameters,
        string Signature);

    private sealed class MetadataSignatureTypeProvider : ISignatureTypeProvider<string, object?>
    {
        public static MetadataSignatureTypeProvider Instance { get; } = new();

        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";

        public string GetByReferenceType(string elementType) => $"ref {elementType}";

        public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(", ", typeArguments)}>";

        public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";

        public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => $"{elementType}*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode)
        {
            return typeCode switch
            {
                PrimitiveTypeCode.Void => "void",
                PrimitiveTypeCode.Boolean => "bool",
                PrimitiveTypeCode.Char => "char",
                PrimitiveTypeCode.SByte => "sbyte",
                PrimitiveTypeCode.Byte => "byte",
                PrimitiveTypeCode.Int16 => "short",
                PrimitiveTypeCode.UInt16 => "ushort",
                PrimitiveTypeCode.Int32 => "int",
                PrimitiveTypeCode.UInt32 => "uint",
                PrimitiveTypeCode.Int64 => "long",
                PrimitiveTypeCode.UInt64 => "ulong",
                PrimitiveTypeCode.Single => "float",
                PrimitiveTypeCode.Double => "double",
                PrimitiveTypeCode.String => "string",
                PrimitiveTypeCode.Object => "object",
                PrimitiveTypeCode.IntPtr => "nint",
                PrimitiveTypeCode.UIntPtr => "nuint",
                _ => typeCode.ToString()
            };
        }

        public string GetSZArrayType(string elementType) => $"{elementType}[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            return GetMetadataFullName(reader, reader.GetTypeDefinition(handle)).Replace('+', '.');
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            return GetTypeReferenceFullName(reader, handle).Replace('+', '.');
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        }
    }
}
