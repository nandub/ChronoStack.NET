# ⏱️ ChronoStack.NET

**ChronoStack.NET** is an enterprise-grade, zero-blocking diagnostics and telemetry library for C#.

It solves the classic "lost context" problem in .NET exception handling: when an exception unwinds the stack, `using` blocks and `finally` blocks execute *before* your outer `catch` handler runs, destroying valuable diagnostic scopes. ChronoStack uses `AppDomain.FirstChanceException` and `AsyncLocal<T>` to capture a high-fidelity snapshot of your application's state, memory, and timed scopes at the exact millisecond an error occurs.

## ✨ Core Enterprise Features

* **Native AOT \& Trimming Compatible:** 100% reflection-free JSON serialization using `System.Text.Json` C# Source Generators. Lightning fast and ready for modern .NET 8+ microservices.
* **Throw-Time Context Preservation:** Captures nested scopes, exact execution timings, and memory allocations before the stack unwinds.
* **Zero-Blocking Background Dispatch:** Application threads drop logs into an in-memory channel (`0.001ms`) and instantly resume. A dedicated background thread handles the disk/network I/O, providing **Log Storm Protection**.
* **Thread-Safe \& Async-Ready:** Uses `AsyncLocal` to guarantee strict isolation of Correlation IDs and scopes across concurrent web requests or `Task.Run` pipelines.
* **Cross-Platform Unification:** Seamlessly adopt Correlation IDs from parent PowerShell scripts via the `CHRONOSTACK\_CORRELATION\_ID` environment variable.
* **Ops Driver Wrapper:** Includes `ChronoDriver`, a standalone executable that wraps legacy black-box applications to monitor their exit codes and standard error output.
* **Multi-Tenancy (Tags):** Attach contextual Baggage (e.g., `UserId`, `TenantId`) to a session that automatically propagates to all error reports.
* **PII / PHI Redaction:** Built-in RegEx pipeline to automatically mask sensitive data (SSNs, Credit Cards) before it hits your logs.
* **Cloud-Native Telemetry:** Automatically syncs with `System.Diagnostics.Activity` to extract **W3C Trace Contexts** (OpenTelemetry).
* **Resiliency (Circuit Breakers):** Prevents self-DDoS by temporarily dropping logs for failing network dependencies.

\---

## 🚀 Quick Start

### 1\. Basic Initialization \& Scopes

Use `RunTimed()` to start a full diagnostic session with a unique `CorrelationId`. Use `tracer.InvokeScope()` to measure specific blocks of code.

```csharp
using ChronoStack;

using (var tracer = Tracer.CreateDefault()) // Defaults to ConsoleTraceSink
{
    var result = tracer.RunTimed(() =>
    {
        tracer.InvokeScope("API.FetchData", () => 
        {
            tracer.InvokeScope("HTTP.Get", () => throw new InvalidOperationException("500 Internal Server Error"));
        });
    });

    if (!result.Success)
    {
        Console.WriteLine(((TraceErrorReport)result.Report!).ToPrettyString());
    }
} // Background queue safely flushes on Dispose!
```

### 2\. Session Tags \& PII Redaction

Tag an entire execution session. If an error occurs, the tags are automatically attached. Configure `TracerOptions` to sanitize error messages automatically.

```csharp
var options = new TracerOptions { MessageRedactor = PiiRedactor.Redact };
using (var tracer = Tracer.Create(new ITraceSink\[] { new ConsoleTraceSink() }, options))
{
    tracer.RunTimed(() =>
    {
        tracer.AddTag("Tenant", "AcmeCorp");
        tracer.AddTag("UserId", "admin\_jsmith");

        tracer.InvokeScope("ProcessCart", () => throw new Exception("Payment failed for Card: 4111-2222-3333-4444"));
    });
}
```

### 3\. Comprehensive Initialization (All Sinks)

ChronoStack allows you to route identical, Native AOT-serialized telemetry to multiple destinations simultaneously.

```csharp
using ChronoStack;
using Microsoft.Extensions.Logging.Abstractions;

Action<string> uaLogger = json => Console.WriteLine($"\[UA] {json}");

using (var tracer = Tracer.Create(new ITraceSink\[] 
{ 
    // LOCAL / DISK SINKS (Safe, handles own file locks)
    new ConsoleTraceSink(),
    new JsonlTraceSink(@"C:\\logs\\chronostack.jsonl"),
    new EventLogTraceSink("Application", "MyAppSource", 42000),

    // FRAMEWORK SINKS (Passes data to robust external loggers)
    new Log4NetSink(log4net.LogManager.GetLogger(typeof(Program))),
    new MicrosoftExtensionsLoggingSink(NullLogger.Instance),
    
    // DELEGATE SINKS (Safely sandboxed)
    new UATraceSink(uaLogger),

    // NETWORK SINKS (Wrapped in Circuit Breakers to prevent application hangs)
    new CircuitBreakerSink(
        new SqlDatabaseSink("Server=...;Database=Logs;..."), 
        failureThreshold: 3, cooldown: TimeSpan.FromSeconds(30)
    ),
    new CircuitBreakerSink(
        new HttpTelemetrySink("https://api.datadoghq.com/v1/logs")
    )
}))
{
    tracer.RunTimed(() => { /\* Your workload \*/ });
}
```

