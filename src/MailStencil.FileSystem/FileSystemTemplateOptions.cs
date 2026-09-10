namespace MailStencil.FileSystem;

/// <summary>Configuration for the exact, read-only filesystem template provider.</summary>
public sealed class FileSystemTemplateOptions
{
    /// <summary>Gets or sets the template root. Required and nonblank. Relative paths are resolved
    /// against the working directory when the singleton reader is created. No directory is created.</summary>
    public string BasePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum UTF-8 bytes per file, including any BOM. Defaults to 128 KiB.
    /// Allowed range is 1 byte through 16 MiB. Three parts bound total source bytes to three times this limit.</summary>
    public int MaxTemplateFileSize { get; set; } = 128 * 1024;
}
