using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using log4net;
using Serilog.Events;
using NLog;

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
        private static readonly Regex SqlIdentifierPattern = new Regex(
            @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$",
            RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(250));

        private readonly string _connectionString;
        private readonly string _tableName;

        public SqlDatabaseSink(string connectionString, string tableName = "ChronoLogs")
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _tableName = QuoteSqlTableName(tableName ?? throw new ArgumentNullException(nameof(tableName)));
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

        private static string QuoteSqlTableName(string tableName)
        {
            if (!SqlIdentifierPattern.IsMatch(tableName))
                throw new ArgumentException("Table name must be a simple SQL Server identifier, optionally schema-qualified.", nameof(tableName));

            var parts = tableName.Split('.');
            for (var i = 0; i < parts.Length; i++)
                parts[i] = "[" + parts[i].Replace("]", "]]") + "]";

            return string.Join(".", parts);
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
                    TraceSeverity.Error => Microsoft.Extensions.Logging.LogLevel.Error,
                    TraceSeverity.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
                    _ => Microsoft.Extensions.Logging.LogLevel.Information
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

    /// <summary>
    /// Pipes ChronoStack telemetry directly into Serilog.
    /// Preserves full JSON structure using Serilog's '@' destructuring operator.
    /// </summary>
    public sealed class SerilogSink : ITraceSink
    {
        private readonly Serilog.ILogger _logger;

        public SerilogSink(Serilog.ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Write(TraceSeverity severity, object report, TracerOptions options)
        {
            try
            {
                var level = severity switch
                {
                    TraceSeverity.Error => LogEventLevel.Error,
                    TraceSeverity.Warning => LogEventLevel.Warning,
                    _ => LogEventLevel.Information
                };

                // The '@' tells Serilog to serialize the entire object into its structured properties!
                _logger.Write(level, "ChronoStack Error: {@ChronoReport}", report);
            }
            catch { /* Never crash the dispatcher */ }
        }
    }

    /// <summary>
    /// Pipes ChronoStack telemetry into NLog.
    /// </summary>
    public sealed class NLogSink : ITraceSink
    {
        private readonly NLog.ILogger _logger;

        public NLogSink(NLog.ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Write(TraceSeverity severity, object report, TracerOptions options)
        {
            try
            {
                var level = severity switch
                {
                    TraceSeverity.Error => NLog.LogLevel.Error,
                    TraceSeverity.Warning => NLog.LogLevel.Warn,
                    _ => NLog.LogLevel.Info
                };

                // For NLog, we serialize it to JSON first to ensure it stays structured
                var json = JsonSerializerShim.Serialize(report, options.JsonCompact);
                _logger.Log(level, $"ChronoStack Error: {json}");
            }
            catch { }
        }
    }

    /// <summary>
    /// Exports telemetry directly to an OpenTelemetry (OTel) Collector using the OTLP/HTTP protocol.
    /// MUST be wrapped in a CircuitBreakerSink!
    /// </summary>
    public sealed class OtlpHttpLogSink : ITraceSink
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private readonly string _otlpEndpoint;
        private readonly string _serviceName;

        /// <param name="endpointUrl">e.g., http://localhost:4318/v1/logs</param>
        /// <param name="serviceName">The name of the service emitting the telemetry (e.g., "MyWebApp").</param>
        public OtlpHttpLogSink(string endpointUrl, string serviceName)
        {
            _otlpEndpoint = endpointUrl ?? throw new ArgumentNullException(nameof(endpointUrl));
            _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
        }

        public void Write(TraceSeverity severity, object report, TracerOptions options)
        {
            var otlpPayload = BuildPayload(severity, report, options);

            var content = new StringContent(otlpPayload, Encoding.UTF8, "application/json");

            // Synchronous call so the CircuitBreaker can catch timeouts!
            var response = _httpClient.PostAsync(_otlpEndpoint, content).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
        }

        internal string BuildPayload(TraceSeverity severity, object report, TracerOptions options)
        {
            var traceId = report is TraceErrorReport tr ? tr.TraceId : null;
            var jsonBody = JsonSerializerShim.Serialize(report, options.JsonCompact);

            // Construct the official OTLP JSON Log Data Model.
            return $@"
            {{
              ""resourceLogs"": [
                {{
                  ""resource"": {{
                    ""attributes"": [
                      {{ ""key"": ""service.name"", ""value"": {{ ""stringValue"": {EscapeJsonString(_serviceName)} }} }}
                    ]
                  }},
                  ""scopeLogs"": [
                    {{
                      ""logRecords"": [
                        {{
                          ""timeUnixNano"": ""{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000000}"",
                          ""severityText"": ""{severity}"",
                          ""traceId"": {EscapeJsonString(traceId ?? "")},
                          ""body"": {{ ""stringValue"": {EscapeJsonString(jsonBody)} }}
                        }}
                      ]
                    }}
                  ]
                }}
              ]
            }}";
        }
    
        // Helper to safely escape our ChronoStack JSON so it fits inside the OTLP string value
        private static string EscapeJsonString(string rawJson)
        {
#if NET6_0_OR_GREATER
            // 100% Native AOT Compatible!
            var ctx = ChronoStackJsonContext.Get(compact: true);
            return System.Text.Json.JsonSerializer.Serialize(rawJson, ctx.String);
#else
            // Legacy .NET Framework fallback
            return Newtonsoft.Json.JsonConvert.ToString(rawJson);
#endif
        }
    }

    /// <summary>
    /// Writes telemetry to a local or remote Linux Syslog daemon (e.g., rsyslog) via UDP.
    /// Acts as the Linux equivalent to the Windows EventLogTraceSink.
    /// </summary>
    public sealed class SyslogTraceSink : ITraceSink, IDisposable
    {
        private readonly UdpClient _udpClient;
        private readonly string _appName;
        private readonly string _hostName;

        /// <param name="appName">The name of your app (appears in the syslog file)</param>
        /// <param name="host">The syslog server (defaults to localhost/127.0.0.1)</param>
        /// <param name="port">The syslog UDP port (defaults to standard 514)</param>
        public SyslogTraceSink(string appName = "ChronoStack", string host = "127.0.0.1", int port = 514)
        {
            _appName = appName;
            _hostName = Environment.MachineName;
            _udpClient = new UdpClient(host, port);
        }

        public void Write(TraceSeverity severity, object report, TracerOptions options)
        {
            try
            {
                // Syslog Priority Calculation: Facility (1 = User-Level) * 8 + Severity
                // Syslog Severities: 3 = Error, 4 = Warning, 6 = Informational
                int syslogSeverity = severity switch
                {
                    TraceSeverity.Error => 3,
                    TraceSeverity.Warning => 4,
                    _ => 6
                };
                int priority = (1 * 8) + syslogSeverity;

                // Format: <Priority>Month Day Time Hostname AppName: Message
                var timestamp = DateTime.Now.ToString("MMM dd HH:mm:ss");
                
                var msg = report is TraceErrorReport tr 
                    ? $"[ID: {tr.CorrelationId}] {tr.Error.ExceptionType}: {tr.Error.Message}" 
                    : report.ToString();

                // Build the RFC 3164 compliant Syslog message
                var syslogMessage = $"<{priority}>{timestamp} {_hostName} {_appName}: {msg}";
                
                var bytes = Encoding.UTF8.GetBytes(syslogMessage);
                
                // UDP is connectionless and fire-and-forget. It won't block the thread!
                _udpClient.Send(bytes, bytes.Length);
            }
            catch { /* Graceful degradation if the local syslog daemon is down */ }
        }

        public void Dispose()
        {
            _udpClient?.Dispose();
        }
    }
}
