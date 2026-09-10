using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MailStencil.Scriban.Tests;

public sealed class RenderingTests : IDisposable
{
    private readonly ServiceProvider provider = new ServiceCollection().AddMailStencil().AddScribanRenderer().BuildServiceProvider();
    private ITemplateRenderer Renderer => provider.GetRequiredService<ITemplateRenderer>();
    private ITemplateValidator Validator => provider.GetRequiredService<ITemplateValidator>();
    public void Dispose() => provider.Dispose();

    [Theory]
    [InlineData("{{ customer_name }}", "Mario")]
    [InlineData("{{ order_number }}:{{ is_vip }}", "123:true")]
    [InlineData("{{ customer.name }}", "Ada")]
    [InlineData("{{ for item in order.items }}{{ item.name }};{{ end }}", "A;B;")]
    [InlineData("{{ if customer.is_vip }}VIP{{ else }}Standard{{ end }}", "VIP")]
    [InlineData("{{ customer.is_vip ? 'yes' : 'no' }}", "yes")]
    [InlineData("{{ if total > 10 && is_vip }}yes{{ end }}", "yes")]
    [InlineData("{{ total + 1 }}", "13.5")]
    [InlineData("{{ -total }}", "-12.5")]
    [InlineData("{{ nullable_number }}|{{ nullable_text }}", "|")]
    [InlineData("Ciao 世界 👋 {{ customer_name }}", "Ciao 世界 👋 Mario")]
    [InlineData("<p>{{ markup }}</p>", "<p><b>A & B</b></p>")]
    [InlineData("{{ markup | html.escape }}", "&lt;b&gt;A &amp; B&lt;/b&gt;")]
    [InlineData("{{ customer_name | string.upcase }}", "MARIO")]
    [InlineData("{{ string.downcase customer_name }}", "mario")]
    [InlineData("{{ customer_name | string.replace 'M' 'D' | string.upcase }}", "DARIO")]
    [InlineData("{{ order.items | array.size }}", "2")]
    [InlineData("{{ math.round total 0 }}", "12")]
    [InlineData("{{ math.abs (-total) }}", "12.5")]
    [InlineData("{{ '  text  ' | string.strip | string.capitalize }}", "Text")]
    [InlineData("{{ customer_name | string.size }}", "5")]
    [InlineData("{{ customer_name | string.contains 'ari' }}", "true")]
    [InlineData("{{ if !is_vip }}no{{ else if total > 1 }}yes{{ else }}other{{ end }}", "yes")]
    [InlineData("{{ alias = customer; nested = alias; nested.name }}", "Ada")]
    [InlineData("{{ for group in groups }}{{ for item in group.items }}{{ for.index }}{{ end }}{{ for.index }}{{ end }}", "010")]
    [InlineData("{{ for i in 1..3 }}{{ i }}{{ break }}{{ end }}", "1")]
    [InlineData("{{ alias = customer; alias.name }}", "Ada")]
    [InlineData("{{ $alias = customer; $alias.name }}", "Ada")]
    [InlineData("{{ for group in groups }}{{ for item in group.items }}{{ item.name }}{{ end }}{{ end }}", "AB")]
    [InlineData("{{ for item in order.items }}{{ for.index }}:{{ item.name }};{{ end }}", "0:A;1:B;")]
    [InlineData("{{ if is_vip }}{{ alias = customer }}{{ else }}{{ alias = customer }}{{ end }}{{ alias.name }}", "Ada")]
    [InlineData("{{ sum = 0; for item in order.items; sum = sum + 1; end; sum }}", "2")]
    [InlineData("{{ order.items[0].name }}", "A")]
    [InlineData("{{ customer['name'] }}", "Ada")]
    [InlineData("{{ for i in 1..3 }}{{ i }}{{ end }}", "123")]
    [InlineData("{{ for i in [1,2,3] }}{{ if i == 2 }}{{ continue }}{{ end }}{{ i }}{{ end }}", "13")]
    public async Task ValidatesAndRendersSupportedConstructs(string template, string expected)
    {
        var content = new EmailTemplateContent(template, textBody: "");
        var validation = await Validator.ValidateAsync<ExampleModel>(content);
        Assert.True(validation.IsValid, string.Join("\n", validation.Diagnostics.Select(d => d.Message)));
        var output = await Renderer.RenderAsync(content, new ExampleModel(), CultureInfo.InvariantCulture);
        Assert.Equal(expected, output.Subject);
    }

