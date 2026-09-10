# Azure Blob template reader

The application supplies a `BlobServiceClient`; MailStencil never constructs credentials, creates
containers, or writes blobs. Each exact template variant is one bounded, versioned JSON document.

```csharp
services.AddSingleton(blobServiceClient);
services.AddMailStencil().AddScribanRenderer()
    .AddAzureBlobTemplateReader(o =>
    {
        o.ContainerName = "email-templates";
        o.Prefix = "mailstencil";
    });
```

The blob path is `<prefix>/<name>/<culture-or-default>/template.json`. The JSON object contains
`formatVersion`, `subject`, optional `htmlBody`, and optional `textBody`. Only Azure's exact
`BlobNotFound` condition returns `null`; authentication, authorization, throttling, container absence,
network, and other service failures remain Azure SDK exceptions.

See the [MailStencil repository](https://github.com/polletto/MailStencil) for the JSON format,
localization behavior, security limits, and Azurite test instructions.
