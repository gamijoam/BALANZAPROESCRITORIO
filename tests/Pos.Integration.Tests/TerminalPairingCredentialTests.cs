using Pos.Server;
using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Persistence;
using System.Net;

namespace Pos.Integration.Tests;

public sealed class TerminalPairingCredentialTests
{
    [Fact]
    public void Generated_terminal_secret_is_random_hex_and_validation_is_constant_time_compatible()
    {
        var first = TerminalPairingCredential.Create();
        var second = TerminalPairingCredential.Create();

        Assert.Equal(64, first.Length);
        Assert.All(first, character => Assert.True(Uri.IsHexDigit(character)));
        Assert.NotEqual(first, second);
        Assert.True(TerminalPairingCredential.Matches(first, TerminalPairingCredential.Hash(first)));
        Assert.False(TerminalPairingCredential.Matches(second, TerminalPairingCredential.Hash(first)));
        Assert.False(TerminalPairingCredential.Matches("invalid", TerminalPairingCredential.Hash(first)));
    }

    [Fact]
    public void Pairing_credentials_are_persisted_as_hashes_in_a_migrated_server_table()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql("Host=localhost;Database=metadata_only;Username=none;Password=none").Options;
        using var db = new PosDbContext(options);
        var entity = db.Model.FindEntityType(typeof(PairedTerminalRecord));

        Assert.NotNull(entity);
        Assert.Equal("paired_terminals", entity!.GetTableName());
        Assert.Equal(64, entity.FindProperty(nameof(PairedTerminalRecord.CredentialHash))!.GetMaxLength());
        Assert.Contains(db.Database.GetMigrations(), name => name.Contains("PairedTerminalCredentials", StringComparison.Ordinal));
    }

    [Fact]
    public void Private_network_requests_require_https_but_not_a_separate_terminal_pairing_secret()
    {
        Assert.False(TerminalPairingPolicy.RequiresCredential(requireForAll: false, IPAddress.Parse("192.168.1.15")));
        Assert.True(TerminalPairingPolicy.RequiresHttps(IPAddress.Parse("192.168.1.15")));
        Assert.True(TerminalPairingPolicy.RequiresCredential(requireForAll: true, IPAddress.Parse("192.168.1.15")));
        Assert.True(TerminalPairingPolicy.RequiresCredential(requireForAll: true, IPAddress.Loopback));
        Assert.False(TerminalPairingPolicy.RequiresCredential(requireForAll: false, remoteAddress: null));
        Assert.False(TerminalPairingPolicy.RequiresHttps(IPAddress.Loopback));
        Assert.False(TerminalPairingPolicy.RequiresCredential(requireForAll: false, IPAddress.Loopback));
    }
}
