# Release process

MailStencil uses Semantic Versioning. Before 1.0, minor versions may contain documented breaking API or
behavior changes; patch versions contain compatible fixes. Prerelease labels identify packages that are
not yet covered by a stable compatibility promise. After 1.0, breaking changes require a new major version.

The default repository version is centralized in `Directory.Build.props` as `VersionPrefix` and
`VersionSuffix`. CI and release candidates may override it once:

```sh
dotnet pack -c Release --no-build --no-restore \
  -p:Version=0.1.0-preview.1 -o artifacts/packages
```

A release candidate must pass restore, Release build/test, dependency audit, package inspection, both
local-package consumer smoke tests, samples, and cross-platform CI. The four runtime packages are packed
together at one version. Test, sample, and tooling projects remain non-packable. Create a tag only after
package contents and changelog are final; the intended first tag is `v0.1.0-preview.1`.

The tag-only `.github/workflows/release.yml` workflow builds, tests, inspects and publishes the four
packages from the immutable `v0.1.0-preview.1` tag. Its verification job has read-only repository access.
Only its `publish` job uses the protected `release` environment and `id-token: write`; it exchanges the
GitHub OIDC token for a short-lived NuGet API key immediately before pushing the verified artifacts.

Before creating the tag, the owner must:

1. confirm the repository's private vulnerability-reporting route and update `SECURITY.md` if needed;
2. recheck package-ID ownership immediately before the first push;
3. create a GitHub environment named `release`, add required reviewers, prevent administrator bypass and
   restrict deployment to tags matching `v*`;
4. define the `NUGET_USER` configuration variable in that environment as the NuGet.org profile name;
5. configure a NuGet.org trusted-publishing policy with owner `polletto`, repository `MailStencil`, workflow
   file `release.yml`, environment `release`, and the intended NuGet.org package owner; restrict its scope
   to the four MailStencil IDs (or the narrow `MailStencil.*` pattern) and allow publishing new packages and
   package versions;
6. verify the protected environment and trusted-publishing policy before creating the tag.

The repository URL is `https://github.com/polletto/MailStencil`, and packages use the MIT license expression
corresponding to the root `LICENSE` file.

The normal CI and package jobs are intentionally nonpublishing and use read-only repository permissions.
The release workflow cannot be dispatched manually and does not use or require a stored NuGet API key.
