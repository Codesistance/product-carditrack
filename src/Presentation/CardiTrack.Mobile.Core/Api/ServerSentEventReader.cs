using System.Runtime.CompilerServices;
using System.Text;

namespace CardiTrack.Mobile.Core.Api;

/// <summary>One event from a <c>text/event-stream</c> body.</summary>
public sealed record ServerSentEvent(string Name, string Data);

/// <summary>
/// Reads <c>text/event-stream</c> events off a response body as they arrive — the subset of
/// the format the API writes (<c>event:</c>, <c>data:</c>, comment lines, blank-line
/// dispatch), read line by line so each event is handed over the moment its blank line lands
/// rather than when the body ends.
/// </summary>
public static class ServerSentEventReader
{
    public static async IAsyncEnumerable<ServerSentEvent> ReadAsync(
        Stream body, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(body, Encoding.UTF8);
        string? name = null;
        var data = new StringBuilder();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                // Blank line: dispatch what has accumulated. An event with no data is legal and
                // carries nothing, so it is skipped the way a browser's EventSource skips it.
                if (data.Length > 0)
                    yield return new ServerSentEvent(name ?? "message", data.ToString());
                name = null;
                data.Clear();
                continue;
            }

            // A comment — the API's keep-alive heartbeat. Bytes on the wire, nothing to read.
            if (line[0] == ':')
                continue;

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];

            switch (field)
            {
                case "event":
                    name = value;
                    break;
                case "data":
                    if (data.Length > 0)
                        data.Append('\n');
                    data.Append(value);
                    break;
            }
        }
    }
}