    [Fact]
    public async Task RendersEachPartIndependentlyAndPreservesMissingBody()
    {
        var content = new EmailTemplateContent("Hi {{ customer_name }}", "<p>{{ customer.name }}</p>", "{{ order_number }}");
        var result = await Renderer.RenderAsync(content, new ExampleModel(), CultureInfo.InvariantCulture);
        Assert.Equal("Hi Mario", result.Subject);
        Assert.Equal("<p>Ada</p>", result.HtmlBody);
        Assert.Equal("123", result.TextBody);
        var onlyText = await Renderer.RenderAsync(new EmailTemplateContent("", textBody: "Text"), new ExampleModel(), CultureInfo.InvariantCulture);
        Assert.Null(onlyText.HtmlBody);
        var onlyHtml = await Renderer.RenderAsync(new EmailTemplateContent("", htmlBody: "HTML"), new ExampleModel(), CultureInfo.InvariantCulture);
        Assert.Null(onlyHtml.TextBody);
    }

    [Fact]
    public async Task SubjectHeaderCharactersRemainTransportResponsibility()
    {
        const string subject = "Hello\r\nBcc: recipient@example.invalid";
        var result = await Renderer.RenderAsync(new EmailTemplateContent(subject, textBody: "Body"),
            new ExampleModel(), CultureInfo.InvariantCulture);

        Assert.Equal(subject, result.Subject);
    }

    [Fact]
    public async Task CultureIsExplicitAndNotMutated()
    {
        var culture = new CultureInfo("it-IT");
        var result = await Renderer.RenderAsync(new EmailTemplateContent("{{ total }}", textBody: ""), new ExampleModel(), culture);
        Assert.Equal("12,5", result.Subject);
        Assert.Equal(",", culture.NumberFormat.NumberDecimalSeparator);
        Assert.False(culture.IsReadOnly);
    }

