# Shipping ChronoStack JSONL with NXLog

ChronoStack does not need a dedicated NXLog runtime dependency. Emit structured JSON Lines with `JsonlTraceSink`, then let the NXLog agent tail, buffer, transform, and forward the file.

This keeps application code simple:

```csharp
using ChronoStack;

var options = new TracerOptions
{
    MessageRedactor = RedactionPolicy.DefaultPiiPolicy().Redact,
    IncludeEnvironmentInfo = false
};

using var tracer = Tracer.Create(
    new ITraceSink[]
    {
        new JsonlTraceSink(@"C:\logs\chronostack\chronostack.jsonl")
    },
    options);
```

## Windows NXLog CE Example

This example tails ChronoStack JSONL and forwards each line over TCP. Adjust the output module for your destination, such as `om_ssl`, `om_http`, `om_udp`, or a vendor-specific NXLog module available in your edition.

The same configuration is available as `examples/nxlog/chronostack-windows.conf`.

```apache
define ROOT C:\Program Files\nxlog
define CHRONOSTACK_LOG C:\logs\chronostack\chronostack.jsonl

Moduledir %ROOT%\modules
CacheDir  %ROOT%\data
Pidfile   %ROOT%\data\nxlog.pid
SpoolDir  %ROOT%\data
LogFile   %ROOT%\data\nxlog.log

<Extension json>
    Module xm_json
</Extension>

<Input chronostack_jsonl>
    Module im_file
    File "%CHRONOSTACK_LOG%"
    SavePos TRUE
    ReadFromLast TRUE
    InputType LineBased
    Exec parse_json();
    Exec $SourceName = "ChronoStack";
    Exec $EventType = "ChronoStack.ErrorReport";
</Input>

<Output siem_tcp>
    Module om_tcp
    Host siem.example.internal
    Port 5514
    Exec to_json();
</Output>

<Route chronostack_to_siem>
    Path chronostack_jsonl => siem_tcp
</Route>
```

## Linux NXLog CE Example

The same configuration is available as `examples/nxlog/chronostack-linux.conf`.

```apache
define CHRONOSTACK_LOG /var/log/chronostack/chronostack.jsonl

<Extension json>
    Module xm_json
</Extension>

<Input chronostack_jsonl>
    Module im_file
    File "%CHRONOSTACK_LOG%"
    SavePos TRUE
    ReadFromLast TRUE
    InputType LineBased
    Exec parse_json();
    Exec $SourceName = "ChronoStack";
    Exec $EventType = "ChronoStack.ErrorReport";
</Input>

<Output siem_tcp>
    Module om_tcp
    Host siem.example.internal
    Port 5514
    Exec to_json();
</Output>

<Route chronostack_to_siem>
    Path chronostack_jsonl => siem_tcp
</Route>
```

## Operational Notes

- Keep ChronoStack output as one JSON object per line. `JsonlTraceSink` already does this.
- Give the application write access to the log directory and the NXLog service account read access.
- Prefer local disk JSONL plus NXLog forwarding over direct in-process HTTP for high-volume or fragile networks.
- Keep redaction in the application before logs hit disk. NXLog can transform fields, but it should not be the first privacy boundary.
- Use NXLog TLS-capable outputs, certificate validation, and destination authentication for production forwarding.
- Configure log rotation for the JSONL file. NXLog `im_file` with `SavePos TRUE` is designed for tailing rotated files when paths remain predictable.
