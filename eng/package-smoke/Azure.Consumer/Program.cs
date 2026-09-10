using Azure.Storage.Blobs;
using MailStencil;
using MailStencil.AzureBlob;
using Microsoft.Extensions.DependencyInjection;

var client = new BlobServiceClient(new Uri("https://mailstencil-package-smoke.invalid"));
using var provider = new ServiceCollection()
    .AddSingleton(client)
    .AddMailStencil(options => options.CacheDuration = TimeSpan.Zero)
    .AddScribanRenderer()
    .AddAzureBlobTemplateReader(options => options.ContainerName = "email-templates")
    .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

_ = provider.GetRequiredService<ITemplateReader>();
_ = provider.GetRequiredService<ITemplateRenderer>();
_ = provider.GetRequiredService<ITemplateValidator>();
_ = provider.GetRequiredService<IEmailTemplateService>();

Console.WriteLine("Azure package DI consumer passed without contacting storage.");