\---

## 🛠️ The ChronoDriver (Ops Wrapper)

If your Operations or SRE team manages legacy or third-party executables (where you don't have access to the source code), you can use the **`ChronoStack.Driver`** to bring them into your telemetry ecosystem.

Instead of running a scheduled task directly:
`C:\\LegacyApps\\DataImporter.exe --force`

Update the task to use the driver:
`C:\\Tools\\ChronoDriver.exe C:\\LegacyApps\\DataImporter.exe --force`

**What happens behind the scenes:**

1. `ChronoDriver` initializes your Enterprise Sinks (JSONL, EventLog, Datadog) and starts the stopwatch.
2. It launches `DataImporter.exe` invisibly and monitors it.
3. If `DataImporter.exe` crashes with a non-zero exit code, the driver catches it.
4. **ChronoStack takes over:** It generates a `TraceErrorReport`, stamps it with `TargetExecutable = DataImporter.exe`, records the exact duration, captures the `stderr` output, and fires the payload to your observability dashboards!

\---

## 🤝 Cross-Platform Unification (PowerShell \& C#)

If your enterprise uses PowerShell orchestration scripts that call compiled C# executables, you can unify their telemetry. If the C# app crashes, it will share the **exact same Correlation ID** as the PowerShell script that launched it.

ChronoStack.NET automatically looks for the `CHRONOSTACK\_CORRELATION\_ID` environment variable at startup.

**1. In your PowerShell Script:**

```powershell
$env:CHRONOSTACK\_CORRELATION\_ID = \[guid]::NewGuid().ToString()

# Call your .NET Executable. It automatically adopts the Correlation ID!
\& "C:\\Path\\To\\MyDotNetApp.exe" --run-job

$env:CHRONOSTACK\_CORRELATION\_ID = $null
```

**2. In your C# Application:**

```csharp
using (var tracer = Tracer.CreateDefault())
{
    tracer.RunTimed(() =>
    {
        tracer.AddTag("Process", "CSharp\_Child\_Worker");
        tracer.InvokeScope("DataProcessing", () => throw new Exception("C# executable crashed!"));
    });
}
```

*Result: Your Splunk/Datadog dashboard will show the PowerShell logs and C# logs perfectly interleaved on the exact same timeline.*

\---

## 🌐 ASP.NET Core Integration

Create a global **Middleware** in your consuming ASP.NET Core application to wrap every HTTP request.

### 1\. Create the Middleware

```csharp
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using ChronoStack; 

namespace MyCompany.WebApp.Middlewares
{
    public class ChronoStackMiddleware
    {
        private readonly RequestDelegate \_next;
        private readonly Tracer \_tracer;

        public ChronoStackMiddleware(RequestDelegate next, Tracer tracer)
        {
            \_next = next;
            \_tracer = tracer;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            \_tracer.AddTag("User", context.User?.Identity?.Name ?? "Anonymous");
            \_tracer.AddTag("Path", context.Request.Path);

            var result = \_tracer.RunTimed(() => \_next(context).GetAwaiter().GetResult());

            if (!result.Success \&\& !context.Response.HasStarted)
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync($"An internal error occurred. ID: {result.CorrelationId}");
            }
        }
    }
}
```

### 2\. Register in `Program.cs`

```csharp
// Register Tracer as a Singleton so the background dispatcher runs for the app's lifetime
builder.Services.AddSingleton<Tracer>(sp => Tracer.CreateDefault());

var app = builder.Build();
app.UseMiddleware<MyCompany.WebApp.Middlewares.ChronoStackMiddleware>();
```

\---

## 📊 Dashboard Observability (Splunk, ELK, Datadog)

Because `JsonlTraceSink` and `HttpTelemetrySink` output dense **JSON Lines (JSONL)**, enterprise dashboards parse 100% of ChronoStack telemetry automatically.

### 🟢 Splunk Integration

Point a Splunk Universal Forwarder at your output directory using this `inputs.conf`:

```ini
\[monitor://C:\\logs\\chronostack\\\*.jsonl]
sourcetype = \_json
index = my\_enterprise\_apps
```

* **Find crash by ID:** `index=my\_enterprise\_apps payload.CorrelationId="<GUID>" | table \_time, payload.Error.ExceptionType, payload.Error.Message`

### 🟡 Elasticsearch / Kibana (ELK Stack)

Configure Filebeat (`filebeat.yml`) to parse the JSON natively:

```yaml
filebeat.inputs:
- type: filestream
  paths: \[ C:\\logs\\chronostack\\\*.jsonl ]
  parsers:
    - ndjson:
        target: "" 
```

* **Find High Memory Crashes (KQL):** `payload.TimedStack.AllocatedBytes > 5000000`

### 🟣 Datadog Integration

ChronoStack automatically captures the `TraceId` from `System.Diagnostics.Activity`, correlating error logs directly with Datadog APM Network Traces. Post directly to Datadog via HTTP:

```csharp
var ddUrl = $"https://http-intake.logs.datadoghq.com/api/v2/logs?dd-api-key={ddApiKey}";
var tracer = Tracer.Create(new ITraceSink\[] { new CircuitBreakerSink(new HttpTelemetrySink(ddUrl)) });
```

\---

## 🧪 Unit Testing (xUnit / NUnit)

ChronoStack includes an **`InMemorySink`** out of the box! Use it in automated tests to assert that specific errors, memory bytes, and tags were captured without hitting the disk/network.

```csharp
using ChronoStack;
using System.Linq;

\[Fact]
public void ProcessCart\_WhenFundsInsufficient\_LogsToChronoStack()
{
    var testSink = new InMemorySink();
    using (var tracer = Tracer.Create(new\[] { testSink }))
    {
        tracer.RunTimed(() => 
        {
            tracer.AddTag("Tenant", "TestTenant");
            tracer.InvokeScope("Charge", () => throw new Exception("Insufficient Funds")); 
        });
    } // Flushes queue

    testSink.CapturedReports.TryDequeue(out var rawReport);
    var report = (TraceErrorReport)rawReport;
    
    Assert.Equal("TestTenant", report.Tags\["Tenant"]);
    Assert.Contains("Charge", report.TimedStack\[0].Name);
}
```

\---

## 🛡️ Resiliency \& The "Sink Rules"

ChronoStack uses the **Circuit Breaker Pattern** to protect your application from external outages. Wrap fragile network sinks in `CircuitBreakerSink`.

|Sink Type|Has internal `try/catch`?|Wrap in `CircuitBreakerSink`?|Why?|
|-|-|-|-|
|**Local I/O** (Console, JSONL)|**Yes**|No|Local disk is fast. File locks are milliseconds long. Fails instantly.|
|**OS APIs** (EventLog)|**Yes**|No|Fails instantly if permissions are missing. No network latency.|
|**Frameworks** (log4net, MEL)|**Yes**|No|External frameworks handle their own file-locking and routing safely.|
|**Delegates** (UATraceSink)|**Yes**|No|The injected host delegate is an unknown "black box" and must be sandboxed.|
|**Network APIs** (HTTP, SQL)|**No**|**Yes**|Network calls can hang for 30+ seconds. Timeout exceptions *must* bubble up to trip the Circuit Breaker!|

\---

## 🧹 Graceful Shutdown

Because ChronoStack uses a high-performance background queue to dispatch logs, you should always wrap your `Tracer` in a `using` block (or call `tracer.Dispose()` at application exit). This intentionally blocks the main thread for up to 3 seconds to ensure any pending logs in memory are fully flushed to disk or the network before the process terminates.

```csharp
using (var tracer = Tracer.CreateDefault())
{
    // Your application logic here...
} // Background queue safely flushes here!
```

\---

## 💻 Build \& Management Script

This repository includes a unified helper script (`Manage-ChronoStack.ps1`) to Clean, Build, Test, Run, Pack, and Publish the solution. It eliminates the need to memorize complex .NET CLI flags and provides tab-completion.

**Usage Examples:**

```powershell
# Build the entire solution in Release mode
.\\Manage-ChronoStack.ps1 -Action Build -Configuration Release

# Run the Demo application using .NET 8.0
.\\Manage-ChronoStack.ps1 -Action Run -Project Demo -Framework net8.0

# Publish the Driver as a single, standalone .exe file for Windows deployments
.\\Manage-ChronoStack.ps1 -Action Publish -Project Driver -Framework net8.0 -SingleFile

# Run the xUnit automated test suite
.\\Manage-ChronoStack.ps1 -Action Test -Project Tests

# Pack the Core Library into a .nupkg file for distribution
.\\Manage-ChronoStack.ps1 -Action Pack -Project Library
```

