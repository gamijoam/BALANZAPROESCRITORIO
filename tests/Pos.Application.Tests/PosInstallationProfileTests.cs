using Pos.Desktop.Presentation;
using Pos.Desktop;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Pos.Application.Tests;

public sealed class PosInstallationProfileTests
{
    [Fact]
    public void Remote_terminal_requires_https_and_cannot_contain_database_credentials()
    {
        var insecure = new PosInstallationProfile(PosInstallationMode.ClientTerminal, "http://192.168.1.10:5155");
        Assert.Throws<ArgumentException>(() => { insecure.Validate(); });

        var valid = new PosInstallationProfile(PosInstallationMode.ClientTerminal, "https://pos.local:5155", TerminalCredential: new string('a', 64));
        valid.Validate();
        Assert.False(valid.DatabaseConfigured);
    }

    [Fact]
    public void Local_modes_use_the_loopback_api_and_server_profile_requires_database_configuration()
    {
        var standalone = new PosInstallationProfile(PosInstallationMode.Standalone, "http://127.0.0.1:5155", DatabaseConfigured: true);
        standalone.Validate();

        var missingDatabase = new PosInstallationProfile(PosInstallationMode.Server, "https://pos.local:5155",
            ServerCertificatePath: "server.pfx", ServerCertificatePassword: "encrypted-at-rest");
        Assert.Throws<InvalidOperationException>(() => PosInstallationStartupPlan.Resolve(missingDatabase));

        var server = missingDatabase with { DatabaseConfigured = true };
        var plan = PosInstallationStartupPlan.Resolve(server);
        Assert.True(plan.StartLocalServer);
        Assert.Equal("https://pos.local:5155", plan.ListenUrls);
    }

    [Fact]
    public void Protected_profile_store_round_trips_profile_without_plaintext_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "pos-profile-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "deployment.dpapi");
        var protector = new TestSecretProtector();
        var store = new PosInstallationProfileStore(path, protector);
        var profile = new PosInstallationProfile(PosInstallationMode.ClientTerminal, "https://pos.local:5155", TerminalCredential: new string('a', 64));

        try
        {
            store.Save(profile);
            Assert.Equal(profile, store.Load());
            Assert.DoesNotContain("https://pos.local", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Startup_plan_never_starts_a_local_database_server_for_a_client_terminal()
    {
        var client = new PosInstallationProfile(PosInstallationMode.ClientTerminal, "https://pos.local:5155", TerminalCredential: new string('a', 64));
        var plan = PosInstallationStartupPlan.Resolve(client);

        Assert.Equal(new Uri("https://pos.local:5155/"), plan.ApiBaseAddress);
        Assert.False(plan.StartLocalServer);
    }

    [Fact]
    public void Startup_plan_starts_loopback_service_for_independent_installation()
    {
        var standalone = new PosInstallationProfile(PosInstallationMode.Standalone, "http://127.0.0.1:5155", DatabaseConfigured: true);
        var plan = PosInstallationStartupPlan.Resolve(standalone);

        Assert.Equal(new Uri("http://127.0.0.1:5155/"), plan.ApiBaseAddress);
        Assert.True(plan.StartLocalServer);
    }

    [Fact]
    public void Client_profile_can_be_saved_with_a_pinned_server_certificate_without_terminal_pairing()
    {
        var directConnection = new PosInstallationProfile(PosInstallationMode.ClientTerminal, "https://192.168.1.20:5155",
            ServerCertificateSha256: new string('A', 64));

        directConnection.Validate();
        Assert.False(directConnection.DatabaseConfigured);
    }

    [Fact]
    public void Client_endpoint_accepts_a_plain_server_ip_and_uses_the_pos_port()
    {
        var endpoint = PosInstallationProfile.CreateClientEndpoint("192.168.1.20");

        Assert.Equal(new Uri("https://192.168.1.20:5155/"), endpoint);
    }

    [Fact]
    public void Server_setup_generates_a_private_certificate_for_the_detected_ip()
    {
        var root = Path.Combine(Path.GetTempPath(), "pos-lan-cert-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var generated = LocalLanServerConfiguration.CreateServerCertificate(IPAddress.Parse("192.168.1.20"), root);
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(generated.CertificatePath, generated.CertificatePassword,
                X509KeyStorageFlags.EphemeralKeySet);

            Assert.True(certificate.HasPrivateKey);
            Assert.True(ServerCertificatePin.IsValid(generated.CertificateSha256));
            Assert.True(ServerCertificatePin.Matches(generated.CertificateSha256, certificate));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Local_server_profile_is_repaired_when_its_saved_ip_is_no_longer_assigned()
    {
        var savedAddress = IPAddress.Parse("192.168.1.20");
        var currentAddresses = new[] { IPAddress.Parse("192.168.1.35"), IPAddress.Loopback };

        Assert.True(LocalLanServerConfiguration.RequiresAddressRepair(new[] { savedAddress }, currentAddresses));
        Assert.False(LocalLanServerConfiguration.RequiresAddressRepair(new[] { currentAddresses[0] }, currentAddresses));
    }

    [Fact]
    public void Local_server_setup_does_not_require_an_administrator_pairing_step()
    {
        var root = FindRepositoryRoot();
        var appStartup = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "App.xaml.cs"));
        var setupWindow = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "InstallationSetupWindow.xaml.cs"));
        var setupLayout = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "InstallationSetupWindow.xaml"));
        Assert.DoesNotContain("TerminalPairingWindow", appStartup, StringComparison.Ordinal);
        Assert.Contains("profileStore.Save(profile)", appStartup, StringComparison.Ordinal);
        Assert.DoesNotContain("PairTerminalAsync", setupWindow, StringComparison.Ordinal);
        Assert.Contains("CreateClientEndpoint", setupWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("AdministratorUsernameBox", setupLayout, StringComparison.Ordinal);
        Assert.DoesNotContain("CertificatePickerRow", setupLayout, StringComparison.Ordinal);
    }

    [Fact]
    public void First_run_profile_is_protected_before_starting_or_migrating_the_local_server()
    {
        var root = FindRepositoryRoot();
        var appStartup = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "App.xaml.cs"));
        var setupIndex = appStartup.IndexOf("setup.ShowDialog() != true", StringComparison.Ordinal);
        var saveIndex = appStartup.IndexOf("profileStore.Save(profile);", setupIndex, StringComparison.Ordinal);
        var serverStartIndex = appStartup.IndexOf("EnsureLocalServerReadyAsync", setupIndex, StringComparison.Ordinal);

        Assert.True(setupIndex >= 0 && saveIndex > setupIndex && saveIndex < serverStartIndex,
            "El perfil con certificado debe quedar cifrado antes de iniciar el servidor, para poder reintentar sin volver a configurarlo.");
    }

    [Fact]
    public void Existing_database_without_deployment_profile_still_gets_a_one_time_mode_selection()
    {
        var root = FindRepositoryRoot();
        var appStartup = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "App.xaml.cs"));

        Assert.DoesNotContain("HasExistingLocalDatabaseConfiguration", appStartup, StringComparison.Ordinal);
        Assert.Contains("var setup = new InstallationSetupWindow(configurationDirectory);", appStartup, StringComparison.Ordinal);
    }

    private sealed class TestSecretProtector : ILocalSecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Select(value => (byte)(value ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] protectedData) => protectedData.Select(value => (byte)(value ^ 0x5A)).ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pos.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
