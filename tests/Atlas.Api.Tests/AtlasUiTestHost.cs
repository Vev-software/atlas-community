using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// Starts the built Atlas API in a real child process on a loopback HTTP port so browser automation
/// can exercise the shipped UI against the same runtime a developer would launch locally.
/// </summary>
public sealed class AtlasUiTestHost : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"atlas-ui-tests-{Guid.NewGuid():N}.db");
    private readonly StringBuilder _logs = new();
    private Process? _process;

    public Uri RootUri { get; private set; } = null!;

    public HttpClient CreateBrowserClient(
        string tenant = "acme",
        string principal = "arch",
        string roles = "AtlasArchitect")
    {
        if (RootUri is null)
        {
            throw new InvalidOperationException("The UI host has not been started yet.");
        }

        var client = new HttpClient { BaseAddress = RootUri };
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        client.DefaultRequestHeaders.Add("X-Principal-Id", principal);
        client.DefaultRequestHeaders.Add("X-Principal-Roles", roles);
        return client;
    }

    public async Task InitializeAsync()
    {
        var port = ReserveLoopbackPort();
        RootUri = new Uri($"http://127.0.0.1:{port}");

        var apiAssemblyPath = typeof(Program).Assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet", $"\"{apiAssemblyPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(apiAssemblyPath)!
        };

        startInfo.Environment["ASPNETCORE_URLS"] = RootUri.ToString();
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ConnectionStrings__Atlas"] = $"Data Source={_databasePath}";

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the Atlas API process.");
        _ = PumpAsync(_process.StandardOutput);
        _ = PumpAsync(_process.StandardError);

        await WaitForHealthyAsync();
    }

    public async Task DisposeAsync()
    {
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            finally
            {
                await _process.WaitForExitAsync();
                _process.Dispose();
            }
        }

        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
            // Best effort only: a failed cleanup must not hide the test result.
        }
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (_logs)
            {
                _logs.AppendLine(line);
            }
        }
    }

    private async Task WaitForHealthyAsync()
    {
        using var client = new HttpClient { BaseAddress = RootUri };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"The Atlas API process exited before becoming healthy.{Environment.NewLine}{CollectedLogs()}");
            }

            try
            {
                using var response = await client.GetAsync("/health");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(250);
        }

        throw new InvalidOperationException(
            $"Timed out waiting for the Atlas API process to become healthy.{Environment.NewLine}{CollectedLogs()}",
            lastError);
    }

    private string CollectedLogs()
    {
        lock (_logs)
        {
            return _logs.Length == 0 ? string.Empty : $"Process output:{Environment.NewLine}{_logs}";
        }
    }
}
