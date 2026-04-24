using System;
using System.Collections.Generic;

#if NET6_0_OR_GREATER
using System.Text.Json;
using System.Text.Json.Serialization;
#endif

namespace ChronoStack
{
    /// <summary>
    /// A strongly-typed wrapper to replace anonymous types. 
    /// Required for Native AOT Source Generation.
    /// </summary>
    public sealed class LogEnvelope<T>
    {
        // Lowercase names to perfectly match the original JSON schema expected by dashboards
        public string severity { get; set; } = string.Empty;
        public T? payload { get; set; }
    }

#if NET6_0_OR_GREATER
    /// <summary>
    /// The System.Text.Json Source Generator Context.
    /// The C# compiler reads these attributes and generates lightning-fast, reflection-free serialization code at compile time!
    /// </summary>
    [JsonSerializable(typeof(TraceErrorReport))]
    [JsonSerializable(typeof(ErrorReport))]
    [JsonSerializable(typeof(LogEnvelope<TraceErrorReport>))]
    [JsonSerializable(typeof(LogEnvelope<ErrorReport>))]
    [JsonSerializable(typeof(EnvironmentInfo))]
    [JsonSerializable(typeof(ExceptionInfo))]
    [JsonSerializable(typeof(StackFrameInfo))]
    [JsonSerializable(typeof(TimedFrame))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(string))]
    internal partial class ChronoStackJsonContext : JsonSerializerContext
    {
        private static readonly JsonSerializerOptions CompactOptions = new JsonSerializerOptions 
        { 
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, 
            WriteIndented = false 
        };
        
        private static readonly JsonSerializerOptions IndentedOptions = new JsonSerializerOptions 
        { 
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, 
            WriteIndented = true 
        };

        // Pre-instantiated contexts for maximum performance
        public static readonly ChronoStackJsonContext Compact = new ChronoStackJsonContext(CompactOptions);
        public static readonly ChronoStackJsonContext Indented = new ChronoStackJsonContext(IndentedOptions);

        public static ChronoStackJsonContext Get(bool compact) => compact ? Compact : Indented;
    }
#endif
}
