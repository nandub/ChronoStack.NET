using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using log4net; // log4net.ILog

namespace ChronoStack
{
        /// <summary>
    /// Sends a JSON payload to an HTTP endpoint.
    /// MUST be wrapped in a CircuitBreakerSink to handle network timeouts safely.
    /// </summary>
    public sealed class HttpTelemetrySink : ITraceSink
    {
        // Reused across instances to prevent socket exhaustion
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private readonly string _endpointUrl;

        public HttpTelemetrySink(string endpointUrl)
        {
            _endpointUrl = endpointUrl ?? throw new ArgumentNullException(nameof(endpointUrl));
        }

        public void Write(TraceSeverity severity, object report, TracerOptions options)
        {
            var payload = JsonSerializerShim.SerializeEnvelope(severity.ToString(), report, options.JsonCompact);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            // Execute the HTTP call directly on the dispatcher thread.
            // We use .GetAwaiter().GetResult() to synchronously wait for the response.
            // If the network times out or fails, it throws an exception straight to the Circuit Breaker!
            var response = _httpClient.PostAsync(_endpointUrl, content).GetAwaiter().GetResult();

            // Optionally, ensure 4xx/5xx status codes also throw an exception to trip the breaker
            response.EnsureSuccessStatusCode(); 
        }
    }

    /// <summary>
    /// Writes the error report directly to a SQL Server database.
    /// Uses parameterized queries to prevent SQL injection.
    /// </summary>
    /* 
     * SQL SCHEMA REQUIRED:
     * CREATE TABLE ChronoLogs (
     *     Id INT IDENTITY(1,1) PRIMARY KEY,
     *     TimeUtc DATETIME2 NOT NULL,
     *     Severity NVARCHAR(50) NOT NULL,
     *     CorrelationId UNIQUEIDENTIFIER NULL,
     *     ExceptionType NVARCHAR(255) NULL,
     *     Message NVARCHAR(MAX) NULL,
     *     PayloadJson NVARCHAR(MAX) NOT NULL
     * );
     */
    public sealed class SqlDatabaseSink : ITraceSink
    {
        private readonly string _connectionString;
        private readonly string _tableName;

        public SqlDatabaseSink(string connectionString, string tableName = "ChronoLogs")
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
        }

