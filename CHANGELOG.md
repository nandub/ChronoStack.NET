This follows the widely accepted [Keep a Changelog](https://keepachangelog.com/) format. I have gone ahead and documented the massive **v1.0.0** release we just finished!

# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

* Added a consumer install smoke test that validates the packed NuGet package from a temporary `net8.0` application.
* Documented the opt-in `IncludeEnvironmentInfo` behavior and the kind of host/process metadata it captures.

### Security

* Replaced hand-built OTLP JSON payload construction with typed serialization.

## [1.2.0] - 2026-07-02

### Added

* Added repository-level `NuGet.config` so restore behavior is deterministic on machines without preconfigured package sources.
* Added `AGENTS.md` guidance for future AI/code agents working in the repository.
* Added NXLog documentation for shipping `JsonlTraceSink` output through an external log agent.
* Added reusable NXLog Windows and Linux example configuration files under `examples/nxlog`.
* Added CI package vulnerability auditing with `dotnet list package --vulnerable --include-transitive`.
* Updated test infrastructure package references to current stable versions to remove vulnerable transitive test dependencies.
* Added NuGet package verification for expected assemblies, XML documentation, README, license, repository metadata, and symbol packages.
* Added SourceLink, repository metadata, deterministic build settings, and symbol package generation for NuGet packages.

### Changed

* `IncludeEnvironmentInfo` now defaults to `false` so host and process metadata are opt-in.
* Expanded redaction to cover exception messages, sources, stack file paths, timed scope names, timed frame paths, and trace tags.
* Hardened `ChronoStack.Driver` process execution by preserving argument boundaries, capping captured output, adding a configurable timeout, and avoiding raw argument logging.

### Security

* Validated and quoted `SqlDatabaseSink` table identifiers to prevent SQL identifier injection.
* Escaped OTLP string fields before embedding them in JSON payloads.
* Added regex match timeouts to redaction rules to reduce catastrophic-backtracking risk.

## [1.0.0] - 2026-04-23

### Added

* **Core Engine:** Initial release of the `ChronoStack.NET` diagnostics engine utilizing `AsyncLocal<T>` for thread-safe context propagation.
* **Throw-Time Snapshots:** Implemented `AppDomain.FirstChanceException` and `InvokeScope` to preserve timed frames before `finally` blocks unwind the stack.
* **Native AOT Support:** Fully integrated `System.Text.Json` Source Generators for reflection-free, lightning-fast serialization.
* **Multi-Targeting:** Added support for `.NET Framework 4.7.2`, `4.8`, `4.8.1`, `.NET 6.0`, and `.NET 8.0`.
* **Zero-Blocking Dispatch:** All logs are now written asynchronously via a high-performance `BlockingCollection` background thread with Log Storm Protection (10k limit).
* **Enterprise Sinks:**

  * `ConsoleTraceSink` (stdout/stderr)
  * `JsonlTraceSink` (Append-only JSON Lines)
  * `EventLogTraceSink` (Windows Event Viewer)
  * `UATraceSink` (Universal Automation delegate)
  * `HttpTelemetrySink` (Datadog/Splunk HTTP Intake)
  * `SqlDatabaseSink` (ADO.NET Parameterized SQL)
  * `Log4NetSink` & `MicrosoftExtensionsLoggingSink`
* **Resiliency:** Implemented the `CircuitBreakerSink` decorator to protect background threads from network timeouts (HTTP/SQL).
* **Security:** Added `PiiRedactor` to automatically mask SSNs and Credit Cards in exception messages.
* **Observability:** Added memory allocation tracking (`GC.GetAllocatedBytesForCurrentThread`) and W3C Trace Context (OpenTelemetry) extraction.
* **Cross-Platform:** Implemented PowerShell Unification via the `CHRONOSTACK_CORRELATION_ID` environment variable.
* **Testing:** Provided `InMemorySink` for unit testing downstream applications.
