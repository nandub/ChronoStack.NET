using System;
using System.IO;
using System.Reflection;
using System.Threading;
using ChronoStack;

internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("=== ChronoStack Demo ===\n");

        Demo_NoInstrumentation();
        Demo_TimedFrames_ExplicitCatch();
        Demo_TimedFrames_InvokeScope();
        Demo_JsonlSink();
        
        Console.WriteLine("\n--- Extended Sinks ---");
        Demo_HttpTelemetrySink();
        Demo_Log4NetSink_Basic();
        Demo_Log4NetSink_Xml();
        
        Console.WriteLine("\n--- Enterprise Features ---");
        Demo_EnterpriseFeatures();
        Demo_CircuitBreaker();
        Demo_UATraceSink();
		Demo_PowerShellUnification();
    }

    private static void Demo_NoInstrumentation()
    {
        Console.WriteLine("\n1) No-instrumentation Mode:");
        using (var tracer = Tracer.CreateDefault())
        {
            var result = tracer.Run(() => File.ReadAllText(@"C:\MissingFile.txt"));
            if (!result.Success)
                Console.WriteLine(((ErrorReport)result.Report!).ToPrettyString());
        }
    }

    private static void Demo_TimedFrames_ExplicitCatch()
    {
        Console.WriteLine("\n2) Timed Frames (Explicit Snapshot):");
        using (var tracer = Tracer.CreateDefault())
        {
            var result = tracer.RunTimed(() =>
            {
                using (var scope = tracer.Scope("Database.Connect"))
                {
                    try { throw new TimeoutException("Connection dropped."); }
                    catch
                    {
                        scope.SnapshotOnError();
                        throw;
                    }
                }
            });
            if (!result.Success)
                Console.WriteLine(((TraceErrorReport)result.Report!).ToPrettyString());
        }
    }

    private static void Demo_TimedFrames_InvokeScope()
    {
        Console.WriteLine("\n3) Timed Frames (Clean InvokeScope):");
        using (var tracer = Tracer.CreateDefault())
        {
            var result = tracer.RunTimed(() =>
            {
                tracer.InvokeScope("API.FetchData", () => 
                {
                    tracer.InvokeScope("HTTP.Get", () => throw new InvalidOperationException("500 Internal Server Error"));
                });
            });
            if (!result.Success)
                Console.WriteLine(((TraceErrorReport)result.Report!).ToPrettyString());
        }
    }

    private static void Demo_JsonlSink()
    {
        Console.WriteLine("\n4) JSONL Sink Demo:");
        var path = Path.Combine(Path.GetTempPath(), "chronostack.jsonl");
        
        using (var tracer = Tracer.Create(new ITraceSink[] { new JsonlTraceSink(path), new ConsoleTraceSink() }))
        {
            var result = tracer.RunTimed(() => 
            {
                tracer.InvokeScope("Data.Parse", () => int.Parse("Not a number"));
            });
            
            if (!result.Success)
                Console.WriteLine(((TraceErrorReport)result.Report!).ToPrettyString());
                
            Console.WriteLine($"\nWrote full JSONL payload to: {path}");
        }
    }

    private static void Demo_HttpTelemetrySink()
    {
        Console.WriteLine("\n5) HTTP Telemetry Sink Demo:");
        var dummyUrl = "http://localhost:9999/api/telemetry/ingest";
        
        using (var tracer = Tracer.Create(new ITraceSink[] { new HttpTelemetrySink(dummyUrl), new ConsoleTraceSink() }))
        {
            Console.WriteLine($"Attempting to POST error telemetry to {dummyUrl} in the background...");
            var result = tracer.RunTimed(() =>
            {
                tracer.InvokeScope("CloudSync.Upload", () => throw new UnauthorizedAccessException("Invalid API Key for Cloud Sync."));
            });

            if (!result.Success)
                Console.WriteLine(((TraceErrorReport)result.Report!).ToPrettyString());
        }
    }

    private static void Demo_Log4NetSink_Basic()
    {
        Console.WriteLine("\n6a) log4net Sink Demo (BasicConfigurator):");
        
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var repo = log4net.LogManager.GetRepository(assembly);
        
        log4net.LogManager.ResetConfiguration(assembly);
        log4net.Config.BasicConfigurator.Configure(repo);
        var log = log4net.LogManager.GetLogger(typeof(Program));
        
        using (var tracer = Tracer.Create(new ITraceSink[] { new Log4NetSink(log) }))
        {
            tracer.RunTimed(() =>
            {
                tracer.InvokeScope("Auth.Login", () => throw new UnauthorizedAccessException("Basic Configurator: Login failed."));
            });
        }
    }

    private static void Demo_Log4NetSink_Xml()
    {
        Console.WriteLine("\n6b) log4net Sink Demo (XML Configured):");
        
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var repo = log4net.LogManager.GetRepository(assembly);

        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        var configFile = new FileInfo(Path.Combine(basePath, "log4net.config"));

        log4net.LogManager.ResetConfiguration(assembly);
        
        if (configFile.Exists)
        {
            log4net.Config.XmlConfigurator.Configure(repo, configFile);
        }
        else
        {
            Console.WriteLine($"WARNING: log4net.config not found at {configFile.FullName}");
            return;
        }
        
        var log = log4net.LogManager.GetLogger(typeof(Program));
        
        using (var tracer = Tracer.Create(new ITraceSink[] { new Log4NetSink(log) }))
        {
            tracer.RunTimed(() =>
            {
                tracer.InvokeScope("PaymentProcessor.Charge", () => throw new InvalidOperationException("XML Configurator: Insufficient funds."));
            });
        }
        
        Console.WriteLine("log4net successfully processed the error using XML (check your console and the /logs folder).");
    }

    private static void Demo_EnterpriseFeatures()
    {
        Console.WriteLine("\n7) Enterprise Features (PII Redaction, Memory Tracking, OTel):");
        var activity = new System.Diagnostics.Activity("Incoming_HttpRequest").Start();

        var options = new TracerOptions { MessageRedactor = PiiRedactor.Redact };
        
        using (var tracer = Tracer.Create(new ITraceSink[] { new ConsoleTraceSink() }, options))
        {
            var result = tracer.RunTimed(() =>
            {
                tracer.AddTag("Tenant", "AcmeCorporation");
                tracer.AddTag("UserId", "admin_jsmith");

                tracer.InvokeScope("Process.Payroll", () => 
                {
                    var dummyData = new byte[5 * 1024 * 1024]; 
                    throw new InvalidOperationException("Failed to process transaction for SSN: 000-12-3456 with Card: 4111-2222-3333-4444.");
                });
            });

            if (!result.Success)
                Console.WriteLine(((TraceErrorReport)result.Report!).ToPrettyString());
        }
        activity.Stop();
    }

    private static void Demo_CircuitBreaker()
    {
        Console.WriteLine("\n8) Circuit Breaker Sink Demo:");
        
        var fragileSink = new MockFragileSink();
        var breakerSink = new CircuitBreakerSink(fragileSink, failureThreshold: 3, cooldown: TimeSpan.FromSeconds(2));
        
        using (var tracer = Tracer.Create(new ITraceSink[] { breakerSink }))
        {
            Console.WriteLine("Firing 5 rapid errors. The breaker should trip after the 3rd...");
            for (int i = 1; i <= 5; i++)
            {
                tracer.Run(() => throw new Exception("Database Timeout"));
                Thread.Sleep(50); 
            }
        }
    }

    private class MockFragileSink : ITraceSink
    {
        public void Write(TraceSeverity severity, object report, TracerOptions options)
        {
            Console.WriteLine("    [MockFragileSink] Attempting to write over the network... FAILED!");
            throw new TimeoutException("The network path was not found.");
        }
    }

    private static void Demo_UATraceSink()
    {
        Console.WriteLine("\n10) Universal Automation (UA) Sink Demo:");
        
        // 1. Simulate a logging function provided by a host automation platform.
        // In the real world, this could be a named pipe, a WebSocket, or a specific UA SDK method.
        Action<string> uaLogDelegate = (jsonPayload) => 
        {
            Console.WriteLine($"[UA_INGEST] Successfully intercepted compact JSON payload:");
            Console.WriteLine(jsonPayload);
        };

        // 2. Wire up the sink by passing in the delegate
        using (var tracer = Tracer.Create(new ITraceSink[] { new UATraceSink(uaLogDelegate) }))
        {
            tracer.RunTimed(() =>
            {
                tracer.InvokeScope("UA.Job.Run", () => 
                {
                    tracer.InvokeScope("UA.Step.Initialize", () => 
                        throw new InvalidOperationException("Host environment missing required UA variables."));
                });
            });
        }
    }
	
	private static void Demo_PowerShellUnification()
    {
        Console.WriteLine("\n11) PowerShell + C# Unification Demo:");
        
        // 1. Simulate the PowerShell script setting the environment variable
        var psCorrelationId = Guid.NewGuid();
        Environment.SetEnvironmentVariable("CHRONOSTACK_CORRELATION_ID", psCorrelationId.ToString());
        
        Console.WriteLine($"[PowerShell] Generated Master ID: {psCorrelationId}");
        Console.WriteLine($"[PowerShell] Calling C# Executable...\n");
        
        // 2. The C# app spins up and creates a tracer...
        // Because of our TracerOptions constructor, it automatically detects the ID!
        using (var tracer = Tracer.CreateDefault())
        {
            var result = tracer.RunTimed(() =>
            {
                tracer.AddTag("Process", "CSharp_Child_Worker");
                tracer.InvokeScope("CSharp.DataProcessing", () => throw new Exception("C# executable crashed!"));
            });

            if (!result.Success)
                Console.WriteLine(((TraceErrorReport)result.Report!).ToPrettyString());
        }

        // 3. Clean up the simulation
        Environment.SetEnvironmentVariable("CHRONOSTACK_CORRELATION_ID", null);
    }
}
