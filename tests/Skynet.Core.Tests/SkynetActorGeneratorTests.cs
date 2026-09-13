using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Skynet.Core;
using Skynet.Generators;
using Xunit;

namespace Skynet.Core.Tests;

/// <summary>
/// Runs <see cref="SkynetActorGenerator"/> against contract sources to verify diagnostics
/// (e.g. SKY007 for sync return types) and the shape of the generated proxy code.
/// </summary>
public sealed class SkynetActorGeneratorTests
{
	private const string SyncContractSource = """
		using System.Threading.Tasks;
		using Skynet.Core;

		[SkynetActor("sync")]
		public interface ISyncActor
		{
			string Ping(string name);
		}
		""";

	[Fact]
	public void SyncReturnType_ShouldProduceSky007Error()
	{
		var (diagnostics, generatedSources) = RunGenerator(SyncContractSource);

		var sky007 = diagnostics.Should().ContainSingle(d => d.Id == "SKY007").Subject;
		sky007.Severity.Should().Be(DiagnosticSeverity.Error);
		diagnostics.Should().NotContain(d => d.Id == "SKY006");

		// 诊断应定位在同步返回的方法声明上（0-based 第 6 行）。
		sky007.Location.IsInSource.Should().BeTrue();
		sky007.Location.GetLineSpan().StartLinePosition.Line.Should().Be(6);
		sky007.GetMessage().Should().Contain("ISyncActor").And.Contain("Ping").And.Contain("string");

		// 报错路径下不应生成任何代码。
		generatedSources.Should().BeEmpty();
	}

	[Fact]
	public void AsyncReturnTypes_ShouldNotProduceSky007()
	{
		var source = """
			using System.Threading.Tasks;
			using Skynet.Core;

			[SkynetActor("async")]
			public interface IAsyncActor
			{
				void Fire(string message);
				Task RunAsync();
				Task<string> PingAsync(string name);
				ValueTask NotifyAsync();
				ValueTask<int> CountAsync();
			}
			""";

		var (diagnostics, _) = RunGenerator(source);

		diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
	}

	[Fact]
	public void GeneratedProxy_ShouldNotContainSyncBlocking()
	{
		var source = """
			using System.Threading.Tasks;
			using Skynet.Core;

			[SkynetActor("mixed")]
			public interface IMixedActor
			{
				void Fire(string message);
				Task<string> PingAsync(string name);
			}
			""";

		var (_, generatedSources) = RunGenerator(source);

		generatedSources.Should().NotBeEmpty();
		foreach (var generatedSource in generatedSources)
		{
			generatedSource.Should().NotContain("GetAwaiter().GetResult()");
		}

		// void 方法：fire-and-forget（入队即返回），并以 OnlyOnFaulted 观察入队异常；
		// XML 注释说明 send 语义。
		var proxySource = generatedSources.Should().Contain(s => s.Contains("IMixedActorProxy")).Subject;
		proxySource.Should().Contain("TaskContinuationOptions.OnlyOnFaulted");
		proxySource.Should().Contain("send 语义：入队即返回，不等待 actor 处理完成");
		proxySource.Should().Contain("call 语义：等待 actor 处理完成并返回结果（请使用 await 调用）");
	}

	private static (ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<string> GeneratedSources) RunGenerator(
		string source)
	{
		var compilation = CSharpCompilation.Create(
			"SkynetActorGeneratorTests.Compilation",
			new[] { CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest)) },
			new[]
			{
				MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
				MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
				MetadataReference.CreateFromFile(typeof(SkynetActorAttribute).Assembly.Location),
			},
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		var driver = CSharpGeneratorDriver.Create(new SkynetActorGenerator().AsSourceGenerator());
		var runResult = driver.RunGenerators(compilation).GetRunResult();
		var diagnostics = runResult.Diagnostics;
		var generatedSources = runResult.Results
			.SelectMany(result => result.GeneratedSources)
			.Select(generated => generated.SourceText?.ToString() ?? string.Empty)
			.ToImmutableArray();

		return (diagnostics, generatedSources);
	}
}
