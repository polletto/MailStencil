using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailStencil.Core.Tests;

public sealed class RuntimeTests
{
    private static EmailTemplateSource Source(TemplateRequest request, string text = "source") =>
        new(request, new EmailTemplateContent(text, textBody: text));

    private static EmailTemplateService Service(FakeReader reader, FakeRenderer renderer,
        string defaultCulture = "", TimeSpan? duration = null, TestClock? clock = null) =>
        new(reader, renderer, Options.Create(new MailStencilOptions { DefaultCulture = defaultCulture,
            CacheDuration = duration ?? TimeSpan.FromMinutes(5) }), new MemoryCacheOptions { Clock = clock });

    [Theory]
    [InlineData("it-IT", "it-IT", "it-IT")]
    [InlineData("it-IT", "it", "it-IT,it")]
    [InlineData("it-IT", null, "it-IT,it,default")]
    [InlineData("it", null, "it,default")]
    [InlineData(null, null, "default")]
    public async Task FallbackUsesExactParentChain(string? requested, string? available, string chain)
    {
        var reader = new FakeReader((r, _) => Task.FromResult(r.Culture == available ? Source(r) : null));
        var renderer = new FakeRenderer();
        using var service = Service(reader, renderer);
        await service.RenderAsync(new TemplateRequest("welcome", requested, "v3"), new Model("Ada"));
        Assert.Equal(chain.Split(','), reader.Requests.Select(r => r.Culture ?? "default"));
        Assert.All(reader.Requests, r => { Assert.Equal("welcome", r.Name); Assert.Equal("v3", r.Version); });
        Assert.Equal(requested ?? "", Assert.Single(renderer.Calls).Culture.Name);
    }

