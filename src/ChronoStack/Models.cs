using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ChronoStack
{
    public enum TraceSeverity { Info = 0, Warning = 1, Error = 2 }

    public sealed class TracerOptions
    {
        public bool IncludeExceptionChain { get; set; } = true;
        public bool IncludeEnvironmentInfo { get; set; } = true;
        public bool IncludeTimedFrames { get; set; } = true;
        public bool ConsoleWriteToStdErr { get; set; } = false;
        public bool JsonCompact { get; set; } = true;
        public Guid? FixedCorrelationId { get; set; }
        public Func<string, string>? MessageRedactor { get; set; }

        public TracerOptions()
        {
            // PHASE 4 UNIFICATION: Automatically adopt the parent process (e.g. PowerShell) 
            // Correlation ID if it was passed down via Environment Variables!
            var envId = Environment.GetEnvironmentVariable("CHRONOSTACK_CORRELATION_ID");
            if (Guid.TryParse(envId, out var parsedId))
            {
                FixedCorrelationId = parsedId;
            }
        }

        public TracerOptions Clone() => new TracerOptions
        {
            IncludeExceptionChain = IncludeExceptionChain,
            IncludeEnvironmentInfo = IncludeEnvironmentInfo,
            IncludeTimedFrames = IncludeTimedFrames,
            ConsoleWriteToStdErr = ConsoleWriteToStdErr,
            JsonCompact = JsonCompact,
            FixedCorrelationId = FixedCorrelationId,
            MessageRedactor = MessageRedactor
        };
    }
   

    public sealed class ExceptionInfo
    {
        public string Type { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int HResult { get; set; }
        public string? Source { get; set; }

        public static ExceptionInfo FromException(Exception ex) => new ExceptionInfo
        {
            Type = ex.GetType().FullName ?? ex.GetType().Name,
            Message = ex.Message,
            HResult = ex.HResult,
            Source = ex.Source
        };
    }

    public sealed class StackFrameInfo
    {
        public string? Method { get; set; }
        public string? DeclaringType { get; set; }
        public string? Assembly { get; set; }
        public string? FilePath { get; set; }
        public int? LineNumber { get; set; }

        public static StackFrameInfo FromFrame(StackFrame frame)
        {
            var method = frame.GetMethod();
            var dt = method?.DeclaringType;
            return new StackFrameInfo
            {
                Method = method?.Name,
                DeclaringType = dt?.FullName,
                Assembly = dt?.Assembly?.GetName()?.Name,
                FilePath = frame.GetFileName(),
                LineNumber = frame.GetFileLineNumber() > 0 ? frame.GetFileLineNumber() : null
            };
        }
    }

    public sealed class TimedFrame
    {
        public string Name { get; set; } = string.Empty;
        public string EnterTimeUtc { get; set; } = string.Empty; 
        public double ElapsedMs { get; set; }
        
        // NEW: Tracks memory allocation during this specific scope
        public long? AllocatedBytes { get; set; } 
        
        public string? FilePath { get; set; }
        public int? LineNumber { get; set; }
    }

    public sealed class EnvironmentInfo
    {
        public string MachineName { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public int ThreadId { get; set; }
        public string FrameworkDescription { get; set; } = string.Empty;
        
        public long ProcessRamBytes { get; set; }
        public int ActiveProcessThreads { get; set; }
        public TimeSpan ProcessUptime { get; set; }

        // NEW: Advanced Garbage Collector Diagnostics
        public int GcGen0Collections { get; set; }
        public int GcGen1Collections { get; set; }
        public int GcGen2Collections { get; set; }
        public long TotalAvailableMemoryBytes { get; set; }

        public static EnvironmentInfo Capture()
        {
            using var p = Process.GetCurrentProcess();
            return new EnvironmentInfo
            {
                MachineName = Environment.MachineName,
                ProcessName = p.ProcessName,
                ProcessId = p.Id,
                ThreadId = Thread.CurrentThread.ManagedThreadId,
                FrameworkDescription = RuntimeFramework.Value,
                
                ProcessRamBytes = p.WorkingSet64,
                ActiveProcessThreads = p.Threads.Count,
                ProcessUptime = DateTime.Now - p.StartTime,

                // Capture exact GC State at the millisecond of the crash
                GcGen0Collections = GC.CollectionCount(0),
                GcGen1Collections = GC.CollectionCount(1),
                GcGen2Collections = GC.CollectionCount(2),
#if NET6_0_OR_GREATER
                TotalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes
#endif
            };
        }

        private static readonly Lazy<string> RuntimeFramework = new Lazy<string>(() =>
        {
#if NET6_0_OR_GREATER
            return System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
#else
            return ".NET Framework " + Environment.Version;
#endif
        });
    }

    public sealed class ErrorReport
    {
        public string Type { get; set; } = "ChronoStack.ErrorReport";
        public string TimeUtc { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string ExceptionType { get; set; } = string.Empty;
        public int HResult { get; set; }
        public string? Source { get; set; }
        public List<ExceptionInfo>? ExceptionChain { get; set; }
        public List<StackFrameInfo> Stack { get; set; } = new List<StackFrameInfo>();
        public EnvironmentInfo? Environment { get; set; }

        // We added an optional parameter here to accept the Correlation ID
        public string ToPrettyString(Guid? correlationId = null)
        {
            var nl = System.Environment.NewLine;
            
            // If we have an ID, inject it into the very first line!
            var idPrefix = correlationId.HasValue ? $"[ID: {correlationId.Value}] " : "";
            var s = $"{TimeUtc}  {idPrefix}{ExceptionType}: {Message} (0x{HResult:X8}){nl}";
            
            if (Environment != null)
                s += $"Machine={Environment.MachineName}  Process={Environment.ProcessName}({Environment.ProcessId})  Thread={Environment.ThreadId}{nl}";
            
            s += "Stack:" + nl;
            foreach (var f in Stack)
            {
                var loc = string.IsNullOrWhiteSpace(f.FilePath) ? "" : $" in {f.FilePath}:{f.LineNumber}";
                s += $"  at {f.DeclaringType}.{f.Method} [{f.Assembly}]{loc}{nl}";
            }
            
            if (ExceptionChain != null && ExceptionChain.Count > 1)
            {
                s += "ExceptionChain:" + nl;
                foreach (var e in ExceptionChain)
                {
                    s += $"  {e.Type}: {e.Message}{nl}";
                }
            }
            return s;
        }
    }

    public sealed class TraceErrorReport
    {
        public string Type { get; set; } = "ChronoStack.TraceErrorReport";
        public string TimeUtc { get; set; } = string.Empty;
        public Guid? CorrelationId { get; set; }
        public string? TraceId { get; set; } 
        
        // NEW: Multi-Tenancy / Session Tags
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();

        public ErrorReport Error { get; set; } = new ErrorReport();
        public List<TimedFrame> TimedStack { get; set; } = new List<TimedFrame>();

        public string ToPrettyString()
        {
            var nl = System.Environment.NewLine;
            var s = Error.ToPrettyString(CorrelationId);
            
            if (!string.IsNullOrEmpty(TraceId))
                s = $"[W3C Trace: {TraceId}]{nl}{s}";

            // NEW: Print Tags
            if (Tags != null && Tags.Count > 0)
            {
                s += "Tags:" + nl;
                foreach (var tag in Tags)
                {
                    s += $"  [{tag.Key}] = {tag.Value}{nl}";
                }
            }

            if (TimedStack.Count > 0)
            {
                s += "TimedStack (oldest-first):" + nl;
                foreach (var t in TimedStack)
                {
                    var loc = string.IsNullOrWhiteSpace(t.FilePath) ? "" : $" in {t.FilePath}:{t.LineNumber}";
                    var mem = t.AllocatedBytes.HasValue ? $"  AllocBytes={t.AllocatedBytes}" : "";
                    s += $"  {t.Name}  Enter={t.EnterTimeUtc}  ElapsedMs={t.ElapsedMs}{mem}{loc}{nl}";
                }
            }
            return s;
        }
    }
}