    [Fact]
    public async Task NullRootIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Renderer.RenderAsync<ExampleModel>(
            new EmailTemplateContent("", textBody: ""), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task NullNestedObjectNeedsGuardOrNullConditional()
    {
        var model = new NullableModel();
        var safe = new EmailTemplateContent("{{ if customer }}{{ customer.name }}{{ end }}|{{ customer?.name }}", textBody: "");
        Assert.True((await Validator.ValidateAsync<NullableModel>(safe)).IsValid);
        Assert.Equal("|", (await Renderer.RenderAsync(safe, model, CultureInfo.InvariantCulture)).Subject);
        var unsafeContent = new EmailTemplateContent("{{ customer.name }}", textBody: "");
        Assert.True((await Validator.ValidateAsync<NullableModel>(unsafeContent)).IsValid);
        var error = await Assert.ThrowsAsync<TemplateRenderingException>(() => Renderer.RenderAsync(unsafeContent, model, CultureInfo.InvariantCulture));
        Assert.Equal("MSR001", error.Diagnostic.Code);
        Assert.NotNull(error.Diagnostic.Span);
    }

    [Fact]
    public async Task NullCollectionIsEmptyForIteration()
    {
        var content = new EmailTemplateContent("{{ for item in items }}{{ item.name }}{{ end }}", textBody: "");
        Assert.True((await Validator.ValidateAsync<NullableModel>(content)).IsValid);
        Assert.Equal("", (await Renderer.RenderAsync(content, new NullableModel(), CultureInfo.InvariantCulture)).Subject);
    }

    [Fact]
    public async Task DeclaredRootAndNestedTypesExcludeDerivedMembers()
    {
        BaseModel model = new DerivedModel();
        var valid = new EmailTemplateContent("{{ name }}", textBody: "");
        Assert.Equal("Base", (await Renderer.RenderAsync<BaseModel>(valid, model, CultureInfo.InvariantCulture)).Subject);
        var invalid = new EmailTemplateContent("{{ secret }}", textBody: "");
        Assert.False((await Validator.ValidateAsync<BaseModel>(invalid)).IsValid);
        await Assert.ThrowsAsync<TemplateValidationException>(() => Renderer.RenderAsync<BaseModel>(invalid, model, CultureInfo.InvariantCulture));
        var nested = new EmailTemplateContent("{{ model.secret }}", textBody: "");
        Assert.False((await Validator.ValidateAsync<Wrapper>(nested)).IsValid);
        await Assert.ThrowsAsync<TemplateValidationException>(() => Renderer.RenderAsync(nested, new Wrapper(), CultureInfo.InvariantCulture));
        var element = new EmailTemplateContent("{{ for item in items }}{{ item.secret }}{{ end }}", textBody: "");
        Assert.False((await Validator.ValidateAsync<Wrapper>(element)).IsValid);
    }

    [Theory]
    [InlineData("{{ model.name = 'changed' }}")]
    [InlineData("{{ alias = model; alias.name = 'changed' }}")]
    [InlineData("{{ items[0].name = 'changed' }}")]
    [InlineData("{{ model['name'] = 'changed' }}")]
    public async Task AssignmentsCannotMutateClrObjects(string template)
    {
        var model = new Wrapper();
        var content = new EmailTemplateContent(template, textBody: "");
        Assert.False((await Validator.ValidateAsync<Wrapper>(content)).IsValid);
        await Assert.ThrowsAsync<TemplateValidationException>(() => Renderer.RenderAsync(content, model, CultureInfo.InvariantCulture));
        Assert.Equal("Base", model.Model.Name);
        Assert.Equal("Base", model.Items[0].Name);
    }

    [Theory]
    [InlineData("get_type")]
    [InlineData("to_string")]
    [InlineData("equals")]
    [InlineData("get_hash_code")]
    public async Task ClrInfrastructureIsUnavailable(string member)
    {
        var content = new EmailTemplateContent("{{ model." + member + " }}", textBody: "");
        Assert.Contains((await Validator.ValidateAsync<Wrapper>(content)).Diagnostics, d => d.Code == "MSV003");
        await Assert.ThrowsAsync<TemplateValidationException>(() => Renderer.RenderAsync(content, new Wrapper(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ConcurrentCallsAndComponentsDoNotShareLocalState()
    {
        var content = new EmailTemplateContent("{{ x = customer_name; x }}", textBody: "{{ x = customer_name; x }}");
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
            await Renderer.RenderAsync(content, new ExampleModel { CustomerName = i.ToString(CultureInfo.InvariantCulture) }, CultureInfo.InvariantCulture))));
        for (var i = 0; i < results.Length; i++)
        {
            Assert.Equal(i.ToString(CultureInfo.InvariantCulture), results[i].Subject);
            Assert.Equal(results[i].Subject, results[i].TextBody);
        }
        var invalid = new EmailTemplateContent("{{ x = 1 }}", textBody: "{{ x }}");
        Assert.Contains((await Validator.ValidateAsync<ExampleModel>(invalid)).Diagnostics,
            d => d.Component == TemplateComponent.TextBody && d.Code == "MSV002");
    }

    [Fact]
    public async Task InterfaceContractIsSupported()
    {
        IName model = new Named();
        var content = new EmailTemplateContent("{{ name }}", textBody: "");
        Assert.True((await Validator.ValidateAsync<IName>(content)).IsValid);
        Assert.Equal("Interface", (await Renderer.RenderAsync<IName>(content, model, CultureInfo.InvariantCulture)).Subject);
    }

    [Fact]
    public async Task DeclaredScalarTypesUseDocumentedRepresentations()
    {
        var content = new EmailTemplateContent("{{ date }}|{{ id }}|{{ state }}|{{ letter }}", textBody: "");
        Assert.True((await Validator.ValidateAsync<ScalarModel>(content)).IsValid);
        Assert.Equal("2026-09-09|00000000-0000-0000-0000-000000000000|Ready|X",
            (await Renderer.RenderAsync(content, new ScalarModel(), CultureInfo.InvariantCulture)).Subject);
    }

    [Fact]
    public async Task NullCollectionAndNullValueCanBeRenderedDirectly()
    {
        var content = new EmailTemplateContent("{{ items }}|{{ customer }}", textBody: "");
        Assert.Equal("|", (await Renderer.RenderAsync(content, new NullableModel(), CultureInfo.InvariantCulture)).Subject);
    }

    [Fact]
    public async Task FieldsStaticsAndMethodsAreNotPartOfTheContract()
    {
        foreach (var text in new[] { "{{ field }}", "{{ static_value }}", "{{ method }}" })
            Assert.False((await Validator.ValidateAsync<NonProperties>(new EmailTemplateContent(text, textBody: ""))).IsValid);
    }

    public class ExampleModel
    {
        public string CustomerName { get; set; } = "Mario";
        public string OrderNumber => "123";
        public bool IsVip => true;
        public decimal Total => 12.5m;
        public int? NullableNumber => null;
        public string? NullableText => null;
        public string Markup => "<b>A & B</b>";
        public Customer Customer => new();
        public Order Order => new();
        public IReadOnlyList<Order> Groups => [new()];
    }
    public sealed class Customer { public string Name => "Ada"; public bool IsVip => true; }
    public sealed class Order { public List<Item> Items => [new("A"), new("B")]; }
    public sealed record Item(string Name);
    public sealed class NullableModel { public Customer? Customer => null; public List<Item>? Items => null; }
    public class BaseModel { public string Name { get; set; } = "Base"; }
    public sealed class DerivedModel : BaseModel { public string Secret => "Never expose"; }
    public sealed class Wrapper
    {
        public BaseModel Model { get; } = new DerivedModel();
        public List<BaseModel> Items { get; } = [new DerivedModel()];
    }
    public interface IName { string Name { get; } }
    public sealed class Named : IName { public string Name => "Interface"; public string Secret => "Hidden"; }
    public enum State { Ready }
    public sealed class ScalarModel
    {
        public DateOnly Date => new(2026, 9, 9);
        public Guid Id => Guid.Empty;
        public State State => State.Ready;
        public char Letter => 'X';
    }
    public sealed class NonProperties
    {
        public string Field = "not exposed";
        public static string StaticValue => "not exposed";
        public string Method() => "not exposed";
    }
}
