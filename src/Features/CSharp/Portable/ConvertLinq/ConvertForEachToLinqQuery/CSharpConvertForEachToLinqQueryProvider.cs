﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.ConvertLinq.ConvertForEachToLinqQuery;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;
using SyntaxNodeOrTokenExtensions = Microsoft.CodeAnalysis.Shared.Extensions.SyntaxNodeOrTokenExtensions;

namespace Microsoft.CodeAnalysis.CSharp.ConvertLinq.ConvertForEachToLinqQuery
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(CSharpConvertForEachToLinqQueryProvider)), Shared]
    internal sealed class CSharpConvertForEachToLinqQueryProvider
        : AbstractConvertForEachToLinqQueryProvider<ForEachStatementSyntax, StatementSyntax>
    {
        protected override IConverter<ForEachStatementSyntax, StatementSyntax> CreateDefaultConverter(
            ForEachInfo<ForEachStatementSyntax, StatementSyntax> forEachInfo)
            => new DefaultConverter(forEachInfo);

        protected override ForEachInfo<ForEachStatementSyntax, StatementSyntax> CreateForEachInfo(
            ForEachStatementSyntax forEachStatement,
            SemanticModel semanticModel)
        {
            var identifiersBuilder = ImmutableArray.CreateBuilder<SyntaxToken>();
            identifiersBuilder.Add(forEachStatement.Identifier);
            var convertingNodesBuilder = ImmutableArray.CreateBuilder<ExtendedSyntaxNode>();
            IEnumerable<StatementSyntax> statementsCannotBeConverted = null;
            var trailingTokensBuilder = ImmutableArray.CreateBuilder<SyntaxToken>();
            var currentLeadingTokens = new List<SyntaxToken>();

            var current = forEachStatement.Statement;
            // Traverse descentants of the forEachStatement.
            // If a statement traversed can be converted into a query clause, 
            //  a. Add it to convertingNodesBuilder.
            //  b. set the current to its nested statement and proceed.
            // Otherwise, set statementsCannotBeConverted and stop processing.
            while (statementsCannotBeConverted == null)
            {
                switch (current.Kind())
                {
                    case SyntaxKind.Block:
                        var block = (BlockSyntax)current;
                        // Keep comment trivia from braces to attach them to the qeury created.
                        currentLeadingTokens.Add(block.OpenBraceToken);
                        trailingTokensBuilder.Add(block.CloseBraceToken);
                        var array = block.Statements.ToArray();
                        if (array.Any())
                        {
                            // All except the last one can be local declaration statements like
                            // {
                            //   var a = 0;
                            //   var b = 0;
                            //   if (x != y) <- this is the last one in the block. 
                            // We can support it to be a copmlex foreach or if or whatever. So, set the current to it.
                            //   ...
                            // }
                            for (var i = 0; i < array.Length - 1; i++)
                            {
                                var statement = array[i];
                                if (statement is LocalDeclarationStatementSyntax localDeclarationStatement &&
                                    // Do not support declarations without initialization.
                                    // int a = 0, b, c = 0;
                                    localDeclarationStatement.Declaration.Variables.All(variable => variable.Initializer != null))
                                {
                                    // Prepare variable declarations to be converted into separate let clauses.
                                    ProcessLocalDeclarationStatement(localDeclarationStatement);
                                }
                                else
                                {
                                    // If this one local declaration or has an empty initializer, stop processing.
                                    statementsCannotBeConverted = array.Skip(i).ToArray();
                                    break;
                                }
                            }

                            // Process the last statement separately.
                            current = array.Last();
                        }
                        else
                        {
                            // Silly case: the block is empty. Stop processing.
                            statementsCannotBeConverted = Enumerable.Empty<StatementSyntax>();
                        }

                        break;

                    case SyntaxKind.ForEachStatement:
                        // foreach can always be converted to a from clause.
                        var currentForEachStatement = (ForEachStatementSyntax)current;
                        identifiersBuilder.Add(currentForEachStatement.Identifier);
                        convertingNodesBuilder.Add(new ExtendedSyntaxNode(currentForEachStatement, currentLeadingTokens, Enumerable.Empty<SyntaxToken>()));
                        currentLeadingTokens = new List<SyntaxToken>();
                        // Proceed the loop with the nested statement.
                        current = currentForEachStatement.Statement;
                        break;

                    case SyntaxKind.IfStatement:
                        // Prepare conversion of 'if (condition)' into where clauses.
                        // Do not support if-else statements in the conversion.
                        var ifStatement = (IfStatementSyntax)current;
                        if (ifStatement.Else == null)
                        {
                            convertingNodesBuilder.Add(new ExtendedSyntaxNode(
                                ifStatement, currentLeadingTokens, Enumerable.Empty<SyntaxToken>()));
                            currentLeadingTokens = new List<SyntaxToken>();
                            // Proceed the loop with the nested statement.
                            current = ifStatement.Statement;
                            break;
                        }
                        else
                        {
                            statementsCannotBeConverted = new[] { current };
                            break;
                        }

                    case SyntaxKind.LocalDeclarationStatement:
                        // This is a situation with "var a = something;" s the innermost statements inside the loop.
                        var localDeclaration = (LocalDeclarationStatementSyntax)current;
                        if (localDeclaration.Declaration.Variables.All(variable => variable.Initializer != null))
                        {
                            // Prepare variable declarations to be converted into separate let clauses.
                            ProcessLocalDeclarationStatement(localDeclaration);
                            statementsCannotBeConverted = Enumerable.Empty<StatementSyntax>();
                        }
                        else
                        {
                            // As above, if there is an empty initializer, stop processing.
                            statementsCannotBeConverted = new[] { current };
                        }

                        break;

                    case SyntaxKind.EmptyStatement:
                        // The innermost statement is an empty statement, stop processing
                        // Example:
                        // foreach(...)
                        // {
                        //    ;<- empty statement
                        // }
                        statementsCannotBeConverted = Enumerable.Empty<StatementSyntax>();
                        break;

                    default:
                        // If no specific case found, stop processing.
                        statementsCannotBeConverted = new[] { current };
                        break;
                }
            }

            // Trailing tokens are collected in the reverse order: from extrenal block down to internal ones. Reverse them.
            trailingTokensBuilder.Reverse();

            return new ForEachInfo<ForEachStatementSyntax, StatementSyntax>(
                forEachStatement,
                semanticModel,
                convertingNodesBuilder.ToImmutable(),
                identifiersBuilder.ToImmutable(),
                statementsCannotBeConverted.ToImmutableArray(),
                currentLeadingTokens.ToImmutableArray(),
                trailingTokensBuilder.ToImmutable());

            void ProcessLocalDeclarationStatement(LocalDeclarationStatementSyntax localDeclarationStatement)
            {
                var localDeclarationLeadingTrivia = new IEnumerable<SyntaxTrivia>[] {
                    currentLeadingTokens.GetTrivia(),
                    localDeclarationStatement.Declaration.Type.GetLeadingTrivia(),
                    localDeclarationStatement.Declaration.Type.GetTrailingTrivia() }.Flatten();
                var localDeclarationTrailingTrivia = SyntaxNodeOrTokenExtensions.GetTrivia(localDeclarationStatement.SemicolonToken);
                var separators = localDeclarationStatement.Declaration.Variables.GetSeparators().ToArray();
                for (var i = 0; i < localDeclarationStatement.Declaration.Variables.Count; i++)
                {
                    var variable = localDeclarationStatement.Declaration.Variables[i];
                    convertingNodesBuilder.Add(new ExtendedSyntaxNode(
                        variable,
                        i == 0 ? localDeclarationLeadingTrivia : separators[i - 1].TrailingTrivia,
                        i == localDeclarationStatement.Declaration.Variables.Count - 1
                            ? (IEnumerable<SyntaxTrivia>)localDeclarationTrailingTrivia
                            : separators[i].LeadingTrivia));
                    identifiersBuilder.Add(variable.Identifier);
                }

                currentLeadingTokens = new List<SyntaxToken>();
            }
        }

        protected override bool TryBuildSpecificConverter(
            ForEachInfo<ForEachStatementSyntax, StatementSyntax> forEachInfo,
            SemanticModel semanticModel,
            StatementSyntax statementCannotBeConverted,
            CancellationToken cancellationToken,
            out IConverter<ForEachStatementSyntax, StatementSyntax> converter)
        {
            switch (statementCannotBeConverted.Kind())
            {
                case SyntaxKind.ExpressionStatement:
                    var expresisonStatement = (ExpressionStatementSyntax)statementCannotBeConverted;
                    var expression = expresisonStatement.Expression;
                    switch (expression.Kind())
                    {
                        case SyntaxKind.PostIncrementExpression:
                            // Input:
                            // foreach (var x in a)
                            // {
                            //     ...
                            //     c++;
                            // }
                            // Output:
                            // (from x in a ... select x).Count();
                            // Here we put SyntaxFactory.IdentifierName(forEachStatement.Identifier) ('x' in the example) 
                            // into the select clause.
                            var postfixUnaryExpression = (PostfixUnaryExpressionSyntax)expression;
                            var operand = postfixUnaryExpression.Operand;
                            converter = new ToCountConverter(
                                forEachInfo,
                                selectExpression: SyntaxFactory.IdentifierName(forEachInfo.ForEachStatement.Identifier),
                                modifyingExpression: operand,
                                trivia: SyntaxNodeOrTokenExtensions.GetTrivia(
                                    operand, postfixUnaryExpression.OperatorToken, expresisonStatement.SemicolonToken));
                            return true;

                        case SyntaxKind.InvocationExpression:
                            var invocationExpression = (InvocationExpressionSyntax)expression;
                            // Check that there is 'list.Add(item)'.
                            if (invocationExpression.Expression is MemberAccessExpressionSyntax memberAccessExpression &&
                                semanticModel.GetSymbolInfo(memberAccessExpression, cancellationToken).Symbol is IMethodSymbol methodSymbol &&
                                TypeSymbolOptIsList(methodSymbol.ContainingType, semanticModel) &&
                                methodSymbol.Name == nameof(IList.Add) &&
                                methodSymbol.Parameters.Length == 1 &&
                                invocationExpression.ArgumentList.Arguments.Count == 1)
                            {
                                // Input:
                                // foreach (var x in a)
                                // {
                                //     ...
                                //     list.Add(...);
                                // }
                                // Output:
                                // (from x in a ... select x).ToList();
                                var selectExpression = invocationExpression.ArgumentList.Arguments.Single().Expression;
                                converter = new ToToListConverter(
                                    forEachInfo,
                                    selectExpression,
                                    modifyingExpression: memberAccessExpression.Expression,
                                    trivia: SyntaxNodeOrTokenExtensions.GetTrivia(
                                        memberAccessExpression,
                                        invocationExpression.ArgumentList.OpenParenToken,
                                        invocationExpression.ArgumentList.CloseParenToken,
                                        expresisonStatement.SemicolonToken));
                                return true;
                            }

                            break;
                    }

                    break;

                case SyntaxKind.YieldReturnStatement:
                    var memberDeclarationSymbol = semanticModel.GetEnclosingSymbol(
                        forEachInfo.ForEachStatement.SpanStart, cancellationToken);

                    // Using Single() is valid even for partial methods.
                    var memberDeclarationSyntax = memberDeclarationSymbol.DeclaringSyntaxReferences.Single().GetSyntax();

                    var yieldStatementsCount = memberDeclarationSyntax.DescendantNodes().OfType<YieldStatementSyntax>()
                        // Exclude yield statements from nested local functions.
                        .Where(statement => semanticModel.GetEnclosingSymbol(
                            statement.SpanStart, cancellationToken) == memberDeclarationSymbol).Count();

                    if (forEachInfo.ForEachStatement.IsParentKind(SyntaxKind.Block) &&
                        forEachInfo.ForEachStatement.Parent.Parent == memberDeclarationSyntax)
                    {
                        // Check that 
                        // a. There are either just a single 'yield return' or 'yield return' with 'yield break' just after.
                        // b. Those foreach and 'yield break' (if exists) are last statements in the method (do not count local function declaration statements).
                        var statementsOnBlockWithForEach = ((BlockSyntax)forEachInfo.ForEachStatement.Parent).Statements
                            .Where(statement => statement.Kind() != SyntaxKind.LocalFunctionStatement).ToArray();
                        var lastNonLocalFunctionStatement = statementsOnBlockWithForEach.Last();
                        if (yieldStatementsCount == 1 && lastNonLocalFunctionStatement == forEachInfo.ForEachStatement)
                        {
                            converter = new YieldReturnConverter(
                                forEachInfo,
                                (YieldStatementSyntax)statementCannotBeConverted,
                                yieldBreakStatement: null);
                            return true;
                        }

                        // foreach()
                        // {
                        //    yield return ...;
                        // }
                        // yield break;
                        // end of member
                        if (yieldStatementsCount == 2 &&
                            lastNonLocalFunctionStatement.Kind() == SyntaxKind.YieldBreakStatement &&
                            !lastNonLocalFunctionStatement.ContainsDirectives &&
                            statementsOnBlockWithForEach[statementsOnBlockWithForEach.Length - 2] == forEachInfo.ForEachStatement)
                        {
                            // This removes the yield break.
                            converter = new YieldReturnConverter(
                                forEachInfo,
                                (YieldStatementSyntax)statementCannotBeConverted,
                                yieldBreakStatement: (YieldStatementSyntax)lastNonLocalFunctionStatement);
                            return true;
                        }
                    }

                    break;
            }

            converter = default;
            return false;
        }

        protected override SyntaxNode AddLinqUsing(
            IConverter<ForEachStatementSyntax, StatementSyntax> converter,
            SemanticModel semanticModel,
            SyntaxNode root)
        {
            var namespaces = semanticModel.GetUsingNamespacesInScope(converter.ForEachInfo.ForEachStatement);
            if (!namespaces.Any(namespaceSymbol => namespaceSymbol.Name == nameof(System.Linq) &&
                namespaceSymbol.ContainingNamespace.Name == nameof(System)) &&
                root is CompilationUnitSyntax compilationUnit)
            {
                return compilationUnit.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Linq")));
            }

            return root;
        }

        internal static bool TypeSymbolOptIsList(ITypeSymbol typeSymbol, SemanticModel semanticModel)
            => Equals(typeSymbol?.OriginalDefinition, semanticModel.Compilation.GetTypeByMetadataName(typeof(List<>).FullName));
    }
}
