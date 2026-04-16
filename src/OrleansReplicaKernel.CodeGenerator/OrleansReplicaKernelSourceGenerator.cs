using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace OrleansReplicaKernel.CodeGenerator;

[Generator(LanguageNames.CSharp)]
public sealed class OrleansReplicaKernelSourceGenerator : IIncrementalGenerator
{
    private const string AlwaysInterleaveAttributeName = "OrleansReplicaKernel.CodeGeneration.AlwaysInterleaveAttribute";
    private const string PreferLocalPlacementAttributeName = "OrleansReplicaKernel.CodeGeneration.PreferLocalPlacementAttribute";
    private const string CollectionAgeLimitAttributeName = "OrleansReplicaKernel.CodeGeneration.CollectionAgeLimitAttribute";
    private const string GrainInterfaceVersionAttributeName = "OrleansReplicaKernel.Versioning.GrainInterfaceVersionAttribute";

    private static readonly DiagnosticDescriptor MissingGrainImplementationDescriptor = new(
        id: "ORKGEN001",
        title: "Missing grain implementation",
        messageFormat: "Grain contract '{0}' requires a concrete implementation named '{1}' in the same compilation",
        category: "OrleansReplicaKernel.SourceGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedMethodShapeDescriptor = new(
        id: "ORKGEN002",
        title: "Unsupported grain method shape",
        messageFormat: "Method '{0}' is not supported by the current OrleansReplicaKernel source generator: {1}",
        category: "OrleansReplicaKernel.SourceGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedOverloadDescriptor = new(
        id: "ORKGEN003",
        title: "Overloaded contract methods are not supported",
        messageFormat: "Contract '{0}' contains overloaded method '{1}', which is not supported by the current OrleansReplicaKernel source generator",
        category: "OrleansReplicaKernel.SourceGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly SymbolDisplayFormat TypeDisplayFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var generation = context.CompilationProvider.Select(static (compilation, cancellationToken) =>
            GenerationResult.Create(compilation, cancellationToken));

        context.RegisterSourceOutput(generation, static (productionContext, result) =>
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                productionContext.ReportDiagnostic(diagnostic);
            }

