using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsharpMcp.CodeAnalysis.Tools;

/// <summary>What a reference site does with the symbol.</summary>
public enum ReferenceKind
{
    Read,
    Write,
    Invocation,
    Instantiation,
    TypeRef,
    Inheritance,
    TypeOf,
    NameOf,
    Attribute
}

/// <summary>
/// Classifies reference sites and locates their enclosing member, using syntax only so that
/// filtering a large reference set never forces a semantic model per document.
/// </summary>
public static class ReferenceClassifier
{
    public record EnclosingMember(SyntaxNode Declaration, string Name);

    public static ReferenceKind Classify(SyntaxNode node)
    {
        var expr = Unwrap(node);

        if (IsInsideNameOf(expr)) return ReferenceKind.NameOf;

        var parent = expr.Parent;

        if (parent is TypeOfExpressionSyntax) return ReferenceKind.TypeOf;
        if (parent is AttributeSyntax attr && attr.Name == expr) return ReferenceKind.Attribute;
        if (parent is BaseTypeSyntax) return ReferenceKind.Inheritance;
        if (parent is ObjectCreationExpressionSyntax creation && creation.Type == expr)
            return ReferenceKind.Instantiation;
        if (parent is InvocationExpressionSyntax invocation && invocation.Expression == expr)
            return ReferenceKind.Invocation;
        if (IsWriteTarget(expr, parent)) return ReferenceKind.Write;
        if (IsTypePosition(expr, parent)) return ReferenceKind.TypeRef;

        return ReferenceKind.Read;
    }

    public static EnclosingMember? FindEnclosing(SyntaxNode node)
    {
        var declaration = node.AncestorsAndSelf().FirstOrDefault(n =>
            n is MethodDeclarationSyntax
            or ConstructorDeclarationSyntax
            or DestructorDeclarationSyntax
            or OperatorDeclarationSyntax
            or ConversionOperatorDeclarationSyntax
            or PropertyDeclarationSyntax
            or IndexerDeclarationSyntax
            or EventDeclarationSyntax
            or LocalFunctionStatementSyntax
            or FieldDeclarationSyntax
            or EventFieldDeclarationSyntax
            or BaseTypeDeclarationSyntax);

        if (declaration is null) return null;

        var typeName = declaration.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
        var memberName = MemberName(declaration);

        var name = (typeName, memberName) switch
        {
            (null, null) => null,
            (null, { } member) => member,
            ({ } type, null) => type,
            ({ } type, { } member) => $"{type}.{member}"
        };

        return name is null ? null : new EnclosingMember(declaration, name);
    }

    /// <summary>
    /// True when the declaration invokes a method or constructs a type matching the pattern.
    /// </summary>
    public static bool CallsMatching(SyntaxNode declaration, SemanticModel model, string pattern)
    {
        foreach (var node in declaration.DescendantNodes())
        {
            var target = node switch
            {
                InvocationExpressionSyntax or ObjectCreationExpressionSyntax
                    or ImplicitObjectCreationExpressionSyntax => model.GetSymbolInfo(node).Symbol,
                _ => null
            };

            if (target is null) continue;

            var name = target is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor
                ? ctor.ContainingType.Name
                : target.Name;

            if (ProjectTools.MatchesPattern(name, pattern)
                || ProjectTools.MatchesPattern(target.ToDisplayString(), pattern))
                return true;
        }

        return false;
    }

    public static ReferenceKind ParseUsage(string usage) =>
        Enum.TryParse<ReferenceKind>(usage, ignoreCase: true, out var kind)
            ? kind
            : throw new ArgumentException(
                $"Unknown usage '{usage}'. Use one of: {string.Join(", ", Enum.GetNames<ReferenceKind>().Select(n => n.ToLowerInvariant()))}.");

    /// <summary>
    /// Climbs from the identifier token to the whole expression it names, so that
    /// "order.Save()" is seen as an invocation rather than a member access.
    /// </summary>
    private static SyntaxNode Unwrap(SyntaxNode node)
    {
        var current = node;

        while (current.Parent is { } parent)
        {
            var climb = parent switch
            {
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name == current,
                MemberBindingExpressionSyntax memberBinding => memberBinding.Name == current,
                QualifiedNameSyntax qualified => qualified.Right == current,
                AliasQualifiedNameSyntax alias => alias.Name == current,
                _ => false
            };

            if (!climb) break;
            current = parent;
        }

        return current;
    }

    private static bool IsInsideNameOf(SyntaxNode node) =>
        node.Ancestors().OfType<InvocationExpressionSyntax>().Any(inv =>
            inv.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" });

    private static bool IsWriteTarget(SyntaxNode expr, SyntaxNode? parent) => parent switch
    {
        AssignmentExpressionSyntax assignment => assignment.Left == expr,
        PrefixUnaryExpressionSyntax prefix =>
            prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression),
        PostfixUnaryExpressionSyntax postfix =>
            postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression),
        ArgumentSyntax argument =>
            argument.Expression == expr
            && (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)),
        _ => false
    };

    private static bool IsTypePosition(SyntaxNode expr, SyntaxNode? parent) => parent switch
    {
        ParameterSyntax parameter => parameter.Type == expr,
        VariableDeclarationSyntax variable => variable.Type == expr,
        MethodDeclarationSyntax method => method.ReturnType == expr,
        PropertyDeclarationSyntax property => property.Type == expr,
        IndexerDeclarationSyntax indexer => indexer.Type == expr,
        EventDeclarationSyntax @event => @event.Type == expr,
        CastExpressionSyntax cast => cast.Type == expr,
        BinaryExpressionSyntax binary =>
            (binary.IsKind(SyntaxKind.AsExpression) || binary.IsKind(SyntaxKind.IsExpression)) && binary.Right == expr,
        TypeArgumentListSyntax or ArrayTypeSyntax or NullableTypeSyntax or TypeConstraintSyntax
            or DeclarationPatternSyntax or TypePatternSyntax => true,
        _ => false
    };

    private static string? MemberName(SyntaxNode declaration) => declaration switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        ConstructorDeclarationSyntax ctor => ctor.Identifier.ValueText,
        DestructorDeclarationSyntax dtor => $"~{dtor.Identifier.ValueText}",
        OperatorDeclarationSyntax op => $"operator {op.OperatorToken.ValueText}",
        ConversionOperatorDeclarationSyntax => "operator",
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        IndexerDeclarationSyntax => "this[]",
        EventDeclarationSyntax @event => @event.Identifier.ValueText,
        LocalFunctionStatementSyntax local => local.Identifier.ValueText,
        FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText,
        EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText,
        _ => null
    };
}
