#pragma warning disable GHCP001

using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace Pagurian.Modules.Copilot;

internal sealed record CopilotUsageAuthentication(
    bool IsAuthenticated, CopilotUsageAccount Account, string? StatusMessage);

internal interface ICopilotUsageRuntime : IDisposable
{
    Task StartAsync(CancellationToken token);
    Task<CopilotUsageAuthentication> GetAuthStatusAsync(CancellationToken token);
    Task<IDictionary<string, AccountQuotaSnapshot>?> GetQuotaAsync(CancellationToken token);
    Task StopAsync();
    Task ForceStopAsync();
}

internal sealed class CopilotUsageRuntime : ICopilotUsageRuntime
{
    private readonly CopilotClient _client;

    internal CopilotUsageRuntime(string runtimePath)
    {
        if (!File.Exists(runtimePath))
            throw new FileNotFoundException(
                "The bundled GitHub Copilot runtime is unavailable.", runtimePath);

        _client = new CopilotClient(new CopilotClientOptions
        {
            Mode = CopilotClientMode.CopilotCli,
            UseLoggedInUser = true,
            Connection = RuntimeConnection.ForStdio(runtimePath),
        });
    }

    public Task StartAsync(CancellationToken token) => _client.StartAsync(token);

    public async Task<CopilotUsageAuthentication> GetAuthStatusAsync(CancellationToken token)
    {
        var auth = await _client.GetAuthStatusAsync(token).ConfigureAwait(false);
        return new(auth.IsAuthenticated,
            new(Clean(auth.Login), Clean(auth.Host), Clean(auth.AuthType)),
            Clean(auth.StatusMessage));
    }

    public async Task<IDictionary<string, AccountQuotaSnapshot>?> GetQuotaAsync(
        CancellationToken token) =>
        (await _client.Rpc.Account.GetQuotaAsync(null, null, token).ConfigureAwait(false))
            .QuotaSnapshots;

    public Task StopAsync() => _client.StopAsync();
    public Task ForceStopAsync() => _client.ForceStopAsync();
    public void Dispose() => _client.Dispose();

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

#pragma warning restore GHCP001
