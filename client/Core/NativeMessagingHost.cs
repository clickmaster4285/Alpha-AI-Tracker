using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace client.Core;

/// <summary>
/// Pure C# native messaging host — the drop-in replacement for the former
/// <c>extensions/native-host.py</c>. Mirrors the Python implementation 1:1 so
/// <c>NativeMessageService</c> needs zero protocol changes.
///
/// Protocol (Native Messaging): each message on stdin is prefixed by a 32-bit
/// native-endian length, followed by the UTF-8 JSON body (1 MB cap). Responses
/// are written back to stdout in the same format.
///
/// Each message is forwarded over a FRESH Unix socket connection to the tracker
/// socket (NativeMessageService reads ONE message per connection, acks, and
/// disposes the handler — so a fresh connection per message is required).
/// "ping" actions are special-cased: forwarded fire-and-forget (so the
/// heartbeat is recorded) and answered with a pong.
///
/// Socket failures are never fatal — an error JSON is written back instead,
/// mirroring native-host.py's <c>forward_to_tracker()</c>.
/// </summary>
public static class NativeMessagingHost
{
    private const int MaxMessageSize = 1024 * 1024;  // 1 MB cap, matches native-host.py
    private const int SocketTimeoutMs = 3000;        // matches native-host.py SOCKET_TIMEOUT

    /// <summary>
    /// Run the host loop: read framed messages from stdin until EOF, forward
    /// each to the tracker socket, write the framed response to stdout.
    /// </summary>
    public static int Run()
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();

            while (true)
            {
                JsonObject? message;
                try
                {
                    message = ReadMessage(stdin);
                }
                catch (Exception ex)
                {
                    // Malformed frame (e.g. bad JSON) — reply with an error frame
                    // and CONTINUE, exactly like native-host.py's main() except
                    // handler. Only EOF / oversized frames break the loop.
                    WriteMessage(stdout, new JsonObject
                    {
                        ["status"] = "error",
                        ["detail"] = ex.Message,
                    });
                    continue;
                }

                if (message == null)
                    break; // stdin EOF (or oversized message) — matches native-host.py

                // Ping — forward fire-and-forget so the heartbeat updates,
                // then acknowledge with a pong.
                if (message["action"]?.GetValue<string>() == "ping")
                {
                    try { ForwardToTracker(message); }
                    catch { /* heartbeat best-effort; still pong below */ }

                    WriteMessage(stdout, new JsonObject
                    {
                        ["status"] = "ok",
                        ["detail"] = "pong",
                    });
                    continue;
                }

                // Regular message — forward and echo the tracker's response.
                var response = ForwardToTracker(message);
                WriteMessage(stdout, response);
            }

            return 0;
        }
        catch (Exception ex)
        {
            // Never crash — attempt to surface the error to the browser.
            try
            {
                WriteMessage(Console.OpenStandardOutput(), new JsonObject
                {
                    ["status"] = "error",
                    ["detail"] = ex.Message,
                });
            }
            catch { /* stdout unusable — nothing else we can do */ }
            return 1;
        }
    }

    /// <summary>
    /// Read one framed message from stdin. Returns null on EOF or when the
    /// length prefix exceeds the 1 MB cap (both terminate the loop, matching
    /// native-host.py's <c>read_message()</c>).
    /// </summary>
    private static JsonObject? ReadMessage(Stream stdin)
    {
        var lengthBytes = ReadExactly(stdin, 4);
        if (lengthBytes == null) return null;

        var length = BitConverter.IsLittleEndian
            ? BitConverter.ToUInt32(lengthBytes, 0)
            : BitConverter.ToUInt32(lengthBytes.Reverse().ToArray(), 0);

        if (length > MaxMessageSize) return null;

        var bodyBytes = ReadExactly(stdin, (int)length);
        if (bodyBytes == null) return null;

        return JsonNode.Parse(Encoding.UTF8.GetString(bodyBytes)) as JsonObject
               ?? throw new FormatException("message body is not a JSON object");
    }

    /// <summary>Read exactly <paramref name="count"/> bytes or return null on EOF.</summary>
    private static byte[]? ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read == 0) return null; // EOF
            offset += read;
        }
        return buffer;
    }

    /// <summary>
    /// Forward a message to the tracker over a fresh Unix socket connection and
    /// read its response. Any socket failure produces an error JSON instead of
    /// throwing — mirroring native-host.py's <c>forward_to_tracker()</c>.
    /// </summary>
    private static JsonObject ForwardToTracker(JsonObject payload)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(NativeMessagingPaths.SocketPath));
        }
        catch (SocketException ex)
        {
            return ErrorJson(TrackerUnreachableDetail(ex));
        }

        try
        {
            socket.SendTimeout = SocketTimeoutMs;
            socket.ReceiveTimeout = SocketTimeoutMs;

            var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
            socket.Send(body);

            var buffer = new byte[8192];
            var received = socket.Receive(buffer);
            if (received <= 0)
                return new JsonObject { ["status"] = "ok" };

            var responseJson = Encoding.UTF8.GetString(buffer, 0, received);
            return JsonNode.Parse(responseJson) as JsonObject
                   ?? new JsonObject { ["status"] = "ok" };
        }
        catch (SocketException)
        {
            return ErrorJson("forward failed");
        }
        catch (Exception)
        {
            return ErrorJson("forward failed");
        }
    }

    private static string TrackerUnreachableDetail(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "tracker not running",
        SocketError.AddressNotAvailable => "tracker not running",
        _ => $"socket error: {ex.SocketErrorCode}",
    };

    private static JsonObject ErrorJson(string detail) => new()
    {
        ["status"] = "error",
        ["detail"] = detail,
    };

    /// <summary>Write a framed JSON message to stdout.</summary>
    private static void WriteMessage(Stream stdout, JsonObject message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var length = BitConverter.IsLittleEndian
            ? BitConverter.GetBytes((uint)body.Length)
            : BitConverter.GetBytes((uint)body.Length).Reverse().ToArray();
        stdout.Write(length, 0, 4);
        stdout.Write(body, 0, body.Length);
        stdout.Flush();
    }
}
