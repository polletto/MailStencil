using MailStencil.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MailStencil.Scriban.Tests;

public sealed class RuntimeIntegrationTests : IDisposable
{
    private static readonly string TempPath = PhysicalTempPath();
    private readonly string root = Path.GetFullPath(Path.Combine(TempPath, "MailStencil.Runtime.Tests-" + Guid.NewGuid().ToString("N")));

    public RuntimeIntegrationTests() => Directory.CreateDirectory(Path.Combine(root, "welcome", "default"));

    private async Task Write(string subject)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "welcome", "default", "subject.txt"), subject);
        await File.WriteAllTextAsync(Path.Combine(root, "welcome", "default", "body.txt"), "Body");
    }

    private ServiceProvider Register(string order = "MSF")
    {
        var services = new ServiceCollection();
        foreach (var registration in order)
            switch (registration)
            {
                case 'M': services.AddMailStencil(o => o.DefaultCulture = "it-IT"); break;
                case 'S': services.AddScribanRenderer(); break;
                case 'F': services.AddFileSystemTemplateReader(o => o.BasePath = root); break;
            }
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Theory]
    [InlineData("MSF")]
    [InlineData("MFS")]
    [InlineData("SMF")]
    [InlineData("SFM")]
    [InlineData("FMS")]
    [InlineData("FSM")]
    public async Task RegistrationOrderDoesNotMatter(string order)
    {
        await Write("Hi {{ name }}");
        using var provider = Register(order);
        var service = provider.GetRequiredService<IEmailTemplateService>();
        Assert.Equal("Hi Ada", (await service.RenderAsync("welcome", new Person("Ada"))).Subject);
        Assert.Equal("Hi Grace", (await service.RenderAsync("welcome", new Person("Grace"))).Subject);
        Assert.Null(await provider.GetRequiredService<ITemplateReader>().GetAsync(new TemplateRequest("welcome", "it-IT")));
    }

    [Fact]
    public async Task DirectReaderBypassesServiceCache()
    {
        await Write("Old");
        using var provider = Register();
        var service = provider.GetRequiredService<IEmailTemplateService>();
        await service.RenderAsync("welcome", new Person("Ada"));
        await Write("New");
        var direct = await provider.GetRequiredService<ITemplateReader>().GetAsync(new TemplateRequest("welcome"));
        Assert.Equal("New", direct!.Content.Subject);
        Assert.Equal("Old", (await service.RenderAsync("welcome", new Person("Ada"))).Subject);
    }

    [Fact]
    public async Task FileSystemFailurePropagates()
    {
        using var provider = Register(); // Existing variant is incomplete.
        await Assert.ThrowsAsync<FileSystemTemplateException>(() => provider.GetRequiredService<IEmailTemplateService>()
            .RenderAsync("welcome", new Person("Ada")));
    }

    [Fact]
    public async Task ScribanValidationFailurePropagates()
    {
        await Write("{{ unknown }}");
        using var provider = Register();
        await Assert.ThrowsAsync<TemplateValidationException>(() => provider.GetRequiredService<IEmailTemplateService>()
            .RenderAsync("welcome", new Person("Ada")));
    }

    [Fact]
    public async Task ScribanRuntimeFailurePropagates()
    {
        await Write("{{ details.name }}");
        using var provider = Register();
        await Assert.ThrowsAsync<TemplateRenderingException>(() => provider.GetRequiredService<IEmailTemplateService>()
            .RenderAsync("welcome", new Person("Ada")));
    }

    public void Dispose()
    {
        var expected = TempPath;
        var relative = Path.GetRelativePath(expected, root);
        if (Path.IsPathRooted(relative) || relative.Contains(Path.DirectorySeparatorChar) || !relative.StartsWith("MailStencil.Runtime.Tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe test cleanup target.");
        Directory.Delete(root, true);
    }

    public sealed record Person(string Name) { public Person? Details => null; }

    private static string PhysicalTempPath()
    {
        var path = Path.GetFullPath(Path.GetTempPath());
        return OperatingSystem.IsMacOS() &&
            (path.Equals("/var", StringComparison.Ordinal) || path.StartsWith("/var/", StringComparison.Ordinal))
            ? "/private" + path
            : path;
    }
}
