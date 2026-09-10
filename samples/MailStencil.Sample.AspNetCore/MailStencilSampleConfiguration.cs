using MailStencil;
using MailStencil.FileSystem;

internal static class MailStencilSampleConfiguration
{
    internal static void Configure(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddMailStencil().AddScribanRenderer()
            .AddFileSystemTemplateReader(_ => { });
        builder.Services.AddOptions<MailStencilOptions>()
            .Bind(builder.Configuration.GetSection("MailStencil")).ValidateOnStart();
        builder.Services.AddOptions<FileSystemTemplateOptions>()
            .Bind(builder.Configuration.GetSection("MailStencilFileSystem"))
            .PostConfigure(options =>
            {
                // Keep empty configuration invalid; resolve relative paths against the application, not process cwd.
                if (!string.IsNullOrWhiteSpace(options.BasePath))
                    options.BasePath = Path.GetFullPath(options.BasePath, builder.Environment.ContentRootPath);
            }).ValidateOnStart();
    }
}
