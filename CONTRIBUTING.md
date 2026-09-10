# Contributing to MailStencil

MailStencil targets .NET 10. Install the SDK selected by `global.json`, then run:

```sh
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build --no-restore
dotnet package list --vulnerable --include-transitive
```

Keep Core independent of template engines and storage SDKs. Runtime providers remain read-only;
Authoring, write, list, history, and activation capabilities require separate design. Preserve the exact
declared-model surface shared by validation and rendering, cancellation semantics, singleton safety,
sanitized logging, and restrictive storage behavior.

Changes to public contracts require a concrete pre-1.0 compatibility reason and corresponding updates to
`docs/public-api.md`, XML documentation, tests, and the public API baseline. Add focused tests for changed
behavior. Do not weaken path, parsing, sandbox, or resource-limit checks to accommodate a test environment.

Before opening a change, run the Console sample and, for ASP.NET changes, exercise the preview endpoint.
Azure provider changes should run the opt-in Azurite suite when Docker is available. See
`docs/azurite-testing.md` for the pinned emulator command.

Security reports must follow `SECURITY.md` and must not include secrets or exploit details in a public
issue.
