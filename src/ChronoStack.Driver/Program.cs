using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using ChronoStack;

namespace ChronoStack.Driver
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: ChronoDriver.exe <path_to_executable> [arguments...]");
                return 1;
            }

            var targetExe = args[0];
            var targetArgs = string.Join(" ", args.Skip(1));
            int exitCode = 0;

            // 1. Configure OS-Aware Enterprise Sinks
            var sinks = new List<ITraceSink> { new ConsoleTraceSink() };
#if NET6_0_OR_GREATER
            // 🌟 Dynamically choose EventLog for Windows and Syslog for Linux/Mac!
            if (OperatingSystem.IsWindows())
            {
                sinks.Add(new EventLogTraceSink("Application", "ChronoDriver"));
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                sinks.Add(new SyslogTraceSink(appName: "ChronoDriver"));
            }
#else
            // Legacy .NET Framework ONLY runs on Windows, so we safely hardcode the EventLog
            sinks.Add(new EventLogTraceSink("Application", "ChronoDriver"));
#endif


            // 2. Run the Driver
            using (var tracer = Tracer.Create(sinks)) 
            {
                var result = tracer.RunTimed(() =>
                {
                    // Tag the session so dashboards know exactly what app we are wrapping
                    tracer.AddTag("Driver", "ChronoDriver");
                    tracer.AddTag("TargetExecutable", targetExe);
                    tracer.AddTag("TargetArguments", targetArgs);

                    tracer.InvokeScope("ExternalProcess.Execute", () =>
                    {
                        exitCode = RunExternalProcess(targetExe, targetArgs);
                    });
                });

                if (!result.Success)
                {
                    // The external process failed. ChronoStack has already sent the 
                    // detailed trace, exit code, and stderr output to your configured sinks!
                    Console.WriteLine($"\n[ChronoDriver] Execution failed. Trace ID: {result.CorrelationId}");
                    return exitCode != 0 ? exitCode : -1;
                }
            }

            return exitCode;
        }

        private static int RunExternalProcess(string exePath, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Pass the Correlation ID down to the child process just in case!
            var currentSessionId = Environment.GetEnvironmentVariable("CHRONOSTACK_CORRELATION_ID");
            if (!string.IsNullOrEmpty(currentSessionId))
            {
                startInfo.EnvironmentVariables["CHRONOSTACK_CORRELATION_ID"] = currentSessionId;
            }

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            using (var process = new Process { StartInfo = startInfo })
            {
                // Capture stdout and stderr asynchronously to prevent deadlocks
                process.OutputDataReceived += (sender, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
                process.ErrorDataReceived += (sender, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                process.WaitForExit();

                // If you want to keep the original stdout flowing to the console:
                if (outputBuilder.Length > 0) Console.Write(outputBuilder.ToString());

                // If the process returned an error code, throw an exception so ChronoStack logs it!
                if (process.ExitCode != 0)
                {
                    var errorOutput = errorBuilder.Length > 0 ? errorBuilder.ToString().Trim() : "No Standard Error output provided.";
                    
                    // Throwing this traps the ExitCode and the StdErr inside the ChronoStack JSON Payload
                    throw new ExternalProcessException($"Process exited with code {process.ExitCode}. Error: {errorOutput}")
                    {
                        ExitCode = process.ExitCode,
                        TargetExecutable = exePath
                    };
                }

                return process.ExitCode;
            }
        }
    }

    /// <summary>
    /// A custom exception to hold black-box failure data.
    /// </summary>
    public class ExternalProcessException : Exception
    {
        public int ExitCode { get; set; }
        public string TargetExecutable { get; set; } = string.Empty;

        public ExternalProcessException(string message) : base(message) { }
    }
}
