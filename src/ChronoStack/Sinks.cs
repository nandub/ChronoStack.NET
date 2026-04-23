using System;
using System.Collections.Concurrent;
using System.Linq;
using System.IO;
using System.Text;

namespace ChronoStack
{
    public interface ITraceSink
    {
        void Write(TraceSeverity severity, object report, TracerOptions options);
    }

    internal static class JsonSerializerShim
    {
        public static string SerializeEnvelope(string severity, object report, bool compact)
        {
#if NET6_0_OR_GREATER
            // AOT-Compatible Serialization!
            var ctx = ChronoStackJsonContext.Get(compact);
            if (report is TraceErrorReport tr)
                return System.Text.Json.JsonSerializer.Serialize(new LogEnvelope<TraceErrorReport> { severity = severity, payload = tr }, ctx.LogEnvelopeTraceErrorReport);
            if (report is ErrorReport er)
                return System.Text.Json.JsonSerializer.Serialize(new LogEnvelope<ErrorReport> { severity = severity, payload = er }, ctx.LogEnvelopeErrorReport);
            
            return "{}"; // Fallback
#else
            // Legacy .NET Framework fallback (Newtonsoft)
            var envelope = new LogEnvelope<object> { severity = severity, payload = report };
            var settings = new Newtonsoft.Json.JsonSerializerSettings
            {
                NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                Formatting = compact ? Newtonsoft.Json.Formatting.None : Newtonsoft.Json.Formatting.Indented
            };
            return Newtonsoft.Json.JsonConvert.SerializeObject(envelope, settings);
#endif
        }

        public static string Serialize(object report, bool compact)
        {
#if NET6_0_OR_GREATER
            // AOT-Compatible Serialization!
            var ctx = ChronoStackJsonContext.Get(compact);
            if (report is TraceErrorReport tr)
                return System.Text.Json.JsonSerializer.Serialize(tr, ctx.TraceErrorReport);
            if (report is ErrorReport er)
                return System.Text.Json.JsonSerializer.Serialize(er, ctx.ErrorReport);
            
            return "{}"; // Fallback
#else
            // Legacy .NET Framework fallback (Newtonsoft)
            var settings = new Newtonsoft.Json.JsonSerializerSettings
            {
                NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                Formatting = compact ? Newtonsoft.Json.Formatting.None : Newtonsoft.Json.Formatting.Indented
            };
            return Newtonsoft.Json.JsonConvert.SerializeObject(report, settings);
#endif
        }
    }

    public sealed class ConsoleTraceSink : ITraceSink
    {
        public void Write(TraceSeverity severity, object report, TracerOptions options)
        {
            try
            {
                var msg = report is TraceErrorReport tr ? $"{tr.TimeUtc} [{severity}] ID={tr.CorrelationId} {tr.Error.ExceptionType}: {tr.Error.Message}" :
                          report is ErrorReport er ? $"{er.TimeUtc} [{severity}] {er.ExceptionType}: {er.Message}" :
                          $"{DateTime.UtcNow:o} [{severity}] {report}";

                if (options.ConsoleWriteToStdErr) Console.Error.WriteLine(msg);
                else Console.WriteLine(msg);
            }
            catch { }
        }
    }

    public sealed class JsonlTraceSink : ITraceSink
    {
        public string Path { get; }
        public JsonlTraceSink(string path) => Path = path ?? throw new ArgumentNullException(nameof(path));

        public void Write(TraceSeverity severity, object report, TracerOptions options)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var line = JsonSerializerShim.SerializeEnvelope(severity.ToString(), report, options.JsonCompact);
                
                using (var fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false))) // No BOM
                {
                    sw.WriteLine(line);
                }
            }
            catch { }
        }
    }

    public sealed class EventLogTraceSink : ITraceSink
    {
        public string LogName { get; }
        public string Source { get; }
        public int EventId { get; }

        public EventLogTraceSink(string logName, string source, int eventId = 42000)
        {
            LogName = logName; Source = source; EventId = eventId;
        }

        public void Write(TraceSeverity severity, object report, TracerOptions options)
        {
            try
            {
                // Ensure we only attempt to write to the Event Log if we are on Windows.
                // This makes the library safe to run on Linux/macOS containers without throwing internal exceptions.
#if NET6_0_OR_GREATER
                if (!OperatingSystem.IsWindows()) return;
#endif

                var msg = report is TraceErrorReport tr ? $"ID={tr.CorrelationId}\n{tr.Error.Message}" : report.ToString();

// Tell the compiler to stop warning us about Windows-only APIs since we just did an OS check
#pragma warning disable CA1416 
                if (!System.Diagnostics.EventLog.SourceExists(Source))
                    System.Diagnostics.EventLog.CreateEventSource(Source, LogName); // Requires Admin

                var type = severity == TraceSeverity.Error ? System.Diagnostics.EventLogEntryType.Error : System.Diagnostics.EventLogEntryType.Warning;
                System.Diagnostics.EventLog.WriteEntry(Source, msg, type, EventId);
#pragma warning restore CA1416
            }
            catch { /* Graceful degradation */ }
        }
    }

    public sealed class UATraceSink : ITraceSink
    {
        private readonly Action<string> _emit;
        public UATraceSink(Action<string> emit) => _emit = emit ?? throw new ArgumentNullException(nameof(emit));

        public void Write(TraceSeverity severity, object report, TracerOptions options)
        {
            try
            {
                var payload = JsonSerializerShim.SerializeEnvelope(severity.ToString(), report, options.JsonCompact);
                _emit(payload);
            }
            catch { }
        }
    }    

    /// <summary>
    /// A thread-safe sink that captures logs in memory rather than writing to external I/O.
    /// Highly useful for Unit Testing (xUnit/NUnit) to assert that specific errors and scopes were captured.
    /// </summary>
    public sealed class InMemorySink : ITraceSink
    {
        /// <summary>
        /// A thread-safe queue containing all reports captured during the application or test run.
        /// </summary>
        public ConcurrentQueue<object> CapturedReports { get; } = new ConcurrentQueue<object>();

        public void Write(TraceSeverity severity, object report, TracerOptions options)
        {
            CapturedReports.Enqueue(report);
        }

        /// <summary>
        /// Clears all captured reports from memory. Useful for resetting state between unit tests.
        /// </summary>
        public void Clear()
        {
            // ConcurrentQueue doesn't have a direct .Clear() in older frameworks, 
            // so we safely dequeue everything to empty it.
            while (CapturedReports.TryDequeue(out _)) { }
        }
    }
}
