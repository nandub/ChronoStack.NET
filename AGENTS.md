# AGENTS.md

Guidance for AI agents working in this repository.

## Project Shape

- Solution: `ChronoStack.sln`
- Main library: `src/ChronoStack`
- Driver wrapper: `src/ChronoStack.Driver`
- Demo app: `src/ChronoStack.Demo`
- Tests: `src/ChronoStack.Tests`
- NXLog shipping guide: `docs/NXLOG.md`
- Supported target frameworks include .NET Framework and modern .NET. Keep changes compatible with `net48`, `net481`, `net6.0`, and `net8.0` unless the user explicitly changes support.

## Security Conventions

- Do not build SQL statements with untrusted identifiers. Values must use parameters; identifiers must be allowlisted or strictly validated and quoted.
- Do not interpolate untrusted strings directly into JSON, syslog, command lines, or telemetry payloads. Use serializers or explicit escaping.
- Treat exception messages, tags, stderr, file paths, and environment metadata as potentially sensitive. Redaction must apply before data reaches sinks.
- Keep environment/host metadata opt-in unless a caller explicitly enables it.
- Bound regex evaluation with timeouts when regexes can be configured or applied to runtime data.
- For process execution, preserve argument boundaries and cap captured output. Avoid logging raw command arguments because they often contain secrets.

## Serialization

- Modern .NET paths should preserve Native AOT-friendly source-generated serialization through `ChronoStackJsonContext`.
- Do not introduce reflection-heavy JSON serialization in modern `.NETCoreApp` paths unless there is a clear compatibility reason.
- If new telemetry model types are added, register them in `SerializationContext.cs`.

## Verification

The repo includes `NuGet.config` with `nuget.org` as the deterministic package source. Use normal restore/build/test commands from the solution root.

Run focused tests after code changes:

```powershell
dotnet test
```

Run a build when public APIs, target frameworks, or project files change:

```powershell
dotnet build
```

After package metadata or release-surface changes, verify the NuGet artifact:

```powershell
dotnet pack src/ChronoStack/ChronoStack.csproj --configuration Release --no-build
pwsh -File ./scripts/Verify-ChronoStackPackage.ps1
```

If a full multi-target test run is too slow or blocked, call that out and run the narrowest relevant target.
