using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MailStencil.Scriban.Tests;

public sealed class SafetyTests : IDisposable
{
    private readonly ServiceProvider provider = new ServiceCollection().AddScribanRenderer().BuildServiceProvider();
    private ITemplateRenderer Renderer => provider.GetRequiredService<ITemplateRenderer>();
    private ITemplateValidator Validator => provider.GetRequiredService<ITemplateValidator>();
    public void Dispose() => provider.Dispose();

    [Theory]
    [InlineData("{{ for i in 1..1001 }}x{{ end }}")]
    [InlineData("{{ for i in 1..40 }}{{ for j in 1..40 }}x{{ end }}{{ end }}")]
    public async Task ExcessiveLoopWorkFailsDeterministically(string template)
    {
        var content = new EmailTemplateContent(template, textBody: "");
        Assert.True((await Validator.ValidateAsync<RenderingTests.ExampleModel>(content)).IsValid);
        var error = await Assert.ThrowsAsync<TemplateRenderingException>(() => Renderer.RenderAsync(content, new RenderingTests.ExampleModel(), CultureInfo.InvariantCulture));
        Assert.Equal("MSR001", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ExcessiveOutputFails()
    {
        var content = new EmailTemplateContent("{{ for i in 1..1000 }}{{ value }}{{ end }}", textBody: "");
        var error = await Assert.ThrowsAsync<TemplateRenderingException>(() => Renderer.RenderAsync(content,
            new TextModel { Value = new string('x', 2000) }, CultureInfo.InvariantCulture));
        Assert.Equal("MSR001", error.Diagnostic.Code);
    }

    [Fact]
    public async Task OversizedAndDeepTemplatesAreValidationErrors()
    {
        var huge = new EmailTemplateContent(new string('x', 128 * 1024 + 1), textBody: "");
        Assert.Contains((await Validator.ValidateAsync<TextModel>(huge)).Diagnostics, d => d.Code == "MSV006");
        var nested = string.Concat(Enumerable.Repeat("{{ if true }}", 100)) + "x" + string.Concat(Enumerable.Repeat("{{ end }}", 100));
        Assert.False((await Validator.ValidateAsync<TextModel>(new EmailTemplateContent(nested, textBody: ""))).IsValid);
    }

    [Fact]
    public async Task CyclesAndDeepModelGraphsAreBounded()
    {
        var model = new Node();
        model.Next = model;
        var content = new EmailTemplateContent("{{ name }}", textBody: "");
        Assert.True((await Validator.ValidateAsync<Node>(content)).IsValid);
        var error = await Assert.ThrowsAsync<TemplateRenderingException>(() => Renderer.RenderAsync(content, model, CultureInfo.InvariantCulture));
        Assert.Equal("MSR002", error.Diagnostic.Code);
        Assert.Equal(TemplateComponent.General, error.Diagnostic.Component);
        var deep = new Node();
        for (var i = 0; i < 40; i++) deep = new Node { Next = deep };
        await Assert.ThrowsAsync<TemplateRenderingException>(() => Renderer.RenderAsync(content, deep, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task InfiniteModelEnumerablesAreStoppedByTheProjectionBudget()
    {
        var error = await Assert.ThrowsAsync<TemplateRenderingException>(() => Renderer.RenderAsync(
            new EmailTemplateContent("{{ for value in values }}{{ value }}{{ end }}", textBody: ""),
            new InfiniteModel(), CultureInfo.InvariantCulture));
        Assert.Equal("MSR002", error.Diagnostic.Code);
        Assert.Equal(TemplateComponent.General, error.Diagnostic.Component);
    }

    [Theory]
    [InlineData(9999, true)]
    [InlineData(10000, true)]
    [InlineData(10001, false)]
    public async Task DeclaredSchemaMemberCountIsBounded(int propertyCount, bool expectedValid)
    {
        var contract = CreateInterfaceWithProperties(propertyCount);
        var method = typeof(ITemplateValidator).GetMethod(nameof(ITemplateValidator.ValidateAsync))!
            .MakeGenericMethod(contract);
        var task = (Task<TemplateValidationResult>)method.Invoke(Validator,
            [new EmailTemplateContent(string.Empty, textBody: string.Empty), CancellationToken.None])!;

        var result = await task;

        Assert.Equal(expectedValid, result.IsValid);
        if (!expectedValid)
            Assert.Contains(result.Diagnostics, diagnostic =>
                diagnostic.Component == TemplateComponent.General && diagnostic.Code == "MSV007");
    }

    [Fact]
    public async Task PreCancelledOperationsThrowStandardCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var content = new EmailTemplateContent("", textBody: "");
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => Renderer.RenderAsync(content, new TextModel(), CultureInfo.InvariantCulture, source.Token));
        Assert.Equal(source.Token, error.CancellationToken);
        await Assert.ThrowsAsync<OperationCanceledException>(() => Validator.ValidateAsync<TextModel>(content, source.Token));
    }

    [Fact]
    public async Task CancellationDuringProjectionDoesNotDependOnTiming()
    {
        using var source = new CancellationTokenSource();
        var content = new EmailTemplateContent("{{ value }}", textBody: "");
        await Assert.ThrowsAsync<OperationCanceledException>(() => Renderer.RenderAsync(content,
            new CancellingModel(source), CultureInfo.InvariantCulture, source.Token));
    }

    [Fact]
    public async Task GetterFailuresAreExecutionExceptions()
    {
        var error = await Assert.ThrowsAsync<TemplateRenderingException>(() => Renderer.RenderAsync(
            new EmailTemplateContent("{{ name }}", textBody: ""), new ValidationTests.ThrowingGetter(), CultureInfo.InvariantCulture));
        Assert.Equal("MSR002", error.Diagnostic.Code);
        Assert.Equal(TemplateComponent.General, error.Diagnostic.Component);
    }

    [Theory]
    [InlineData(TemplateComponent.Subject)]
    [InlineData(TemplateComponent.HtmlBody)]
    [InlineData(TemplateComponent.TextBody)]
    public async Task ExecutionErrorsIdentifyComponent(TemplateComponent component)
    {
        const string bad = "{{ customer.name }}";
        var content = new EmailTemplateContent(component == TemplateComponent.Subject ? bad : "",
            component == TemplateComponent.HtmlBody ? bad : "", component == TemplateComponent.TextBody ? bad : "");
        var error = await Assert.ThrowsAsync<TemplateRenderingException>(() => Renderer.RenderAsync(content,
            new RenderingTests.NullableModel(), CultureInfo.InvariantCulture));
        Assert.Equal(component, error.Diagnostic.Component);
    }

    [Fact]
    public void RegistrationIsIdempotentAndPreservesCustomServices()
    {
        var services = new ServiceCollection();
        var renderer = Renderer;
        services.AddSingleton(renderer);
        Assert.Same(services, services.AddScribanRenderer().AddScribanRenderer());
        using var custom = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.Same(renderer, Assert.Single(custom.GetServices<ITemplateRenderer>()));
        Assert.Single(custom.GetServices<ITemplateValidator>());
        Assert.Null(custom.GetService<IEmailTemplateService>());
        Assert.Null(custom.GetService<ITemplateReader>());
    }

    public sealed class TextModel { public string Value { get; init; } = ""; }
    public sealed class Node { public string Name => "node"; public Node? Next { get; set; } }
    public sealed class CancellingModel(CancellationTokenSource source)
    {
        public string Value { get { source.Cancel(); return "cancelled"; } }
    }
    public sealed class InfiniteModel
    {
        public IEnumerable<int> Values { get { while (true) yield return 1; } }
    }

    private static Type CreateInterfaceWithProperties(int propertyCount)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("MailStencil.SchemaBudget." + propertyCount), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("Contracts").DefineType(
            "Contract" + propertyCount, TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        for (var index = 0; index < propertyCount; index++)
        {
            var property = type.DefineProperty("Value" + index, PropertyAttributes.None, typeof(string), null);
            var getter = type.DefineMethod("get_Value" + index,
                MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual |
                MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                typeof(string), Type.EmptyTypes);
            property.SetGetMethod(getter);
        }
        return type.CreateType()!;
    }
}
