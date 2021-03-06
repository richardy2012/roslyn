' Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System.Collections.Immutable
Imports System.Threading
Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.Completion
Imports Microsoft.CodeAnalysis.Completion.Providers
Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic.Symbols
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax
Imports Microsoft.CodeAnalysis.LanguageServices
Imports Microsoft.CodeAnalysis.VisualBasic.Extensions.ContextQuery
Imports Microsoft.CodeAnalysis.Options
Imports Microsoft.CodeAnalysis.Shared.Extensions.ContextQuery

Namespace Microsoft.CodeAnalysis.VisualBasic.Completion.Providers
    Partial Friend Class ImplementsClauseCompletionProvider
        Inherits AbstractSymbolCompletionProvider

        Friend Overrides Function IsInsertionTrigger(text As SourceText, characterPosition As Integer, options As OptionSet) As Boolean
            Return CompletionUtilities.IsDefaultTriggerCharacter(text, characterPosition, options)
        End Function

        Protected Overrides Function IsExclusive() As Boolean
            Return True
        End Function

        Protected Overrides Function GetSymbolsWorker(context As SyntaxContext, position As Integer, options As OptionSet, cancellationToken As CancellationToken) As Task(Of IEnumerable(Of ISymbol))
            If context.TargetToken.Kind = SyntaxKind.None Then
                Return SpecializedTasks.EmptyEnumerable(Of ISymbol)()
            End If

            If context.SyntaxTree.IsInNonUserCode(position, cancellationToken) OrElse
                context.SyntaxTree.IsInSkippedText(position, cancellationToken) Then
                Return SpecializedTasks.EmptyEnumerable(Of ISymbol)()
            End If

            ' We only care about Methods, Properties, and Events
            Dim memberKindKeyword As SyntaxKind = Nothing
            Dim methodDeclaration = context.TargetToken.GetAncestor(Of MethodStatementSyntax)()
            If methodDeclaration IsNot Nothing Then
                memberKindKeyword = methodDeclaration.DeclarationKeyword.Kind
            End If
            Dim propertyDeclaration = context.TargetToken.GetAncestor(Of PropertyStatementSyntax)()
            If propertyDeclaration IsNot Nothing Then
                memberKindKeyword = propertyDeclaration.DeclarationKeyword.Kind
            End If
            Dim eventDeclaration = context.TargetToken.GetAncestor(Of EventStatementSyntax)()
            If eventDeclaration IsNot Nothing Then
                memberKindKeyword = eventDeclaration.DeclarationKeyword.Kind
            End If

            ' We couldn't find a declaration. Bail.
            If memberKindKeyword = Nothing Then
                Return SpecializedTasks.EmptyEnumerable(Of ISymbol)()
            End If

            Dim result As IEnumerable(Of ISymbol) = Nothing

            ' Valid positions: Immediately after 'Implements, after  ., or after a ,
            If context.TargetToken.Kind = SyntaxKind.ImplementsKeyword AndAlso context.TargetToken.Parent.IsKind(SyntaxKind.ImplementsClause) Then
                result = GetInterfacesAndContainers(position, context.TargetToken.Parent, context.SemanticModel, memberKindKeyword, cancellationToken)
            End If

            If context.TargetToken.Kind = SyntaxKind.CommaToken AndAlso context.TargetToken.Parent.IsKind(SyntaxKind.ImplementsClause) Then
                result = GetInterfacesAndContainers(position, context.TargetToken.Parent, context.SemanticModel, memberKindKeyword, cancellationToken)
            End If

            If context.TargetToken.IsKindOrHasMatchingText(SyntaxKind.DotToken) AndAlso WalkUpQualifiedNames(context.TargetToken) Then
                result = GetDottedMembers(position, DirectCast(context.TargetToken.Parent, QualifiedNameSyntax), context.SemanticModel, memberKindKeyword, cancellationToken)
            End If

            If result IsNot Nothing Then
                Return Task.FromResult(result.Where(Function(s) MatchesMemberKind(s, memberKindKeyword)))
            End If

            Return SpecializedTasks.EmptyEnumerable(Of ISymbol)()
        End Function

        Private Function MatchesMemberKind(symbol As ISymbol, memberKindKeyword As SyntaxKind) As Boolean
            If symbol.Kind = SymbolKind.Alias Then
                symbol = DirectCast(symbol, IAliasSymbol).Target
            End If

            If TypeOf symbol Is INamespaceOrTypeSymbol Then
                Return True
            End If

            Dim method = TryCast(symbol, IMethodSymbol)
            If method IsNot Nothing Then
                If Not method.ReturnsVoid Then
                    Return memberKindKeyword = SyntaxKind.FunctionKeyword
                End If
                Return memberKindKeyword = SyntaxKind.SubKeyword
            End If

            Dim [property] = TryCast(symbol, IPropertySymbol)
            If [property] IsNot Nothing Then
                Return memberKindKeyword = SyntaxKind.PropertyKeyword
            End If

            Return memberKindKeyword = SyntaxKind.EventKeyword
        End Function

        Private Function GetDottedMembers(position As Integer, qualifiedName As QualifiedNameSyntax, semanticModel As SemanticModel, memberKindKeyword As SyntaxKind, cancellationToken As CancellationToken) As IEnumerable(Of ISymbol)
            Dim containingType = semanticModel.GetEnclosingNamedType(position, cancellationToken)
            If containingType Is Nothing Then
                Return Nothing
            End If

            Dim unimplementedInterfacesAndMembers = From item In containingType.GetAllUnimplementedMembersInThis(containingType.Interfaces, cancellationToken)
                                                    Select New With {.interface = item.Item1, .members = item.Item2.Where(Function(s) MatchesMemberKind(s, memberKindKeyword))}


            Dim interfaces = unimplementedInterfacesAndMembers.Where(Function(i) i.members.Any()) _
                                                                .Select(Function(i) i.interface)

            Dim members = unimplementedInterfacesAndMembers.SelectMany(Function(i) i.members)

            Dim interfacesAndContainers = New HashSet(Of ISymbol)(interfaces)
            For Each [interface] In interfaces
                AddAliasesAndContainers([interface], interfacesAndContainers, Nothing, Nothing)
            Next

            Dim namespaces = interfacesAndContainers.OfType(Of INamespaceSymbol)()

            Dim left = qualifiedName.Left

            Dim leftHandTypeInfo = semanticModel.GetTypeInfo(left, cancellationToken)
            Dim leftHandBinding = semanticModel.GetSymbolInfo(left, cancellationToken)

            Dim container As INamespaceOrTypeSymbol = leftHandTypeInfo.Type
            If container Is Nothing OrElse container.IsErrorType Then
                container = TryCast(leftHandBinding.Symbol, INamespaceOrTypeSymbol)
            End If

            If container Is Nothing Then
                container = TryCast(leftHandBinding.CandidateSymbols.FirstOrDefault(), INamespaceOrTypeSymbol)
            End If

            If container Is Nothing Then
                Return Nothing
            End If
            Dim symbols = semanticModel.LookupSymbols(position, container)

            Return New HashSet(Of ISymbol)(symbols.ToArray() _
                                           .Where(Function(s As ISymbol) interfacesAndContainers.Contains(s, SymbolEquivalenceComparer.Instance) OrElse
                                                      (TypeOf (s) Is INamespaceSymbol AndAlso namespaces.Contains(TryCast(s, INamespaceSymbol), INamespaceSymbolExtensions.EqualityComparer)) OrElse
                                                      members.Contains(s)))

        End Function

        Private Function interfaceMemberGetter([interface] As ITypeSymbol, within As ISymbol) As ImmutableArray(Of ISymbol)
            Return ImmutableArray.CreateRange(Of ISymbol)([interface].AllInterfaces.SelectMany(Function(i) i.GetMembers()).Where(Function(s) s.IsAccessibleWithin(within))) _
                .AddRange([interface].GetMembers())
        End Function

        Private Function GetInterfacesAndContainers(position As Integer, node As SyntaxNode, semanticModel As SemanticModel, kind As SyntaxKind, cancellationToken As CancellationToken) As IEnumerable(Of ISymbol)
            Dim containingType = semanticModel.GetEnclosingNamedType(position, cancellationToken)
            If containingType Is Nothing Then
                Return Nothing
            End If

            Dim interfaceWithUnimplementedMembers = containingType.GetAllUnimplementedMembersInThis(containingType.Interfaces, AddressOf interfaceMemberGetter, cancellationToken) _
                                                .Where(Function(i) i.Item2.Any(Function(s) MatchesMemberKind(s, kind))) _
                                                .Select(Function(i) i.Item1)

            Dim interfacesAndContainers = New HashSet(Of ISymbol)(interfaceWithUnimplementedMembers)
            For Each i In interfaceWithUnimplementedMembers
                AddAliasesAndContainers(i, interfacesAndContainers, node, semanticModel)
            Next

            Dim symbols = semanticModel.LookupSymbols(position)

            Dim result = TryAddGlobalTo(interfacesAndContainers.Intersect(symbols.ToArray()))

            ' Even if there's not anything left to implement, we'll show the list of interfaces, 
            ' the global namespace, and the project root namespace (if any), as long as the class implements something.
            If Not result.Any() AndAlso containingType.Interfaces.Any() Then
                Dim defaultListing = New List(Of ISymbol)(containingType.Interfaces.ToArray())
                defaultListing.Add(semanticModel.Compilation.GlobalNamespace)
                If containingType.ContainingNamespace IsNot Nothing Then
                    defaultListing.Add(containingType.ContainingNamespace)
                    AddAliasesAndContainers(containingType.ContainingNamespace, defaultListing, node, semanticModel)
                End If
                Return defaultListing
            End If

            Return result
        End Function

        Private Sub AddAliasesAndContainers(symbol As ISymbol, interfacesAndContainers As ICollection(Of ISymbol), node As SyntaxNode, semanticModel As SemanticModel)
            ' Add aliases, if any for 'symbol'
            AddAlias(symbol, interfacesAndContainers, node, semanticModel)

            ' Add containers for 'symbol'
            Dim containingSymbol = symbol.ContainingSymbol
            If containingSymbol IsNot Nothing AndAlso Not interfacesAndContainers.Contains(containingSymbol) Then
                interfacesAndContainers.Add(containingSymbol)

                ' Add aliases, if any for 'containingSymbol'
                AddAlias(containingSymbol, interfacesAndContainers, node, semanticModel)

                If Not IsGlobal(containingSymbol) Then
                    AddAliasesAndContainers(containingSymbol, interfacesAndContainers, node, semanticModel)
                End If
            End If
        End Sub

        Private Shared Sub AddAlias(symbol As ISymbol, interfacesAndContainers As ICollection(Of ISymbol), node As SyntaxNode, semanticModel As SemanticModel)
            If node IsNot Nothing AndAlso semanticModel IsNot Nothing AndAlso TypeOf symbol Is INamespaceOrTypeSymbol Then
                Dim aliasSymbol = DirectCast(symbol, INamespaceOrTypeSymbol).GetAliasForSymbol(node, semanticModel)
                If aliasSymbol IsNot Nothing AndAlso Not interfacesAndContainers.Contains(aliasSymbol) Then
                    interfacesAndContainers.Add(aliasSymbol)
                End If
            End If
        End Sub

        Private Function IsGlobal(containingSymbol As ISymbol) As Boolean
            Dim [namespace] = TryCast(containingSymbol, INamespaceSymbol)
            Return [namespace] IsNot Nothing AndAlso [namespace].IsGlobalNamespace
        End Function

        Private Function TryAddGlobalTo(symbols As IEnumerable(Of ISymbol)) As IEnumerable(Of ISymbol)
            Dim withGlobalContainer = symbols.FirstOrDefault(Function(s) s.ContainingNamespace.IsGlobalNamespace)
            If withGlobalContainer IsNot Nothing Then
                Return symbols.Concat(SpecializedCollections.SingletonEnumerable(withGlobalContainer.ContainingNamespace))
            End If

            Return symbols
        End Function

        Private Function WalkUpQualifiedNames(token As SyntaxToken) As Boolean
            Dim parent = token.Parent
            While parent IsNot Nothing AndAlso parent.IsKind(SyntaxKind.QualifiedName)
                parent = parent.Parent
            End While

            Return parent IsNot Nothing AndAlso parent.IsKind(SyntaxKind.ImplementsClause)
        End Function

        Protected Overrides Function GetDisplayAndInsertionText(symbol As ISymbol, context As SyntaxContext) As ValueTuple(Of String, String)
            If IsGlobal(symbol) Then
                Return ValueTuple.Create("Global", "Global")
            End If

            Dim displayText As String = Nothing
            Dim insertionText As String = Nothing

            If symbol.MatchesKind(SymbolKind.NamedType) AndAlso symbol.GetAllTypeArguments().Any() Then
                displayText = symbol.ToMinimalDisplayString(context.SemanticModel, context.Position)
                insertionText = displayText
            Else
                Dim displayAndInsertionText = CompletionUtilities.GetDisplayAndInsertionText(symbol, context)

                displayText = displayAndInsertionText.Item1
                insertionText = displayAndInsertionText.Item2
            End If

            Return ValueTuple.Create(displayText, insertionText)
        End Function

        Protected Overrides Async Function CreateContext(document As Document, position As Integer, cancellationToken As CancellationToken) As Task(Of SyntaxContext)
            Dim semanticModel = Await document.GetSemanticModelForSpanAsync(New TextSpan(position, 0), cancellationToken).ConfigureAwait(False)
            Return Await VisualBasicSyntaxContext.CreateContextAsync(document.Project.Solution.Workspace, semanticModel, position, cancellationToken).ConfigureAwait(False)
        End Function

        Protected Overrides Function GetCompletionItemRules(symbols As IReadOnlyList(Of ISymbol), context As SyntaxContext) As CompletionItemRules
            Return CompletionItemRules.Default
        End Function

        Protected Overrides Function GetInsertionText(
                item As CompletionItem, symbol As ISymbol, context As SyntaxContext, ch As Char) As String
            Return CompletionUtilities.GetInsertionTextAtInsertionTime(symbol, context, ch)
        End Function
    End Class
End Namespace