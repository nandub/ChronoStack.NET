# Contributing to ChronoStack.NET



First off, thank you for considering contributing to ChronoStack!



This document outlines the development workflow, architectural rules, and Pull Request (PR) process to ensure the library remains highly performant, thread-safe, and resilient.



## Development Environment Setup



Because ChronoStack multi-targets legacy and modern .NET, your development machine must meet the following requirements:

1. **Windows OS:** Required to compile the `.NET Framework 4.8` and `4.8.1` targets.
2. **.NET 8.0 SDK:** Required for the modern targets and Source Generators.
3. **.NET Framework 4.8 Developer Pack:** Required to compile the legacy targets.



### Building the Project



From the root directory, use the solution file to build all targets simultaneously:

```bash

dotnet build ChronoStack.sln -c Release

```



### Running Tests



All PRs must pass the automated test suite. We maintain 100% architectural coverage on core engine features.

```bash

dotnet test src/ChronoStack.Tests/ChronoStack.Tests.csproj

```



## Architectural Rules



If you are adding a new feature or a new `ITraceSink`, you **must** adhere to the following rules:



### 1\. The "Sink Rules" (Critical)



ChronoStack operates on a Zero-Blocking Background Dispatcher. Sinks must never crash this thread.

* **Local Sinks (Disk, OS, Memory):** Must wrap their I/O logic in a `try/catch` block and gracefully degrade.
* **Network Sinks (HTTP, SQL, TCP):** Must **NOT** use a `try/catch` block. They must allow timeout exceptions to bubble up so the `CircuitBreakerSink` can trip and protect the application from thread exhaustion.



### 2\. Native AOT Compliance



ChronoStack is 100% Native AOT compatible.

* Do not introduce `Newtonsoft.Json` or Reflection-based serialization into the modern `.NET CoreApp` execution paths.
* Always use `JsonSerializerShim.SerializeEnvelope()` which utilizes our compile-time C# Source Generators (`ChronoStackJsonContext`).



## Pull Request Process



1. **Branch:** Create a feature branch from `main` (e.g., `feature/add-redis-sink`).
2. **Code:** Implement your feature or bug fix.
3. **Test:** Add an xUnit test to `TracerTests.cs` using the `InMemorySink` to mathematically prove your logic works.
4. **Submit:** Open a PR. The automated CI/CD pipeline will verify that it builds across all target frameworks and passes all tests.
5. **Merge:** Once approved, your code will be merged. The repository maintainers will tag the next release (e.g., `v1.1.0`), and MinVer will automatically publish the new `.nupkg`.