            if (!string.IsNullOrWhiteSpace(result.Source))
            {
                productionContext.AddSource(
                    "OrleansReplicaKernel.SourceGenerated.g.cs",
                    SourceText.From(result.Source, Encoding.UTF8));
            }
        });
    }

    private sealed record GenerationResult(string Source, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static GenerationResult Create(Compilation compilation, CancellationToken cancellationToken)
        {
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            var model = GenerationModel.Create(compilation, diagnostics, cancellationToken);
            return new GenerationResult(model.EmitSource(), diagnostics.ToImmutable());
        }
    }

    private sealed record GenerationModel(
        ImmutableArray<GrainContractModel> GrainContracts,
        ImmutableArray<ObjectReferenceContractModel> ObjectReferenceContracts)
    {
        public static GenerationModel Create(
            Compilation compilation,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
        {
            var allTypes = EnumerateTypes(compilation.Assembly.GlobalNamespace)
                .OrderBy(static type => type.ToDisplayString(), StringComparer.Ordinal)
                .ToArray();
            var grainContracts = ImmutableArray.CreateBuilder<GrainContractModel>();
            var grainContractSymbols = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            foreach (var contract in allTypes.Where(IsPotentialGrainContract))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var model = GrainContractModel.TryCreate(compilation, contract, allTypes, diagnostics);
                if (model is null)
                {
                    continue;
                }

                grainContracts.Add(model);
                grainContractSymbols.Add(contract);
            }

            var objectReferenceContracts = ImmutableArray.CreateBuilder<ObjectReferenceContractModel>();
            foreach (var contract in grainContracts
                         .SelectMany(static contract => contract.Methods)
                         .SelectMany(static method => method.Parameters)
                         .Where(static parameter => parameter.IsObjectReference)
                         .Select(static parameter => parameter.ContractTypeSymbol)
                         .OfType<INamedTypeSymbol>()
                         .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
                         .OrderBy(static symbol => symbol.ToDisplayString(), StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (grainContractSymbols.Contains(contract))
                {
                    continue;
                }

                var model = ObjectReferenceContractModel.TryCreate(compilation, contract, diagnostics);
                if (model is not null)
                {
                    objectReferenceContracts.Add(model);
                }
            }

            return new GenerationModel(grainContracts.ToImmutable(), objectReferenceContracts.ToImmutable());
        }

        public string EmitSource()
        {
            if (GrainContracts.Length == 0 && ObjectReferenceContracts.Length == 0)
            {
                return string.Empty;
            }

            var byNamespace = GrainContracts
                .Select(static contract => contract.Namespace)
                .Concat(ObjectReferenceContracts.Select(static contract => contract.Namespace))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static item => item, StringComparer.Ordinal)
                .ToArray();
            var builder = new StringBuilder();

            builder.AppendLine("// <auto-generated />");
            builder.AppendLine("#nullable enable");
            builder.AppendLine("using System;");
            builder.AppendLine("using System.Threading;");
            builder.AppendLine("using System.Threading.Tasks;");
            builder.AppendLine("using OrleansReplicaKernel.Identity;");
            builder.AppendLine("using OrleansReplicaKernel.Invocation;");
            builder.AppendLine("using OrleansReplicaKernel.Runtime;");
            builder.AppendLine("using OrleansReplicaKernel.Serialization;");
            builder.AppendLine();

            foreach (var currentNamespace in byNamespace)
            {
                builder.Append("namespace ").AppendLine(currentNamespace);
                builder.AppendLine("{");
                builder.AppendLine();

                foreach (var grainContract in GrainContracts
                             .Where(contract => string.Equals(contract.Namespace, currentNamespace, StringComparison.Ordinal))
                             .OrderBy(static contract => contract.ContractName, StringComparer.Ordinal))
                {
                    grainContract.AppendSource(builder);
                }

                foreach (var objectReferenceContract in ObjectReferenceContracts
                             .Where(contract => string.Equals(contract.Namespace, currentNamespace, StringComparison.Ordinal))
                             .OrderBy(static contract => contract.ContractName, StringComparer.Ordinal))
                {
                    objectReferenceContract.AppendSource(builder);
                }

                builder.AppendLine("}");
                builder.AppendLine();
            }

            return builder.ToString();
        }
    }

    private sealed record GrainContractModel(
        string Namespace,
        string ContractName,
        string ContractTypeName,
        string ImplementationName,
        string GrainType,
        string ReferenceName,
        string InvokablePrefix,
        string AliasPrefix,
        int? CollectionAgeLimitMilliseconds,
        bool PreferLocalPlacement,
        ImmutableArray<string> InterleavableMethods,
        ImmutableArray<MethodModel> Methods)
    {
        public static GrainContractModel? TryCreate(
            Compilation compilation,
            INamedTypeSymbol contract,
            IReadOnlyList<INamedTypeSymbol> allTypes,
            ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            var implementationName = StripLeadingInterfacePrefix(contract.Name);
            var implementation = FindImplementation(contract, implementationName, allTypes);
            if (implementation is null)
            {
                diagnostics.Add(Diagnostic.Create(
                    MissingGrainImplementationDescriptor,
                    contract.Locations.FirstOrDefault(),
                    contract.ToDisplayString(),
                    implementationName));
                return null;
            }

            var methods = GetAllInterfaceMethods(contract);
            var overloadedMethod = methods
                .GroupBy(static method => method.Name, StringComparer.Ordinal)
                .FirstOrDefault(static group => group.Count() > 1);
            if (overloadedMethod is not null)
            {
                diagnostics.Add(Diagnostic.Create(
                    UnsupportedOverloadDescriptor,
                    overloadedMethod.First().Locations.FirstOrDefault(),
                    contract.ToDisplayString(),
                    overloadedMethod.Key));
                return null;
            }

            var methodModels = ImmutableArray.CreateBuilder<MethodModel>();
            foreach (var method in methods)
            {
                var implementationMethod = implementation.FindImplementationForInterfaceMember(method);
                var model = MethodModel.TryCreate(compilation, method, implementationMethod, diagnostics);
                if (model is null)
                {
                    return null;
                }

                methodModels.Add(model);
            }

            var collectionAgeLimit = ResolveCollectionAgeLimitMilliseconds(implementation);
            var preferLocalPlacement = HasAttribute(implementation, PreferLocalPlacementAttributeName);
            var interleavableMethods = methodModels
                .Where((methodModel, index) =>
                {
                    var method = methods[index];
                    var implementationMethod = implementation.FindImplementationForInterfaceMember(method);
                    return HasAttribute(method, AlwaysInterleaveAttributeName)
                           || (implementationMethod is not null
                               && HasAttribute(implementationMethod, AlwaysInterleaveAttributeName));
                })
                .Select(static method => method.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static item => item, StringComparer.Ordinal)
                .ToImmutableArray();
            var baseName = TrimSuffix(implementationName, "Grain");
            var grainType = ToCamelCase(baseName);
            var namespaceName = GetNamespace(contract);

            return new GrainContractModel(
                namespaceName,
                contract.Name,
                contract.ToDisplayString(TypeDisplayFormat),
                implementationName,
                grainType,
                implementationName + "Reference",
                baseName,
                GetAliasPrefix(namespaceName),
                collectionAgeLimit,
                preferLocalPlacement,
                interleavableMethods,
                methodModels.ToImmutable());
        }

        public void AppendSource(StringBuilder builder)
        {
            builder.Append("[GeneratedGrainReference(typeof(")
                .Append(ContractTypeName)
                .Append("), \"")
                .Append(GrainType)
                .AppendLine("\")]");
            builder.Append("public sealed class ")
                .Append(ReferenceName)
                .Append(" : ")
                .Append(ContractName)
                .AppendLine();
            builder.AppendLine("{");
            builder.AppendLine("    private readonly IInvocationRuntime _runtime;");
            builder.AppendLine("    private readonly GrainId _grainId;");
            builder.AppendLine();
            builder.Append("    public ")
                .Append(ReferenceName)
                .AppendLine("(IInvocationRuntime runtime, GrainId grainId)");
            builder.AppendLine("    {");
            builder.AppendLine("        _runtime = runtime;");
            builder.AppendLine("        _grainId = grainId;");
            builder.AppendLine("    }");
            builder.AppendLine();

            foreach (var method in Methods)
            {
                method.AppendReferenceMethod(builder);
            }

            builder.AppendLine("}");
            builder.AppendLine();

            builder.Append("[GeneratedGrainImplementation(\"")
                .Append(GrainType)
                .Append("\"");
            if (CollectionAgeLimitMilliseconds.HasValue)
            {
                builder.Append(", CollectionAgeLimitMilliseconds = ")
                    .Append(CollectionAgeLimitMilliseconds.Value);
            }

            if (PreferLocalPlacement)
            {
                builder.Append(", PreferLocalPlacement = true");
            }

            if (InterleavableMethods.Length > 0)
            {
                builder.Append(", InterleavableMethods = new[] { ");
                for (var index = 0; index < InterleavableMethods.Length; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append("nameof(")
                        .Append(ContractName)
                        .Append('.')
                        .Append(InterleavableMethods[index])
                        .Append(')');
                }

                builder.Append(" }");
            }

            builder.AppendLine(")]");
            builder.Append("public sealed partial class ")
                .Append(ImplementationName)
                .AppendLine();
            builder.AppendLine("{");
            builder.AppendLine("}");
            builder.AppendLine();

            foreach (var method in Methods)
            {
                method.AppendInvokableSource(builder, ContractName, InvokablePrefix, AliasPrefix, GrainType);
            }
        }
    }

    private sealed record ObjectReferenceContractModel(
        string Namespace,
        string ContractName,
        string ContractTypeName,
        string ReferenceName,
        string InvokablePrefix,
        string AliasPrefix,
        string ContractAlias,
        ImmutableArray<MethodModel> Methods)
    {
        public static ObjectReferenceContractModel? TryCreate(
            Compilation compilation,
            INamedTypeSymbol contract,
            ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            var methods = GetAllInterfaceMethods(contract);
            var overloadedMethod = methods
                .GroupBy(static method => method.Name, StringComparer.Ordinal)
                .FirstOrDefault(static group => group.Count() > 1);
            if (overloadedMethod is not null)
            {
                diagnostics.Add(Diagnostic.Create(
                    UnsupportedOverloadDescriptor,
                    overloadedMethod.First().Locations.FirstOrDefault(),
                    contract.ToDisplayString(),
                    overloadedMethod.Key));
                return null;
            }

            var methodModels = ImmutableArray.CreateBuilder<MethodModel>();
            foreach (var method in methods)
            {
                var model = MethodModel.TryCreate(compilation, method, implementationMethod: null, diagnostics);
                if (model is null)
                {
                    return null;
                }

                methodModels.Add(model);
            }

            var contractBaseName = StripLeadingInterfacePrefix(contract.Name);
            var namespaceName = GetNamespace(contract);
            return new ObjectReferenceContractModel(
                namespaceName,
                contract.Name,
                contract.ToDisplayString(TypeDisplayFormat),
                contractBaseName + "Reference",
                contractBaseName,
                GetAliasPrefix(namespaceName),
                ToKebabCase(contractBaseName),
                methodModels.ToImmutable());
        }

        public void AppendSource(StringBuilder builder)
        {
            builder.Append("[GeneratedObjectReference(typeof(")
                .Append(ContractTypeName)
                .AppendLine("))]");
            builder.Append("public sealed class ")
                .Append(ReferenceName)
                .Append(" : ")
                .Append(ContractName)
                .AppendLine(", IObjectReference");
            builder.AppendLine("{");
            builder.AppendLine("    private readonly IInvocationRuntime _fallbackRuntime;");
            builder.AppendLine("    private readonly GrainId _grainId;");
            builder.AppendLine();
            builder.Append("    public ")
                .Append(ReferenceName)
                .AppendLine("(IInvocationRuntime fallbackRuntime, GrainId grainId)");
            builder.AppendLine("    {");
            builder.AppendLine("        _fallbackRuntime = fallbackRuntime;");
            builder.AppendLine("        _grainId = grainId;");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    public GrainId GrainId => _grainId;");
            builder.AppendLine();

            foreach (var method in Methods)
            {
                method.AppendObjectReferenceMethod(builder);
            }

            builder.AppendLine("}");
            builder.AppendLine();

            foreach (var method in Methods)
            {
                method.AppendInvokableSource(builder, ContractName, InvokablePrefix, AliasPrefix, ContractAlias);
            }
        }
    }

    private sealed record MethodModel(
        string Name,
        string MethodStem,
        string InvokableName,
        string CodecName,
        ReturnShape ReturnShape,
        string? ResultTypeName,
        ImmutableArray<ParameterModel> Parameters,
        bool HasCancellationToken,
        string InterfaceCompatibilityFamily,
        int InterfaceVersion,
        string MethodAlias)
    {
        public static MethodModel? TryCreate(
            Compilation compilation,
            IMethodSymbol method,
            ISymbol? implementationMethod,
            ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            if (method.TypeParameters.Length > 0)
            {
                diagnostics.Add(Diagnostic.Create(
                    UnsupportedMethodShapeDescriptor,
                    method.Locations.FirstOrDefault(),
                    method.ToDisplayString(),
                    "generic methods are not supported"));
                return null;
            }

            if (!TryGetReturnShape(compilation, method, out var returnShape, out var resultTypeName))
            {
                diagnostics.Add(Diagnostic.Create(
                    UnsupportedMethodShapeDescriptor,
                    method.Locations.FirstOrDefault(),
                    method.ToDisplayString(),
                    "only Task, Task<T>, ValueTask, and ValueTask<T> return types are supported"));
                return null;
            }

            var cancellationTokenSymbol = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");
            var hasCancellationToken = false;
            var parameters = ImmutableArray.CreateBuilder<ParameterModel>();
            for (var index = 0; index < method.Parameters.Length; index++)
            {
                var parameter = method.Parameters[index];
                if (parameter.RefKind != RefKind.None)
                {
                    diagnostics.Add(Diagnostic.Create(
                        UnsupportedMethodShapeDescriptor,
                        parameter.Locations.FirstOrDefault(),
                        method.ToDisplayString(),
                        "ref, in, and out parameters are not supported"));
                    return null;
                }

                if (cancellationTokenSymbol is not null
                    && SymbolEqualityComparer.Default.Equals(parameter.Type, cancellationTokenSymbol))
                {
                    if (index != method.Parameters.Length - 1 || hasCancellationToken)
                    {
                        diagnostics.Add(Diagnostic.Create(
                            UnsupportedMethodShapeDescriptor,
                            parameter.Locations.FirstOrDefault(),
                            method.ToDisplayString(),
                            "CancellationToken must appear at most once and only as the final parameter"));
                        return null;
                    }

                    hasCancellationToken = true;
                    continue;
                }

                if (parameter.Type is INamedTypeSymbol namedType
                    && namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                {
                    diagnostics.Add(Diagnostic.Create(
                        UnsupportedMethodShapeDescriptor,
                        parameter.Locations.FirstOrDefault(),
                        method.ToDisplayString(),
                        "nullable value type parameters are not supported yet"));
                    return null;
                }

                if (parameter.NullableAnnotation == NullableAnnotation.Annotated)
                {
                    diagnostics.Add(Diagnostic.Create(
                        UnsupportedMethodShapeDescriptor,
                        parameter.Locations.FirstOrDefault(),
                        method.ToDisplayString(),
                        "nullable reference type parameters are not supported yet"));
                    return null;
                }

                var isObjectReference = parameter.Type.TypeKind == TypeKind.Interface
                    && parameter.Type is INamedTypeSymbol paramInterfaceType
                    && !IsPotentialGrainContract(paramInterfaceType)
                    && IsPotentialObjectReferenceContract(paramInterfaceType);
                parameters.Add(ParameterModel.Create(parameter, isObjectReference));
            }

            var methodStem = TrimAsyncSuffix(method.Name);
            var contractPrefix = StripLeadingInterfacePrefix(method.ContainingType.Name);
            if (method.ContainingType.Name.EndsWith("Grain", StringComparison.Ordinal))
            {
                contractPrefix = TrimSuffix(contractPrefix, "Grain");
            }

            if (implementationMethod is IMethodSymbol implementationMethodSymbol
                && implementationMethodSymbol.Name != method.Name)
            {
                diagnostics.Add(Diagnostic.Create(
                    UnsupportedMethodShapeDescriptor,
                    implementationMethodSymbol.Locations.FirstOrDefault(),
                    method.ToDisplayString(),
                    "explicit interface implementations are not supported"));
                return null;
            }

            return new MethodModel(
                method.Name,
                methodStem,
                contractPrefix + methodStem + "Invokable",
                contractPrefix + methodStem + "InvokableCodec",
                returnShape,
                resultTypeName,
                parameters.ToImmutable(),
                hasCancellationToken,
                ResolveInterfaceCompatibilityFamily(method.ContainingType),
                ResolveInterfaceVersion(method.ContainingType),
                ToKebabCase(methodStem));
        }

        public void AppendReferenceMethod(StringBuilder builder)
        {
            builder.Append("    public ")
                .Append(GetPublicReturnTypeName())
                .Append(' ')
                .Append(Name)
                .Append('(')
                .Append(GetMethodSignature())
                .AppendLine(")");
            builder.AppendLine("    {");
            builder.Append("        var invokable = new ")
                .Append(InvokableName)
                .Append('(')
                .Append(string.Join(", ", Parameters.Select(static parameter => parameter.Name)))
                .AppendLine(");");
            builder.Append("        return ")
                .Append(GetReferenceReturnExpression("_runtime"))
                .AppendLine(";");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        public void AppendObjectReferenceMethod(StringBuilder builder)
        {
            builder.Append("    public ")
                .Append(GetPublicReturnTypeName())
                .Append(' ')
                .Append(Name)
                .Append('(')
                .Append(GetMethodSignature())
                .AppendLine(")");
            builder.AppendLine("    {");
            builder.AppendLine("        var runtime = ActivationExecutionContext.CurrentRuntime ?? _fallbackRuntime;");
            builder.Append("        var invokable = new ")
                .Append(InvokableName)
                .Append('(')
                .Append(string.Join(", ", Parameters.Select(static parameter => parameter.Name)))
                .AppendLine(");");
            builder.Append("        return ")
                .Append(GetReferenceReturnExpression("runtime"))
                .AppendLine(";");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        public void AppendInvokableSource(
            StringBuilder builder,
            string contractName,
            string invokablePrefix,
            string aliasPrefix,
            string contractAlias)
        {
            builder.Append("internal sealed class ")
                .Append(InvokableName)
                .AppendLine(" : IInvokable");
            builder.AppendLine("{");

            foreach (var parameter in Parameters)
            {
                builder.Append("    private readonly ")
                    .Append(parameter.StorageTypeName)
                    .Append(' ')
                    .Append(parameter.FieldName)
                    .AppendLine(";");
            }

            if (Parameters.Length > 0)
            {
                builder.AppendLine();
                builder.Append("    public ")
                    .Append(InvokableName)
                    .Append('(')
                    .Append(string.Join(", ", Parameters.Select(static parameter => parameter.GetPublicConstructorSignature())))
                    .AppendLine(")");
                builder.AppendLine("    {");
                foreach (var parameter in Parameters)
                {
                    builder.Append("        ")
                        .Append(parameter.FieldName)
                        .Append(" = ")
                        .Append(parameter.GetPublicConstructorAssignment())
                        .AppendLine(";");
                }
                builder.AppendLine("    }");

                if (Parameters.Any(static parameter => parameter.IsObjectReference))
                {
                    builder.AppendLine();
                    builder.Append("    internal ")
                        .Append(InvokableName)
                        .Append('(')
                        .Append(string.Join(", ", Parameters.Select(static parameter => parameter.GetInternalConstructorSignature())))
                        .AppendLine(")");
                    builder.AppendLine("    {");
                    foreach (var parameter in Parameters)
                    {
                        builder.Append("        ")
                            .Append(parameter.FieldName)
                            .Append(" = ")
                            .Append(parameter.Name)
                            .AppendLine(";");
                    }
                    builder.AppendLine("    }");
                }

                builder.AppendLine();
                foreach (var parameter in Parameters)
                {
                    builder.Append("    internal ")
                        .Append(parameter.StorageTypeName)
                        .Append(' ')
                        .Append(parameter.PropertyName)
                        .Append(" => ")
                        .Append(parameter.FieldName)
                        .AppendLine(";");
                }
            }

            builder.AppendLine();
            builder.Append("    public string InterfaceName => nameof(")
                .Append(contractName)
                .AppendLine(");");
            builder.Append("    public string InterfaceCompatibilityFamily => \"")
                .Append(EscapeStringLiteral(InterfaceCompatibilityFamily))
                .AppendLine("\";");
            builder.Append("    public int InterfaceVersion => ")
                .Append(InterfaceVersion)
                .AppendLine(";");
            builder.Append("    public string MethodName => nameof(")
                .Append(contractName)
                .Append('.')
                .Append(Name)
                .AppendLine(");");
            builder.AppendLine();
            builder.AppendLine("    public async ValueTask<object?> InvokeAsync(object target, CancellationToken cancellationToken)");
            builder.AppendLine("    {");
            builder.Append("        var contract = (")
                .Append(contractName)
                .AppendLine(")target;");

            if (Parameters.Any(static parameter => parameter.IsObjectReference))
            {
                builder.AppendLine("        var runtime = ActivationExecutionContext.CurrentRuntime");
                builder.AppendLine("            ?? throw new InvalidOperationException(\"No activation execution runtime is available for object reference rehydration.\");");
                builder.AppendLine("        if (runtime is not IObjectReferenceRuntime objectReferenceRuntime)");
                builder.AppendLine("        {");
                builder.AppendLine("            throw new InvalidOperationException(");
                builder.AppendLine("                $\"Runtime '{runtime.GetType().Name}' does not provide an object reference factory registry.\");");
                builder.AppendLine("        }");
                builder.AppendLine();
                foreach (var parameter in Parameters.Where(static parameter => parameter.IsObjectReference))
                {
                    builder.Append("        var ")
                        .Append(parameter.RehydratedVariableName)
                        .Append(" = ObjectReferenceSerializer.Rehydrate<")
                        .Append(parameter.SourceTypeName)
                        .Append(">(")
                        .Append(parameter.FieldName)
                        .AppendLine(", runtime, objectReferenceRuntime.ObjectReferences);");
                }
            }

            builder.Append("        ");
            AppendInvocationReturn(builder);
            builder.AppendLine();
            builder.AppendLine("    }");
            builder.AppendLine("}");
            builder.AppendLine();

            builder.Append("internal sealed class ")
                .Append(CodecName)
                .Append(" : BinaryObjectCodec<")
                .Append(InvokableName)
                .AppendLine(">");
            builder.AppendLine("{");
            builder.Append("    public override string Alias => \"")
                .Append(aliasPrefix)
                .Append(".invokable.")
                .Append(contractAlias)
                .Append('.')
                .Append(MethodAlias)
                .AppendLine("\";");
            builder.AppendLine();
            builder.Append("    protected override ")
                .Append(InvokableName)
                .AppendLine(" ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)");
            builder.AppendLine("    {");

            if (Parameters.Length == 0)
            {
                builder.AppendLine("        reader.SkipRemainingFields();");
                builder.Append("        return new ")
                    .Append(InvokableName)
                    .AppendLine("();");
            }
            else
            {
                foreach (var parameter in Parameters)
                {
                    builder.Append("        ")
                        .Append(parameter.GetReadLocalDeclaration())
                        .AppendLine(";");
                }
                builder.AppendLine();
                builder.AppendLine("        while (reader.TryReadField(out var fieldId, out var payload))");
                builder.AppendLine("        {");
                builder.AppendLine("            switch (fieldId)");
                builder.AppendLine("            {");
                foreach (var parameter in Parameters)
                {
                    builder.Append("                case ")
                        .Append(parameter.FieldId)
                        .AppendLine(":");
                    builder.Append("                    ")
                        .Append(parameter.Name)
                        .Append(" = ")
                        .Append(parameter.GetReadExpression())
                        .AppendLine(";");
                    builder.AppendLine("                    break;");
                }
                builder.AppendLine("            }");
                builder.AppendLine("        }");
                builder.AppendLine();
                builder.Append("        return new ")
                    .Append(InvokableName)
                    .Append('(')
                    .Append(string.Join(", ", Parameters.Select(static parameter => parameter.GetCodecConstructorArgument())))
                    .AppendLine(");");
            }

            builder.AppendLine("    }");
            builder.AppendLine();
            builder.Append("    protected override void WriteFields(BinaryObjectWriter writer, ")
                .Append(InvokableName)
                .AppendLine(" value, BinarySerializer serializer)");
            builder.AppendLine("    {");
            foreach (var parameter in Parameters)
            {
                builder.Append("        writer.WriteField(")
                    .Append(parameter.FieldId)
                    .Append(", value.")
                    .Append(parameter.PropertyName)
                    .AppendLine(", serializer);");
            }
            builder.AppendLine("    }");
            builder.AppendLine("}");
            builder.AppendLine();
        }

        private void AppendInvocationReturn(StringBuilder builder)
        {
            var invocationArguments = string.Join(", ", Parameters.Select(static parameter => parameter.GetInvocationArgument()).AppendIf(HasCancellationToken, "cancellationToken"));

            switch (ReturnShape)
            {
                case ReturnShape.Task:
                    builder.Append("await contract.")
                        .Append(Name)
                        .Append('(')
                        .Append(invocationArguments)
                        .AppendLine(");");
                    builder.Append("        return null;");
                    break;
                case ReturnShape.TaskOfT:
                case ReturnShape.ValueTaskOfT:
                    builder.Append("return await contract.")
                        .Append(Name)
                        .Append('(')
                        .Append(invocationArguments)
                        .Append(");");
                    break;
                case ReturnShape.ValueTask:
                    builder.Append("await contract.")
                        .Append(Name)
                        .Append('(')
                        .Append(invocationArguments)
                        .AppendLine(");");
                    builder.Append("        return null;");
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported return shape '{ReturnShape}'.");
            }
        }

        private string GetPublicReturnTypeName()
            => ReturnShape switch
            {
                ReturnShape.Task => "Task",
                ReturnShape.TaskOfT => $"Task<{ResultTypeName}>",
                ReturnShape.ValueTask => "ValueTask",
                ReturnShape.ValueTaskOfT => $"ValueTask<{ResultTypeName}>",
                _ => throw new InvalidOperationException($"Unsupported return shape '{ReturnShape}'.")
            };

        private string GetMethodSignature()
        {
            var parameters = Parameters
                .Select(static parameter => parameter.GetMethodSignature())
                .ToList();
            if (HasCancellationToken)
            {
                parameters.Add("CancellationToken cancellationToken = default");
            }

            return string.Join(", ", parameters);
        }

        private string GetReferenceReturnExpression(string runtimeExpression)
        {
            return ReturnShape switch
            {
                ReturnShape.Task => $"{runtimeExpression}.InvokeAsync<object?>(_grainId, invokable, cancellationToken).AsTask()",
                ReturnShape.TaskOfT => $"{runtimeExpression}.InvokeAsync<{ResultTypeName}>(_grainId, invokable, cancellationToken).AsTask()",
                ReturnShape.ValueTask => $"new ValueTask({runtimeExpression}.InvokeAsync<object?>(_grainId, invokable, cancellationToken).AsTask())",
                ReturnShape.ValueTaskOfT => $"{runtimeExpression}.InvokeAsync<{ResultTypeName}>(_grainId, invokable, cancellationToken)",
                _ => throw new InvalidOperationException($"Unsupported return shape '{ReturnShape}'.")
            };
        }

        private static string ResolveInterfaceCompatibilityFamily(INamedTypeSymbol contractType)
        {
            var attribute = contractType.GetAttributes()
                .FirstOrDefault(attribute =>
                    string.Equals(
                        attribute.AttributeClass?.ToDisplayString(),
                        GrainInterfaceVersionAttributeName,
                        StringComparison.Ordinal));
            if (attribute is null)
            {
                return GetDefaultInterfaceCompatibilityFamily(contractType);
            }

            return attribute.ConstructorArguments[0].Value as string
                   ?? GetDefaultInterfaceCompatibilityFamily(contractType);
        }

        private static int ResolveInterfaceVersion(INamedTypeSymbol contractType)
        {
            var attribute = contractType.GetAttributes()
                .FirstOrDefault(attribute =>
                    string.Equals(
                        attribute.AttributeClass?.ToDisplayString(),
                        GrainInterfaceVersionAttributeName,
                        StringComparison.Ordinal));
            if (attribute is null)
            {
                return 1;
            }

            return attribute.ConstructorArguments[1].Value is int version && version > 0
                ? version
                : 1;
        }

        private static string GetDefaultInterfaceCompatibilityFamily(INamedTypeSymbol contractType)
        {
            var namespaceName = GetNamespace(contractType);
            return string.IsNullOrWhiteSpace(namespaceName)
                ? contractType.Name
                : namespaceName + "." + contractType.Name;
        }
    }

    private sealed record ParameterModel(
        string Name,
        string PropertyName,
        string SourceTypeName,
        string StorageTypeName,
        string FieldName,
        bool IsObjectReference,
        bool IsValueType,
        int FieldId,
        INamedTypeSymbol? ContractTypeSymbol)
    {
        public string RehydratedVariableName => Name + "Reference";

        public static ParameterModel Create(IParameterSymbol parameter, bool isObjectReference)
        {
            var sourceTypeName = parameter.Type.ToDisplayString(TypeDisplayFormat);
            var storageTypeName = isObjectReference
                ? "ObjectReferenceData"
                : sourceTypeName;
            return new ParameterModel(
                parameter.Name,
                ToPascalCase(parameter.Name),
                sourceTypeName,
                storageTypeName,
                "_" + parameter.Name,
                isObjectReference,
                parameter.Type.IsValueType,
                parameter.Ordinal + 1,
                parameter.Type as INamedTypeSymbol);
        }

        public string GetMethodSignature() => $"{SourceTypeName} {Name}";

        public string GetPublicConstructorSignature() => $"{SourceTypeName} {Name}";

        public string GetInternalConstructorSignature() => $"{StorageTypeName} {Name}";

        public string GetPublicConstructorAssignment()
            => IsObjectReference
                ? $"ObjectReferenceSerializer.Export<{SourceTypeName}>({Name})"
                : Name;

        public string GetReadLocalDeclaration()
        {
            if (IsObjectReference)
            {
                return "ObjectReferenceData? " + Name + " = null";
            }

            if (string.Equals(SourceTypeName, "string", StringComparison.Ordinal)
                || string.Equals(SourceTypeName, "global::System.String", StringComparison.Ordinal))
            {
                return "string " + Name + " = string.Empty";
            }

            if (IsValueType)
            {
                return SourceTypeName + " " + Name + " = default";
            }

            return SourceTypeName + "? " + Name + " = null";
        }

        public string GetReadExpression()
            => IsObjectReference
                ? "serializer.Read<ObjectReferenceData>(payload)"
                : $"serializer.Read<{SourceTypeName}>(payload)";

        public string GetCodecConstructorArgument()
        {
            if (IsObjectReference || !IsValueType)
            {
                return $"{Name} ?? throw new InvalidOperationException(\"Invokable payload is missing required field '{Name}'.\")";
            }

            return Name;
        }

        public string GetInvocationArgument()
            => IsObjectReference ? RehydratedVariableName : FieldName;
    }

    private enum ReturnShape
    {
        Task,
        TaskOfT,
        ValueTask,
        ValueTaskOfT
    }

    private static bool TryGetReturnShape(
        Compilation compilation,
        IMethodSymbol method,
        out ReturnShape returnShape,
        out string? resultTypeName)
    {
        resultTypeName = null;
        var taskSymbol = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        var taskOfTSymbol = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
        var valueTaskSymbol = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
        var valueTaskOfTSymbol = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");

        if (taskSymbol is not null && SymbolEqualityComparer.Default.Equals(method.ReturnType, taskSymbol))
        {
            returnShape = ReturnShape.Task;
            return true;
        }

        if (method.ReturnType is INamedTypeSymbol namedReturnType
            && taskOfTSymbol is not null
            && SymbolEqualityComparer.Default.Equals(namedReturnType.OriginalDefinition, taskOfTSymbol))
        {
            returnShape = ReturnShape.TaskOfT;
            resultTypeName = namedReturnType.TypeArguments[0].ToDisplayString(TypeDisplayFormat);
            return true;
        }

        if (valueTaskSymbol is not null && SymbolEqualityComparer.Default.Equals(method.ReturnType, valueTaskSymbol))
        {
            returnShape = ReturnShape.ValueTask;
            return true;
        }

        if (method.ReturnType is INamedTypeSymbol namedValueTaskType
            && valueTaskOfTSymbol is not null
            && SymbolEqualityComparer.Default.Equals(namedValueTaskType.OriginalDefinition, valueTaskOfTSymbol))
        {
            returnShape = ReturnShape.ValueTaskOfT;
            resultTypeName = namedValueTaskType.TypeArguments[0].ToDisplayString(TypeDisplayFormat);
            return true;
        }

        returnShape = default;
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol @namespace)
    {
        foreach (var member in @namespace.GetTypeMembers())
        {
            yield return member;

            foreach (var nested in EnumerateNestedTypes(member))
            {
                yield return nested;
            }
        }

        foreach (var nestedNamespace in @namespace.GetNamespaceMembers())
        {
            foreach (var nestedType in EnumerateTypes(nestedNamespace))
            {
                yield return nestedType;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNestedTypes(INamedTypeSymbol type)
    {
        foreach (var member in type.GetTypeMembers())
        {
            yield return member;

            foreach (var nested in EnumerateNestedTypes(member))
            {
                yield return nested;
            }
        }
    }

    private static bool IsPotentialGrainContract(INamedTypeSymbol type)
        => type.TypeKind == TypeKind.Interface
           && !type.IsImplicitlyDeclared
           && type.Name.Length > 1
           && type.Name[0] == 'I'
           && type.Name.EndsWith("Grain", StringComparison.Ordinal);

    private static bool IsPotentialObjectReferenceContract(INamedTypeSymbol type)
        => type.TypeKind == TypeKind.Interface
           && !type.IsImplicitlyDeclared
           && type.Name.Length > 1
           && type.Name[0] == 'I'
           && type.GetMembers().OfType<IMethodSymbol>()
               .Any(m => m.MethodKind == MethodKind.Ordinary && IsAsyncReturnType(m.ReturnType));

    private static bool IsAsyncReturnType(ITypeSymbol type)
        => type.Name is "Task" or "ValueTask"
           && type.ContainingNamespace?.ToDisplayString() is "System.Threading.Tasks";

    private static INamedTypeSymbol? FindImplementation(
        INamedTypeSymbol contract,
        string implementationName,
        IReadOnlyList<INamedTypeSymbol> allTypes)
    {
        var contractNamespace = GetNamespace(contract);
        return allTypes.FirstOrDefault(type =>
            type.TypeKind == TypeKind.Class
            && !type.IsAbstract
            && string.Equals(type.Name, implementationName, StringComparison.Ordinal)
            && string.Equals(GetNamespace(type), contractNamespace, StringComparison.Ordinal)
            && type.AllInterfaces.Any(current =>
                SymbolEqualityComparer.Default.Equals(current, contract)));
    }

    private static ImmutableArray<IMethodSymbol> GetAllInterfaceMethods(INamedTypeSymbol contract)
        => contract.AllInterfaces
            .Select(static item => (INamedTypeSymbol)item)
            .Concat(Enumerable.Repeat(contract, 1))
            .SelectMany(static item => item.GetMembers().OfType<IMethodSymbol>())
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
            .OrderBy(static method => method.Locations.FirstOrDefault() is { } location ? location.SourceSpan.Start : int.MaxValue)
            .ToImmutableArray();

    private static int? ResolveCollectionAgeLimitMilliseconds(INamedTypeSymbol implementation)
    {
        foreach (var attribute in implementation.GetAttributes())
        {
            if (!string.Equals(attribute.AttributeClass?.ToDisplayString(), CollectionAgeLimitAttributeName, StringComparison.Ordinal))
            {
                continue;
            }

            if (attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is int value)
            {
                return value;
            }
        }

        return null;
    }

    private static bool HasAttribute(ISymbol symbol, string attributeName)
        => symbol.GetAttributes().Any(attribute =>
            string.Equals(attribute.AttributeClass?.ToDisplayString(), attributeName, StringComparison.Ordinal));

    private static string GetNamespace(INamedTypeSymbol type)
        => type.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : type.ContainingNamespace.ToDisplayString();

    private static string EscapeStringLiteral(string value)
        => value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");

    private static string StripLeadingInterfacePrefix(string name)
        => name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1])
            ? name.Substring(1)
            : name;

    private static string TrimAsyncSuffix(string name)
        => name.EndsWith("Async", StringComparison.Ordinal) && name.Length > "Async".Length
            ? name.Substring(0, name.Length - "Async".Length)
            : name;

    private static string TrimSuffix(string value, string suffix)
        => value.EndsWith(suffix, StringComparison.Ordinal) && value.Length > suffix.Length
            ? value.Substring(0, value.Length - suffix.Length)
            : value;

    private static string ToCamelCase(string value)
        => string.IsNullOrEmpty(value)
            ? value
            : char.ToLowerInvariant(value[0]) + value.Substring(1);

    private static string ToPascalCase(string value)
        => string.IsNullOrEmpty(value)
            ? value
            : char.ToUpperInvariant(value[0]) + value.Substring(1);

    private static string GetAliasPrefix(string namespaceName)
    {
        if (string.IsNullOrWhiteSpace(namespaceName))
        {
            return "generated";
        }

        var lastSegment = namespaceName.Split('.').Last();
        return ToKebabCase(lastSegment);
    }

    private static string ToKebabCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsUpper(current))
            {
                if (index > 0)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }
}

internal static class EnumerableExtensions
{
    public static IEnumerable<T> AppendIf<T>(this IEnumerable<T> source, bool condition, T value)
    {
        foreach (var item in source)
        {
            yield return item;
        }

        if (condition)
        {
            yield return value;
        }
    }
}