        public void Write(TraceSeverity severity, object report, TracerOptions options)
        {
            var timeUtc = DateTime.UtcNow;
            Guid? correlationId = null;
            string? exceptionType = null;
            string? message = null;

            if (report is TraceErrorReport tr)
            {
                correlationId = tr.CorrelationId;
                exceptionType = tr.Error.ExceptionType;
                message = tr.Error.Message;
            }
            else if (report is ErrorReport er)
            {
                exceptionType = er.ExceptionType;
                message = er.Message;
            }

            var payloadJson = JsonSerializerShim.SerializeEnvelope(severity.ToString(), report, options.JsonCompact);
            // Using synchronous ADO.NET for guaranteed execution before process termination
            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                var sql = $"INSERT INTO {_tableName} (TimeUtc, Severity, CorrelationId, ExceptionType, Message, PayloadJson) VALUES (@Time, @Sev, @Corr, @Type, @Msg, @Json)";
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@Time", timeUtc);
                    cmd.Parameters.AddWithValue("@Sev", severity.ToString());
                    cmd.Parameters.AddWithValue("@Corr", (object?)correlationId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Type", (object?)exceptionType ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Msg", (object?)message ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Json", payloadJson);
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }

    /// <summary>
    /// Forwards ChronoStack diagnostics to log4net.
    /// </summary>
    public sealed class Log4NetSink : ITraceSink
    {
        private readonly log4net.ILog _logger;

        public Log4NetSink(log4net.ILog logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Write(TraceSeverity severity, object report, TracerOptions options)
        {
            try
            {
                var msg = BuildSummary(report);
                
                switch (severity)
                {
                    case TraceSeverity.Error:
                        _logger.Error(msg);
                        break;
                    case TraceSeverity.Warning:
                        _logger.Warn(msg);
                        break;
                    default:
                        _logger.Info(msg);
                        break;
                }
            }
            catch { }
        }

        private static string BuildSummary(object report)
        {
            // UPGRADE: Tell log4net to log the entire stack trace and timed scopes!
            if (report is TraceErrorReport tr)
                return tr.ToPrettyString();
            if (report is ErrorReport er)
                return er.ToPrettyString();
            
            return report.ToString() ?? "Unknown Error";
        }
    }

    /// <summary>
    /// Forwards ChronoStack diagnostics to Microsoft.Extensions.Logging (Standard in modern .NET / ASP.NET).
    /// </summary>
    public sealed class MicrosoftExtensionsLoggingSink : ITraceSink
    {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;

        public MicrosoftExtensionsLoggingSink(Microsoft.Extensions.Logging.ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Write(TraceSeverity severity, object report, TracerOptions options)
        {
            try
            {
                var msg = BuildSummary(report);

                // Map TraceSeverity to MEL LogLevel
                var logLevel = severity switch
                {
                    TraceSeverity.Error => LogLevel.Error,
                    TraceSeverity.Warning => LogLevel.Warning,
                    _ => LogLevel.Information
                };

                _logger.Log(logLevel, "{ChronoMessage}", msg);
            }
            catch { }
        }

        private static string BuildSummary(object report)
        {
            if (report is TraceErrorReport tr)
                return $"[ID: {tr.CorrelationId}] {tr.Error.ExceptionType}: {tr.Error.Message}";
            if (report is ErrorReport er)
                return $"{er.ExceptionType}: {er.Message}";
            return report.ToString() ?? "Unknown Error";
        }
    }

    /// <summary>
    /// Decorator that protects external network dependencies from being hammered during an outage.
    /// If the inner sink fails repeatedly, the breaker trips and temporarily drops logs.
    /// </summary>
    public sealed class CircuitBreakerSink : ITraceSink
    {
        private readonly ITraceSink _innerSink;
        private readonly int _failureThreshold;
        private readonly TimeSpan _cooldown;

        private int _consecutiveFailures = 0;
        private long _nextRetryTicks = 0;

        public CircuitBreakerSink(ITraceSink innerSink, int failureThreshold = 3, TimeSpan? cooldown = null)
        {
            _innerSink = innerSink ?? throw new ArgumentNullException(nameof(innerSink));
            _failureThreshold = failureThreshold;
            _cooldown = cooldown ?? TimeSpan.FromSeconds(30);
        }

        public void Write(TraceSeverity severity, object report, TracerOptions options)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var retryTicks = Interlocked.Read(ref _nextRetryTicks);

            // STATE: OPEN (Tripped)
            if (_consecutiveFailures >= _failureThreshold)
            {
                if (nowTicks < retryTicks)
                {
                    // We are still in the cooldown period. 
                    // Fail fast and silently drop the log to protect the network.
                    return; 
                }
                // Cooldown has passed. We allow this ONE log through to test the connection (Half-Open).
            }

            try
            {
                // Attempt to write to the underlying sink (e.g., SQL Database or HTTP)
                _innerSink.Write(severity, report, options);

                // STATE: CLOSED (Healthy). If we succeeded, reset the failure counter!
                if (_consecutiveFailures > 0)
                {
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    Console.WriteLine($"[Circuit Breaker] Connection restored to {_innerSink.GetType().Name}.");
                }
            }
            catch
            {
                // Record the failure
                var failures = Interlocked.Increment(ref _consecutiveFailures);
                
                // If we just hit the threshold, trip the breaker and set the cooldown timer
                if (failures == _failureThreshold)
                {
                    Interlocked.Exchange(ref _nextRetryTicks, DateTime.UtcNow.Add(_cooldown).Ticks);
                    Console.WriteLine($"[Circuit Breaker] TRIPPED for {_innerSink.GetType().Name}! Pausing logs for {_cooldown.TotalSeconds} seconds.");
                }

                // The Circuit Breaker catches the exception so it doesn't crash the background dispatcher
            }
        }
    }
}
