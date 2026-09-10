using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailStencil.FileSystem;

internal enum ReadStage { BeforeRead, AfterChunk, AfterRead }

internal sealed class FileSystemTemplateReader : ITemplateReader
{
    private static readonly string[] PartNames = ["subject.txt", "body.html", "body.txt"];
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string basePath;
    private readonly int sizeLimit;
    private readonly Func<ReadStage, int, CancellationToken, ValueTask>? checkpoint;
    private readonly ILogger<FileSystemTemplateReader> logger;

    public FileSystemTemplateReader(IOptions<FileSystemTemplateOptions> options, ILogger<FileSystemTemplateReader> logger) : this(options, null, logger) { }

    // Instance-only seam for deterministic race/cancellation tests, not a public filesystem abstraction.
    internal FileSystemTemplateReader(IOptions<FileSystemTemplateOptions> options,
        Func<ReadStage, int, CancellationToken, ValueTask>? checkpoint, ILogger<FileSystemTemplateReader>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        if (!IsValidBasePath(value.BasePath) || value.MaxTemplateFileSize is <= 0 or > 16 * 1024 * 1024)
            throw new OptionsValidationException(Options.DefaultName, typeof(FileSystemTemplateOptions), ["Invalid filesystem template configuration."]);
        basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.BasePath));
        sizeLimit = value.MaxTemplateFileSize;
        this.checkpoint = checkpoint;
        this.logger = logger ?? NullLogger<FileSystemTemplateReader>.Instance;
    }

    internal static bool IsValidBasePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { _ = Path.GetFullPath(path); return true; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public async Task<EmailTemplateSource?> GetAsync(TemplateRequest request, CancellationToken cancellationToken = default)
    {
        try { return await ReadExactAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (FileSystemTemplateException)
        {
            logger.LogWarning(new EventId(2001, "FileSystemContentFailed"), "Filesystem template content is malformed, unsafe or unstable.");
            throw;
        }
    }

    private async Task<EmailTemplateSource?> ReadExactAsync(TemplateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Version is not null) throw new NotSupportedException("The filesystem provider does not support explicit template versions.");
        ValidateIdentifier(request.Name, nameof(request));
        if (request.Culture is not null)
        {
            ValidateIdentifier(request.Culture, nameof(request));
            if (string.Equals(request.Culture, "default", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The default directory is reserved for null culture.", nameof(request));
        }
        var variant = request.Culture ?? "default";
        var directory = ContainedPath(request.Name, variant);
        var observed = false;
        Exception? lastChange = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var before = Inspect(request, directory, variant, cancellationToken);
                if (before is null)
                {
                    if (!observed) return null;
                    throw new SnapshotChangedException();
                }
                observed = true;
                if (before.Parts[0] is null || before.Parts[1] is null && before.Parts[2] is null)
                    throw new FileSystemTemplateException(request, "An existing template requires subject.txt and at least one of body.html or body.txt.");

                await Checkpoint(ReadStage.BeforeRead, attempt, cancellationToken).ConfigureAwait(false);
                var bytes = new byte[]?[3];
                for (var i = 0; i < PartNames.Length; i++)
                    if (before.Parts[i] is { } stamp)
                        bytes[i] = await ReadPart(request, directory, PartNames[i], stamp, attempt, cancellationToken).ConfigureAwait(false);
                await Checkpoint(ReadStage.AfterRead, attempt, cancellationToken).ConfigureAwait(false);
                var after = Inspect(request, directory, variant, cancellationToken);
                if (after is null || before.Directory != after.Directory || !before.Parts.SequenceEqual(after.Parts))
                    throw new SnapshotChangedException();

                var text = new string?[3];
                for (var i = 0; i < bytes.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (bytes[i] is not { } data) continue;
                    try
                    {
                        var offset = data.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
                        text[i] = Utf8.GetString(data, offset, data.Length - offset);
                    }
                    catch (DecoderFallbackException ex)
                    {
                        throw new FileSystemTemplateException(request, $"{PartNames[i]} is not valid UTF-8.", ex);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                var lastWrite = before.Parts.Where(p => p is not null).Max(p => p!.LastWriteTimeUtc);
                return new EmailTemplateSource(request, new EmailTemplateContent(text[0]!, text[1], text[2]),
                    new TemplateMetadata { LastModified = new DateTimeOffset(DateTime.SpecifyKind(lastWrite, DateTimeKind.Utc)) });
            }
            catch (Exception ex) when (ex is SnapshotChangedException or FileNotFoundException or DirectoryNotFoundException)
            {
                // A racing disappearance after inspection is not a successful missing lookup.
                observed = true;
                lastChange = ex;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new FileSystemTemplateException(request, "Template changed or disappeared during all three snapshot attempts.", lastChange);
    }

    private Snapshot? Inspect(TemplateRequest request, string directory, string variant, CancellationToken token)
    {
        if (!CheckDirectoryChain(basePath, request, token)) return null;
        var templateDirectory = ExactEntry(basePath, request.Name, token);
        if (templateDirectory is null) return null;
        CheckEntry(templateDirectory, true, request);
        var exactDirectory = ExactEntry(templateDirectory, variant, token);
        if (exactDirectory is null) return null;
        CheckEntry(exactDirectory, true, request);
        var info = new DirectoryInfo(directory);
        var directoryStamp = new DirectoryStamp(info.CreationTimeUtc, info.LastWriteTimeUtc);
        var parts = new FileStamp?[3];
        for (var i = 0; i < parts.Length; i++)
        {
            var path = ExactEntry(directory, PartNames[i], token);
            if (path is null) continue;
            CheckEntry(path, false, request);
            var file = new FileInfo(path);
            var length = file.Length;
            if (length > sizeLimit) throw new FileSystemTemplateException(request, $"{PartNames[i]} exceeds MaxTemplateFileSize ({sizeLimit} bytes).");
            parts[i] = new FileStamp(length, file.LastWriteTimeUtc, file.CreationTimeUtc);
        }
        return new Snapshot(directoryStamp, parts);
    }

    private async Task<byte[]> ReadPart(TemplateRequest request, string directory, string part, FileStamp stamp,
        int attempt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CheckDirectoryChain(directory, request, token)) throw new SnapshotChangedException();
        var path = ContainedPath(request.Name, request.Culture ?? "default", part);
        CheckEntry(path, false, request);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != stamp.Length) throw new SnapshotChangedException();
        var bytes = new byte[checked((int)stamp.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(offset, Math.Min(8192, bytes.Length - offset)), token).ConfigureAwait(false);
            await Checkpoint(ReadStage.AfterChunk, attempt, token).ConfigureAwait(false);
            if (count == 0) throw new SnapshotChangedException();
            offset += count;
        }
        if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw new SnapshotChangedException();
        token.ThrowIfCancellationRequested();
        return bytes;
    }

    private async ValueTask Checkpoint(ReadStage stage, int attempt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (checkpoint is not null) await checkpoint(stage, attempt, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private string ContainedPath(params string[] segments)
    {
        var path = Path.GetFullPath(Path.Combine([basePath, .. segments]));
        var relative = Path.GetRelativePath(basePath, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Template path must remain inside BasePath.");
        return path;
    }

    private static void ValidateIdentifier(string value, string parameter)
    {
        if (value.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') || IsDeviceName(value))
            throw new ArgumentException("Identifiers must contain 1–128 ASCII letters/digits/hyphens/underscores, start with a letter or digit, and not be a reserved device name.", parameter);
    }

    private static bool IsDeviceName(string value) => value.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" ||
        value.Length == 4 && (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && value[3] is >= '1' and <= '9';

    private static bool CheckDirectoryChain(string path, TemplateRequest request, CancellationToken token)
    {
        var chain = new Stack<string>();
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent) chain.Push(current.FullName);
        while (chain.TryPop(out var entry))
        {
            token.ThrowIfCancellationRequested();
            try { CheckEntry(entry, true, request); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        }
        return true;
    }

    private static void CheckEntry(string path, bool directory, TemplateRequest request)
    {
        var attributes = File.GetAttributes(path); // Unlike Exists(), access failures are not hidden.
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new FileSystemTemplateException(request, "Symbolic links, reparse points and device entries are not supported in the template path.");
        if (((attributes & FileAttributes.Directory) != 0) != directory)
            throw new FileSystemTemplateException(request, "A template path has an unexpected file/directory type.");
    }

    private static string? ExactEntry(string directory, string name, CancellationToken token)
    {
        // Enforce Core's ordinal identity even on case-insensitive filesystems. No recursive traversal.
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFileName(path), name, StringComparison.Ordinal)) return path;
        }
        return null;
    }

    private sealed record FileStamp(long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc);
    private sealed record DirectoryStamp(DateTime CreationTimeUtc, DateTime LastWriteTimeUtc);
    private sealed record Snapshot(DirectoryStamp Directory, FileStamp?[] Parts);
    private sealed class SnapshotChangedException : IOException;
}
