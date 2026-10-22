using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using client.Core;

namespace client.Services;

/// <summary>
/// Client for the LocalSystem DLP service named pipe (APP_IDENTIFIERS WINDOWS_DLP_PIPE_NAME).
/// All calls are hard-bounded (connect + response) so a hung service never blocks the CLI.
/// </summary>
public static class DlpPipeClient
{
    public static string DefaultPipeName => AppInfo.WindowsDlpPipeName;

    /// <summary>Default: 2s connect + 12s response (covers service→server verify).</summary>
    public const int DefaultConnectTimeoutMs = 2_000;
    public const int DefaultResponseTimeoutMs = 12_000;

    public static async Task<(bool Ok, string Body)> SendAsync(
        object request,
        string? pipeName = null,
        int connectTimeoutMs = DefaultConnectTimeoutMs,
        int responseTimeoutMs = DefaultResponseTimeoutMs,
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

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(Math.Clamp(connectTimeoutMs, 500, 10_000));
            await client.ConnectAsync(connectCts.Token);

            await using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);

            using var responseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            responseCts.CancelAfter(Math.Clamp(responseTimeoutMs, 1_000, 30_000));

            await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), responseCts.Token);
            var line = await reader.ReadLineAsync(responseCts.Token);
            return (true, line ?? "{\"ok\":false,\"error\":\"empty_response\"}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "{\"ok\":false,\"error\":\"timeout\"}");
        }
        catch (Exception ex)
        {
            return (false, JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
        }
    }

    /// <summary>
    /// Service verifies the one-time code with the server, writes teardown.ok, unlocks ACL.
    /// Prefer <see cref="ApplyTeardownGateAsync"/> after the CLI has already verified via HTTP.
    /// </summary>
    public static async Task<bool> AuthorizeTeardownAsync(
        string code,
        string purpose = "both",
        CancellationToken ct = default)
    {
        var (connected, body) = await SendAsync(
            new { op = "AuthorizeTeardown", code, purpose },
            connectTimeoutMs: DefaultConnectTimeoutMs,
            responseTimeoutMs: DefaultResponseTimeoutMs,
            ct: ct);
        return connected && IsOk(body);
    }

    /// <summary>
    /// After the CLI wrote teardown.ok (HTTP verify already done), ask the service to
    /// unlock SCM ACL and stop — no second code consume.
    /// </summary>
    public static async Task<bool> ApplyTeardownGateAsync(
        string purpose = "both",
        CancellationToken ct = default)
    {
        var (connected, body) = await SendAsync(
            new { op = "ApplyTeardownGate", purpose },
            connectTimeoutMs: DefaultConnectTimeoutMs,
            responseTimeoutMs: 5_000,
            ct: ct);
        return connected && IsOk(body);
    }

    private static bool IsOk(string body)
    {
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
