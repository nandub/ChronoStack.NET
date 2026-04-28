using System;
using System.Linq;
using Xunit;
using ChronoStack;

namespace ChronoStack.Tests
{
    public class TracerTests
    {
        [Fact]
        public void RunTimed_WhenExceptionThrown_CapturesThrowTimeSnapshot()
        {
            // Arrange
            var sink = new InMemorySink();
            
            // Act
            using (var tracer = Tracer.Create(new[] { sink }))
            {
                var result = tracer.RunTimed(() =>
                {
                    tracer.InvokeScope("OuterScope", () =>
                    {
                        tracer.InvokeScope("InnerScope", () => throw new InvalidOperationException("Boom"));
                    });
                });
                Assert.False(result.Success);
            } // tracer.Dispose() safely flushes the background queue here!

            // Assert
            Assert.Single(sink.CapturedReports);

            var report = (TraceErrorReport)sink.CapturedReports.First();
            Assert.NotNull(report.CorrelationId);
            Assert.Equal(2, report.TimedStack.Count);
            Assert.Equal("OuterScope", report.TimedStack[0].Name);
            Assert.Equal("InnerScope", report.TimedStack[1].Name);
        }

        [Fact]
        public void PiiRedactor_WhenConfigured_MasksSensitiveData()
        {
            // Arrange
            var sink = new InMemorySink();
            var options = new TracerOptions { MessageRedactor = PiiRedactor.Redact };
            
            // Act
            using (var tracer = Tracer.Create(new[] { sink }, options))
            {
                tracer.Run(() => throw new Exception("User SSN is 123-45-6789 and Card is 4111-2222-3333-4444."));
            } // Flush!

            // Assert
            var report = (ErrorReport)sink.CapturedReports.First();
            Assert.Contains("***-**-****", report.Message);
            Assert.Contains("****-****-****-****", report.Message);
            Assert.DoesNotContain("123-45-6789", report.Message);
        }

        [Fact]
        public void CircuitBreaker_TripsAndDropsLogs_AfterThresholdReached()
        {
            // Arrange
            var fragileSink = new MockFragileSink();
            var breaker = new CircuitBreakerSink(fragileSink, failureThreshold: 2, cooldown: TimeSpan.FromSeconds(10));
            
            // Act
            using (var tracer = Tracer.Create(new[] { breaker }))
            {
                for (int i = 0; i < 5; i++)
                {
                    tracer.Run(() => throw new Exception("Test Error"));
                }
            } // Flush!

            // Assert
            // The sink should only have been called exactly 2 times before the breaker tripped!
            Assert.Equal(2, fragileSink.AttemptCount);
        }

        // A mock network sink that always throws an exception
        private class MockFragileSink : ITraceSink
        {
            public int AttemptCount { get; private set; } = 0;
            public void Write(TraceSeverity severity, object report, TracerOptions options)
            {
                AttemptCount++;
                throw new TimeoutException("Network offline");
            }
        }
		
		[Fact]
        public void Run_NoInstrumentation_GeneratesReportWithoutCorrelationId()
        {
            // Arrange
            var sink = new InMemorySink();
            
            // Act
            using (var tracer = Tracer.Create(new[] { sink }))
            {
                tracer.Run(() => throw new DivideByZeroException("Math error"));
            } // Flush

            // Assert
            Assert.Single(sink.CapturedReports);
            var report = (ErrorReport)sink.CapturedReports.First(); // Notice it's an ErrorReport, not TraceErrorReport!
            
            Assert.Equal("System.DivideByZeroException", report.ExceptionType);
            Assert.Equal("Math error", report.Message);
            Assert.NotEmpty(report.Stack); // Proves we still captured the stack trace
        }

        [Fact]
        public void AddTag_WhenCalled_InjectsTagsIntoFinalReport()
        {
            // Arrange
            var sink = new InMemorySink();
            
            // Act
            using (var tracer = Tracer.Create(new[] { sink }))
            {
                tracer.RunTimed(() =>
                {
                    tracer.AddTag("TenantId", "DoD-Alpha");
                    tracer.AddTag("Transaction", "Tx-999");
                    throw new Exception("Tag test");
                });
            } // Flush

            // Assert
            var report = (TraceErrorReport)sink.CapturedReports.First();
            
            Assert.Equal(2, report.Tags.Count);
            Assert.Equal("DoD-Alpha", report.Tags["TenantId"]);
            Assert.Equal("Tx-999", report.Tags["Transaction"]);
        }
		
		        [Fact]
        public void W3C_OpenTelemetry_WhenActivityActive_CapturesTraceId()
        {
            // Arrange
            var sink = new InMemorySink();
            
            // Act: Start a standard .NET Activity (simulating an incoming HTTP request with a W3C Traceparent)
            var activity = new System.Diagnostics.Activity("Incoming_HttpRequest").Start();
            var expectedTraceId = activity.Id;

            using (var tracer = Tracer.Create(new[] { sink }))
            {
                tracer.RunTimed(() => throw new Exception("OTel Test"));
            } // Flush

            activity.Stop();

            // Assert
            var report = (TraceErrorReport)sink.CapturedReports.First();
            Assert.NotNull(report.TraceId);
            Assert.Equal(expectedTraceId, report.TraceId);
        }

        [Fact]
        public void MemoryTracking_WhenScopeEntered_CalculatesAllocatedBytes()
        {
            // Arrange
            var sink = new InMemorySink();
            
            // Act
            using (var tracer = Tracer.Create(new[] { sink }))
            {
                tracer.RunTimed(() =>
                {
                    tracer.InvokeScope("MemoryHogScope", () => 
                    {
                        var dummyData = new byte[1024 * 1024]; 
                        throw new OutOfMemoryException("Memory Test");
                    });
                });
            } // Flush

            // Assert
            var report = (TraceErrorReport)sink.CapturedReports.First();
            var memScope = report.TimedStack.First(s => s.Name == "MemoryHogScope");
            
#if NET6_0_OR_GREATER
            // Modern .NET tracks memory perfectly
            Assert.NotNull(memScope.AllocatedBytes);
            Assert.True(memScope.AllocatedBytes >= 1048576, $"Allocated bytes was only {memScope.AllocatedBytes}");
#else
            // Legacy .NET Framework doesn't support thread memory tracking, so we expect null!
            Assert.Null(memScope.AllocatedBytes);
#endif
        }

        [Fact]
        public void Sinks_WhenExceptionThrown_DoNotCrashApplication()
        {
            // Arrange
            var hostileSink = new MockHostileSink(); // This sink throws an unhandled exception
            var memorySink = new InMemorySink();     // This sink works perfectly
            
            // Act
            // We register the Hostile sink FIRST. If it crashes the loop, the Memory sink will never get called!
            using (var tracer = Tracer.Create(new ITraceSink[] { hostileSink, memorySink }))
            {
                // This shouldn't crash the test runner
                tracer.Run(() => throw new Exception("Testing Sink Resilience"));
            } // Flush

            // Assert
            // The dispatcher loop safely caught the HostileSink exception and continued to the MemorySink!
            Assert.Equal(1, hostileSink.AttemptCount);
            Assert.Single(memorySink.CapturedReports);
        }

        // A mock sink that simulates a completely broken custom sink (e.g. NullReferenceException)
        private class MockHostileSink : ITraceSink
        {
            public int AttemptCount { get; private set; } = 0;
            public void Write(TraceSeverity severity, object report, TracerOptions options)
            {
                AttemptCount++;
                throw new DivideByZeroException("I am a badly written sink that crashes unexpectedly!");
            }
        }
    }
}
