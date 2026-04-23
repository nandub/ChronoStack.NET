using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ChronoStack
{
    public sealed class TraceSession
    {
        public TraceSession(Guid correlationId)
        {
            CorrelationId = correlationId;
            Stopwatch = Stopwatch.StartNew();
        }

        public Guid CorrelationId { get; }
        public Stopwatch Stopwatch { get; }
        internal readonly List<ScopeFrame> Stack = new List<ScopeFrame>();
        public List<TimedFrame>? LastTimedStackSnapshot { get; internal set; }
        
        // Thread-safe dictionary for Multi-Tenancy / Session Context
        public ConcurrentDictionary<string, string> Tags { get; } = new ConcurrentDictionary<string, string>();

        internal sealed class ScopeFrame
        {
            public string Name { get; set; } = string.Empty;
            public DateTime EnterUtc { get; set; }
            public long EnterTicks { get; set; }
            public long EnterAllocatedBytes { get; set; }
            public string? FilePath { get; set; }
            public int? LineNumber { get; set; }
        }
    }

    public sealed class TraceScope : IDisposable
    {
        private readonly Tracer _tracer;
        private readonly TraceSession _session;
        private readonly TraceSession.ScopeFrame _frame;
        private bool _disposed;

        internal TraceScope(Tracer tracer, TraceSession session, TraceSession.ScopeFrame frame)
        {
            _tracer = tracer;
            _session = session;
            _frame = frame;
        }

        /// <summary>
        /// Call in a catch block before rethrowing to snapshot timed frames BEFORE stack unwind.
        /// </summary>
        public void SnapshotOnError() => _tracer.CaptureSnapshot(_session);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _tracer.ExitScope(_session, _frame);
        }
    }

    public sealed class TracerRunResult
    {
        public bool Success { get; internal set; }
        public Exception? Exception { get; internal set; }
        public object? Report { get; internal set; }
        public Guid? CorrelationId { get; internal set; }
    }

    public sealed class Tracer : IDisposable
    {
        private readonly List<ITraceSink> _sinks;
        public TracerOptions Options { get; }

        // Thread-safe context propagation across async/await and TPL tasks
        private readonly AsyncLocal<TraceSession?> _currentSession = new AsyncLocal<TraceSession?>();

        // Background Dispatcher Engine
        private readonly BlockingCollection<(TraceSeverity severity, object report, TracerOptions opts)> _logQueue;
        private readonly Thread _dispatcherThread;
        private bool _isDisposed;

        private Tracer(List<ITraceSink> sinks, TracerOptions options)
        {
            _sinks = sinks;
            Options = options;

            // Initialize Background Queue (Max 10,000 logs in memory to prevent OOM during Log Storms)
            _logQueue = new BlockingCollection<(TraceSeverity, object, TracerOptions)>(10000);
            
            // Start the dedicated logging thread
            _dispatcherThread = new Thread(DispatchLoop)
            {
                IsBackground = true,
                Name = "ChronoStack_Dispatcher"
            };
            _dispatcherThread.Start();
        }

        public static Tracer CreateDefault() => new Tracer(new List<ITraceSink> { new ConsoleTraceSink() }, new TracerOptions());
        
        public static Tracer Create(IEnumerable<ITraceSink> sinks, TracerOptions? options = null)
        {
            var list = new List<ITraceSink>(sinks);
            return new Tracer(list, options ?? new TracerOptions());
        }

        /// <summary>
        /// Attaches a custom key-value pair to the current diagnostic session (e.g. UserId, TenantId).
        /// </summary>
        public void AddTag(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key)) return;

            var session = _currentSession.Value;
            if (session == null)
            {
                session = new TraceSession(Options.FixedCorrelationId ?? Guid.NewGuid());
                _currentSession.Value = session;
            }
            session.Tags[key] = value ?? string.Empty;
        }

        public TracerRunResult Run(Action action, TracerOptions? overrideOptions = null)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var opts = (overrideOptions ?? Options).Clone();
            _currentSession.Value = null;

            try
            {
                action();
                return new TracerRunResult { Success = true };
            }
            catch (Exception ex)
            {
                var report = BuildErrorReport(ex, opts);
                WriteToSinks(TraceSeverity.Error, report, opts);
                return new TracerRunResult { Success = false, Exception = ex, Report = report };
            }
        }

        public TracerRunResult RunTimed(Action action, TracerOptions? overrideOptions = null)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var opts = (overrideOptions ?? Options).Clone();
            var correlationId = opts.FixedCorrelationId ?? Guid.NewGuid();

            var session = new TraceSession(correlationId);
            _currentSession.Value = session;

            try
            {
                action();
                return new TracerRunResult { Success = true, CorrelationId = correlationId };
            }
            catch (Exception ex)
            {
                if (session.LastTimedStackSnapshot == null || session.LastTimedStackSnapshot.Count == 0)
                    CaptureSnapshot(session);

                var report = BuildTraceErrorReport(ex, session, opts);
                WriteToSinks(TraceSeverity.Error, report, opts);
                return new TracerRunResult { Success = false, Exception = ex, Report = report, CorrelationId = correlationId };
            }
            finally
            {
                _currentSession.Value = null;
            }
        }

        /// <summary>
        /// Ergonomic wrapper that creates a scope, runs the action, and automatically snapshots on error.
        /// </summary>
        public void InvokeScope(string name, Action action)
        {
            using (var scope = Scope(name))
            {
                try { action(); }
                catch
                {
                    scope.SnapshotOnError();
                    throw;
                }
            }
        }

        public TraceScope Scope(string name, string? filePath = null, int? lineNumber = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Scope name is required.", nameof(name));
            
            var session = _currentSession.Value;
            if (session == null)
            {
                session = new TraceSession(Options.FixedCorrelationId ?? Guid.NewGuid());
                _currentSession.Value = session;
            }

            // FIX: Clear any old snapshots from previously swallowed exceptions
            session.LastTimedStackSnapshot = null;

            var frame = new TraceSession.ScopeFrame
            {
                Name = name,
                EnterUtc = DateTime.UtcNow,
                EnterTicks = session.Stopwatch.ElapsedTicks,
                EnterAllocatedBytes = GetAllocatedBytes(),
                FilePath = filePath,
                LineNumber = lineNumber
            };

            session.Stack.Add(frame);
            return new TraceScope(this, session, frame);
        }

        internal void ExitScope(TraceSession session, TraceSession.ScopeFrame frame)
        {
            for (var i = session.Stack.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(session.Stack[i], frame))
                {
                    session.Stack.RemoveAt(i);
                    break;
                }
            }
        }

        internal void CaptureSnapshot(TraceSession session)
        {
            // FIX: If we are unwinding the stack, do not let outer scopes overwrite the deep snapshot!
            if (session.LastTimedStackSnapshot != null && session.LastTimedStackSnapshot.Count >= session.Stack.Count)
            {
                return;
            }

            var nowTicks = session.Stopwatch.ElapsedTicks;
            var nowBytes = GetAllocatedBytes();
            var freq = Stopwatch.Frequency;
            var frames = new List<TimedFrame>(session.Stack.Count);

            foreach (var f in session.Stack)
            {
                var elapsedMs = Math.Round(((nowTicks - f.EnterTicks) * 1000.0) / freq, 3);
                var allocBytes = nowBytes - f.EnterAllocatedBytes;
                
                frames.Add(new TimedFrame
                {
                    Name = f.Name,
                    EnterTimeUtc = f.EnterUtc.ToString("o"),
                    ElapsedMs = elapsedMs,
                    AllocatedBytes = allocBytes > 0 ? allocBytes : (long?)null,
                    FilePath = f.FilePath,
                    LineNumber = f.LineNumber
                });
            }
            session.LastTimedStackSnapshot = frames;
        }

        private static long GetAllocatedBytes()
        {
#if NET6_0_OR_GREATER
            return GC.GetAllocatedBytesForCurrentThread();
#else
            return 0; // Fallback for older .NET Frameworks
#endif
        }

        private static ErrorReport BuildErrorReport(Exception ex, TracerOptions opts)
        {
            // Apply PII/PHI Redactor if configured
            var msg = opts.MessageRedactor != null ? opts.MessageRedactor(ex.Message) : ex.Message;

            var er = new ErrorReport
            {
                TimeUtc = DateTime.UtcNow.ToString("o"),
                Message = msg,
                ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
                HResult = ex.HResult,
                Source = ex.Source
            };

            if (opts.IncludeEnvironmentInfo) er.Environment = EnvironmentInfo.Capture();
            
            var st = new StackTrace(ex, true);
            var frames = st.GetFrames();
            if (frames != null)
            {
                foreach (var f in frames) er.Stack.Add(StackFrameInfo.FromFrame(f));
            }
            return er;
        }

        private TraceErrorReport BuildTraceErrorReport(Exception ex, TraceSession session, TracerOptions opts)
        {
            var baseReport = BuildErrorReport(ex, opts);
            
            var report = new TraceErrorReport
            {
                TimeUtc = baseReport.TimeUtc,
                CorrelationId = session.CorrelationId,
                TraceId = System.Diagnostics.Activity.Current?.Id,
                Error = baseReport,
                TimedStack = opts.IncludeTimedFrames ? (session.LastTimedStackSnapshot ?? new List<TimedFrame>()) : new List<TimedFrame>()
            };

            // Copy Tags into the report before dispatching
            foreach (var kvp in session.Tags)
            {
                report.Tags[kvp.Key] = kvp.Value;
            }

            return report;
        }

        private void WriteToSinks(TraceSeverity severity, object report, TracerOptions opts)
        {
            if (_isDisposed) return;

            // Zero-Blocking Drop into the in-memory queue.
            // If the queue hits 10,000 items, TryAdd safely drops the log to protect the app.
            _logQueue.TryAdd((severity, report, opts));
        }

        private void DispatchLoop()
        {
            try
            {
                // Consumes logs from the queue on a background thread as they arrive
                foreach (var item in _logQueue.GetConsumingEnumerable())
                {
                    foreach (var sink in _sinks)
                    {
                        try { sink.Write(item.severity, item.report, item.opts); }
                        catch { /* Sinks never fail the background thread */ }
                    }
                }
            }
            catch { /* Catch graceful shutdown exceptions */ }
        }

        /// <summary>
        /// Waits for all pending logs to be written to their sinks before the application shuts down.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _logQueue.CompleteAdding();
            
            // Wait up to 3 seconds for the sinks to finish writing remaining logs to disk/SQL
            if (_dispatcherThread.IsAlive)
            {
                _dispatcherThread.Join(TimeSpan.FromSeconds(3)); 
            }
            
            _logQueue.Dispose();
        }
    }
}
