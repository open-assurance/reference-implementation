using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cai.Reference.Engine;

/// <summary>Small syntax helpers shared by the detectors.</summary>
public static class Cs
{
    /// <summary>Method-like declarations with a body: methods, constructors, local functions, accessors, operators.</summary>
    public static IEnumerable<SyntaxNode> MethodLike(SyntaxNode root) =>
        root.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax).Where(n => n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax);

    public static SyntaxNode? BodyOf(SyntaxNode m) => m switch
    {
        BaseMethodDeclarationSyntax b => (SyntaxNode?)b.Body ?? b.ExpressionBody,
        LocalFunctionStatementSyntax l => (SyntaxNode?)l.Body ?? l.ExpressionBody,
        AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
        _ => null,
    };

    public static string NameOf(SyntaxNode m) => m switch
    {
        MethodDeclarationSyntax md => md.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text + " (ctor)",
        LocalFunctionStatementSyntax l => l.Identifier.Text,
        AccessorDeclarationSyntax a => (a.Parent?.Parent as PropertyDeclarationSyntax)?.Identifier.Text + "." + a.Keyword.Text,
        OperatorDeclarationSyntax o => "operator " + o.OperatorToken.Text,
        ConversionOperatorDeclarationSyntax => "conversion operator",
        DestructorDeclarationSyntax d => "~" + d.Identifier.Text,
        _ => m.Kind().ToString(),
    };

    public static SyntaxToken IdentifierOf(SyntaxNode m) => m switch
    {
        MethodDeclarationSyntax md => md.Identifier,
        ConstructorDeclarationSyntax c => c.Identifier,
        LocalFunctionStatementSyntax l => l.Identifier,
        AccessorDeclarationSyntax a => a.Keyword,
        OperatorDeclarationSyntax o => o.OperatorToken,
        DestructorDeclarationSyntax d => d.Identifier,
        _ => m.GetFirstToken(),
    };

    public static string EnclosingNamespace(SyntaxNode n) => n.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? n.SyntaxTree.GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "";
    public static TypeDeclarationSyntax? EnclosingType(SyntaxNode n) => n.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
    public static string TypeName(SyntaxNode n) => EnclosingType(n)?.Identifier.Text ?? "";
    public static SyntaxNode? EnclosingMethod(SyntaxNode n) => n.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax or AnonymousFunctionExpressionSyntax);

    public static int Lines(SyntaxNode n) { var s = n.GetLocation().GetLineSpan(); return s.EndLinePosition.Line - s.StartLinePosition.Line + 1; }
    public static int Line(SyntaxNode n) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    public static int Line(SyntaxToken t) => t.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    public static bool HasModifier(SyntaxNode n, SyntaxKind kind) => n switch
    {
        MemberDeclarationSyntax m => m.Modifiers.Any(kind),
        LocalFunctionStatementSyntax l => l.Modifiers.Any(kind),
        AccessorDeclarationSyntax a => a.Modifiers.Any(kind),
        _ => false,
    };

    public static bool IsMain(SyntaxNode? method) => method is MethodDeclarationSyntax md && md.Identifier.Text == "Main" && md.Modifiers.Any(SyntaxKind.StaticKeyword);
    public static bool IsTopLevel(SyntaxNode n) => n.Ancestors().Any(a => a is GlobalStatementSyntax);

    public static string Simple(TypeSyntax? t) => t switch
    {
        null => "",
        GenericNameSyntax g => g.Identifier.Text,
        QualifiedNameSyntax q => Simple(q.Right),
        NullableTypeSyntax nt => Simple(nt.ElementType),
        AliasQualifiedNameSyntax a => Simple(a.Name),
        PredefinedTypeSyntax p => p.Keyword.Text,
        _ => t.ToString(),
    };

    public static string MemberName(ExpressionSyntax e) => e switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        MemberBindingExpressionSyntax b => b.Name.Identifier.Text,
        _ => "",
    };

    public static string ReceiverText(ExpressionSyntax e) => e is MemberAccessExpressionSyntax m ? m.Expression.ToString() : "";

    public static bool IsEventHandlerSignature(ParameterListSyntax? pl) =>
        pl is not null && pl.Parameters.Count == 2 && Simple(pl.Parameters[0].Type) is "object" or "Object" && Simple(pl.Parameters[1].Type).EndsWith("EventArgs", StringComparison.Ordinal);

    public static ITypeSymbol? TypeOf(SemanticModel model, ExpressionSyntax e) { try { return model.GetTypeInfo(e).Type; } catch (Exception) { return null; } }
    public static ISymbol? SymbolOf(SemanticModel model, SyntaxNode n) { try { return model.GetSymbolInfo(n).Symbol ?? model.GetSymbolInfo(n).CandidateSymbols.FirstOrDefault(); } catch (Exception) { return null; } }
    public static ISymbol? Declared(SemanticModel model, SyntaxNode n) { try { return model.GetDeclaredSymbol(n); } catch (Exception) { return null; } }

    public static bool IsTaskLike(ITypeSymbol? t) => t is not null && (t.Name is "Task" or "ValueTask" || t.Name.StartsWith("Task", StringComparison.Ordinal) && t.ContainingNamespace?.ToString() == "System.Threading.Tasks");
    public static bool LooksAsyncCall(ExpressionSyntax e) => e is InvocationExpressionSyntax inv && MemberName(inv.Expression).EndsWith("Async", StringComparison.Ordinal) || e is InvocationExpressionSyntax inv2 && inv2.Expression.ToString().StartsWith("Task.", StringComparison.Ordinal);

    public static string TypeDisplay(ITypeSymbol? t) => t?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "?";

    public static IEnumerable<string> CommentLines(SyntaxTrivia trivia)
    {
        var text = trivia.ToFullString();
        if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)) yield return text.TrimStart('/').Trim();
        else if (trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) foreach (var l in text.TrimStart('/', '*').TrimEnd('/', '*').Split('\n')) yield return l.Trim().TrimStart('*').Trim();
        else if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)) foreach (var l in text.Split('\n')) yield return l.Trim().TrimStart('/', '*').Trim();
    }

    public static bool IsComment(SyntaxTrivia t) => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia) || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

    public static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, params string[] names) =>
        lists.SelectMany(l => l.Attributes).Any(a => names.Any(n => Simple(a.Name) == n || Simple(a.Name) == n + "Attribute"));

    public static SyntaxList<AttributeListSyntax> Attributes(SyntaxNode n) => n switch
    {
        MemberDeclarationSyntax m => m.AttributeLists,
        LocalFunctionStatementSyntax l => l.AttributeLists,
        AccessorDeclarationSyntax a => a.AttributeLists,
        _ => default,
    };
}
