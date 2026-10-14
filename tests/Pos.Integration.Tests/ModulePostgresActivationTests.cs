using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Infrastructure.Modules;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Example;
using Pos.Modules.Packaging;

namespace Pos.Integration.Tests;

public sealed class ModulePostgresActivationTests
{
    [Fact]
    public async Task ProductSearch_UsesAuthenticatedTerminalsWarehouseAndFallsBackToGlobalStockWithoutModule()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable("POS_MODULE_VALIDATION_ADMIN_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminConnectionString))
            throw Xunit.Sdk.SkipException.ForSkip("Configura POS_MODULE_VALIDATION_ADMIN_CONNECTION para habilitar la prueba PostgreSQL aislada de existencias F2.");

        var databaseName = "pos_f2_stock_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString);
        var databaseCreated = false;
        string? tempRoot = null;
        Process? server = null;
        try
        {
            await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await admin.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\";", admin);
                await create.ExecuteNonQueryAsync();
                databaseCreated = true;
            }

            var databaseBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = databaseName };
            var connectionString = databaseBuilder.ConnectionString;
            var options = new DbContextOptionsBuilder<PosDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(PosDbContext).Assembly.FullName)).Options;
            var productId = Guid.NewGuid();
            await using (var db = new PosDbContext(options))
            {
                await db.Database.MigrateAsync();
                db.Products.Add(new ProductRecord { Id = productId, Sku = "F2-STOCK", Name = "Producto de prueba", UnitPrice = 1m, Currency = "VES", Active = true, CreatedAtUtc = DateTime.UtcNow });
                db.Stocks.Add(new StockRecord { ProductId = productId, Quantity = 90m, Version = 1 });
                await db.SaveChangesAsync();
            }

            var terminalA = Guid.NewGuid();
            var terminalB = Guid.NewGuid();
            var terminalC = Guid.NewGuid();
            var warehouseId = Guid.NewGuid();
            var emptyWarehouseId = Guid.NewGuid();
            var schema = ModuleMigrationSqlGenerator.SchemaForModule(WarehouseStockBridge.ModuleId);

            tempRoot = Path.Combine(Path.GetTempPath(), "pos-f2-stock-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var endpoint = ReserveLoopbackEndpoint();
            var serverDll = Path.Combine(FindRepositoryRoot(), "src", "Pos.Server", "bin", "Debug", "net10.0", "Pos.Server.dll");
            Assert.True(File.Exists(serverDll), $"No se encontró el servidor compilado: {serverDll}");
            using var signer = RSA.Create(2048);
            var keyRingPath = Path.Combine(tempRoot, "module-trust.json");
            await File.WriteAllTextAsync(keyRingPath, $$"""
                {"schemaVersion":1,"keys":[{"keyId":"test","publicKeyPem":{{JsonSerializer.Serialize(signer.ExportSubjectPublicKeyInfoPem())}},"status":"Active"}]}
                """);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            server = StartServer(serverDll, connectionString, "Development", endpoint,
                keyRingPath, Path.Combine(tempRoot, "Modules"));
            await WaitForReadyAsync(client, endpoint, server);
            using (var setup = await client.PostAsJsonAsync(endpoint + "/api/v1/setup/first-operator", new { username = "f2-admin", pin = "4826" }))
                Assert.Equal(HttpStatusCode.Created, setup.StatusCode);

            async Task<decimal> SearchQuantityAsync(Guid terminalId)
            {
                using var login = await client.PostAsJsonAsync(endpoint + "/api/v1/auth/login", new { username = "f2-admin", pin = "4826", terminalId, clientVersion = "1.0.0" });
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                using var loginJson = await JsonDocument.ParseAsync(await login.Content.ReadAsStreamAsync());
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginJson.RootElement.GetProperty("accessToken").GetString()!);
                using var response = await client.GetAsync(endpoint + "/api/v1/products?query=F2-STOCK");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                var product = Assert.Single(result.RootElement.EnumerateArray());
                return product.GetProperty("quantity").GetDecimal();
            }

            Assert.Equal(90m, await SearchQuantityAsync(terminalA));

            await using (var adminDb = new NpgsqlConnection(connectionString))
            {
                await adminDb.OpenAsync();
                await using var createModuleTables = new NpgsqlCommand($"""
                    CREATE SCHEMA "{schema}";
                    CREATE TABLE "{schema}".warehouses (id uuid PRIMARY KEY, code text NOT NULL, name text NOT NULL, is_active boolean NOT NULL, is_primary boolean NOT NULL, created_at_utc timestamptz NOT NULL);
                    CREATE TABLE "{schema}".warehouse_stock (id uuid PRIMARY KEY, warehouse_id uuid NOT NULL, product_id uuid NOT NULL, quantity numeric(18,4) NOT NULL, version bigint NOT NULL, updated_at_utc timestamptz NOT NULL, UNIQUE (warehouse_id,product_id));
                    CREATE TABLE "{schema}".terminal_warehouses (terminal_id uuid PRIMARY KEY, warehouse_id uuid NOT NULL, updated_at_utc timestamptz NOT NULL);
                    INSERT INTO "{schema}".warehouses (id,code,name,is_active,is_primary,created_at_utc) VALUES ('{WarehouseStockBridge.MainWarehouseId}','PRINCIPAL','Principal',TRUE,TRUE,NOW()), ('{warehouseId}','SECUNDARIO','Secundario',TRUE,FALSE,NOW()), ('{emptyWarehouseId}','AGOTADO','Agotado',TRUE,FALSE,NOW());
                    INSERT INTO "{schema}".warehouse_stock (id,warehouse_id,product_id,quantity,version,updated_at_utc) VALUES
                        (gen_random_uuid(),'{WarehouseStockBridge.MainWarehouseId}','{productId}',90,0,NOW()),
                        (gen_random_uuid(),'{warehouseId}','{productId}',12,0,NOW()),
                        (gen_random_uuid(),'{emptyWarehouseId}','{productId}',0,0,NOW());
                    INSERT INTO "{schema}".terminal_warehouses (terminal_id,warehouse_id,updated_at_utc) VALUES
                        ('{terminalA}','{warehouseId}',NOW()), ('{terminalC}','{emptyWarehouseId}',NOW());
                    """, adminDb);
                await createModuleTables.ExecuteNonQueryAsync();
            }

            Assert.Equal(12m, await SearchQuantityAsync(terminalA));
            Assert.Equal(90m, await SearchQuantityAsync(terminalB));
            Assert.Equal(0m, await SearchQuantityAsync(terminalC));

            await using (var db = new PosDbContext(options))
            {
                await db.Database.OpenConnectionAsync();
                Assert.False(await WarehouseStockBridge.ConsumeAsync(db, warehouseId, productId, 13m, CancellationToken.None));
                await db.Database.CloseConnectionAsync();
            }
        }
        finally
        {
            if (server is { HasExited: false })
            {
                try { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
                catch (InvalidOperationException) { }
            }
            server?.Dispose();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            if (databaseCreated)
            {
                await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @database AND pid <> pg_backend_pid();", admin);
                terminate.Parameters.AddWithValue("database", databaseName);
                await terminate.ExecuteNonQueryAsync();
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\";", admin);
                await drop.ExecuteNonQueryAsync();
            }
            adminBuilder.Password = string.Empty;
            if (tempRoot is not null && Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ModulePlan_RecoversAfterApplicationRoleReceivesReadAccessToLegacyTerminalTable()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable("POS_MODULE_VALIDATION_ADMIN_CONNECTION");
        var postgresBinaryDirectory = Environment.GetEnvironmentVariable("POS_MODULE_VALIDATION_PG_BIN");
        if (string.IsNullOrWhiteSpace(adminConnectionString) || string.IsNullOrWhiteSpace(postgresBinaryDirectory))
            throw Xunit.Sdk.SkipException.ForSkip("Configura POS_MODULE_VALIDATION_ADMIN_CONNECTION y POS_MODULE_VALIDATION_PG_BIN para habilitar la prueba PostgreSQL aislada.");

        var databaseName = "pos_module_plan_" + Guid.NewGuid().ToString("N");
        var applicationRole = "pos_plan_" + Guid.NewGuid().ToString("N");
        var applicationPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var tempRoot = Path.Combine(Path.GetTempPath(), "pos-module-plan-" + Guid.NewGuid().ToString("N"));
        var databaseCreated = false;
        var roleCreated = false;
        Process? server = null;
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString);
        Directory.CreateDirectory(tempRoot);

        try
        {
            await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await admin.OpenAsync();
                await using (var createRole = new NpgsqlCommand($"CREATE ROLE \"{applicationRole}\" LOGIN PASSWORD '{applicationPassword}';", admin))
                    await createRole.ExecuteNonQueryAsync();
                roleCreated = true;
                await using (var createDatabase = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\";", admin))
                    await createDatabase.ExecuteNonQueryAsync();
                databaseCreated = true;
            }

            var adminDatabaseBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = databaseName };
            var adminDatabaseConnectionString = adminDatabaseBuilder.ConnectionString;
            await using (var migrationDb = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>()
                             .UseNpgsql(adminDatabaseConnectionString, options => options.MigrationsAssembly(typeof(PosDbContext).Assembly.FullName))
                             .Options))
                await migrationDb.Database.MigrateAsync();

            var roleConnectionBuilder = new NpgsqlConnectionStringBuilder(adminDatabaseConnectionString)
            {
                Username = applicationRole,
                Password = applicationPassword
            };
            var applicationConnectionString = roleConnectionBuilder.ConnectionString;
            using (var adminDatabase = new NpgsqlConnection(adminDatabaseConnectionString))
            {
                await adminDatabase.OpenAsync();
                await using (var grantTables = new NpgsqlCommand($"GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO \"{applicationRole}\";", adminDatabase))
                    await grantTables.ExecuteNonQueryAsync();
                await using (var grantSequences = new NpgsqlCommand($"GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO \"{applicationRole}\";", adminDatabase))
                    await grantSequences.ExecuteNonQueryAsync();
                await using (var revokeTerminalRead = new NpgsqlCommand($"REVOKE SELECT ON TABLE public.paired_terminals FROM \"{applicationRole}\";", adminDatabase))
                    await revokeTerminalRead.ExecuteNonQueryAsync();
            }

            using var signer = RSA.Create(2048);
            var trustRing = CreateTrustRing(signer);
            var packageBytes = BuildExamplePackage(signer);
            var moduleStorage = Path.Combine(tempRoot, "Modules");
            Directory.CreateDirectory(moduleStorage);
            var keyRingPath = Path.Combine(tempRoot, "module-trust.json");
            await File.WriteAllTextAsync(keyRingPath, $$"""
                {"schemaVersion":1,"keys":[{"keyId":"publisher","publicKeyPem":{{JsonSerializer.Serialize(signer.ExportSubjectPublicKeyInfoPem())}},"status":"Active"}]}
                """);

            var endpoint = ReserveLoopbackEndpoint();
            var serverDll = Path.Combine(FindRepositoryRoot(), "src", "Pos.Server", "bin", "Debug", "net10.0", "Pos.Server.dll");
            Assert.True(File.Exists(serverDll), $"No se encontró el servidor compilado: {serverDll}");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            server = StartServer(serverDll, applicationConnectionString, "Development", endpoint, keyRingPath, moduleStorage);
            await WaitForReadyAsync(client, endpoint, server);

            using (var setup = await client.PostAsJsonAsync(endpoint + "/api/v1/setup/first-operator", new { username = "module-admin", pin = "4826" }))
                Assert.Equal(HttpStatusCode.Created, setup.StatusCode);
            using (var login = await client.PostAsJsonAsync(endpoint + "/api/v1/auth/login", new
            {
                username = "module-admin", pin = "4826", terminalId = Guid.NewGuid(), clientVersion = "1.0.0"
            }))
            {
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                using var loginJson = await JsonDocument.ParseAsync(await login.Content.ReadAsStreamAsync());
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginJson.RootElement.GetProperty("accessToken").GetString()!);
            }

            async Task<HttpResponseMessage> PostModulePlanAsync()
            {
                var form = new MultipartFormDataContent();
                form.Add(new StringContent("example-status"), "requestedModuleId");
                form.Add(new ByteArrayContent(packageBytes), "packages", "example-status.zip");
                return await client.PostAsync(endpoint + "/api/v1/modules/plan", form);
            }

            using (var deniedPlan = await PostModulePlanAsync())
            {
                Assert.Equal(HttpStatusCode.InternalServerError, deniedPlan.StatusCode);
                using var error = await JsonDocument.ParseAsync(await deniedPlan.Content.ReadAsStreamAsync());
                Assert.Equal("module_plan_failed", error.RootElement.GetProperty("code").GetString());
                Assert.False(string.IsNullOrWhiteSpace(error.RootElement.GetProperty("diagnosticId").GetString()));
            }

            using (var adminDatabase = new NpgsqlConnection(adminDatabaseConnectionString))
            {
                await adminDatabase.OpenAsync();
                await using var grantTerminalRead = new NpgsqlCommand($"GRANT SELECT ON TABLE public.paired_terminals TO \"{applicationRole}\";", adminDatabase);
                await grantTerminalRead.ExecuteNonQueryAsync();
            }

            using var validPlan = await PostModulePlanAsync();
            Assert.Equal(HttpStatusCode.OK, validPlan.StatusCode);
            using var plan = await JsonDocument.ParseAsync(await validPlan.Content.ReadAsStreamAsync());
            Assert.True(plan.RootElement.GetProperty("planValid").GetBoolean());
            Assert.Equal("example-status", plan.RootElement.GetProperty("requestedModuleId").GetString());
        }
        finally
        {
            if (server is { HasExited: false })
            {
                try { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
                catch (InvalidOperationException) { }
            }
            server?.Dispose();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            if (databaseCreated)
            {
                await using var terminate = new NpgsqlCommand(
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @database AND pid <> pg_backend_pid();", admin);
                terminate.Parameters.AddWithValue("database", databaseName);
                await terminate.ExecuteNonQueryAsync();
                await using var dropDatabase = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\";", admin);
                await dropDatabase.ExecuteNonQueryAsync();
            }
            if (roleCreated)
            {
                await using var dropRole = new NpgsqlCommand($"DROP ROLE IF EXISTS \"{applicationRole}\";", admin);
                await dropRole.ExecuteNonQueryAsync();
            }
            adminBuilder.Password = string.Empty;
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SignedExample_InstallsActivatesDeactivatesRestoresAndKeepsDataAgainstDisposablePostgres()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable("POS_MODULE_VALIDATION_ADMIN_CONNECTION");
        var postgresBinaryDirectory = Environment.GetEnvironmentVariable("POS_MODULE_VALIDATION_PG_BIN");
        if (string.IsNullOrWhiteSpace(adminConnectionString) || string.IsNullOrWhiteSpace(postgresBinaryDirectory))
            throw Xunit.Sdk.SkipException.ForSkip("Configura POS_MODULE_VALIDATION_ADMIN_CONNECTION y POS_MODULE_VALIDATION_PG_BIN para habilitar la prueba PostgreSQL aislada.");

        var tempRoot = Path.Combine(Path.GetTempPath(), "pos-module-validation-" + Guid.NewGuid().ToString("N"));
        var databaseName = "pos_module_validation_" + Guid.NewGuid().ToString("N");
        var recoveryDatabaseName = "pos_module_validation_recovery_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString);
        var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
        Process? server = null;
        var databaseCreated = false;
        var recoveryDatabaseCreated = false;
        Directory.CreateDirectory(tempRoot);
        try
        {
            await adminConnection.OpenAsync();
            await using (var createDatabase = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\";", adminConnection))
                await createDatabase.ExecuteNonQueryAsync();
            databaseCreated = true;

            var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = databaseName };
            var connectionString = testBuilder.ConnectionString;
            var databaseOptions = new DbContextOptionsBuilder<PosDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(PosDbContext).Assembly.FullName))
                .Options;
            await using (var db = new PosDbContext(databaseOptions))
                await db.Database.MigrateAsync();

            using var signer = RSA.Create(2048);
            var trustRing = CreateTrustRing(signer);
            var packageBytes = BuildExamplePackage(signer);
            var moduleStorage = Path.Combine(tempRoot, "Modules");
            var backups = Path.Combine(tempRoot, "Backups");
            Directory.CreateDirectory(moduleStorage);
            Directory.CreateDirectory(backups);
            var planInput = new MemoryStream(packageBytes, writable: false);
            var plan = ModuleInstallationPlanner.CreatePlan([planInput], "example-status", 1,
                trustRing.VerifierPublicKeys, new HashSet<string>(StringComparer.Ordinal));
            var installInput = new MemoryStream(packageBytes, writable: false);
            var request = new ModuleInstallationRequest(plan,
                [new ModuleInstallationPackage("example-status", new Version(1, 0, 0), installInput)],
                connectionString, postgresBinaryDirectory, backups, moduleStorage, 1, trustRing,
                new HashSet<string>(StringComparer.Ordinal));
            var backup = new ModuleDatabaseBackupService(new ProcessPostgresToolRunner());
            var coordinator = new ModuleInstallationCoordinator( new ModuleInstallationBackupAdapter(backup),
                new ModuleInstallationPackageStoreAdapter(), new ModuleInstallationMigrationAdapter(new ModuleMigrationExecutor()));
            var installation = await coordinator.InstallAsync(request);
            Assert.Equal("RestartPending", installation.Status);
            Assert.Single(installation.Migrations);
            var registry = new ModuleInstallationRegistry();
            await registry.RecordRestartPendingAsync(connectionString, installation);

            var publicKeyPem = signer.ExportSubjectPublicKeyInfoPem();
            var keyRingPath = Path.Combine(tempRoot, "module-trust.json");
            await File.WriteAllTextAsync(keyRingPath, $$"""
                {"schemaVersion":1,"keys":[{"keyId":"publisher","publicKeyPem":{{JsonSerializer.Serialize(publicKeyPem)}},"status":"Active"}]}
                """);

            var endpoint = ReserveLoopbackEndpoint();
            var repoRoot = FindRepositoryRoot();
            var serverDll = Path.Combine(repoRoot, "src", "Pos.Server", "bin", "Debug", "net10.0", "Pos.Server.dll");
            Assert.True(File.Exists(serverDll), $"No se encontró el servidor compilado: {serverDll}");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            server = StartServer(serverDll, connectionString, "Development", endpoint, keyRingPath, moduleStorage);
            await WaitForReadyAsync(client, endpoint, server);

            var moduleResponse = await client.GetAsync(endpoint + "/api/v1/modules/example-status/status");
            Assert.Equal(HttpStatusCode.Unauthorized, moduleResponse.StatusCode);
            var active = Assert.Single(await registry.GetAllAsync(connectionString));
            Assert.Equal("Active", active.Status);
            Assert.Null(active.ActivationDetail);

            var terminalId = Guid.NewGuid();
            using (var unauthorizedDeactivate = await client.PostAsync(endpoint + "/api/v1/modules/example-status/deactivate", content: null))
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedDeactivate.StatusCode);
            using (var setup = await client.PostAsJsonAsync(endpoint + "/api/v1/setup/first-operator", new { username = "module-admin", pin = "4826" }))
                Assert.Equal(HttpStatusCode.Created, setup.StatusCode);
            using (var login = await client.PostAsJsonAsync(endpoint + "/api/v1/auth/login", new
            {
                username = "module-admin", pin = "4826", terminalId, clientVersion = "1.0.0"
            }))
            {
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                using var loginJson = await JsonDocument.ParseAsync(await login.Content.ReadAsStreamAsync());
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginJson.RootElement.GetProperty("accessToken").GetString()!);
            }
            using (var activeRoute = await client.GetAsync(endpoint + "/api/v1/modules/example-status/status"))
                Assert.Equal(HttpStatusCode.OK, activeRoute.StatusCode);

            using (var deactivate = await client.PostAsync(endpoint + "/api/v1/modules/example-status/deactivate", content: null))
                Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
            using (var disabledRoute = await client.GetAsync(endpoint + "/api/v1/modules/example-status/status"))
                Assert.Equal(HttpStatusCode.NotFound, disabledRoute.StatusCode);
            Assert.Equal("Disabled", Assert.Single(await registry.GetAllAsync(connectionString)).Status);
            var auditOptions = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(connectionString).Options;
            await using (var auditDb = new PosDbContext(auditOptions))
                Assert.Equal(1, await auditDb.AuditEvents.CountAsync(x => x.Action == "module.deactivated" && x.TerminalId == terminalId));

            await StopServerAsync(server);
            server.Dispose();
            server = null;
            server = StartServer(serverDll, connectionString, "Development", endpoint, keyRingPath, moduleStorage);
            await WaitForReadyAsync(client, endpoint, server);
            using (var disabledAfterRestart = await client.GetAsync(endpoint + "/api/v1/modules/example-status/status"))
                Assert.Equal(HttpStatusCode.NotFound, disabledAfterRestart.StatusCode);
            Assert.Equal("Disabled", Assert.Single(await registry.GetAllAsync(connectionString)).Status);

            using (var reactivate = await client.PostAsync(endpoint + "/api/v1/modules/example-status/activate", content: null))
                Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);
            var restartPending = Assert.Single(await registry.GetAllAsync(connectionString));
            Assert.Equal("RestartPending", restartPending.Status);
            Assert.Null(restartPending.ActivationDetail);
            await using (var auditDb = new PosDbContext(auditOptions))
                Assert.Equal(1, await auditDb.AuditEvents.CountAsync(x => x.Action == "module.reactivation_restart_pending" && x.TerminalId == terminalId));

            await StopServerAsync(server);
            server.Dispose();
            server = StartServer(serverDll, connectionString, "Development", endpoint, keyRingPath, moduleStorage);
            await WaitForReadyAsync(client, endpoint, server);
            using (var activeAfterReactivation = await client.GetAsync(endpoint + "/api/v1/modules/example-status/status"))
                Assert.Equal(HttpStatusCode.OK, activeAfterReactivation.StatusCode);
            Assert.Equal("Active", Assert.Single(await registry.GetAllAsync(connectionString)).Status);

            await using var verifyConnection = new NpgsqlConnection(connectionString);
            await verifyConnection.OpenAsync();
            await using var verifyMigration = new NpgsqlCommand(
                "SELECT to_regclass(@table) IS NOT NULL;", verifyConnection);
            verifyMigration.Parameters.AddWithValue("table", "m_example_status_" + ModuleMigrationSqlGenerator.SchemaForModule("example-status").Split('_').Last() + ".status_check");
            Assert.True((bool)(await verifyMigration.ExecuteScalarAsync())!);

            await using (var createRecovery = new NpgsqlCommand($"CREATE DATABASE \"{recoveryDatabaseName}\";", adminConnection))
                await createRecovery.ExecuteNonQueryAsync();
            recoveryDatabaseCreated = true;
            var recoveryConnection = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = recoveryDatabaseName };
            var restoreExecutable = Path.Combine(postgresBinaryDirectory, OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore");
            var restore = await new ProcessPostgresToolRunner().RunAsync(restoreExecutable,
                ["--no-password", "--exit-on-error", "--single-transaction", "--no-owner", "--no-privileges",
                    "--host", adminBuilder.Host!, "--port", adminBuilder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--username", adminBuilder.Username!, "--dbname", recoveryDatabaseName, installation.Backup.BackupPath],
                new Dictionary<string, string?> { ["PGPASSWORD"] = adminBuilder.Password });
            Assert.True(restore.ExitCode == 0, $"pg_restore falló en la base temporal: {restore.StandardError}");
            await using var verifyRecovery = new NpgsqlConnection(recoveryConnection.ConnectionString);
            await verifyRecovery.OpenAsync();
            await using var verifyBaseMigrations = new NpgsqlCommand("SELECT count(*) FROM \"__EFMigrationsHistory\";", verifyRecovery);
            Assert.True(Convert.ToInt32(await verifyBaseMigrations.ExecuteScalarAsync()) > 0);
            await using var verifyModuleAbsent = new NpgsqlCommand("SELECT to_regclass(@table) IS NULL;", verifyRecovery);
            verifyModuleAbsent.Parameters.AddWithValue("table", "m_example_status_" + ModuleMigrationSqlGenerator.SchemaForModule("example-status").Split('_').Last() + ".status_check");
            Assert.True((bool)(await verifyModuleAbsent.ExecuteScalarAsync())!);

            await StopServerAsync(server);
            server.Dispose();
            server = null;
        }
        finally
        {
            if (server is { HasExited: false })
            {
                try { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
                catch (InvalidOperationException) { }
            }
            server?.Dispose();
            if (recoveryDatabaseCreated)
            {
                await using var terminateRecovery = new NpgsqlCommand(
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @database AND pid <> pg_backend_pid();", adminConnection);
                terminateRecovery.Parameters.AddWithValue("database", recoveryDatabaseName);
                await terminateRecovery.ExecuteNonQueryAsync();
                await using var dropRecovery = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{recoveryDatabaseName}\";", adminConnection);
                await dropRecovery.ExecuteNonQueryAsync();
            }
            if (databaseCreated)
            {
                await using var terminate = new NpgsqlCommand(
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @database AND pid <> pg_backend_pid();", adminConnection);
                terminate.Parameters.AddWithValue("database", databaseName);
                await terminate.ExecuteNonQueryAsync();
                await using var dropDatabase = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\";", adminConnection);
                await dropDatabase.ExecuteNonQueryAsync();
            }
            await adminConnection.DisposeAsync();
            adminBuilder.Password = string.Empty;
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static Process StartServer(string serverDll, string connectionString, string environment, string endpoint,
        string keyRingPath, string moduleStorage)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(serverDll)!
        };
        startInfo.ArgumentList.Add(serverDll);
        startInfo.Environment["ConnectionStrings__Pos"] = connectionString;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        startInfo.Environment["ASPNETCORE_URLS"] = endpoint;
        startInfo.Environment["ModuleTrust__KeyRingPath"] = keyRingPath;
        startInfo.Environment["Modules__StoragePath"] = moduleStorage;
        startInfo.Environment.Remove("POS_MODULE_VALIDATION_ADMIN_CONNECTION");
        startInfo.Environment.Remove("POS_MODULE_VALIDATION_PG_BIN");
        var server = Process.Start(startInfo) ?? throw new InvalidOperationException("No se pudo iniciar el servidor POS de prueba.");
        _ = server.StandardOutput.ReadToEndAsync();
        _ = server.StandardError.ReadToEndAsync();
        return server;
    }

    private static async Task WaitForReadyAsync(HttpClient client, string endpoint, Process server)
    {
        for (var attempt = 0; attempt < 60 && !server.HasExited; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(endpoint + "/health/ready");
                if (response.StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(250);
        }
        Assert.Fail("El servidor de prueba no quedó listo.");
    }

    private static async Task StopServerAsync(Process server)
    {
        if (server.HasExited) return;
        server.Kill(entireProcessTree: true);
        await server.WaitForExitAsync();
    }

    private static byte[] BuildExamplePackage(RSA signer)
    {
        var assemblyPath = typeof(ExampleStatusModule).Assembly.Location;
        var assemblyName = Path.GetFileName(assemblyPath);
        var manifest = $$"""
            {"id":"example-status","version":"1.0.0","hostApi":{"min":1,"max":1},
             "clientApi":{"min":"1.0.0","maxExclusive":"2.0.0"},
             "entryAssembly":"{{assemblyName}}","entryType":"{{typeof(ExampleStatusModule).FullName}}",
             "dependencies":[],"permissions":[],"entitlement":null,"migrations":["example-status:0001"]}
            """;
        return ModulePackageBuilder.Build(manifest, new Dictionary<string, byte[]>
        {
            [assemblyName] = File.ReadAllBytes(assemblyPath),
            ["migrations/example-status-0001.json"] = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "example-status-0001.json"))
        }, "publisher", signer);
    }

    private static ModuleTrustKeyRing CreateTrustRing(RSA signer) => ModuleTrustKeyRing.Parse($$"""
        {"schemaVersion":1,"keys":[{"keyId":"publisher","publicKeyPem":{{JsonSerializer.Serialize(signer.ExportSubjectPublicKeyInfoPem())}},"status":"Active"}]}
        """);

    private static string ReserveLoopbackEndpoint()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        return $"http://127.0.0.1:{port}";
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pos.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz de Pos.sln.");
    }
}
