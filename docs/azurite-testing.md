# Azure Blob tests

Normal `dotnet test -c Release` runs 66 deterministic Azure provider tests without network access.
These include Core service plus real Scriban composition using an Azure SDK client double.
They verify provider logic, not the actual Azure transport or service.

The separate Category=Azurite suite has **11 cases across 7 methods**. Without the opt-in variable,
xUnit reports seven skipped methods (the skipped theories are not expanded). Once enabled, emulator
connection/setup errors fail tests rather than silently skipping. No real account or credentials are used.

Start the official emulator on a machine with a running Docker engine:

```sh
docker run --rm -d --name mailstencil-azurite -p 127.0.0.1:10000:10000 mcr.microsoft.com/azure-storage/azurite@sha256:830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5 azurite-blob --blobHost 0.0.0.0 --silent
```

After the Blob endpoint is listening, in PowerShell:

```powershell
$env:MAILSTENCIL_AZURITE = '1'
dotnet test tests/MailStencil.AzureBlob.Tests -c Release --filter Category=Azurite
Remove-Item Env:MAILSTENCIL_AZURITE
docker stop mailstencil-azurite
```

In bash, use `MAILSTENCIL_AZURITE=1 dotnet test tests/MailStencil.AzureBlob.Tests -c Release --filter Category=Azurite`.
The [GitHub Actions job](https://github.com/polletto/MailStencil/actions/workflows/azurite.yml) starts the emulator, waits for its endpoint,
enables the suite and always cleans up. It can also be dispatched manually. The image is pinned to
the digest exercised during the Milestone 7 local run; update it deliberately after testing a newer image.

The tests use `UseDevelopmentStorage=true`, localhost port 10000 and SDK REST API version 2021-12-02
for emulator compatibility, while retaining Azure.Storage.Blobs 12.29.2. Each test owns a uniquely
named temporary container and deletes it afterwards. Only test code creates/uploads/deletes resources;
the production reader remains strictly read-only. No environment override can target a real account.

Coverage includes JSON/octet-stream content types, BOM/Unicode, metadata, exact culture/case lookup,
missing container errors, malformed content, concurrency, cancellation/version rejection and runtime
fallback/cache/HTML escaping. Unit tests provide detailed malformed-format, size and error-code cases.

Milestone 7 local verification used Docker 29.7.2 on Windows and the pinned image above. All 11
Azurite cases passed. This verifies the emulator path only; live Azure authentication, permissions,
service configuration and network behavior remain application deployment concerns.
