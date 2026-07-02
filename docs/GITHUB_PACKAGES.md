# Installing ChronoStack from GitHub Packages

ChronoStack packages are published to GitHub Packages by the release workflow. Consumers need an authenticated NuGet source because GitHub Packages requires authentication for package restore.

Official reference: https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry

## Local Developer Setup

Do not commit GitHub package credentials to this repository or to consuming application repositories. Add the authenticated source to your user-level NuGet configuration instead.

```powershell
dotnet nuget add source `
  --name github-chronostack `
  --username YOUR_GITHUB_USERNAME `
  --password YOUR_CLASSIC_PAT `
  --store-password-in-clear-text `
  "https://nuget.pkg.github.com/nandub/index.json"
```

The token needs package read access. For private packages, use a classic GitHub token with `read:packages`.

Then install the package:

```powershell
dotnet add package ChronoStack --version 1.2.0
```

## Repository NuGet.config for Consumers

If a consuming repository needs a checked-in `NuGet.config`, keep credentials out of the file and route packages to the intended source with package source mapping.

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="github-chronostack" value="https://nuget.pkg.github.com/nandub/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
    <packageSource key="github-chronostack">
      <package pattern="ChronoStack" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

Credentials should come from the user profile, CI secrets, or a temporary source added during the build.

## GitHub Actions Consumer Example

```yaml
- name: Add GitHub Packages source
  run: |
    dotnet nuget add source `
      --name github-chronostack `
      --username ${{ github.actor }} `
      --password ${{ secrets.GITHUB_TOKEN }} `
      --store-password-in-clear-text `
      "https://nuget.pkg.github.com/nandub/index.json"

- name: Restore
  run: dotnet restore
```

If the consuming workflow is in a different private repository, grant it package access or use a classic token stored as a secret with `read:packages`.

## Smoke Test

After adding the source, verify a consuming project can restore, compile, and write a JSONL event:

```csharp
using ChronoStack;

using var tracer = Tracer.Create(new ITraceSink[]
{
    new JsonlTraceSink("chronostack-smoke.jsonl")
});

tracer.RunTimed(() => throw new InvalidOperationException("package smoke"));
```