    [Fact]
    public async Task ComplexCultureUsesDotNetParents()
    {
        const string cultureName = "zh-Hant-TW";
        var expected = new List<string?>();
        for (var culture = CultureInfo.GetCultureInfo(cultureName); !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
            expected.Add(culture.Name);
        expected.Add(null);
        var reader = new FakeReader((r, _) => Task.FromResult(r.Culture is null ? Source(r) : null));
        var renderer = new FakeRenderer();
        using var service = Service(reader, renderer);
        await service.RenderAsync(new TemplateRequest("welcome", cultureName), new Model("Ada"));
        Assert.Equal(expected, reader.Requests.Select(r => r.Culture));
        Assert.Equal(cultureName, Assert.Single(renderer.Calls).Culture.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("it")]
    [InlineData("it-IT")]
    public async Task StringOverloadUsesConfiguredCulture(string culture)
    {
        var reader = new FakeReader((r, _) => Task.FromResult(r.Culture is null ? Source(r) : null));
        var renderer = new FakeRenderer();
        using var service = Service(reader, renderer, culture);
        await service.RenderAsync("welcome", new Model("Ada"));
        Assert.Equal(new TemplateRequest("welcome", culture), reader.Requests.First());
        Assert.Equal(CultureInfo.GetCultureInfo(culture).Name, Assert.Single(renderer.Calls).Culture.Name);
        Assert.Null(reader.Requests.Last().Culture);
    }

    [Fact]
    public async Task ExplicitNullCultureOverridesConfiguredAndAmbientCultures()
    {
        var reader = new FakeReader();
        var renderer = new FakeRenderer();
        using var service = Service(reader, renderer, "it-IT");
        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentUICulture = new CultureInfo("ja-JP");
            await service.RenderAsync(new TemplateRequest("welcome"), new Model("Ada"));
            Assert.Null(Assert.Single(reader.Requests).Culture);
            Assert.Equal(CultureInfo.InvariantCulture, Assert.Single(renderer.Calls).Culture);
        }
        finally { CultureInfo.CurrentCulture = original; CultureInfo.CurrentUICulture = originalUi; }
    }

    [Fact]
    public async Task FormattingRetainsRequestedCultureAfterFallback()
    {
        var reader = new FakeReader((r, _) => Task.FromResult(r.Culture == "fr" ? Source(r) : null));
        var renderer = new FakeRenderer();
        using var service = Service(reader, renderer);
        await service.RenderAsync(new TemplateRequest("welcome", "fr-CA"), new Model("Ada"));
        Assert.Equal("fr-CA", Assert.Single(renderer.Calls).Culture.Name);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("it-IT", 3)]
    public async Task OnlyCleanMissingLookupsBecomeNotFound(string? culture, int count)
    {
        var reader = new FakeReader((_, _) => Task.FromResult<EmailTemplateSource?>(null));
        var renderer = new FakeRenderer();
        using var service = Service(reader, renderer);
        var request = new TemplateRequest("welcome", culture, "v3");
        var error = await Assert.ThrowsAsync<TemplateNotFoundException>(() => service.RenderAsync(request, new Model("Ada")));
        Assert.Same(request, error.Request);
        Assert.Equal(count, reader.Requests.Count);
        Assert.Empty(renderer.Calls);
    }

    [Fact]
    public async Task PositiveSourceCachedButModelsAndRenderedOutputAreNot()
    {
        var reader = new FakeReader();
        var renderer = new FakeRenderer();
        using var service = Service(reader, renderer);
        var a = await service.RenderAsync("welcome", new Model("A"));
        var b = await service.RenderAsync("welcome", new Model("B"));
        Assert.Single(reader.Requests);
        Assert.Equal(2, renderer.Calls.Count);
        Assert.NotEqual(a.Subject, b.Subject);
        Assert.Same(renderer.Calls.First().Content, renderer.Calls.Last().Content);
    }

    [Fact]
    public async Task KeysSeparateNameCultureVersionAndCase()
    {
        var reader = new FakeReader();
        using var service = Service(reader, new FakeRenderer());
        var requests = new[] { new TemplateRequest("welcome"), new TemplateRequest("Welcome"),
            new TemplateRequest("other"), new TemplateRequest("welcome", "it"), new TemplateRequest("welcome", "it-IT"),
            new TemplateRequest("welcome", version: "v1"), new TemplateRequest("welcome", version: "V1"),
            new TemplateRequest("welcome", version: "v2") };
        foreach (var request in requests)
        {
            await service.RenderAsync(request, new Model("A"));
            await service.RenderAsync(request, new Model("B"));
        }
        Assert.Equal(requests, reader.Requests);
    }

    [Fact]
    public async Task FallbackCachesActualVariantAndNewSpecificVariantAppearsImmediately()
    {
        var specificExists = false;
        var reader = new FakeReader((r, _) => Task.FromResult(r.Culture == "it" || specificExists ? Source(r) : null));
        using var service = Service(reader, new FakeRenderer());
        var specific = new TemplateRequest("welcome", "it-IT");
        await service.RenderAsync(specific, new Model("A"));
        await service.RenderAsync(specific, new Model("A"));
        await service.RenderAsync(new TemplateRequest("welcome", "it"), new Model("A"));
        Assert.Equal(new[] { "it-IT", "it", "it-IT" }, reader.Requests.Select(r => r.Culture));
        specificExists = true;
        await service.RenderAsync(specific, new Model("A"));
        await service.RenderAsync(specific, new Model("A"));
        Assert.Equal(4, reader.Requests.Count);
        Assert.Equal("it-IT", reader.Requests.Last().Culture);
    }

    [Fact]
    public async Task CompletelyMissingLookupsAreNotCached()
    {
        var reader = new FakeReader((_, _) => Task.FromResult<EmailTemplateSource?>(null));
        using var service = Service(reader, new FakeRenderer());
        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<TemplateNotFoundException>(() => service.RenderAsync(new TemplateRequest("welcome", "it"), new Model("A")));
        Assert.Equal(4, reader.Requests.Count);
    }

    [Fact]
    public async Task ZeroDurationDisablesCaching()
    {
        var reader = new FakeReader();
        using var service = Service(reader, new FakeRenderer(), duration: TimeSpan.Zero);
        await service.RenderAsync("welcome", new Model("A"));
        await service.RenderAsync("welcome", new Model("A"));
        Assert.Equal(2, reader.Requests.Count);
    }

    [Fact]
    public async Task AbsoluteExpirationReloadsAndHitsDoNotExtendLifetime()
    {
        var clock = new TestClock();
        var text = "Old";
        var reader = new FakeReader((r, _) => Task.FromResult<EmailTemplateSource?>(Source(r, text)));
        var renderer = new FakeRenderer();
        using var service = Service(reader, renderer, clock: clock);
        await service.RenderAsync("welcome", new Model("A"));
        text = "New";
        clock.UtcNow += TimeSpan.FromMinutes(4);
        await service.RenderAsync("welcome", new Model("A"));
        Assert.Equal("Old", renderer.Calls.Last().Content.Subject);
        clock.UtcNow += TimeSpan.FromMinutes(1);
        await service.RenderAsync("welcome", new Model("A"));
        Assert.Equal("New", renderer.Calls.Last().Content.Subject);
        Assert.Equal(2, reader.Requests.Count);
    }

    [Fact]
    public async Task VeryLargeNonnegativeDurationDoesNotOverflow()
    {
        var reader = new FakeReader();
        using var service = Service(reader, new FakeRenderer(), duration: TimeSpan.MaxValue);
        await service.RenderAsync("welcome", new Model("A"));
        await service.RenderAsync("welcome", new Model("A"));
        Assert.Single(reader.Requests);
    }

    [Fact]
    public async Task StorageAndCapabilityFailuresPropagateWithoutFallbackOrCaching()
    {
        foreach (var error in new Exception[] { new IOException("storage"), new UnauthorizedAccessException(), new NotSupportedException() })
        {
            var reader = new FakeReader((_, _) => Task.FromException<EmailTemplateSource?>(error));
            using var service = Service(reader, new FakeRenderer());
            for (var i = 0; i < 2; i++)
                Assert.Same(error, await Record.ExceptionAsync(() => service.RenderAsync(new TemplateRequest("welcome", "it-IT", "v3"), new Model("A"))));
            Assert.Equal(2, reader.Requests.Count);
            Assert.All(reader.Requests, r => Assert.Equal("it-IT", r.Culture));
        }
    }

    [Fact]
    public async Task RendererFailuresAreNotCachedAndKeepTheirTypes()
    {
        var diagnostic = new TemplateDiagnostic(TemplateComponent.Subject, TemplateDiagnosticSeverity.Error, "test", "failure");
        foreach (var error in new Exception[] { new TemplateValidationException(new([diagnostic])), new TemplateRenderingException(diagnostic) })
        {
            var reader = new FakeReader();
            var renderer = new FakeRenderer { Failure = error };
            using var service = Service(reader, renderer);
            Assert.Same(error, await Record.ExceptionAsync(() => service.RenderAsync("welcome", new Model("A"))));
            renderer.Failure = null;
            await service.RenderAsync("welcome", new Model("B"));
            Assert.Single(reader.Requests); // Successfully loaded source is still cacheable.
            Assert.Equal(2, renderer.Calls.Count);
        }
    }

    [Fact]
    public async Task ReaderCancellationAndLateObservedCancellationDoNotCache()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new FakeReader((r, _) => { cancellation.Cancel(); return Task.FromResult<EmailTemplateSource?>(Source(r)); });
        var renderer = new FakeRenderer();
        using var service = Service(reader, renderer);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => service.RenderAsync("welcome", new Model("A"), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(renderer.Calls);
        reader.Load = (r, _) => Task.FromResult<EmailTemplateSource?>(Source(r));
        await service.RenderAsync("welcome", new Model("B"));
        Assert.Equal(2, reader.Requests.Count);
    }

    [Fact]
    public async Task CancelledMissingLookupStopsFallback()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new FakeReader((_, _) => { cancellation.Cancel(); return Task.FromResult<EmailTemplateSource?>(null); });
        using var service = Service(reader, new FakeRenderer());
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RenderAsync(new TemplateRequest("welcome", "it-IT"), new Model("A"), cancellation.Token));
        Assert.Single(reader.Requests);
    }

    [Fact]
    public async Task CancellationFlowsToBothDependenciesIncludingCacheHits()
    {
        var reader = new FakeReader();
        var renderer = new FakeRenderer();
        using var service = Service(reader, renderer);
        using var tokenSource = new CancellationTokenSource();
        await service.RenderAsync("welcome", new Model("A"), tokenSource.Token);
        Assert.Equal(tokenSource.Token, Assert.Single(reader.Tokens));
        Assert.Equal(tokenSource.Token, Assert.Single(renderer.Calls).Token);
        tokenSource.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RenderAsync("welcome", new Model("A"), tokenSource.Token));
        Assert.Single(renderer.Calls);
    }

    [Fact]
    public async Task RendererCancellationRemainsCancellation()
    {
        var reader = new FakeReader();
        var cancellation = new OperationCanceledException();
        var renderer = new FakeRenderer { Failure = cancellation };
        using var service = Service(reader, renderer);
        Assert.Same(cancellation, await Record.ExceptionAsync(() => service.RenderAsync("welcome", new Model("A"))));
    }

    [Fact]
    public async Task ConcurrentColdLoadsAndOneCallersCancellationRemainIndependent()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var reader = new FakeReader(async (r, ct) =>
        {
            if (Interlocked.Increment(ref count) == 2) started.SetResult();
            await release.Task.WaitAsync(ct);
            return Source(r);
        });
        var renderer = new FakeRenderer();
        using var service = Service(reader, renderer);
        using var cancellation = new CancellationTokenSource();
        var a = service.RenderAsync("welcome", new Model("A"), cancellation.Token);
        var b = service.RenderAsync("welcome", new Model("B"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); // Deadlock watchdog, not a timing assertion.
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        release.SetResult();
        Assert.Contains("B", (await b).Subject);
        await service.RenderAsync("welcome", new Model("C"));
        Assert.Equal(2, reader.Requests.Count);
        Assert.Equal(2, renderer.Calls.Count);
    }

    [Fact]
    public async Task ConcurrentCallsKeepCorrectModelAndContent()
    {
        var reader = new FakeReader();
        using var service = Service(reader, new FakeRenderer());
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            service.RenderAsync(new TemplateRequest("welcome", i % 2 == 0 ? "it" : "fr"), new Model(i.ToString(CultureInfo.InvariantCulture))))));
        for (var i = 0; i < results.Length; i++) Assert.Equal("source:" + i, results[i].Subject);
    }

    [Fact]
    public async Task OriginalContentModelAndDeclaredTypeReachRenderer()
    {
        var request = new TemplateRequest("welcome");
        var source = Source(request);
        var reader = new FakeReader((_, _) => Task.FromResult<EmailTemplateSource?>(source));
        var renderer = new FakeRenderer();
        using var service = Service(reader, renderer);
        object model = new Model("A");
        await service.RenderAsync<object>(request, model);
        var call = Assert.Single(renderer.Calls);
        Assert.Same(model, call.Model);
        Assert.Same(source.Content, call.Content);
        Assert.Equal(typeof(object), call.DeclaredType);
    }

    [Fact]
    public async Task NullArgumentsAreRejectedBeforeReading()
    {
        var reader = new FakeReader();
        using var service = Service(reader, new FakeRenderer());
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.RenderAsync((string)null!, new Model("A")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.RenderAsync((TemplateRequest)null!, new Model("A")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.RenderAsync<Model>("welcome", null!));
        Assert.Empty(reader.Requests);
        Assert.Throws<ArgumentNullException>(() => new TemplateNotFoundException(null!));
    }

    [Fact]
    public void NegativeDurationFailsOptionsValidation()
    {
        using var provider = new ServiceCollection().AddMailStencil(o => o.CacheDuration = TimeSpan.FromTicks(-1)).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<MailStencilOptions>>().Value);
    }

    [Fact]
    public async Task ContainerCachesAreIsolatedAndCustomServicesArePreserved()
    {
        using var first = new ServiceCollection().AddMailStencil().AddSingleton<ITemplateReader>(new FakeReader((r, _) => Task.FromResult<EmailTemplateSource?>(Source(r, "First"))))
            .AddSingleton<ITemplateRenderer, FakeRenderer>().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var second = new ServiceCollection().AddMailStencil().AddSingleton<ITemplateReader>(new FakeReader((r, _) => Task.FromResult<EmailTemplateSource?>(Source(r, "Second"))))
            .AddSingleton<ITemplateRenderer, FakeRenderer>().BuildServiceProvider();
        var a = first.GetRequiredService<IEmailTemplateService>();
        Assert.Same(a, first.GetRequiredService<IEmailTemplateService>());
        Assert.Equal("First:A", (await a.RenderAsync("welcome", new Model("A"))).Subject);
        Assert.Equal("Second:A", (await second.GetRequiredService<IEmailTemplateService>().RenderAsync("welcome", new Model("A"))).Subject);
        using var custom = new ServiceCollection().AddSingleton(a).AddMailStencil().AddMailStencil().BuildServiceProvider();
        Assert.Same(a, Assert.Single(custom.GetServices<IEmailTemplateService>()));
    }

    private sealed record Model(string Name);
    private sealed class FakeReader : ITemplateReader
    {
        internal ConcurrentQueue<TemplateRequest> Requests { get; } = new();
        internal ConcurrentQueue<CancellationToken> Tokens { get; } = new();
        internal Func<TemplateRequest, CancellationToken, Task<EmailTemplateSource?>> Load { get; set; }
        internal FakeReader(Func<TemplateRequest, CancellationToken, Task<EmailTemplateSource?>>? load = null) =>
            Load = load ?? ((r, _) => Task.FromResult<EmailTemplateSource?>(Source(r)));
        public Task<EmailTemplateSource?> GetAsync(TemplateRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            Tokens.Enqueue(cancellationToken);
            return Load(request, cancellationToken);
        }
    }
    private sealed record Call(EmailTemplateContent Content, object Model, Type DeclaredType, CultureInfo Culture, CancellationToken Token);
    private sealed class FakeRenderer : ITemplateRenderer
    {
        internal ConcurrentQueue<Call> Calls { get; } = new();
        internal Exception? Failure { get; set; }
        public Task<RenderedEmailTemplate> RenderAsync<TModel>(EmailTemplateContent content, TModel model, CultureInfo culture,
            CancellationToken cancellationToken = default) where TModel : notnull
        {
            Calls.Enqueue(new(content, model, typeof(TModel), culture, cancellationToken));
            return Failure is { } error ? Task.FromException<RenderedEmailTemplate>(error) :
                Task.FromResult(new RenderedEmailTemplate(content.Subject + ":" + ((Model)(object)model).Name, textBody: content.TextBody));
        }
    }
    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
    }
}
