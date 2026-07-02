# Release Checklist

Use this checklist for ChronoStack releases.

## Prepare

- Confirm `CHANGELOG.md` has a dated entry for the release version.
- Confirm any behavior changes are documented in `README.md`.
- Confirm `NuGet.config` restore works on a clean machine or runner.

## Validate

```powershell
dotnet restore ChronoStack.sln
dotnet list ChronoStack.sln package --vulnerable --include-transitive
dotnet build ChronoStack.sln --configuration Release --no-restore
dotnet test ChronoStack.sln --configuration Release --no-restore
dotnet pack src/ChronoStack/ChronoStack.csproj --configuration Release --no-build
pwsh -File ./scripts/Verify-ChronoStackPackage.ps1
pwsh -File ./scripts/Test-ChronoStackPackageInstall.ps1
```

## Tag

```powershell
git status --short
git tag -a vX.Y.Z -m "Release vX.Y.Z"
git push origin vX.Y.Z
```

## Verify Published Release

- Confirm the tag workflow passed.
- Confirm the package version is exactly `X.Y.Z`.
- Confirm `ChronoStack.X.Y.Z.nupkg` was published to GitHub Packages.
- Confirm both `ChronoStack.X.Y.Z.nupkg` and `ChronoStack.X.Y.Z.snupkg` are attached as workflow artifacts.
- Install the package from the published feed in a throwaway consumer project.

## NuGet.org

NuGet.org publishing is intentionally deferred. Before enabling it, add a dedicated source/API-key path and keep GitHub Packages publishing unchanged.
