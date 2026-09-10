# Console sample

Run from the solution root:

```sh
dotnet run --project samples/MailStencil.Sample.Console -c Release
```

The sample registers options, the FileSystem reader and Scriban renderer/validator through DI.
It loads `templates/order-confirmation/default/` from its output directory, validates the declared
model, renders Subject/HTML/Text, and prints them. Template files are copied during build.
HTML uses explicit `html.escape` for untrusted string values. Ctrl+C requests cancellation.

No IEmailTemplateService, fallback, cache or writing is used. Symlinks/reparse points in the sample
output path are rejected by the provider, just as in an application deployment.
