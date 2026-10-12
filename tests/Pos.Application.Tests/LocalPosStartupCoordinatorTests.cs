using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

/// <summary>
/// Criterios de aceptación del arranque local:
/// - reutiliza un servicio ya listo sin crear otro proceso;
/// - inicia el servidor local cuando no está disponible y espera a PostgreSQL;
/// - distingue un servidor activo con PostgreSQL caído de un servidor que aún inicia.
/// </summary>
public sealed class LocalPosStartupCoordinatorTests
{
    [Fact]
    public async Task Reuses_ready_server_without_starting_another_process()
    {
        var host = new FakePosServerHost(PosServerHealth.Ready);
        var coordinator = new PosLocalStartupCoordinator(host, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        await coordinator.EnsureReadyAsync();

        Assert.Equal(0, host.StartCount);
    }

    [Fact]
    public async Task Starts_server_once_and_waits_until_database_is_ready()
    {
        var host = new FakePosServerHost(PosServerHealth.NotRunning, PosServerHealth.Starting, PosServerHealth.Ready);
        var coordinator = new PosLocalStartupCoordinator(host, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        await coordinator.EnsureReadyAsync();

        Assert.Equal(1, host.StartCount);
        Assert.Equal(3, host.ProbeCount);
    }

    [Fact]
    public async Task Does_not_start_duplicate_server_when_database_is_unavailable()
    {
        var host = new FakePosServerHost(PosServerHealth.DatabaseUnavailable);
        var coordinator = new PosLocalStartupCoordinator(host, TimeSpan.Zero, TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<PosDatabaseUnavailableException>(() => coordinator.EnsureReadyAsync());

        Assert.Equal(0, host.StartCount);
        Assert.Contains("PostgreSQL", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Can_retry_readiness_after_postgresql_recovers_without_restarting_the_pos()
    {
        var host = new FakePosServerHost(PosServerHealth.DatabaseUnavailable, PosServerHealth.Ready);
        var coordinator = new PosLocalStartupCoordinator(host, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<PosDatabaseUnavailableException>(() => coordinator.EnsureReadyAsync());
        await coordinator.EnsureReadyAsync();

        Assert.Equal(0, host.StartCount);
        Assert.Equal(2, host.ProbeCount);
    }

    [Fact]
    public async Task Stops_the_process_it_started_when_database_never_becomes_ready()
    {
        var host = new FakePosServerHost(PosServerHealth.NotRunning, PosServerHealth.DatabaseUnavailable);
        var coordinator = new PosLocalStartupCoordinator(host, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<PosDatabaseUnavailableException>(() => coordinator.EnsureReadyAsync());

        Assert.Equal(1, host.StartCount);
        Assert.Equal(1, host.StopCount);
    }

    private sealed class FakePosServerHost(params PosServerHealth[] healthSequence) : IPosServerHost
    {
        private readonly Queue<PosServerHealth> _healthSequence = new(healthSequence);
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int ProbeCount { get; private set; }

        public Task<PosServerHealth> ProbeAsync(CancellationToken cancellationToken = default)
        {
            ProbeCount++;
            var health = _healthSequence.Count > 1 ? _healthSequence.Dequeue() : _healthSequence.Peek();
            return Task.FromResult(health);
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }
}
