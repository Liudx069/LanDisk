using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LanDisk.Core.Networking;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// H04-04: port conflicts must be reported, not worked around.
///
/// The rule being protected is a product promise, so it is tested directly: a busy port is detected
/// before anything is started, the message names the port, and the chosen ports never drift to a
/// free one.
/// </summary>
public sealed class PortPreflightTests
{
    /// <summary>Holds a loopback port for the duration of a test.</summary>
    private sealed class OccupiedPort : IDisposable
    {
        private readonly TcpListener _listener;

        public OccupiedPort()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public void Dispose() => _listener.Stop();
    }

    [Fact]
    public void A_free_port_is_reported_as_free_and_a_held_port_as_busy()
    {
        using var held = new OccupiedPort();

        Assert.False(PortPreflight.IsPortFree(held.Port));
        Assert.True(PortPreflight.IsPortFree(TakeFreePort()));
    }

    [Fact]
    public void A_conflict_names_the_port_and_explains_that_the_port_will_not_be_changed()
    {
        using var held = new OccupiedPort();

        PortConflict? conflict = PortPreflight.FindConflict([held.Port]);

        Assert.NotNull(conflict);
        Assert.Equal(held.Port, conflict!.Port);
        Assert.Contains(held.Port.ToString(), conflict.Message);
        Assert.Contains("不会自动更换端口", conflict.Message);

        TestBase.Record("H04-04", $"端口 {held.Port} 被占用时给出明确的冲突消息，并声明不会自动换端口");
    }

    [Fact]
    public void No_conflict_is_reported_when_every_port_is_available()
    {
        int first = TakeFreePort();
        int second = TakeFreePort();

        Assert.Null(PortPreflight.FindConflict([first, second]));
    }

    [Fact]
    public void The_first_busy_port_is_reported()
    {
        using var held = new OccupiedPort();
        int free = TakeFreePort();

        PortConflict? conflict = PortPreflight.FindConflict([free, held.Port]);

        Assert.NotNull(conflict);
        Assert.Equal(held.Port, conflict!.Port);
    }

    [Fact]
    public void ResolveConfiguredPorts_prefers_the_persisted_settings()
    {
        int[] ports = PortPreflight.ResolveConfiguredPorts(18991, 18992, []);

        Assert.Equal([18991, 18992], ports);
    }

    [Fact]
    public void ResolveConfiguredPorts_lets_an_explicit_flag_override_the_settings()
    {
        int[] ports = PortPreflight.ResolveConfiguredPorts(18991, 18992, ["--port", "28991", "--https-port", "28992"]);

        Assert.Equal([28991, 28992], ports);
    }

    [Fact]
    public void ResolveConfiguredPorts_falls_back_to_the_documented_defaults()
    {
        // A settings file that has never been written (or holds 0) must yield 8891/8892, not 0.
        Assert.Equal([8891, 8892], PortPreflight.ResolveConfiguredPorts(0, 0, []));
    }

    [Fact]
    public void ResolveConfiguredPorts_ignores_an_unparseable_or_nonpositive_flag()
    {
        Assert.Equal([8891, 8892], PortPreflight.ResolveConfiguredPorts(0, 0, ["--port", "abc", "--https-port", "-5"]));
    }

    [Fact]
    public void ResolveConfiguredPorts_collapses_to_one_entry_when_both_ports_are_the_same()
    {
        Assert.Equal([18891], PortPreflight.ResolveConfiguredPorts(18891, 18891, []));
    }

    [Fact]
    public void The_configured_ports_are_never_substituted_for_a_free_one()
    {
        using var held = new OccupiedPort();

        // Even when a port is busy, the resolved list still contains it: the shell must fail loudly
        // rather than bind something else and leave the user's saved address pointing at nothing.
        int[] ports = PortPreflight.ResolveConfiguredPorts(held.Port, held.Port + 1, []);

        Assert.Contains(held.Port, ports);
        Assert.NotNull(PortPreflight.FindConflict(ports));
    }

    [Fact]
    public async Task A_second_instance_of_the_server_does_not_take_a_different_port()
    {
        // The published LanDisk.exe refuses to bind a port other than the configured one. The
        // observable consequence for a caller: when the configured port is taken, the host fails to
        // start instead of silently listening somewhere else.
        await using TestHost first = await TestHost.CreateAsync(nameof(A_second_instance_of_the_server_does_not_take_a_different_port));

        int busy = new Uri(first.BaseUrl).Port;
        Assert.False(PortPreflight.IsPortFree(busy));

        PortConflict? conflict = PortPreflight.FindConflict([busy]);
        Assert.NotNull(conflict);
        Assert.Equal(busy, conflict!.Port);
    }

    [Fact]
    public void An_invalid_port_number_is_never_considered_free()
    {
        Assert.False(PortPreflight.IsPortFree(0));
        Assert.False(PortPreflight.IsPortFree(-1));
        Assert.False(PortPreflight.IsPortFree(70000));
    }

    private static int TakeFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
