using Microsoft.CodeAnalysis;

namespace Disasmo;

public static class SymbolUtils
{
    public static DisasmoSymbolInfo FromSymbol(ISymbol symbol)
    {
        string hostType;
        string target;
        string methodName;

        var prefix = "";
        var containingType = symbol as ITypeSymbol ?? symbol.ContainingType;

        // Match all for nested types
        if (containingType.ContainingType is not null)
        {
            prefix = "*";
        }
        else
        {
            var @namespace = containingType.ContainingNamespace;
            while (@namespace?.Name is { Length: > 0 } containingNamespace)
            {
                prefix = containingNamespace + "." + prefix;
                @namespace = @namespace.ContainingNamespace;
            }
        }

        prefix += containingType.MetadataName;

        if (containingType is INamedTypeSymbol { IsGenericType: true })
            prefix += "*";

        if (symbol is IMethodSymbol methodSymbol)
        {
            hostType = symbol.ContainingType.ToString();
            if (methodSymbol.MethodKind == MethodKind.LocalFunction)
            {
                // Hack for mangled names
                target = prefix + ":*" + symbol.MetadataName + "*";
                methodName = "*";
            }
            else if (methodSymbol.MethodKind == MethodKind.Constructor)
            {
                target = prefix + ":.ctor";
                methodName = "*";
            }
            else
            {
                target = prefix + ":" + symbol.MetadataName;
                methodName = symbol.MetadataName;
            }
        }
        else if (symbol is IPropertySymbol)
        {
            hostType = symbol.ContainingType.ToString();
            target = prefix + ":get_" + symbol.MetadataName + " " + prefix + ":set_" + symbol.MetadataName;
            methodName = symbol.MetadataName;
        }
        else
        {
            // The whole class
            hostType = symbol.ToString();
            target = prefix + ":*";
            methodName = "*";
        }

        return new DisasmoSymbolInfo(target, hostType, methodName);
    }
}
