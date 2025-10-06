using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Microsoft.CodeAnalysis;
using System.Xml;
using System.Text;

namespace RoslynAssemblyAnalyzerMcp;

internal static class Extensions
{
    public static IPackageSearchMetadata? GetVersion(this IEnumerable<IPackageSearchMetadata> versions, string? version)
    {
        IPackageSearchMetadata? targetMetadata;
        if (string.IsNullOrWhiteSpace(version))
        {
            targetMetadata = versions.OrderByDescending(m => m.Identity.Version).First();
            return targetMetadata;
        }
        else
        {
            if (!NuGetVersion.TryParse(version, out var nugetVersion))
            {
                return null;
            }

            targetMetadata = versions.FirstOrDefault(m => m.Identity.Version.Equals(nugetVersion));
            return targetMetadata;
        }
    }

    public static string ToFullDisplay(this IMethodSymbol method)
    {
        var accessibility = method.DeclaredAccessibility.ToString().ToLower();
        var modifiers = new List<string>();

        if (method.IsStatic)
            modifiers.Add("static");
        if (method.IsAbstract)
            modifiers.Add("abstract");
        if (method.IsVirtual)
            modifiers.Add("virtual");
        if (method.IsOverride)
            modifiers.Add("override");
        if (method.IsSealed)
            modifiers.Add("sealed");
        if (method.IsAsync)
            modifiers.Add("async");

        var modifiersStr = modifiers.Any() ? $"{string.Join(" ", modifiers)} " : "";

        var parameters = string.Join(", ", method.Parameters.Select(p =>
        {
            var refKind = p.RefKind switch
            {
                RefKind.Ref => "ref ",
                RefKind.Out => "out ",
                RefKind.In => "in ",
                _ => ""
            };
            var defaultValue = p.HasExplicitDefaultValue ? $" = {FormatDefaultValue(p.ExplicitDefaultValue)}" : "";
            return $"{refKind}{p.Type.ToDisplayString()} {p.Name}{defaultValue}";
        }));

        var returnType = method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString();

        var typeParameters = method.TypeParameters.Length != 0 ? $"<{string.Join(", ", method.TypeParameters.Select(tp => tp.Name))}>" : string.Empty;

        return $"{accessibility} {modifiersStr}{returnType} {method.Name}{typeParameters}({parameters})";
    }

    public static string ToFullDisplayText(this IPropertySymbol property)
    {
        var accessibility = property.DeclaredAccessibility.ToString().ToLower();
        var modifiers = new List<string>();

        if (property.IsStatic)
            modifiers.Add("static");
        if (property.IsAbstract)
            modifiers.Add("abstract");
        if (property.IsVirtual)
            modifiers.Add("virtual");
        if (property.IsOverride)
            modifiers.Add("override");

        var modifiersStr = modifiers.Any() ? $"{string.Join(" ", modifiers)} " : "";

        var accessors = new List<string>();
        if (property.GetMethod != null)
            accessors.Add("get");
        if (property.SetMethod != null)
            accessors.Add("set");
        var accessorStr = accessors.Any() ? $" {{ {string.Join("; ", accessors)}; }}" : "";

        if (property.IsIndexer)
        {
            var parameters = string.Join(", ", property.Parameters.Select(p =>
                $"{p.Type.ToDisplayString()} {p.Name}"));
            return $"{accessibility} {modifiersStr}{property.Type.ToDisplayString()} this[{parameters}]{accessorStr}";
        }
        else
        {
            return $"{accessibility} {modifiersStr}{property.Type.ToDisplayString()} {property.Name}{accessorStr}";
        }
    }

    public static string ToFullDisplayText(this INamedTypeSymbol type)
    {
        var accessibility = type.DeclaredAccessibility.ToString().ToLower();
        var modifiers = new List<string>();

        if (type.IsAbstract && type.TypeKind == TypeKind.Class)
            modifiers.Add("abstract");
        if (type.IsSealed)
            modifiers.Add("sealed");
        if (type.IsStatic)
            modifiers.Add("static");

        var modifiersStr = modifiers.Count != 0 ? $" {string.Join(" ", modifiers)}" : "";

        var baseTypeAndInterfaces = new List<string>();

        if (type.BaseType != null)
        {
            baseTypeAndInterfaces.Add(type.BaseType.ToDisplayString());
        }
        if (type.Interfaces.Length > 0)
        {
            baseTypeAndInterfaces.AddRange(type.Interfaces.Select(v => v.Name));
        }

        return $"{accessibility}{modifiersStr} {type.TypeKind.ToString().ToLower()} {type.ToDisplayString()}{(baseTypeAndInterfaces.Count > 0 ? " : " : "")}{string.Join(", ", baseTypeAndInterfaces)}";
    }

