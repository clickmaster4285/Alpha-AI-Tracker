using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace client.Services;

/// <summary>
/// Client for \\.\pipe\AlphaAITracker.Dlp (LocalSystem DLP service).
/// </summary>
public static class DlpPipeClient
{
    public const string DefaultPipeName = "AlphaAITracker.Dlp";

    public static async Task<(bool Ok, string Body)> SendAsync(
        object request,
        string? pipeName = null,
        int connectTimeoutMs = 3000,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "{\"ok\":false,\"error\":\"windows_only\"}");

        var name = string.IsNullOrWhiteSpace(pipeName)
            ? Environment.GetEnvironmentVariable("ALPHA_DLP_PIPE_NAME")
            : pipeName;
        if (string.IsNullOrWhiteSpace(name))
            name = DefaultPipeName;

        try
        {
            await using var client = new NamedPipeClientStream(
                ".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(connectTimeoutMs);
            await client.ConnectAsync(cts.Token);

            await using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);

            await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), ct);
            var line = await reader.ReadLineAsync(ct);
            return (true, line ?? "{\"ok\":false,\"error\":\"empty_response\"}");
        }
        catch (Exception ex)
        {
            return (false, JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
        }
    }

    public static async Task<bool> AuthorizeTeardownAsync(string code, string purpose = "both", CancellationToken ct = default)
    {
        var (connected, body) = await SendAsync(new { op = "AuthorizeTeardown", code, purpose }, ct: ct);
        if (!connected) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch
        {
            return false;
        }
    }
}
