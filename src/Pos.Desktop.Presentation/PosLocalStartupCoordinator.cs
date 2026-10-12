using System.Diagnostics;

namespace Pos.Desktop.Presentation;

public enum PosServerHealth
{
    NotRunning,
    Starting,
    DatabaseUnavailable,
    Ready
}

public interface IPosServerHost
{
    Task<PosServerHealth> ProbeAsync(CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class PosDatabaseUnavailableException(string message) : InvalidOperationException(message);

public sealed class PosServerStartupException(string message) : InvalidOperationException(message);

/// <summary>Ensures the local POS API is ready without starting a duplicate process.</summary>
public sealed class PosLocalStartupCoordinator(
    IPosServerHost host,
    TimeSpan? pollInterval = null,
    TimeSpan? startupTimeout = null)
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
    private readonly TimeSpan _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(30);

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        var currentHealth = await host.ProbeAsync(cancellationToken);
        if (currentHealth == PosServerHealth.Ready) return;
        if (currentHealth == PosServerHealth.DatabaseUnavailable)
            throw new PosDatabaseUnavailableException("El servicio del POS está activo, pero no puede conectarse a PostgreSQL. Revisa que PostgreSQL esté iniciado y que la configuración de conexión sea correcta.");

        var startedHere = currentHealth == PosServerHealth.NotRunning;
        if (startedHere)
            await host.StartAsync(cancellationToken);

        var timer = Stopwatch.StartNew();
        var databaseUnavailable = false;
        try
        {
            while (timer.Elapsed <= _startupTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentHealth = await host.ProbeAsync(cancellationToken);
                if (currentHealth == PosServerHealth.Ready) return;
                databaseUnavailable |= currentHealth == PosServerHealth.DatabaseUnavailable;
                if (_pollInterval > TimeSpan.Zero)
                    await Task.Delay(_pollInterval, cancellationToken);
            }

            if (databaseUnavailable)
                throw new PosDatabaseUnavailableException("El servidor del POS inició, pero PostgreSQL no quedó disponible. Verifica PostgreSQL y la configuración guardada.");

            throw new PosServerStartupException("El servicio local del POS no respondió a tiempo. Revisa los registros de la aplicación.");
        }
        catch
        {
            if (startedHere) await host.StopAsync(CancellationToken.None);
            throw;
        }
    }
}
