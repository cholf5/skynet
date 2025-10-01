using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Skynet.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class SkynetActorGenerator : IIncrementalGenerator
{
	private const string AttributeMetadataName = "Skynet.Core.SkynetActorAttribute";

	private static readonly DiagnosticDescriptor InvalidTargetDescriptor = new(
		"SKY001",
		"[SkynetActor] 只能应用于 interface",
		"[SkynetActor] 只能应用于 interface（当前应用于 {0}）",
		"Skynet",
		DiagnosticSeverity.Error,
		true);

	private static readonly DiagnosticDescriptor GenericInterfaceDescriptor = new(
		"SKY002",
		"不支持泛型接口",
		"接口 {0} 是泛型类型，SourceGenerator 暂不支持",
		"Skynet",
		DiagnosticSeverity.Error,
		true);

	private static readonly DiagnosticDescriptor GenericMethodDescriptor = new(
		"SKY003",
		"不支持泛型方法",
		"接口 {0} 的方法 {1} 使用了泛型参数，SourceGenerator 暂不支持",
		"Skynet",
		DiagnosticSeverity.Error,
		true);

	private static readonly DiagnosticDescriptor UnsupportedParameterDescriptor = new(
		"SKY004",
		"不支持的参数声明",
		"接口 {0} 的方法 {1} 包含不受支持的参数 {2}",
		"Skynet",
		DiagnosticSeverity.Error,
		true);

	private static readonly DiagnosticDescriptor MultipleCancellationTokensDescriptor = new(
		"SKY005",
		"只允许一个 CancellationToken 参数",
		"接口 {0} 的方法 {1} 同时声明了多个 CancellationToken 参数",
		"Skynet",
		DiagnosticSeverity.Error,
		true);

	private static readonly DiagnosticDescriptor UnsupportedReturnTypeDescriptor = new(
		"SKY006",
		"不支持的返回类型",
		"接口 {0} 的方法 {1} 返回类型 {2} 不受支持",
		"Skynet",
		DiagnosticSeverity.Error,
		true);

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var contracts = context.SyntaxProvider.ForAttributeWithMetadataName(
			AttributeMetadataName,
			static (node, _) => node is InterfaceDeclarationSyntax,
			static (syntaxContext, cancellationToken) => CreateModel(syntaxContext, cancellationToken));

		context.RegisterSourceOutput(contracts, static (sourceContext, result) =>
		{
			foreach (var diagnostic in result.Diagnostics)
			{
				sourceContext.ReportDiagnostic(diagnostic);
			}

			if (result.Model is not { } model)
			{
				return;
			}

			var writer = new SourceWriter();
			WriteContract(writer, model);
			sourceContext.AddSource($"{model.HintName}.g.cs", writer.ToString());
		});
	}

	private static ContractGenerationResult CreateModel(GeneratorAttributeSyntaxContext context,
		CancellationToken cancellationToken)
	{
		if (context.TargetSymbol is not INamedTypeSymbol interfaceSymbol)
		{
			return ContractGenerationResult.Failure(Diagnostic.Create(InvalidTargetDescriptor,
				context.TargetNode.GetLocation(),
				context.TargetSymbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? "unknown"));
		}

		if (interfaceSymbol.TypeKind != TypeKind.Interface)
		{
			return ContractGenerationResult.Failure(Diagnostic.Create(InvalidTargetDescriptor,
				context.TargetNode.GetLocation(),
				interfaceSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
		}

		if (interfaceSymbol.IsGenericType)
		{
			return ContractGenerationResult.Failure(Diagnostic.Create(GenericInterfaceDescriptor,
				context.TargetNode.GetLocation(),
				interfaceSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
		}

		var attributeData = context.Attributes[0];
		string? serviceName = null;
		var unique = false;
		if (attributeData.ConstructorArguments.Length > 0 &&
		    attributeData.ConstructorArguments[0].Value is string ctorName)
		{
			serviceName = string.IsNullOrWhiteSpace(ctorName) ? null : ctorName;
		}

		foreach (var named in attributeData.NamedArguments)
		{
			switch (named)
			{
				case { Key: "Name", Value.Value: string namedValue }:
					serviceName = string.IsNullOrWhiteSpace(namedValue) ? null : namedValue;
					break;
				case { Key: "Unique", Value.Value: bool uniqueValue }:
					unique = uniqueValue;
					break;
			}
		}

		var compilation = context.SemanticModel.Compilation;
		var cancellationTokenType = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");
		var taskType = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
		var taskOfTType = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
		var valueTaskType = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
		var valueTaskOfTType = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");

		var methods = CollectMethods(interfaceSymbol);
		var methodModels = ImmutableArray.CreateBuilder<ActorMethodModel>(methods.Length);
		for (var i = 0; i < methods.Length; i++)
		{
			var method = methods[i];
			if (method.IsStatic || method.MethodKind != MethodKind.Ordinary)
			{
				continue;
			}

			if (method.TypeParameters.Length > 0)
			{
				return ContractGenerationResult.Failure(Diagnostic.Create(GenericMethodDescriptor,
					method.Locations.FirstOrDefault() ?? context.TargetNode.GetLocation(),
					interfaceSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), method.Name));
			}

			var parameters = ImmutableArray.CreateBuilder<ActorParameterModel>(method.Parameters.Length);
			ActorParameterModel? cancellationParameter = null;
			foreach (var parameter in method.Parameters)
			{
				if (parameter.RefKind != RefKind.None)
				{
					return ContractGenerationResult.Failure(Diagnostic.Create(UnsupportedParameterDescriptor,
						parameter.Locations.FirstOrDefault() ??
						method.Locations.FirstOrDefault() ?? context.TargetNode.GetLocation(),
						interfaceSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), method.Name,
						parameter.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
				}

				var parameterTypeDisplay = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
				var defaultClause = GetDefaultClause(parameter);
				var isCancellationToken = cancellationTokenType is not null &&
				                          SymbolEqualityComparer.Default.Equals(parameter.Type, cancellationTokenType);
				if (isCancellationToken)
				{
					if (cancellationParameter is not null)
					{
						return ContractGenerationResult.Failure(Diagnostic.Create(MultipleCancellationTokensDescriptor,
							parameter.Locations.FirstOrDefault() ??
							method.Locations.FirstOrDefault() ?? context.TargetNode.GetLocation(),
							interfaceSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
							method.Name));
					}

					if (parameter.Ordinal != method.Parameters.Length - 1)
					{
						return ContractGenerationResult.Failure(Diagnostic.Create(UnsupportedParameterDescriptor,
							parameter.Locations.FirstOrDefault() ??
							method.Locations.FirstOrDefault() ?? context.TargetNode.GetLocation(),
							interfaceSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), method.Name,
							parameter.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
					}

					cancellationParameter = new ActorParameterModel(parameter, parameterTypeDisplay, parameter.Name,
						defaultClause, true, parameter.IsParams);
					continue;
				}

				parameters.Add(new ActorParameterModel(parameter, parameterTypeDisplay, parameter.Name, defaultClause,
					false, parameter.IsParams));
			}

			if (!TryDetermineReturnModel(context, interfaceSymbol, method, taskType, taskOfTType, valueTaskType,
				    valueTaskOfTType, out var returnModel, out var returnDiagnostic))
			{
				return ContractGenerationResult.Failure(returnDiagnostic!);
			}

			methodModels.Add(new ActorMethodModel(method, parameters.ToImmutable(), cancellationParameter, returnModel,
				i));
		}

		var @namespace = interfaceSymbol.ContainingNamespace is { IsGlobalNamespace: false } ns
			? ns.ToDisplayString()
			: null;
		var hintName = interfaceSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).Replace('<', '_')
			.Replace('>', '_');
		return ContractGenerationResult.Success(new ActorContractModel(interfaceSymbol, @namespace, serviceName, unique,
			methodModels.ToImmutable(), hintName));
	}

	private static ImmutableArray<IMethodSymbol> CollectMethods(INamedTypeSymbol interfaceSymbol)
	{
		var result = new List<IMethodSymbol>();
		var seen = new HashSet<string>(StringComparer.Ordinal);

		void Collect(INamedTypeSymbol symbol)
		{
			foreach (var member in symbol.GetMembers())
			{
				if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } method)
				{
					var signature = method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
					if (seen.Add(signature))
					{
						result.Add(method);
					}
				}
			}

			foreach (var baseInterface in symbol.Interfaces)
			{
				Collect(baseInterface);
			}
		}

		Collect(interfaceSymbol);
		return result.ToImmutableArray();
	}

	private static bool TryDetermineReturnModel(
		GeneratorAttributeSyntaxContext context,
		INamedTypeSymbol interfaceSymbol,
		IMethodSymbol method,
		INamedTypeSymbol? taskType,
		INamedTypeSymbol? taskOfTType,
		INamedTypeSymbol? valueTaskType,
		INamedTypeSymbol? valueTaskOfTType,
		out ActorReturnModel returnModel,
		out Diagnostic? diagnostic)
	{
		returnModel = default;
		diagnostic = null;
		var returnType = method.ReturnType;
		var returnDisplay = returnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
		var isVoid = returnType.SpecialType == SpecialType.System_Void;

		if (isVoid)
		{
			returnModel = new ActorReturnModel(returnDisplay, ActorReturnKind.Void, returnType);
			return true;
		}

		if (taskType is not null && SymbolEqualityComparer.Default.Equals(returnType, taskType))
		{
			returnModel = new ActorReturnModel(returnDisplay, ActorReturnKind.Task, returnType);
			return true;
		}

		if (valueTaskType is not null && SymbolEqualityComparer.Default.Equals(returnType, valueTaskType))
		{
			returnModel = new ActorReturnModel(returnDisplay, ActorReturnKind.ValueTask, returnType);
			return true;
		}

		if (taskOfTType is not null && returnType is INamedTypeSymbol taskOfT &&
		    SymbolEqualityComparer.Default.Equals(taskOfT.OriginalDefinition, taskOfTType))
		{
			returnModel = new ActorReturnModel(returnDisplay, ActorReturnKind.TaskOfT, taskOfT.TypeArguments[0]);
			return true;
		}

		if (valueTaskOfTType is not null && returnType is INamedTypeSymbol valueTaskOfT &&
		    SymbolEqualityComparer.Default.Equals(valueTaskOfT.OriginalDefinition, valueTaskOfTType))
		{
			returnModel =
				new ActorReturnModel(returnDisplay, ActorReturnKind.ValueTaskOfT, valueTaskOfT.TypeArguments[0]);
			return true;
		}

		if (returnType.TypeKind != TypeKind.Error)
		{
			returnModel = new ActorReturnModel(returnDisplay, ActorReturnKind.Sync, returnType);
			return true;
		}

		diagnostic = Diagnostic.Create(UnsupportedReturnTypeDescriptor,
			method.Locations.FirstOrDefault() ?? context.TargetNode.GetLocation(),
			interfaceSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), method.Name, returnDisplay);
		return false;
	}

	private static string? GetDefaultClause(IParameterSymbol parameter)
	{
		var syntaxReference = parameter.DeclaringSyntaxReferences.FirstOrDefault();
		if (syntaxReference is null)
		{
			return parameter.HasExplicitDefaultValue ? " = default" : null;
		}

		if (syntaxReference.GetSyntax() is not ParameterSyntax syntax)
		{
			return parameter.HasExplicitDefaultValue ? " = default" : null;
		}

		return syntax.Default is null ? null : " " + syntax.Default.ToFullString();
	}

	private static void WriteContract(SourceWriter writer, ActorContractModel model)
	{
		writer.AppendHeader();
		var generatedNamespace =
			string.IsNullOrEmpty(model.Namespace) ? "Skynet.Generated" : model.Namespace + ".__Skynet";
		writer.AppendLine();
		writer.AppendLine($"namespace {generatedNamespace};");
		writer.AppendLine();

		WriteRegistration(writer, model);
		writer.AppendLine();
		WriteProxy(writer, model);
		writer.AppendLine();
		WriteDispatcher(writer, model);

		foreach (var method in model.Methods)
		{
			if (method.PayloadParameters.Length == 0)
			{
				continue;
			}

			writer.AppendLine();
			WriteRequestType(writer, model, method);
		}
	}

	private static void WriteRegistration(SourceWriter writer, ActorContractModel model)
	{
		writer.AppendLine("[global::System.Runtime.CompilerServices.CompilerGenerated]");
		writer.BeginBlock($"internal static class {model.InterfaceSymbol.Name}Registration");
		writer.AppendLine("[global::System.Runtime.CompilerServices.ModuleInitializer]");
		writer.BeginBlock("internal static void Register()");
		var serviceNameLiteral =
			model.ServiceName is null ? "null" : SymbolDisplay.FormatLiteral(model.ServiceName, true);
		writer.AppendLine(
			$"global::Skynet.Core.RpcContractRegistry.Register<{model.InterfaceSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>(");
		writer.Indent();
		writer.AppendLine($"new {model.InterfaceSymbol.Name}Dispatcher(),");
		writer.AppendLine($"static (actor, options) => new {model.InterfaceSymbol.Name}Proxy(actor, options),");
		writer.AppendLine($"{serviceNameLiteral},");
		writer.AppendLine(model.IsUnique ? "true" : "false");
		writer.Unindent();
		writer.AppendLine(");");
		writer.EndBlock();
		if (model.ServiceName is not null)
		{
			writer.AppendLine(
				$"internal const string ServiceName = {SymbolDisplay.FormatLiteral(model.ServiceName, true)};");
		}

		writer.AppendLine($"internal const bool IsUnique = {(model.IsUnique ? "true" : "false")};");
		writer.EndBlock();
	}

	private static void WriteProxy(SourceWriter writer, ActorContractModel model)
	{
		var interfaceDisplay = model.InterfaceSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
		writer.AppendLine("[global::System.Runtime.CompilerServices.CompilerGenerated]");
		writer.BeginBlock($"public sealed class {model.InterfaceSymbol.Name}Proxy : {interfaceDisplay}");
		writer.AppendLine("private readonly global::Skynet.Core.ActorRef _actor;");
		writer.AppendLine("public global::MessagePack.MessagePackSerializerOptions SerializerOptions { get; }");
		writer.AppendLine();
		writer.BeginBlock(
			$"public {model.InterfaceSymbol.Name}Proxy(global::Skynet.Core.ActorRef actor, global::MessagePack.MessagePackSerializerOptions? options)");
		writer.AppendLine("global::System.ArgumentNullException.ThrowIfNull(actor);");
		writer.AppendLine("_actor = actor;");
		writer.AppendLine("SerializerOptions = options ?? global::MessagePack.MessagePackSerializerOptions.Standard;");
		writer.EndBlock();
		writer.AppendLine();

		foreach (var method in model.Methods)
		{
			WriteProxyMethod(writer, model, method);
			writer.AppendLine();
		}

		writer.EndBlock();
	}

	private static void WriteProxyMethod(SourceWriter writer, ActorContractModel model, ActorMethodModel method)
	{
		var signature = BuildMethodSignature(method);
		writer.BeginBlock($"public {method.ReturnModel.DisplayType} {method.Symbol.Name}({signature})");
		var cancellationName = method.CancellationParameter?.Name ?? "global::System.Threading.CancellationToken.None";
		string payloadExpression;
		if (method.PayloadParameters.Length == 0)
		{
			payloadExpression = "new global::Skynet.Core.RpcMessages.EmptyPayload()";
		}
		else
		{
			var arguments = string.Join(", ", method.PayloadParameters.Select(p => p.Name));
			payloadExpression = $"new {method.RequestTypeName}({arguments})";
		}

		writer.AppendLine($"var payload = {payloadExpression};");
		switch (method.ReturnModel.Kind)
		{
			case ActorReturnKind.Void:
				writer.AppendLine($"_actor.SendAsync(payload, {cancellationName}).AsTask().GetAwaiter().GetResult();");
				writer.AppendLine("return;");
				break;
			case ActorReturnKind.Task:
				writer.AppendLine($"return _actor.CallAsync<object?>(payload, cancellationToken: {cancellationName});");
				break;
			case ActorReturnKind.ValueTask:
				writer.AppendLine(
					$"return new global::System.Threading.Tasks.ValueTask(_actor.CallAsync<object?>(payload, cancellationToken: {cancellationName}));");
				break;
			case ActorReturnKind.TaskOfT:
				var taskReturn = method.ReturnModel.InnerTypeDisplay;
				writer.AppendLine(
					$"return _actor.CallAsync<{taskReturn}>(payload, cancellationToken: {cancellationName});");
				break;
			case ActorReturnKind.ValueTaskOfT:
				var valueTaskReturn = method.ReturnModel.InnerTypeDisplay;
				writer.AppendLine(
					$"return new global::System.Threading.Tasks.ValueTask<{valueTaskReturn}>(_actor.CallAsync<{valueTaskReturn}>(payload, cancellationToken: {cancellationName}));");
				break;
			case ActorReturnKind.Sync:
				var syncReturn = method.ReturnModel.InnerTypeDisplay;
				writer.AppendLine(
					$"return _actor.CallAsync<{syncReturn}>(payload, cancellationToken: {cancellationName}).GetAwaiter().GetResult();");
				break;
		}

		writer.EndBlock();
	}

	private static string BuildMethodSignature(ActorMethodModel method)
	{
		var builder = new StringBuilder();
		var first = true;
		foreach (var parameter in method.AllParameters)
		{
			if (!first)
			{
				builder.Append(", ");
			}

			first = false;
			var modifier = parameter.IsParams ? "params " : string.Empty;
			builder.Append(modifier);
			builder.Append(parameter.TypeDisplay);
			builder.Append(' ');
			builder.Append(parameter.Name);
			if (!string.IsNullOrWhiteSpace(parameter.DefaultClause))
			{
				builder.Append(parameter.DefaultClause);
			}
		}

		return builder.ToString();
	}

	private static void WriteDispatcher(SourceWriter writer, ActorContractModel model)
	{
		var interfaceDisplay = model.InterfaceSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
		writer.AppendLine("[global::System.Runtime.CompilerServices.CompilerGenerated]");
		writer.BeginBlock(
			$"internal sealed class {model.InterfaceSymbol.Name}Dispatcher : global::Skynet.Core.IRpcDispatcher<{interfaceDisplay}>");
		writer.BeginBlock(
			$"public async global::System.Threading.Tasks.Task<object?> DispatchAsync({interfaceDisplay} target, global::Skynet.Core.MessageEnvelope envelope, global::System.Threading.CancellationToken cancellationToken)");
		writer.BeginBlock("switch (envelope.Payload)");
		foreach (var method in model.Methods)
		{
			var caseType = method.PayloadParameters.Length == 0
				? "global::Skynet.Core.RpcMessages.EmptyPayload"
				: method.RequestTypeName;
			writer.BeginBlock($"case {caseType} request:");
			var callArguments = string.Join(", ", method.PayloadParameters.Select(p => $"request.{p.PropertyName}")
				.Concat(
					method.CancellationParameter is not null ? new[] { "cancellationToken" } : Array.Empty<string>()));
			switch (method.ReturnModel.Kind)
			{
				case ActorReturnKind.Void:
					writer.AppendLine($"target.{method.Symbol.Name}({callArguments});");
					writer.AppendLine("return null;");
					break;
				case ActorReturnKind.Task:
					writer.AppendLine($"await target.{method.Symbol.Name}({callArguments}).ConfigureAwait(false);");
					writer.AppendLine("return null;");
					break;
				case ActorReturnKind.ValueTask:
					writer.AppendLine($"await target.{method.Symbol.Name}({callArguments}).ConfigureAwait(false);");
					writer.AppendLine("return null;");
					break;
				case ActorReturnKind.TaskOfT:
					writer.AppendLine(
						$"return await target.{method.Symbol.Name}({callArguments}).ConfigureAwait(false);");
					break;
				case ActorReturnKind.ValueTaskOfT:
					writer.AppendLine(
						$"return await target.{method.Symbol.Name}({callArguments}).ConfigureAwait(false);");
					break;
				case ActorReturnKind.Sync:
					writer.AppendLine($"return target.{method.Symbol.Name}({callArguments});");
					break;
			}

			writer.EndBlock();
		}

		writer.BeginBlock("default:");
		writer.AppendLine(
			$"throw new global::Skynet.Core.RpcDispatchException($\"Unsupported payload '{{envelope.Payload?.GetType().FullName}}' for contract '{model.InterfaceSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)}'.\");");
		writer.EndBlock();
		writer.EndBlock();
		writer.EndBlock();
		writer.EndBlock();
	}

	private static void WriteRequestType(SourceWriter writer, ActorContractModel model, ActorMethodModel method)
	{
		writer.AppendLine("[global::System.Runtime.CompilerServices.CompilerGenerated]");
		writer.AppendLine("[global::MessagePack.MessagePackObject]");
		writer.BeginBlock($"public sealed partial class {method.RequestTypeName}");

		for (var i = 0; i < method.PayloadParameters.Length; i++)
		{
			var parameter = method.PayloadParameters[i];
			writer.AppendLine($"[global::MessagePack.Key({i})]");
			writer.AppendLine($"public {parameter.TypeDisplay} {parameter.PropertyName} {{ get; set; }}");
			writer.AppendLine();
		}

		writer.BeginBlock($"public {method.RequestTypeName}()");
		writer.EndBlock();

		if (method.PayloadParameters.Length > 0)
		{
			var constructorParameters =
				string.Join(", ", method.PayloadParameters.Select(p => $"{p.TypeDisplay} {p.Name}"));
			writer.AppendLine();
			writer.AppendLine("[global::MessagePack.SerializationConstructor]");
			writer.BeginBlock($"public {method.RequestTypeName}({constructorParameters})");
			foreach (var parameter in method.PayloadParameters)
			{
				writer.AppendLine($"{parameter.PropertyName} = {parameter.Name};");
			}

			writer.EndBlock();
		}

		writer.EndBlock();
	}

	private readonly record struct ActorReturnModel(string DisplayType, ActorReturnKind Kind, ITypeSymbol InnerType)
	{
		public string InnerTypeDisplay => InnerType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
	}

	private sealed record ActorParameterModel(
		IParameterSymbol Symbol,
		string TypeDisplay,
		string Name,
		string? DefaultClause,
		bool IsCancellationToken,
		bool IsParams)
	{
		public string PropertyName
		{
			get
			{
				if (string.IsNullOrEmpty(Name))
				{
					return "Arg0";
				}

				var characters = Name.ToCharArray();
				characters[0] = char.ToUpper(characters[0], CultureInfo.InvariantCulture);
				return new string(characters);
			}
		}
	}

	private sealed record ActorMethodModel(
		IMethodSymbol Symbol,
		ImmutableArray<ActorParameterModel> PayloadParameters,
		ActorParameterModel? CancellationParameter,
		ActorReturnModel ReturnModel,
		int Index)
	{
		public ImmutableArray<ActorParameterModel> AllParameters => CancellationParameter is null
			? PayloadParameters
			: PayloadParameters.Add(CancellationParameter);

		public string RequestTypeName => PayloadParameters.Length == 0
			? string.Empty
			: $"{Symbol.ContainingType.Name}_{Symbol.Name}_{Index}Request";
	}

	private sealed record ActorContractModel(
		INamedTypeSymbol InterfaceSymbol,
		string? Namespace,
		string? ServiceName,
		bool IsUnique,
		ImmutableArray<ActorMethodModel> Methods,
		string HintName)
	{
	}

	private readonly record struct ContractGenerationResult(
		ActorContractModel? Model,
		ImmutableArray<Diagnostic> Diagnostics)
	{
		public static ContractGenerationResult Success(ActorContractModel model) =>
			new(model, ImmutableArray<Diagnostic>.Empty);

		public static ContractGenerationResult Failure(Diagnostic diagnostic) =>
			new(null, ImmutableArray.Create(diagnostic));
	}

	private enum ActorReturnKind
	{
		Void,
		Task,
		ValueTask,
		TaskOfT,
		ValueTaskOfT,
		Sync
	}

	private sealed class SourceWriter
	{
		private readonly StringBuilder _builder = new();
		private int _indent;

		public void AppendHeader()
		{
			_builder.AppendLine("// <auto-generated />");
			_builder.AppendLine("#pragma warning disable");
			_builder.AppendLine("#nullable enable");
		}

		public void AppendLine(string? text = null)
		{
			if (text is null)
			{
				_builder.AppendLine();
				return;
			}

			_builder.Append(new string('	', _indent));
			_builder.AppendLine(text);
		}

		public void BeginBlock(string text)
		{
			AppendLine(text);
			AppendLine("{");
			_indent++;
		}

		public void EndBlock()
		{
			_indent--;
			AppendLine("}");
		}

		public void Indent()
		{
			_indent++;
		}

		public void Unindent()
		{
			_indent--;
		}

		public override string ToString()
		{
			return _builder.ToString();
		}
	}
}