    public static string ToConstructorFullDisplayText(this IMethodSymbol constructorMethod)
    {
        var accessibility = constructorMethod.DeclaredAccessibility.ToString().ToLower();
        var parameters = string.Join(", ", constructorMethod.Parameters.Select(p =>
            $"{p.Type.ToDisplayString()} {p.Name}{(p.HasExplicitDefaultValue ? $" = {FormatDefaultValue(p.ExplicitDefaultValue)}" : "")}"));

        return $"{accessibility} {constructorMethod.ContainingType.Name}({parameters})";
    }

    public static string ToFullDisplayText(this IFieldSymbol field)
    {
        var accessibility = field.DeclaredAccessibility.ToString().ToLower();
        var modifiers = new List<string>();

        if (field.IsStatic)
            modifiers.Add("static");
        if (field.IsReadOnly)
            modifiers.Add("readonly");
        if (field.IsConst)
            modifiers.Add("const");

        var modifiersStr = modifiers.Count != 0 ? $"{string.Join(" ", modifiers)} " : "";

        var value = field.HasConstantValue ? $" = {FormatDefaultValue(field.ConstantValue)}" : "";

        return $"{accessibility} {modifiersStr}{field.Type.ToDisplayString()} {field.Name}{value}";
    }

    public static string ToFullDisplayText(this IEventSymbol evt)
    {
        var accessibility = evt.DeclaredAccessibility.ToString().ToLower();
        var modifiers = new List<string>();

        if (evt.IsStatic)
            modifiers.Add("static");

        var modifiersStr = modifiers.Count != 0 ? $"{string.Join(" ", modifiers)} " : "";

        return $"{accessibility} {modifiersStr}event {evt.Type.ToDisplayString()} {evt.Name}";
    }

    /*
        /// <summary>
        /// comment
        /// </summary>
        /// <returns></returns>
     */
    public static string? GetCommentText(this ISymbol symbol, int indent = 0)
    {
        var xmlString = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xmlString))
            return null;

        var xml = new XmlDocument();
        xml.LoadXml(xmlString);
        var root = xml.DocumentElement;
        if (root is null)
            return null;

        StringBuilder s = new();
        foreach (var line in root.InnerXml.EnumerateLines())
        {
            s.Append($"{new string(' ', indent)}/// ");
            s.Append(line);
            s.AppendLine();
        }
        for (int i = 1; i <= 2; i++)
        {
            if (s[^1] is '\r' or '\n')
                s.Remove(s.Length - 1, 1);
            else
                break;
        }
        return s.ToString();
    }

    private static string FormatDefaultValue(object? value)
    {
        if (value == null)
            return "null";
        if (value is string str)
            return $"\"{str}\"";
        if (value is bool b)
            return b.ToString().ToLower();
        if (value is char c)
            return $"'{c}'";
        return value.ToString() ?? "null";
    }


    extension(INamedTypeSymbol symbol)
    {
        public IReadOnlyCollection<INamedTypeSymbol> GetInterfaces(bool includeBaseMembers = false)
        {
            if (!includeBaseMembers)
            {
                return symbol.Interfaces;
            }
            else
            {
                HashSet<INamedTypeSymbol> interfaces = new(SymbolEqualityComparer.IncludeNullability);

                var type = symbol;
                while (type != null)
                {
                    foreach (var item in type.Interfaces)
                    {
                        interfaces.Add(item);
                    }
                    type = type.BaseType;
                }

                return interfaces;
            }
        }


        public IReadOnlyCollection<ISymbol> GetMembers(bool includeBaseMembers = false)
        {
            if (!includeBaseMembers)
            {
                return symbol.GetMembers();
            }
            else
            {
                HashSet<ISymbol> members = new(SymbolEqualityComparer.IncludeNullability);

                var type = symbol;
                while (type != null)
                {
                    foreach (var item in type.GetMembers())
                    {
                        members.Add(item);
                    }
                    type = type.BaseType;
                }

                return members;
            }
        }
    }
}