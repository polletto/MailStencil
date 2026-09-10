using MailStencil;
using Microsoft.Extensions.DependencyInjection;

using var services = new ServiceCollection()
    .AddMailStencil(options => options.DefaultCulture = "en-US")
    .AddScribanRenderer()
    .AddFileSystemTemplateReader(options => options.BasePath = Path.Combine(AppContext.BaseDirectory, "templates"))
    .BuildServiceProvider();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var service = services.GetRequiredService<IEmailTemplateService>();
var rendered = await service.RenderAsync("order-confirmation",
    new OrderConfirmationModel("Ada & friends", "MS-123", 42.50m), cancellation.Token);
Console.WriteLine($"Subject: {rendered.Subject}");
Console.WriteLine($"HTML: {rendered.HtmlBody}");
Console.WriteLine($"Text: {rendered.TextBody}");
return 0;

internal sealed record OrderConfirmationModel(string CustomerName, string OrderNumber, decimal Total);
