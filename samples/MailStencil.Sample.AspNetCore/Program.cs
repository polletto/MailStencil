using System.Globalization;
using MailStencil;
using MailStencil.AzureBlob;
using MailStencil.FileSystem;

var builder = WebApplication.CreateBuilder(args);
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

var app = builder.Build();
app.MapGet("/preview/order-confirmation", async (IEmailTemplateService templates, string? culture, CancellationToken cancellationToken) =>
{
    try
    {
        var model = new OrderConfirmationModel { CustomerName = "Ada & friends", OrderNumber = "MS-123", Total = 42.50m };
        var result = culture is null
            ? await templates.RenderAsync("order-confirmation", model, cancellationToken)
            : await templates.RenderAsync(new TemplateRequest("order-confirmation", culture), model, cancellationToken);
        return Results.Ok(result);
    }
    catch (CultureNotFoundException) { return Results.Problem(statusCode: 400, title: "Invalid culture.", type: "urn:mailstencil:invalid-culture"); }
    catch (TemplateNotFoundException) { return Results.Problem(statusCode: 404, title: "Template unavailable.", type: "urn:mailstencil:template-not-found"); }
    catch (TemplateValidationException) { return Results.Problem(statusCode: 500, title: "Stored template failed validation.", type: "urn:mailstencil:template-validation"); }
    catch (TemplateRenderingException) { return Results.Problem(statusCode: 500, title: "Template could not be rendered.", type: "urn:mailstencil:template-rendering"); }
    catch (FileSystemTemplateException) { return Results.Problem(statusCode: 500, title: "Filesystem template content is invalid.", type: "urn:mailstencil:filesystem-content"); }
    catch (AzureBlobTemplateException) { return Results.Problem(statusCode: 500, title: "Blob template content is invalid.", type: "urn:mailstencil:azure-content"); }
    catch (Azure.RequestFailedException) { return Results.Problem(statusCode: 503, title: "Template storage is unavailable.", type: "urn:mailstencil:storage-unavailable"); }
    catch (IOException) { return Results.Problem(statusCode: 503, title: "Template storage is unavailable.", type: "urn:mailstencil:storage-unavailable"); }
    catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 503, title: "Template storage is unavailable.", type: "urn:mailstencil:storage-unavailable"); }
});
app.Run();

/// <summary>ASP.NET sample entry point, exposed for in-process application integration tests.</summary>
public partial class Program { }

/// <summary>Example declared template contract. Scriban exposes its properties in snake_case.</summary>
public sealed class OrderConfirmationModel
{
    /// <summary>Gets or initializes the display name, explicitly escaped in the HTML template.</summary>
    public required string CustomerName { get; init; }
    /// <summary>Gets or initializes the order's display reference.</summary>
    public required string OrderNumber { get; init; }
    /// <summary>Gets or initializes the total formatted using the requested culture.</summary>
    public decimal Total { get; init; }
}
