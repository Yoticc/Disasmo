using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Disasmo.Properties;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Document = Microsoft.CodeAnalysis.Document;

namespace Disasmo;

public class DisasmMethodOrClassAction : BaseSuggestedAction
{
    public DisasmMethodOrClassAction(CommonSuggestedActionsSource actionsSource) : base(actionsSource) {}

    public override async void Invoke(CancellationToken cancellationToken)
    {
        try
        {
            if (LastDocument is null)
                return;
             
            var window = await IdeUtils.ShowWindowAsync<DisasmWindow>(true, cancellationToken);
            if (window?.ViewModel is {} viewModel)
            {
                var symbol = await GetSymbolAsync(LastDocument, LastTokenPosition, cancellationToken);
                var project = LastDocument.Project;
                viewModel.RunOperationAsync(symbol, project);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    protected override async Task<bool> IsValidSymbolAsync(Document document, int tokenPosition, CancellationToken cancellationToken)
    {
        try
        {
            if (Settings.Default.DisableLightBulb)
                return false;

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel is null)
                return false;

            var syntaxTree = await semanticModel.SyntaxTree.GetRootAsync(cancellationToken);
            var token = syntaxTree.FindToken(tokenPosition);
            var isValidSymbol = token.Parent
                is MethodDeclarationSyntax
                or ClassDeclarationSyntax
                or StructDeclarationSyntax
                or LocalFunctionStatementSyntax
                or ConstructorDeclarationSyntax
                or PropertyDeclarationSyntax
                or OperatorDeclarationSyntax;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        return false;
    }


    static ISymbol FindRelatedSymbol(
        SemanticModel semanticModel, 
        SyntaxNode node, 
        bool allowClassesAndStructs, 
        CancellationToken cancellationToken)
    {
        if ((node 
                is LocalFunctionStatementSyntax
                or MethodDeclarationSyntax
                or PropertyDeclarationSyntax
                or OperatorDeclarationSyntax
                or ConstructorDeclarationSyntax)
            || (allowClassesAndStructs && node 
                is ClassDeclarationSyntax 
                or StructDeclarationSyntax))
        {
            return semanticModel.GetDeclaredSymbol(node, cancellationToken);
        }

        return null;
    }

    public static async Task<ISymbol> GetSymbolStaticAsync(
        Document document, 
        int tokenPosition, 
        CancellationToken cancellationToken, 
        bool recursive = false)
    {
        try
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel is null)
                return null;

            var syntaxTree = await semanticModel.SyntaxTree.GetRootAsync(cancellationToken);
            var token = syntaxTree.FindToken(tokenPosition);
            var parent = token.Parent;
            if (parent is null)
                return null;

            var symbol = FindRelatedSymbol(semanticModel, parent, true, cancellationToken);
            if (symbol is null && recursive)
            {
                while (true)
                {
                    parent = parent?.Parent;
                    if (parent is null)
                        return null;

                    symbol = FindRelatedSymbol(semanticModel, parent, false, cancellationToken);
                    if (symbol is not null)
                        return symbol;
                }
            }

            return symbol;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    protected virtual Task<ISymbol> GetSymbolAsync(Document document, int tokenPosition, CancellationToken cancellationToken) => 
        GetSymbolStaticAsync(document, tokenPosition, cancellationToken);

    public override string DisplayText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DisasmoPackage.HotKey))
                return "Disasm this";

            return $"Disasm this ({DisasmoPackage.HotKey})";
        }
    }
}