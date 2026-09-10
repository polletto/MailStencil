using System.Globalization;
using System.Text;
using MailStencil.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailStencil.FileSystem.Tests;

public sealed class FileSystemTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(TestPaths.PhysicalTempPath, "MailStencil.FileSystem.Tests-" + Guid.NewGuid().ToString("N")));
    private readonly List<(string Path, bool Directory)> links = [];

    public FileSystemTests() => Directory.CreateDirectory(root);

    private FileSystemTemplateReader Reader(int limit = 128 * 1024,
        Func<ReadStage, int, CancellationToken, ValueTask>? hook = null, string? path = null) =>
        new(Options.Create(new FileSystemTemplateOptions { BasePath = path ?? root, MaxTemplateFileSize = limit }), hook);

    private string Variant(string name = "welcome", string culture = "default") => Path.Combine(root, name, culture);

    private async Task<string> Write(string? html = "<p>Hello</p>", string? text = "Hello", string subject = "Subject",
        string culture = "default", string name = "welcome", bool bom = false)
    {
        var directory = Variant(name, culture);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "subject.txt"), subject, new UTF8Encoding(bom));
        if (html is not null) await File.WriteAllTextAsync(Path.Combine(directory, "body.html"), html, new UTF8Encoding(bom));
        if (text is not null) await File.WriteAllTextAsync(Path.Combine(directory, "body.txt"), text, new UTF8Encoding(bom));
        return directory;
    }

    private void DeleteTree(string path)
    {
        var full = Path.GetFullPath(path);
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Test cleanup target escaped its unique temporary directory.");
        Directory.Delete(full, true);
    }

    public void Dispose()
    {
        foreach (var (path, directory) in links.AsEnumerable().Reverse())
        {
            if (directory) Directory.Delete(path);
            else File.Delete(path);
        }
        if (Directory.Exists(root)) DeleteTree(root);
    }

    [Theory]
    [InlineData("<p>Hello</p>", null)]
    [InlineData(null, "Hello")]
    [InlineData("<p>Hello</p>", "Hello")]
    [InlineData("", "")]
    public async Task ReadsAllValidPartCombinations(string? html, string? text)
    {
        await Write(html, text, "");
        var request = new TemplateRequest("welcome");
        var result = await Reader().GetAsync(request);
        Assert.NotNull(result);
        Assert.Same(request, result.Request);
        Assert.Equal("", result.Content.Subject);
        Assert.Equal(html, result.Content.HtmlBody);
        Assert.Equal(text, result.Content.TextBody);
        Assert.Null(result.Metadata!.Version);
        Assert.Null(result.Metadata.ETag);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodesUtf8WithOrWithoutBom(bool bom)
    {
        const string unicode = "Caffè 世界 👋";
        await Write(unicode, unicode, unicode, bom: bom);
        var result = await Reader().GetAsync(new("welcome"));
        Assert.Equal(unicode, result!.Content.Subject);
        Assert.Equal(unicode, result.Content.HtmlBody);
        Assert.Equal(unicode, result.Content.TextBody);
    }

    [Fact]
    public async Task StorageDoesNotParseTemplateLanguage()
    {
        await Write(subject: "{{ invalid scriban if }}");
        Assert.Equal("{{ invalid scriban if }}", (await Reader().GetAsync(new("welcome")))!.Content.Subject);
    }

    [Fact]
    public async Task LastModifiedUsesMostRecentPartInUtc()
    {
        var directory = await Write();
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(directory, "subject.txt"), start);
        File.SetLastWriteTimeUtc(Path.Combine(directory, "body.html"), start.AddDays(2));
        File.SetLastWriteTimeUtc(Path.Combine(directory, "body.txt"), start.AddDays(1));
        var result = await Reader().GetAsync(new("welcome"));
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(directory, "body.html"))), result!.Metadata!.LastModified);
        Assert.Equal(TimeSpan.Zero, result.Metadata.LastModified!.Value.Offset);
    }

    [Fact]
    public async Task ReadsOnlyRequestedCulture()
    {
        await Write(subject: "Default");
        await Write(subject: "Italian", culture: "it");
        await Write(subject: "Italy", culture: "it-IT");
        var reader = Reader();
        Assert.Equal("Italy", (await reader.GetAsync(new("welcome", "it-IT")))!.Content.Subject);
        Assert.Equal("Italian", (await reader.GetAsync(new("welcome", "it")))!.Content.Subject);
        Assert.Equal("Default", (await reader.GetAsync(new("welcome")))!.Content.Subject);
    }

    [Theory]
    [InlineData("it")]
    [InlineData("default")]
    public async Task MissingCultureDoesNotFallBack(string available)
    {
        await Write(culture: available);
        Assert.Null(await Reader().GetAsync(new("welcome", "it-IT")));
    }

    [Fact]
    public async Task NullCultureIgnoresAmbientCulture()
    {
        await Write(subject: "Default");
        await Write(subject: "Italian", culture: "it-IT");
        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("it-IT");
            CultureInfo.CurrentUICulture = new CultureInfo("it-IT");
            Assert.Equal("Default", (await Reader().GetAsync(new("welcome")))!.Content.Subject);
        }
        finally { CultureInfo.CurrentCulture = original; CultureInfo.CurrentUICulture = originalUi; }
    }

    [Fact]
    public async Task ExplicitVersionsAreRejectedEvenIfTemplateIsMissing() =>
        await Assert.ThrowsAsync<NotSupportedException>(() => Reader().GetAsync(new("welcome", version: "v1")));

    [Fact]
    public async Task MissingExactDirectoryOrBaseReturnsNull()
    {
        Assert.Null(await Reader().GetAsync(new("welcome")));
        Assert.Null(await Reader(path: Path.Combine(root, "missing-base")).GetAsync(new("welcome")));
    }

    [Theory]
    [InlineData("subject.txt")]
    [InlineData("bodies")]
    [InlineData("all")]
    public async Task ExistingIncompleteDirectoryIsNotMissing(string missing)
    {
        var directory = await Write();
        if (missing is "subject.txt" or "all") File.Delete(Path.Combine(directory, "subject.txt"));
        if (missing is "bodies" or "all")
        {
            File.Delete(Path.Combine(directory, "body.html"));
            File.Delete(Path.Combine(directory, "body.txt"));
        }
        var request = new TemplateRequest("welcome");
        var error = await Assert.ThrowsAsync<FileSystemTemplateException>(() => Reader().GetAsync(request));
        Assert.Same(request, error.Request);
    }

    [Theory]
    [InlineData("subject.txt")]
    [InlineData("body.html")]
    [InlineData("body.txt")]
    public async Task InvalidUtf8IsAProviderError(string part)
    {
        var directory = await Write();
        await File.WriteAllBytesAsync(Path.Combine(directory, part), [0xC3, 0x28]);
        var error = await Assert.ThrowsAsync<FileSystemTemplateException>(() => Reader().GetAsync(new("welcome")));
        Assert.IsType<DecoderFallbackException>(error.InnerException);
        Assert.Contains(part, error.Message);
    }

    [Fact]
    public async Task Utf16BomDoesNotEnableEncodingDetection()
    {
        var directory = await Write();
        await File.WriteAllTextAsync(Path.Combine(directory, "subject.txt"), "Hello", Encoding.Unicode);
        await Assert.ThrowsAsync<FileSystemTemplateException>(() => Reader().GetAsync(new("welcome")));
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("..\\secret")]
    [InlineData("foo/../../secret")]
    [InlineData("foo\\..\\secret")]
    [InlineData("/root/path")]
    [InlineData("C:\\Windows")]
    [InlineData("\\\\server\\share")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData("foo/..\\bar")]
    [InlineData("..")]
    [InlineData("%2e%2e%2fsecret")]
    [InlineData("welcome:stream")]
    [InlineData("welcome.")]
    [InlineData("welcome ")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public async Task UnsafeTemplateNamesAreRejected(string name) =>
        await Assert.ThrowsAsync<ArgumentException>(() => Reader().GetAsync(new(name)));

    [Theory]
    [InlineData("../secret")]
    [InlineData("..\\secret")]
    [InlineData("/root")]
    [InlineData("C:\\Windows")]
    [InlineData("it/../default")]
    public async Task CultureTraversalIsRejectedByRequestOrProvider(string culture)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await Reader().GetAsync(new("welcome", culture)));
    }

    [Fact]
    public async Task PrefixConfusionCannotEscapeBase()
    {
        var safe = Path.Combine(root, "templates");
        var evil = Path.Combine(root, "templates-evil", "welcome", "default");
        Directory.CreateDirectory(safe);
        Directory.CreateDirectory(evil);
        await File.WriteAllTextAsync(Path.Combine(evil, "subject.txt"), "secret");
        await Assert.ThrowsAsync<ArgumentException>(() => Reader(path: safe).GetAsync(new("../templates-evil/welcome")));
    }

    [Fact]
    public async Task IdentityAndPartNamesAreOrdinalEvenOnWindows()
    {
        var directory = await Write();
        Assert.Null(await Reader().GetAsync(new("Welcome")));
        await Write(culture: "IT");
        Assert.Null(await Reader().GetAsync(new("welcome", "it")));
        File.Move(Path.Combine(directory, "subject.txt"), Path.Combine(directory, "SUBJECT.TXT"));
        await Assert.ThrowsAsync<FileSystemTemplateException>(() => Reader().GetAsync(new("welcome")));
    }

    [Fact]
    public async Task WrongEntryTypesAreErrors()
    {
        var directory = await Write();
        File.Delete(Path.Combine(directory, "subject.txt"));
        Directory.CreateDirectory(Path.Combine(directory, "subject.txt"));
        await Assert.ThrowsAsync<FileSystemTemplateException>(() => Reader().GetAsync(new("welcome")));
    }

    [Fact]
    public async Task PerFileLimitAlsoBoundsAggregateWithoutAnExtraOption()
    {
        await Write("12345678", "12345678", "12345678");
        Assert.NotNull(await Reader(8).GetAsync(new("welcome"))); // 24 bytes aggregate, the exact derived maximum.
        await File.WriteAllTextAsync(Path.Combine(Variant(), "body.txt"), "123456789");
        await Assert.ThrowsAsync<FileSystemTemplateException>(() => Reader(8).GetAsync(new("welcome")));
    }

    [Fact]
    public async Task SizeLimitCountsBytesAndBom()
    {
        await Write("", null, "é", bom: true);
        Assert.NotNull(await Reader(5).GetAsync(new("welcome")));
        await Assert.ThrowsAsync<FileSystemTemplateException>(() => Reader(4).GetAsync(new("welcome")));
    }

    [Fact]
    public async Task CancelledTokenAndNullRequestKeepStandardExceptions()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => Reader().GetAsync(new("welcome"), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        await Assert.ThrowsAsync<ArgumentNullException>(() => Reader().GetAsync(null!));
    }

    [Fact]
    public async Task CancellationDuringAsyncReadIsDeterministic()
    {
        await Write(subject: new string('x', 20000));
        using var cancellation = new CancellationTokenSource();
        var reached = false;
        var reader = Reader(hook: (stage, _, _) =>
        {
            if (stage == ReadStage.AfterChunk) { reached = true; cancellation.Cancel(); }
            return ValueTask.CompletedTask;
        });
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => reader.GetAsync(new("welcome"), cancellation.Token));
        Assert.True(reached);
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task ConcurrentReadsOfSameAndDifferentTemplatesAreIndependent()
    {
        await Write(subject: "One");
        await Write(subject: "Two", name: "other");
        var reader = Reader();
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => reader.GetAsync(new(i % 2 == 0 ? "welcome" : "other"))));
        for (var i = 0; i < results.Length; i++) Assert.Equal(i % 2 == 0 ? "One" : "Two", results[i]!.Content.Subject);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSnapshotsAreRetried(bool afterRead)
    {
        var stageToChange = afterRead ? ReadStage.AfterRead : ReadStage.BeforeRead;
        var directory = await Write("old-html", "old-text", "old-subject");
        var attempts = 0;
        var reader = Reader(hook: async (stage, attempt, _) =>
        {
            if (stage == ReadStage.BeforeRead) attempts++;
            if (stage != stageToChange || attempt != 0) return;
            foreach (var part in new[] { "subject.txt", "body.html", "body.txt" })
            {
                await File.WriteAllTextAsync(Path.Combine(directory, part), "new-" + part);
                File.SetLastWriteTimeUtc(Path.Combine(directory, part), new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            }
        });
        var result = await reader.GetAsync(new("welcome"));
        Assert.Equal(2, attempts);
        Assert.Equal("new-subject.txt", result!.Content.Subject);
        Assert.Equal("new-body.html", result.Content.HtmlBody);
        Assert.Equal("new-body.txt", result.Content.TextBody);
    }

    [Fact]
    public async Task ContinuousChangesExhaustExactlyThreeAttempts()
    {
        var directory = await Write();
        var attempts = 0;
        var reader = Reader(hook: (stage, attempt, _) =>
        {
            if (stage == ReadStage.AfterRead)
            {
                attempts++;
                File.SetLastWriteTimeUtc(Path.Combine(directory, "subject.txt"), new DateTime(2030, 1, 1, 0, 0, attempt, DateTimeKind.Utc));
            }
            return ValueTask.CompletedTask;
        });
        await Assert.ThrowsAsync<FileSystemTemplateException>(() => reader.GetAsync(new("welcome")));
        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisappearanceDuringReadIsNotReportedAsInitiallyMissing(bool removeDirectory)
    {
        var directory = await Write();
        var reader = Reader(hook: (stage, attempt, _) =>
        {
            if (stage == ReadStage.BeforeRead && attempt == 0)
            {
                if (removeDirectory) DeleteTree(directory);
                else File.Delete(Path.Combine(directory, "subject.txt"));
            }
            return ValueTask.CompletedTask;
        });
        await Assert.ThrowsAsync<FileSystemTemplateException>(() => reader.GetAsync(new("welcome")));
    }

    [Fact]
    public async Task OptionalBodyAddedDuringReadIsIncludedOnRetry()
    {
        var directory = await Write(text: null);
        var reader = Reader(hook: async (stage, attempt, _) =>
        {
            if (stage == ReadStage.AfterRead && attempt == 0)
                await File.WriteAllTextAsync(Path.Combine(directory, "body.txt"), "Added");
        });
        Assert.Equal("Added", (await reader.GetAsync(new("welcome")))!.Content.TextBody);
    }

    [Fact]
    public async Task GrowingFileCannotBypassSizeLimit()
    {
        var directory = await Write("", "", "12345678");
        var reader = Reader(8, (stage, attempt, _) =>
        {
            if (stage == ReadStage.AfterChunk && attempt == 0)
            {
                using var stream = new FileStream(Path.Combine(directory, "subject.txt"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.SetLength(1000);
            }
            return ValueTask.CompletedTask;
        });
        await Assert.ThrowsAsync<FileSystemTemplateException>(() => reader.GetAsync(new("welcome")));
    }

    [Fact]
    public async Task FilesAreClosedAndLaterReadsObserveChanges()
    {
        var directory = await Write();
        var reader = Reader();
        Assert.NotNull(await reader.GetAsync(new("welcome")));
        var path = Path.Combine(directory, "subject.txt");
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            await stream.WriteAsync(Encoding.UTF8.GetBytes("Updated"));
        Assert.Equal("Updated", (await reader.GetAsync(new("welcome")))!.Content.Subject);
    }

    [Fact]
    public async Task RegistrationCreatesOneSingletonAndSnapshotsOptions()
    {
        await Write();
        var services = new ServiceCollection();
        Assert.Same(services, services.AddMailStencil().AddFileSystemTemplateReader(o => o.BasePath = root));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var reader = provider.GetRequiredService<ITemplateReader>();
        Assert.Same(reader, provider.GetRequiredService<ITemplateReader>());
        Assert.Single(provider.GetServices<ITemplateReader>());
        provider.GetRequiredService<IOptions<FileSystemTemplateOptions>>().Value.BasePath = Path.Combine(root, "missing");
        Assert.NotNull(await reader.GetAsync(new("welcome")));
        Assert.Null(provider.GetService<ITemplateRenderer>());
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IEmailTemplateService>()); // Renderer intentionally absent.
    }

    [Fact]
    public void ExistingReaderAndRepeatRegistrationAreRejected()
    {
        var services = new ServiceCollection().AddFileSystemTemplateReader(o => o.BasePath = root);
        Assert.Throws<InvalidOperationException>(() => services.AddFileSystemTemplateReader(o => o.BasePath = root));
        var custom = new ServiceCollection().AddSingleton<ITemplateReader>(Reader());
        Assert.Throws<InvalidOperationException>(() => custom.AddFileSystemTemplateReader(o => o.BasePath = root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16777217)]
    public void InvalidLimitsFailOptionsValidation(int limit)
    {
        using var provider = new ServiceCollection().AddFileSystemTemplateReader(o => { o.BasePath = root; o.MaxTemplateFileSize = limit; }).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<ITemplateReader>());
    }

    [Fact]
    public void MissingConfigurationAndNullArgumentsAreRejected()
    {
        using var provider = new ServiceCollection().AddFileSystemTemplateReader(_ => { }).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<ITemplateReader>());
        Assert.Throws<ArgumentNullException>(() => MailStencilFileSystemServiceCollectionExtensions.AddFileSystemTemplateReader(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddFileSystemTemplateReader(null!));
        Assert.Throws<ArgumentNullException>(() => new FileSystemTemplateException(null!, "error"));
        Assert.Throws<ArgumentNullException>(() => new FileSystemTemplateException(new("welcome"), null!));
    }

    [SymbolicLinkFact]
    public async Task DirectoryLinkCannotRedirectOutsideConfiguredBase()
    {
        await Write(name: "outside");
        var safe = Path.Combine(root, "templates");
        Directory.CreateDirectory(safe);
        var link = Path.Combine(safe, "welcome");
        Directory.CreateSymbolicLink(link, Path.Combine(root, "outside"));
        links.Add((link, true));
        await Assert.ThrowsAsync<FileSystemTemplateException>(() => Reader(path: safe).GetAsync(new("welcome")));
    }

    [SymbolicLinkFact]
    public async Task BaseAndAncestorLinksAreRejected()
    {
        await Write();
        var link = Path.Combine(root, "base-link");
        Directory.CreateSymbolicLink(link, Path.Combine(root, "welcome"));
        links.Add((link, true));
        await Assert.ThrowsAsync<FileSystemTemplateException>(() => Reader(path: link).GetAsync(new("welcome")));
        await Assert.ThrowsAsync<FileSystemTemplateException>(() => Reader(path: Path.Combine(link, "default")).GetAsync(new("welcome")));
    }

    [SymbolicLinkFact]
    public async Task FileLinksIncludingBrokenLinksAreRejected()
    {
        var directory = await Write();
        var target = Path.Combine(root, "external.txt");
        await File.WriteAllTextAsync(target, "Secret");
        var link = Path.Combine(directory, "body.html");
        File.Delete(link);
        File.CreateSymbolicLink(link, target);
        links.Add((link, false));
        await Assert.ThrowsAsync<FileSystemTemplateException>(() => Reader().GetAsync(new("welcome")));
        File.Delete(target);
        await Assert.ThrowsAsync<FileSystemTemplateException>(() => Reader().GetAsync(new("welcome")));
    }

    [SymbolicLinkFact]
    public async Task LinkIntroducedBetweenInspectionAndReadFailsClosed()
    {
        var directory = await Write();
        var outside = await Write(name: "outside");
        var reader = Reader(hook: (stage, attempt, _) =>
        {
            if (stage == ReadStage.BeforeRead && attempt == 0)
            {
                Directory.Move(directory, Path.Combine(root, "backup"));
                Directory.CreateSymbolicLink(directory, outside);
                links.Add((directory, true));
            }
            return ValueTask.CompletedTask;
        });
        await Assert.ThrowsAsync<FileSystemTemplateException>(() => reader.GetAsync(new("welcome")));
    }

    [Fact]
    public async Task AtomicFileReplacementDuringReadCausesRetry()
    {
        var directory = await Write();
        var reader = Reader(hook: async (stage, attempt, _) =>
        {
            if (stage == ReadStage.AfterChunk && attempt == 0)
            {
                var temp = Path.Combine(directory, "subject.tmp");
                await File.WriteAllTextAsync(temp, "Replacement");
                File.SetLastWriteTimeUtc(temp, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.Replace(temp, Path.Combine(directory, "subject.txt"), null);
            }
        });
        Assert.Equal("Replacement", (await reader.GetAsync(new("welcome")))!.Content.Subject);
    }

    [Fact]
    public async Task RelativeBasePathIsCanonicalizedWithoutCreatingDirectories()
    {
        await Write();
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, root);
        Assert.NotNull(await Reader(path: relative).GetAsync(new("welcome")));
    }
}

public sealed class SymbolicLinkFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> UnavailableReason = new(Probe);
    public SymbolicLinkFactAttribute() => Skip = UnavailableReason.Value;

    private static string? Probe()
    {
        var directory = Path.GetFullPath(Path.Combine(TestPaths.PhysicalTempPath, "MailStencil.LinkProbe-" + Guid.NewGuid().ToString("N")));
        var link = Path.Combine(directory, "link");
        Directory.CreateDirectory(directory);
        try
        {
            Directory.CreateSymbolicLink(link, directory);
            Directory.Delete(link);
            return null;
        }
        catch (UnauthorizedAccessException) { return "Host does not grant symbolic-link creation permission."; }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == 1314) { return "Windows symbolic-link privilege/developer mode is unavailable."; }
        finally { Directory.Delete(directory); }
    }
}

internal static class TestPaths
{
    // macOS exposes /var as a symlink to /private/var. Tests use the physical spelling so the
    // provider's intentional rejection of symlink ancestors does not make the test root unsafe.
    internal static string PhysicalTempPath
    {
        get
        {
            var path = Path.GetFullPath(Path.GetTempPath());
            return OperatingSystem.IsMacOS() &&
                (path.Equals("/var", StringComparison.Ordinal) || path.StartsWith("/var/", StringComparison.Ordinal))
                ? "/private" + path
                : path;
        }
    }
}
