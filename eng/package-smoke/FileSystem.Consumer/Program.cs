using MailStencil;
using Microsoft.Extensions.DependencyInjection;

var root = Path.Combine(Path.GetTempPath(), $"mailstencil-package-smoke-{Guid.NewGuid():N}");
var template = Path.Combine(root, "welcome", "default");

try
{
    Directory.CreateDirectory(template);
    await File.WriteAllTextAsync(Path.Combine(template, "subject.txt"), "Hello {{ customer_name }}");
    await File.WriteAllTextAsync(Path.Combine(template, "body.txt"), "Order {{ order_number }}");

    using var provider = new ServiceCollection()
        .AddMailStencil(options => options.CacheDuration = TimeSpan.Zero)
        .AddScribanRenderer()
        .AddFileSystemTemplateReader(options => options.BasePath = root)
        .BuildServiceProvider();

    var service = provider.GetRequiredService<IEmailTemplateService>();
    var rendered = await service.RenderAsync("welcome", new SmokeModel("Ada", "MS-123"));
    if (rendered.Subject != "Hello Ada" || rendered.TextBody != "Order MS-123" || rendered.HtmlBody is not null)
        throw new InvalidOperationException("The package-based FileSystem render returned unexpected content.");

    Console.WriteLine("FileSystem package consumer passed.");
}
finally
{
    if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}

internal sealed record SmokeModel(string CustomerName, string OrderNumber);
