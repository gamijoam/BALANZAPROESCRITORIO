using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Domain;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Sales;
using Pos.Domain.Identity;
using Pos.Domain.Inventory;
using Pos.Domain.Finance;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Pos.Server;
using Pos.Modules.Packaging;
using Pos.Infrastructure.Modules;
using Pos.Modules.Abstractions;

var builder = WebApplication.CreateBuilder(args);
var moduleMaintenanceGate = new ModuleMaintenanceGate();
var activeModuleRuntimes = new List<LoadedPosModule>();
var activeModuleIds = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
var migrateOnly = args.Contains("--migrate", StringComparer.Ordinal);
var requireTerminalPairing = builder.Configuration.GetValue<bool>("PosSecurity:RequireTerminalPairing");
var moduleHostApiVersion = builder.Configuration.GetValue("Modules:HostApiVersion", 1);
if (moduleHostApiVersion < 1)
    throw new InvalidOperationException("Modules:HostApiVersion must be a positive integer.");
var connectionString = builder.Configuration.GetConnectionString("Pos");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Missing ConnectionStrings:Pos. Complete the local POS database configuration before starting the server.");
var moduleTrustKeyRingPath = builder.Configuration["ModuleTrust:KeyRingPath"];
if (!string.IsNullOrWhiteSpace(moduleTrustKeyRingPath))
{
    var expandedTrustPath = Environment.ExpandEnvironmentVariables(moduleTrustKeyRingPath);
    if (!Path.IsPathFullyQualified(expandedTrustPath))
        throw new InvalidOperationException("ModuleTrust:KeyRingPath must be an absolute path outside the POS update folder.");
    var trustKeyRing = ModuleTrustKeyRing.LoadFromFile(expandedTrustPath);
    builder.Services.AddSingleton(new ModuleTrustKeyRingStore(trustKeyRing));
}
builder.Services.AddDbContext<PosDbContext>(options => options.UseNpgsql(connectionString,
    npgsql => npgsql.MigrationsAssembly(typeof(PosDbContext).Assembly.FullName)));
builder.Services.AddScoped<AuthenticationService>();
builder.Services.AddScoped<SalesService>();
builder.Services.AddScoped<SaleReturnService>();
builder.Services.AddSingleton<SupervisorAuthorizationStore>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    var remoteAddress = context.Connection.RemoteIpAddress;
    if (TerminalPairingPolicy.RequiresHttps(remoteAddress) && !context.Request.IsHttps)
    {
        await Results.Problem("Las conexiones de terminales por red requieren HTTPS.", statusCode: StatusCodes.Status400BadRequest)
            .ExecuteAsync(context);
        return;
    }
    await next();
});
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (context.Request.Method != HttpMethods.Get && path.StartsWith("/api/v1/", StringComparison.Ordinal) &&
        !string.Equals(path, "/api/v1/modules/install", StringComparison.Ordinal) &&
        !(HttpMethods.IsPost(context.Request.Method) && System.Text.RegularExpressions.Regex.IsMatch(path,
            @"^/api/v1/modules/[a-z][a-z0-9]*(?:-[a-z0-9]+)*/deactivate$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
    {
        await using var operation = await moduleMaintenanceGate.EnterOperationAsync(context.RequestAborted);
        await next();
        return;
    }
    await next();
});
app.Use(async (context, next) =>
{
    using var bodyHash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
    if (context.Request.ContentLength is > 0)
    {
        context.Request.EnableBuffering();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            bodyHash.AppendData(buffer, 0, read);
        context.Request.Body.Position = 0;
    }
    var operation = context.Request.Method + " " + context.Request.Path + context.Request.QueryString;
    var payloadHash = Convert.ToHexString(bodyHash.GetHashAndReset()).ToLowerInvariant();
    var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(operation + "\n" + payloadHash))).ToLowerInvariant();
    context.Items["permission.request_fingerprint"] = fingerprint;
    await next();
});
if (migrateOnly)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
    await db.Database.MigrateAsync();
    Console.WriteLine("Migraciones de PostgreSQL aplicadas.");
    return;
}

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (PosDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready", database = "postgresql" })
        : Results.Problem("Database is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapPost("/api/v1/modules/plan", async (HttpContext context, PosDbContext db, AuthenticationService auth,
    SupervisorAuthorizationStore approvals, ILogger<Program> logger) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, context.RequestAborted);
    if (access is null) return Results.Unauthorized();
    if (!await AuthorizePermissionAsync(context, auth, approvals, access, Permission.ManageModules, context.RequestAborted))
        return PermissionFailure(context);

    var trustStore = app.Services.GetService<ModuleTrustKeyRingStore>();
    if (trustStore is null)
        return Results.Problem("La confianza de publicadores de módulos no está configurada.", statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0].Trim(), "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        return Results.Problem("Envía los paquetes como formulario multipart/form-data.", statusCode: StatusCodes.Status415UnsupportedMediaType);

    IFormCollection form;
    try
    {
        form = await context.Request.ReadFormAsync(context.RequestAborted);
    }
    catch (InvalidDataException)
    {
        return Results.BadRequest(new { error = "invalid_module_plan_form" });
    }

    if (!form.TryGetValue("requestedModuleId", out var requestedIds) || requestedIds.Count != 1 ||
        string.IsNullOrWhiteSpace(requestedIds[0]))
        return Results.BadRequest(new { error = "requested_module_id_required" });
    if (form.Keys.Any(key => key != "requestedModuleId"))
        return Results.BadRequest(new { error = "module_plan_form_has_unknown_fields" });

    var packageFiles = form.Files.Where(file => file.Name == "packages").ToArray();
    if (packageFiles.Length is 0 or > 32 || packageFiles.Length != form.Files.Count ||
        packageFiles.Any(file => file.Length <= 0) || packageFiles.Sum(file => file.Length) > 64L * 1024 * 1024)
        return Results.BadRequest(new { error = "module_package_bundle_invalid", maxPackages = 32, maxUploadBytes = 64 * 1024 * 1024 });

    var streams = new List<Stream>(packageFiles.Length);
    try
    {
        streams.AddRange(packageFiles.Select(file => file.OpenReadStream()));
        var plan = ModuleInstallationPlanner.CreatePlan(
            streams,
            requestedIds[0]!,
            moduleHostApiVersion,
            trustStore.Current.VerifierPublicKeys,
            new HashSet<string>(StringComparer.Ordinal));

        var plannedModules = new List<object>();
        foreach (var module in plan.InstallationOrder)
        {
            var terminalCompatibility = await EvaluateTerminalCompatibilityAsync(db, module.ClientApi, context.RequestAborted);
            plannedModules.Add(new
            {
                module.Id,
                version = module.Version.ToString(3),
                module.SignerKeyId,
                module.Entitlement,
                clientApi = new { min = module.ClientApi.Min.ToString(3), maxExclusive = module.ClientApi.MaxExclusive.ToString(3) },
                hasRuntimeEntry = module.HasRuntimeEntry,
                module.Permissions,
                module.Migrations,
                terminalCompatibility
            });
        }

        return Results.Ok(new
        {
            plan.RequestedModuleId,
            planValid = plan.IsValid,
            installationOrder = plannedModules
        });
    }
    catch (ModuleInstallationPlanningException exception)
    {
        var status = exception.Code switch
        {
            "package_invalid" => StatusCodes.Status422UnprocessableEntity,
            "dependency_invalid" or "entitlement_unavailable" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(new { error = exception.Code, detail = exception.Message }, statusCode: status);
    }
    catch (Exception exception) when (exception is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..12];
        try
        {
            var logPath = ModulePlanFailureLog.Write(diagnosticId, requestedIds[0]!, exception, connectionString);
            logger.LogError(exception, "Falló la validación del plan de módulo. Diagnóstico {DiagnosticId}; módulo {ModuleId}. Registro {LogPath}.",
                diagnosticId, requestedIds[0], logPath);
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogError(logException, "No se pudo guardar el diagnóstico {DiagnosticId} de validación de módulos.", diagnosticId);
            logger.LogError(exception, "Falló la validación del plan de módulo. Diagnóstico {DiagnosticId}; módulo solicitado {ModuleId}.",
                diagnosticId, requestedIds[0]);
        }
        return Results.Problem(
            title: "Error al validar el módulo",
            detail: "No se pudo validar el paquete.",
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "module_plan_failed",
                ["diagnosticId"] = diagnosticId
            });
    }
    finally
    {
        foreach (var stream in streams) stream.Dispose();
    }
})
    .WithMetadata(new RequestSizeLimitAttribute(64L * 1024 * 1024))
    .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = 64L * 1024 * 1024, ValueLengthLimit = 4096, ValueCountLimit = 8 });
app.MapGet("/api/v1/modules", async (HttpContext context, AuthenticationService auth, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await AuthorizePermissionAsync(context, auth, app.Services.GetRequiredService<SupervisorAuthorizationStore>(), access, Permission.ManageModules, cancellationToken))
        return PermissionFailure(context);
    try { return Results.Ok(await new ModuleInstallationRegistry().GetAllAsync(connectionString, cancellationToken)); }
    catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
    { return Results.Problem("No se pudo consultar el registro de módulos.", statusCode: StatusCodes.Status503ServiceUnavailable); }
});
app.MapGet("/api/v1/modules/desktop", async (HttpContext context, AuthenticationService auth, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();

    var trustStore = app.Services.GetService<ModuleTrustKeyRingStore>();
    if (trustStore is null) return Results.Problem("La confianza de publicadores de módulos no está configurada.", statusCode: StatusCodes.Status503ServiceUnavailable);
    var role = await auth.GetRoleAsync(access.OperatorId, cancellationToken);
    var modules = activeModuleRuntimes
        .Where(runtime => runtime.Package.Module.DesktopEntryAssembly is not null)
        .Select(async runtime =>
        {
            var granted = new List<string>();
            foreach (var permission in runtime.Package.Module.Permissions)
            {
                var mapped = ResolveModulePermission(permission);
                if (role == Pos.Domain.Identity.OperatorRole.Administrator ||
                    mapped is { } known && await auth.GetPermissionAccessModeAsync(access.OperatorId, known, cancellationToken) != PermissionAccessMode.Deny)
                    granted.Add(permission);
            }
            return (runtime, granted);
        })
        .Select(task => task)
        .ToArray();
    var moduleResults = await Task.WhenAll(modules);
    var descriptors = moduleResults.Where(item => item.granted.Count != 0).Select(item => new
        {
            moduleId = item.runtime.Package.Module.Id,
            version = item.runtime.Package.Module.Version.ToString(3),
            signerKeyId = item.runtime.Package.SignerKeyId,
            signerPublicKeyPem = trustStore.Current.VerifierPublicKeys[item.runtime.Package.SignerKeyId],
            grantedPermissions = item.granted
        }).ToArray();
    return Results.Ok(descriptors);
});
app.MapGet("/api/v1/modules/desktop/{moduleId}/package", async (string moduleId, HttpContext context,
    AuthenticationService auth, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    var runtime = activeModuleRuntimes.FirstOrDefault(item =>
        string.Equals(item.Package.Module.Id, moduleId, StringComparison.Ordinal) &&
        item.Package.Module.DesktopEntryAssembly is not null);
    if (runtime is null || !activeModuleIds.ContainsKey(moduleId)) return Results.NotFound();
    var role = await auth.GetRoleAsync(access.OperatorId, cancellationToken);
    var canUse = role == Pos.Domain.Identity.OperatorRole.Administrator;
    if (!canUse)
    {
        foreach (var permission in runtime.Package.Module.Permissions)
        {
            if (ResolveModulePermission(permission) is { } mapped &&
                await auth.GetPermissionAccessModeAsync(access.OperatorId, mapped, cancellationToken) != PermissionAccessMode.Deny)
            { canUse = true; break; }
        }
    }
    if (!canUse) return Results.Forbid();

    var storageRoot = ResolveConfiguredModulePath(builder.Configuration["Modules:StoragePath"], "Modules:StoragePath", "Modules");
    var moduleDirectory = Path.GetFullPath(Path.Combine(storageRoot, moduleId, runtime.Package.Module.Version.ToString(3)));
    var safeRoot = Path.GetFullPath(storageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!moduleDirectory.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
    var packagePath = Path.Combine(moduleDirectory, "package.zip");
    if (!File.Exists(packagePath) || (File.GetAttributes(packagePath) & FileAttributes.ReparsePoint) != 0) return Results.NotFound();
    var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    return Results.File(stream, "application/zip", $"{moduleId}-{runtime.Package.Module.Version.ToString(3)}.zip", enableRangeProcessing: true);
});
app.MapPost("/api/v1/modules/install", async (HttpContext context, PosDbContext db, AuthenticationService auth,
    SupervisorAuthorizationStore approvals, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await AuthorizePermissionAsync(context, auth, approvals, access, Permission.ManageModules, cancellationToken))
        return PermissionFailure(context);

    var trustStore = app.Services.GetService<ModuleTrustKeyRingStore>();
    if (trustStore is null)
        return Results.Problem("La confianza de publicadores de módulos no está configurada.", statusCode: StatusCodes.Status503ServiceUnavailable);
    var storagePath = ResolveConfiguredModulePath(builder.Configuration["Modules:StoragePath"], "Modules:StoragePath", "Modules");
    var backupPath = ResolveConfiguredModulePath(builder.Configuration["Modules:BackupPath"], "Modules:BackupPath", "Backups");
    var postgresPath = builder.Configuration["PostgreSql:BinaryDirectory"] ?? Environment.GetEnvironmentVariable("POS_POSTGRES_BIN");
    if (string.IsNullOrWhiteSpace(postgresPath))
        return Results.Problem("Configura PostgreSql:BinaryDirectory para habilitar la instalación segura de módulos.", statusCode: StatusCodes.Status503ServiceUnavailable);
    postgresPath = Environment.ExpandEnvironmentVariables(postgresPath);
    if (!Path.IsPathFullyQualified(postgresPath) || !Path.IsPathFullyQualified(storagePath) || !Path.IsPathFullyQualified(backupPath))
        return Results.Problem("Las rutas de PostgreSQL, módulos y respaldos deben ser absolutas.", statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0].Trim(), "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        return Results.Problem("Envía los paquetes como formulario multipart/form-data.", statusCode: StatusCodes.Status415UnsupportedMediaType);

    IFormCollection form;
    try { form = await context.Request.ReadFormAsync(cancellationToken); }
    catch (InvalidDataException) { return Results.BadRequest(new { error = "invalid_module_install_form" }); }
    if (!form.TryGetValue("requestedModuleId", out var requestedIds) || requestedIds.Count != 1 || string.IsNullOrWhiteSpace(requestedIds[0]) ||
        form.Keys.Any(key => key != "requestedModuleId"))
        return Results.BadRequest(new { error = "requested_module_id_required" });
    var files = form.Files.Where(file => file.Name == "packages").ToArray();
    if (files.Length is 0 or > 32 || files.Length != form.Files.Count || files.Any(file => file.Length <= 0) ||
        files.Sum(file => file.Length) > 64L * 1024 * 1024)
        return Results.BadRequest(new { error = "module_package_bundle_invalid", maxPackages = 32, maxUploadBytes = 64 * 1024 * 1024 });

    var streams = files.Select(file => file.OpenReadStream()).ToArray();
    try
    {
        var plan = ModuleInstallationPlanner.CreatePlan(streams, requestedIds[0]!, moduleHostApiVersion,
            trustStore.Current.VerifierPublicKeys, new HashSet<string>(StringComparer.Ordinal));
        var compatibility = new List<object>();
        foreach (var module in plan.InstallationOrder)
        {
            var compatibilityResult = await EvaluateTerminalCompatibilityAsync(db, module.ClientApi, cancellationToken);
            compatibility.Add(new { moduleId = module.Id, compatibilityResult.IsCompatible, compatibilityResult.RegisteredTerminalCount,
                compatibilityResult.UnreportedTerminalCount, incompatibleVersions = compatibilityResult.IncompatibleVersions });
            if (!compatibilityResult.IsCompatible)
                return Results.Conflict(new { error = "terminal_client_version_incompatible", moduleId = module.Id,
                    compatibilityResult.RegisteredTerminalCount, compatibilityResult.UnreportedTerminalCount, incompatibleVersions = compatibilityResult.IncompatibleVersions,
                    detail = "Actualiza e inicia sesión en todas las terminales registradas antes de instalar este módulo." });
        }
        var packagesById = new Dictionary<string, ModuleInstallationPackage>(StringComparer.Ordinal);
        for (var index = 0; index < streams.Length; index++)
        {
            streams[index].Position = 0;
            var verified = ModulePackageVerifier.Verify(streams[index], trustStore.Current);
            packagesById.Add(verified.Module.Id, new ModuleInstallationPackage(verified.Module.Id, verified.Module.Version, streams[index]));
        }
        var orderedPackages = plan.InstallationOrder.Select(entry => packagesById.TryGetValue(entry.Id, out var package) && package.Version == entry.Version
            ? package : throw new InvalidOperationException("No se pudo asociar el paquete firmado con su plan.")).ToArray();
        var request = new ModuleInstallationRequest(plan, orderedPackages, connectionString, postgresPath,
            backupPath, storagePath, moduleHostApiVersion, trustStore.Current, new HashSet<string>(StringComparer.Ordinal));
        await using var installationLease = await moduleMaintenanceGate.EnterInstallationAsync(cancellationToken);
        var coordinator = new ModuleInstallationCoordinator(
            new ModuleInstallationBackupAdapter(new ModuleDatabaseBackupService(new ProcessPostgresToolRunner())),
            new ModuleInstallationPackageStoreAdapter(), new ModuleInstallationMigrationAdapter(new ModuleMigrationExecutor()));
        var result = await coordinator.InstallAsync(request, cancellationToken);
        await new ModuleInstallationRegistry().RecordRestartPendingAsync(connectionString, result, cancellationToken);
        var now = DateTime.UtcNow;
        db.AuditEvents.Add(new AuditRecord { Id = Guid.NewGuid(), ActorId = access.OperatorId, TerminalId = access.TerminalId,
            Action = "module.installation_restart_pending", Payload = JsonSerializer.Serialize(new { result.RequestedModuleId, result.Status, backupSha256 = result.Backup.Sha256, modules = result.Packages.Select(p => new { p.ModuleId, version = p.Version.ToString(3) }) }), OccurredAtUtc = now });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { result.RequestedModuleId, result.Status, result.RestartRequired, backupPath = result.Backup.BackupPath,
            backupSha256 = result.Backup.Sha256, packages = result.Packages.Select(package => new { package.ModuleId, version = package.Version.ToString(3) }), migrations = result.Migrations });
    }
    catch (ModuleInstallationPlanningException exception)
    { return Results.Json(new { error = exception.Code, detail = exception.Message }, statusCode: StatusCodes.Status422UnprocessableEntity); }
    catch (Exception exception) when (exception is ModuleDatabaseBackupException or ModulePackageStoreException or ModuleMigrationExecutionException or IOException or UnauthorizedAccessException or InvalidOperationException or NpgsqlException)
    { return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict, title: "No se pudo completar la instalación del módulo"); }
    finally { foreach (var stream in streams) await stream.DisposeAsync(); }
})
    .WithMetadata(new RequestSizeLimitAttribute(64L * 1024 * 1024))
    .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = 64L * 1024 * 1024, ValueLengthLimit = 4096, ValueCountLimit = 8 });
app.MapPost("/api/v1/modules/{moduleId}/activate", async (string moduleId, HttpContext context,
    PosDbContext db, AuthenticationService auth, SupervisorAuthorizationStore approvals, ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(moduleId, "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        return Results.NotFound();
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await AuthorizePermissionAsync(context, auth, approvals, access, Permission.ManageModules, cancellationToken))
        return PermissionFailure(context);

    try
    {
        await using var maintenance = await moduleMaintenanceGate.EnterInstallationAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var queued = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "pos_modules"."installed_modules"
            SET "status" = 'RestartPending', "activation_detail" = NULL
            WHERE "module_id" = {{moduleId}} AND "status" = 'Disabled';
            """, cancellationToken);
        if (queued != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            var installed = await new ModuleInstallationRegistry().GetAllAsync(connectionString, cancellationToken);
            return installed.Any(module => string.Equals(module.ModuleId, moduleId, StringComparison.Ordinal))
                ? Results.Conflict(new { error = "module_not_disabled", detail = "El módulo no está desactivado y no se puede reactivar desde este estado." })
                : Results.NotFound();
        }
        var now = DateTime.UtcNow;
        db.AuditEvents.Add(new AuditRecord
        {
            Id = Guid.NewGuid(), ActorId = access.OperatorId, TerminalId = access.TerminalId,
            Action = "module.reactivation_restart_pending", Payload = JsonSerializer.Serialize(new { moduleId, status = "RestartPending", packageAndDataPreserved = true }), OccurredAtUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(new { moduleId, status = "RestartPending", restartRequired = true, packageAndDataPreserved = true, migrationsReapplied = false });
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        var diagnosticId = Guid.NewGuid().ToString("N")[..12];
        logger.LogError(exception, "Falló la reactivación del módulo {ModuleId}. Diagnóstico {DiagnosticId}.", moduleId, diagnosticId);
        return Results.Problem("El servidor no pudo reactivar el módulo.", statusCode: StatusCodes.Status500InternalServerError,
            title: "Error al reactivar el módulo", extensions: new Dictionary<string, object?>
            {
                ["code"] = "module_reactivation_failed",
                ["diagnosticId"] = diagnosticId
            });
    }
});
app.MapPost("/api/v1/modules/{moduleId}/deactivate", async (string moduleId, HttpContext context,
    PosDbContext db, AuthenticationService auth, SupervisorAuthorizationStore approvals, CancellationToken cancellationToken) =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(moduleId, "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        return Results.NotFound();
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await AuthorizePermissionAsync(context, auth, approvals, access, Permission.ManageModules, cancellationToken))
        return PermissionFailure(context);

    await using var maintenance = await moduleMaintenanceGate.EnterInstallationAsync(cancellationToken);
    var registry = new ModuleInstallationRegistry();
    try { await registry.SetActivationStatusAsync(connectionString, moduleId, "Disabled", "Desactivado por un administrador.", cancellationToken); }
    catch (InvalidOperationException) { return Results.NotFound(); }
    activeModuleIds.TryRemove(moduleId, out _);
    var now = DateTime.UtcNow;
    db.AuditEvents.Add(new AuditRecord
    {
        Id = Guid.NewGuid(), ActorId = access.OperatorId, TerminalId = access.TerminalId,
        Action = "module.deactivated", Payload = JsonSerializer.Serialize(new { moduleId, status = "Disabled", dataPreserved = true }), OccurredAtUtc = now
    });
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new { moduleId, status = "Disabled", dataPreserved = true, restartRequired = false });
});
app.MapPost("/api/v1/terminals/pair", async (HttpContext context, PosDbContext db, AuthenticationService auth,
    TerminalPairRequest request, CancellationToken cancellationToken) =>
{
    if (!context.Request.IsHttps) return Results.BadRequest(new { error = "https_required_for_terminal_pairing" });
    if (request.TerminalId == Guid.Empty) return Results.BadRequest(new { error = "terminal_id_required" });
    var administratorId = await auth.VerifyAdministratorPinAsync(request.AdministratorUsername, request.AdministratorPin, cancellationToken);
    if (administratorId is null) return Results.Json(new { error = "administrator_credentials_invalid" }, statusCode: StatusCodes.Status403Forbidden);

    var terminal = await db.PairedTerminals.SingleOrDefaultAsync(x => x.TerminalId == request.TerminalId, cancellationToken);
    var credential = TerminalPairingCredential.Create();
    if (terminal is null)
    {
        terminal = new PairedTerminalRecord { TerminalId = request.TerminalId };
        db.PairedTerminals.Add(terminal);
    }
    terminal.CredentialHash = TerminalPairingCredential.Hash(credential);
    terminal.PairedByOperatorId = administratorId.Value;
    var pairedAt = DateTime.UtcNow;
    terminal.PairedAtUtc = pairedAt;
    terminal.Active = true;
    var existingSessions = await db.AccessSessions.Where(x => x.TerminalId == request.TerminalId && x.RevokedAtUtc == null)
        .ToListAsync(cancellationToken);
    foreach (var session in existingSessions) session.RevokedAtUtc = pairedAt;
    db.AuditEvents.Add(new AuditRecord { Id = Guid.NewGuid(), ActorId = administratorId.Value, TerminalId = request.TerminalId,
        Action = "terminal.paired", Payload = JsonSerializer.Serialize(new { terminalId = request.TerminalId }), OccurredAtUtc = pairedAt });
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new { terminalCredential = credential });
});
app.MapPost("/api/v1/setup/first-operator", async (HttpContext context, PosDbContext db, AuthenticationService auth,
    CreateOperatorRequest request, CancellationToken cancellationToken) =>
{
    var remoteIp = context.Connection.RemoteIpAddress;
    if (!app.Environment.IsDevelopment() || remoteIp is null || !System.Net.IPAddress.IsLoopback(remoteIp))
        return Results.NotFound();
    if (await db.OperatorUsers.AnyAsync(cancellationToken)) return Results.Conflict(new { error = "Initial setup is already complete." });
    try
    {
        var operatorId = await auth.CreateOperatorAsync(request.Username, request.Pin, cancellationToken);
        return Results.Created("/api/v1/operators/" + operatorId, new { operatorId });
    }
    catch (ArgumentException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["credentials"] = [exception.Message] });
    }
});
app.MapPost("/api/v1/auth/login", async (HttpContext context, PosDbContext db, AuthenticationService auth, LoginRequest request, CancellationToken cancellationToken) =>
{
    if (request.TerminalId == Guid.Empty) return Results.BadRequest(new { error = "TerminalId is required." });
    if (!ClientApiVersionParser.TryParseOptional(request.ClientVersion, out var clientVersion))
        return Results.BadRequest(new { error = "client_version_invalid", detail = "Actualiza el POS para que informe una versión válida de terminal." });
    var remoteAddress = context.Connection.RemoteIpAddress;
    if (TerminalPairingPolicy.RequiresCredential(requireTerminalPairing, remoteAddress))
    {
        if (!context.Request.IsHttps) return Results.BadRequest(new { error = "https_required_for_terminal_login" });
        var terminal = await db.PairedTerminals.AsNoTracking().SingleOrDefaultAsync(x => x.TerminalId == request.TerminalId && x.Active, cancellationToken);
        var credential = context.Request.Headers["X-POS-Terminal-Credential"].ToString();
        if (terminal is null || !TerminalPairingCredential.Matches(credential, terminal.CredentialHash)) return Results.Unauthorized();
    }
    AuthenticationResult? result;
    try { result = await auth.AuthenticateAsync(request.Username, request.Pin, request.TerminalId, cancellationToken); }
    catch (ArgumentException) { return Results.Unauthorized(); }
    if (result is null) return Results.Unauthorized();
    var reportedVersion = await db.TerminalClientVersions.SingleOrDefaultAsync(x => x.TerminalId == request.TerminalId, cancellationToken);
    if (reportedVersion is null)
    {
        reportedVersion = new TerminalClientVersionRecord { TerminalId = request.TerminalId };
        db.TerminalClientVersions.Add(reportedVersion);
    }
    reportedVersion.ClientVersion = clientVersion?.ToString(3);
    reportedVersion.LastReportedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new { accessToken = result.Token, tokenType = "Bearer", expiresAtUtc = result.ExpiresAtUtc, operatorId = result.OperatorId, terminalId = result.TerminalId });
});
app.MapGet("/api/v1/auth/session", async (HttpContext context, AuthenticationService auth, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var session = await auth.FindValidSessionAsync(token, cancellationToken);
    var role = session is null ? null : await auth.GetRoleAsync(session.OperatorId, cancellationToken);
    return session is null ? Results.Unauthorized() : Results.Ok(new { operatorId = session.OperatorId, terminalId = session.TerminalId, expiresAtUtc = session.ExpiresAtUtc, role = role?.ToString() });
});
app.MapPost("/api/v1/auth/logout", async (HttpContext context, AuthenticationService auth, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    await auth.RevokeSessionAsync(token, cancellationToken);
    return Results.NoContent();
});
app.MapPost("/api/v1/auth/authorize", async (HttpContext context, PosDbContext db, AuthenticationService auth,
    SupervisorAuthorizationStore approvals, SupervisorAuthorizationRequest request, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!Enum.TryParse<Permission>(request.Permission, false, out var permission) || !Enum.IsDefined(permission) ||
        request.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(request.Operation) || request.Operation.Length > 400 ||
        request.RequestFingerprint is null || request.RequestFingerprint.Length != 64 || request.RequestFingerprint.Any(c => !Uri.IsHexDigit(c)))
        return Results.BadRequest(new { error = "invalid_approval_request" });
    if (await auth.GetPermissionAccessModeAsync(access.OperatorId, permission, cancellationToken) != PermissionAccessMode.RequireAdminPin)
        return Results.Conflict(new { error = "admin_approval_not_required" });
    var approverId = await auth.VerifyAdministratorPinAsync(request.AdministratorUsername, request.AdministratorPin, cancellationToken);
    if (approverId is null) return Results.Json(new { error = "administrator_credentials_invalid" }, statusCode: StatusCodes.Status403Forbidden);
    var approvalToken = approvals.Issue(access.OperatorId, approverId.Value, access.TerminalId, permission, request.Operation, request.RequestFingerprint, request.OperationId);
    db.AuditEvents.Add(new AuditRecord { Id = Guid.NewGuid(), ActorId = access.OperatorId, TerminalId = access.TerminalId,
        Action = "permission.admin_pin_approved", Payload = JsonSerializer.Serialize(new { approverId, permission = permission.ToString(), operationId = request.OperationId, operation = request.Operation }), OccurredAtUtc = DateTime.UtcNow });
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new { approvalToken });
});
app.MapPost("/api/v1/operators", async (HttpContext context, PosDbContext db, AuthenticationService auth, SupervisorAuthorizationStore approvals, OperatorCreateRequest request, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await AuthorizePermissionAsync(context, auth, approvals, access, Permission.ManageUsers, cancellationToken)) return PermissionFailure(context);
    if (!Enum.TryParse<OperatorRole>(request.Role, ignoreCase: false, out var role) || !Enum.IsDefined(role))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["Role must be Cashier, Supervisor or Administrator."] });
    Guid operatorId;
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    try { operatorId = await auth.CreateOperatorAsync(request.Username, request.Pin, cancellationToken, role); }
    catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["operator"] = [exception.Message] }); }
    catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
    { return Results.Conflict(new { error = "username_already_exists" }); }
    db.AuditEvents.Add(new AuditRecord { Id = Guid.NewGuid(), ActorId = access.OperatorId, TerminalId = access.TerminalId,
        Action = "operator.created", Payload = JsonSerializer.Serialize(new { operatorId, role }), OccurredAtUtc = DateTime.UtcNow });
    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return Results.Created($"/api/v1/operators/{operatorId}", new { operatorId, role = role.ToString() });
});
app.MapGet("/api/v1/operators", async (HttpContext context, PosDbContext db, AuthenticationService auth, SupervisorAuthorizationStore approvals, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await AuthorizePermissionAsync(context, auth, approvals, access, Permission.ManageUsers, cancellationToken)) return PermissionFailure(context);
    return Results.Ok(await db.OperatorUsers.AsNoTracking().OrderBy(x => x.Username)
        .Select(x => new { x.Id, x.Username, x.Role, x.Active, x.CreatedAtUtc }).ToListAsync(cancellationToken));
});
app.MapPost("/api/v1/cash-sessions", async (HttpContext context, PosDbContext db, AuthenticationService auth, SupervisorAuthorizationStore approvals,
    OpenCashSessionRequest request, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await AuthorizePermissionAsync(context, auth, approvals, access, Permission.CloseCashSession, cancellationToken)) return PermissionFailure(context);
    CashOpeningAmounts opening;
    try
    {
        opening = CashOpeningPolicy.Resolve(request.OpeningBalance, request.Currency,
            request.OpeningBalanceUsd, request.OpeningBalanceVes);
    }
    catch (DomainRuleException)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["openingBalance"] = ["Los saldos iniciales deben ser iguales o mayores que cero y usar USD o VES."] });
    }
    if (await db.CashSessions.AnyAsync(x => x.TerminalId == access.TerminalId && x.Status == "Open", cancellationToken))
        return Results.Conflict(new { error = "cash_session_already_open" });
    var cashSession = new CashSessionRecord { Id = Guid.NewGuid(), OperatorId = access.OperatorId, ActiveOperatorId = access.OperatorId, TerminalId = access.TerminalId, OpeningBalance = opening.OpeningVes, OpeningBalanceUsd = opening.OpeningUsd, OpeningBalanceVes = opening.OpeningVes, Currency = opening.Currency, OpenedAtUtc = DateTime.UtcNow };
    db.CashSessions.Add(cashSession);
    try { await db.SaveChangesAsync(cancellationToken); }
    catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
    { return Results.Conflict(new { error = "cash_session_already_open" }); }
    var openedByOperatorName = await db.OperatorUsers.AsNoTracking().Where(x => x.Id == access.OperatorId).Select(x => x.Username).SingleAsync(cancellationToken);
    return Results.Created("/api/v1/cash-sessions/" + cashSession.Id, new { cashSession.Id, cashSession.Currency, cashSession.OpeningBalance,
        cashSession.OpeningBalanceUsd, cashSession.OpeningBalanceVes, cashSession.OpenedAtUtc, openedByOperatorName,
        activeOperatorName = openedByOperatorName, isActiveForCurrentOperator = true });
});
app.MapGet("/api/v1/cash-sessions/current", async (HttpContext context, PosDbContext db, AuthenticationService auth, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    var cashSession = await db.CashSessions.AsNoTracking().SingleOrDefaultAsync(x => x.TerminalId == access.TerminalId && x.Status == "Open", cancellationToken);
    if (cashSession is null) return Results.NotFound();
    var openedByOperatorName = await db.OperatorUsers.AsNoTracking().Where(x => x.Id == cashSession.OperatorId).Select(x => x.Username).SingleOrDefaultAsync(cancellationToken) ?? "—";
    var activeOperatorName = cashSession.ActiveOperatorId is { } activeOperatorId
        ? await db.OperatorUsers.AsNoTracking().Where(x => x.Id == activeOperatorId).Select(x => x.Username).SingleOrDefaultAsync(cancellationToken)
        : null;
    var isActiveForCurrentOperator = cashSession.ActiveOperatorId == access.OperatorId;
    if (!isActiveForCurrentOperator)
        return Results.Ok(new { cashSession.Id, cashSession.Currency, openingBalance = 0m, openingBalanceUsd = 0m, openingBalanceVes = 0m,
            cashSession.OpenedAtUtc, expectedUsd = (decimal?)null, expectedVes = (decimal?)null,
            openedByOperatorName, activeOperatorName, isActiveForCurrentOperator });
    var receipts = await (from payment in db.Payments.AsNoTracking()
                          join sale in db.Sales.AsNoTracking() on payment.SaleId equals sale.Id
                          where sale.CashSessionId == cashSession.Id &&
                                (payment.Method.ToLower() == "cash" || payment.Method.ToLower() == "efectivo")
                          select new { payment.Amount, payment.Currency }).ToListAsync(cancellationToken);
    var movements = await db.CashMovements.AsNoTracking().Where(x => x.CashSessionId == cashSession.Id).ToListAsync(cancellationToken);
    var openingUsd = cashSession.OpeningBalanceUsd == 0m && cashSession.Currency == "USD" ? cashSession.OpeningBalance : cashSession.OpeningBalanceUsd;
    var openingVes = cashSession.OpeningBalanceVes == 0m && cashSession.Currency == "VES" ? cashSession.OpeningBalance : cashSession.OpeningBalanceVes;
    var cashReceipts = receipts.Select(x => new CashReceipt(x.Currency, x.Amount)).ToArray();
    var cashMovements = movements.Select(x => new CashMovement(x.Currency, x.Direction, x.Amount)).ToArray();
    var expectedUsd = CashSessionLedger.ExpectedBalance(openingUsd, cashReceipts, cashMovements, "USD");
    var expectedVes = CashSessionLedger.ExpectedBalance(openingVes, cashReceipts, cashMovements, "VES");
    return Results.Ok(new { cashSession.Id, cashSession.Currency, cashSession.OpeningBalance, cashSession.OpeningBalanceUsd,
        cashSession.OpeningBalanceVes, cashSession.OpenedAtUtc, expectedUsd, expectedVes,
        openedByOperatorName, activeOperatorName, isActiveForCurrentOperator });
});
app.MapPost("/api/v1/cash-sessions/current/claim", async (HttpContext context, PosDbContext db, AuthenticationService auth, SupervisorAuthorizationStore approvals, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await AuthorizePermissionAsync(context, auth, approvals, access, Permission.ConfirmSale, cancellationToken)) return PermissionFailure(context);
    await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
    var cashSession = await db.CashSessions.FromSqlInterpolated($"SELECT * FROM cash_sessions WHERE \"TerminalId\" = {access.TerminalId} AND \"Status\" = 'Open' FOR UPDATE")
        .SingleOrDefaultAsync(cancellationToken);
    if (cashSession is null) return Results.NotFound(new { error = "open_cash_session_not_found" });
    if (cashSession.ActiveOperatorId is { } activeOperatorId && activeOperatorId != access.OperatorId)
    {
        var activeName = await db.OperatorUsers.AsNoTracking().Where(x => x.Id == activeOperatorId).Select(x => x.Username).SingleOrDefaultAsync(cancellationToken);
        return Results.Conflict(new { error = "cash_session_in_use", activeOperatorName = activeName });
    }
    if (cashSession.ActiveOperatorId is null)
    {
        cashSession.ActiveOperatorId = access.OperatorId;
        db.AuditEvents.Add(new AuditRecord { Id = Guid.NewGuid(), ActorId = access.OperatorId, TerminalId = access.TerminalId,
            Action = "cash-session.operator_claimed", Payload = JsonSerializer.Serialize(new { cashSessionId = cashSession.Id, openedByOperatorId = cashSession.OperatorId }), OccurredAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
    }
    await transaction.CommitAsync(cancellationToken);
    return Results.NoContent();
});
app.MapPost("/api/v1/cash-sessions/current/release", async (HttpContext context, PosDbContext db, AuthenticationService auth, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
    var cashSession = await db.CashSessions.FromSqlInterpolated($"SELECT * FROM cash_sessions WHERE \"TerminalId\" = {access.TerminalId} AND \"Status\" = 'Open' FOR UPDATE")
        .SingleOrDefaultAsync(cancellationToken);
    if (cashSession is null) return Results.NotFound(new { error = "open_cash_session_not_found" });
    if (cashSession.ActiveOperatorId != access.OperatorId)
        return Results.Conflict(new { error = "cash_session_not_assigned_to_operator" });
    cashSession.ActiveOperatorId = null;
    db.AuditEvents.Add(new AuditRecord { Id = Guid.NewGuid(), ActorId = access.OperatorId, TerminalId = access.TerminalId,
        Action = "cash-session.operator_released", Payload = JsonSerializer.Serialize(new { cashSessionId = cashSession.Id, openedByOperatorId = cashSession.OperatorId }), OccurredAtUtc = DateTime.UtcNow });
    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return Results.NoContent();
});
app.MapPost("/api/v1/cash-sessions/{cashSessionId:guid}/close", async (Guid cashSessionId, HttpContext context, PosDbContext db, AuthenticationService auth, SupervisorAuthorizationStore approvals, CloseCashSessionRequest request, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await AuthorizePermissionAsync(context, auth, approvals, access, Permission.CloseCashSession, cancellationToken)) return PermissionFailure(context);
    if ((request.CountedClosingUsd ?? request.CountedClosingBalance ?? 0m) < 0m || (request.CountedClosingVes ?? 0m) < 0m) return Results.ValidationProblem(new Dictionary<string, string[]> { ["countedClosingBalance"] = ["Los saldos contados no pueden ser negativos."] });
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    var cashSession = await db.CashSessions.FromSqlInterpolated($"SELECT * FROM cash_sessions WHERE \"Id\" = {cashSessionId} AND \"ActiveOperatorId\" = {access.OperatorId} AND \"TerminalId\" = {access.TerminalId} AND \"Status\" = 'Open' FOR UPDATE")
        .SingleOrDefaultAsync(cancellationToken);
    if (cashSession is null) return Results.NotFound();
    var receipts = await (from payment in db.Payments
                          join sale in db.Sales on payment.SaleId equals sale.Id
                          where sale.CashSessionId == cashSessionId &&
                                (payment.Method.ToLower() == "cash" || payment.Method.ToLower() == "efectivo")
                          select new { payment.Amount, payment.Currency }).ToListAsync(cancellationToken);
    var movements = await db.CashMovements.Where(x => x.CashSessionId == cashSessionId).ToListAsync(cancellationToken);
    var countedUsd = request.CountedClosingUsd ?? (cashSession.Currency == "USD" ? request.CountedClosingBalance ?? 0m : 0m);
    var countedVes = request.CountedClosingVes ?? (cashSession.Currency == "VES" ? request.CountedClosingBalance ?? 0m : 0m);
    var openingUsd = cashSession.OpeningBalanceUsd == 0m && cashSession.Currency == "USD" ? cashSession.OpeningBalance : cashSession.OpeningBalanceUsd;
    var openingVes = cashSession.OpeningBalanceVes == 0m && cashSession.Currency == "VES" ? cashSession.OpeningBalance : cashSession.OpeningBalanceVes;
    var ledger = CashSessionLedger.Calculate(openingUsd, openingVes,
        receipts.Select(x => new CashReceipt(x.Currency, x.Amount)),
        movements.Select(x => new CashMovement(x.Currency, x.Direction, x.Amount)), countedUsd, countedVes);
    var usd = ledger.Usd;
    var ves = ledger.Ves;
    var reconciliation = cashSession.Currency == "USD" ? usd : ves;
    cashSession.Status = "Closed";
    cashSession.ClosedAtUtc = DateTime.UtcNow;
    cashSession.ExpectedClosingBalance = reconciliation.ExpectedBalance;
    cashSession.CountedClosingBalance = reconciliation.CountedBalance;
    cashSession.Variance = reconciliation.Variance;
    cashSession.ExpectedClosingBalanceUsd = usd.ExpectedBalance; cashSession.CountedClosingBalanceUsd = usd.CountedBalance; cashSession.VarianceUsd = usd.Variance;
    cashSession.ExpectedClosingBalanceVes = ves.ExpectedBalance; cashSession.CountedClosingBalanceVes = ves.CountedBalance; cashSession.VarianceVes = ves.Variance;
    db.AuditEvents.Add(new AuditRecord { Id = Guid.NewGuid(), ActorId = access.OperatorId, TerminalId = access.TerminalId,
        Action = "cash-session.closed", Payload = JsonSerializer.Serialize(new { cashSessionId, usd, ves }), OccurredAtUtc = DateTime.UtcNow });
    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return Results.Ok(new { cashSessionId, reconciliation.ExpectedBalance, reconciliation.CountedBalance, reconciliation.Variance,
        expectedUsd = usd.ExpectedBalance, countedUsd = usd.CountedBalance, varianceUsd = usd.Variance,
        expectedVes = ves.ExpectedBalance, countedVes = ves.CountedBalance, varianceVes = ves.Variance });
});
app.MapPost("/api/v1/cash-sessions/{cashSessionId:guid}/movements", async (Guid cashSessionId, HttpContext context, PosDbContext db, AuthenticationService auth, SupervisorAuthorizationStore approvals, CashMovementRequest request, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await AuthorizePermissionAsync(context, auth, approvals, access, Permission.CloseCashSession, cancellationToken)) return PermissionFailure(context);
    var direction = (request.Direction ?? string.Empty).Trim();
    var currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant();
    var reason = (request.Reason ?? string.Empty).Trim();
    if (request.Amount <= 0m || direction is not ("In" or "Out") || (request.Currency is not null && currency is not ("USD" or "VES")) || reason.Length is < 3 or > 250)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["movement"] = ["A positive amount, direction In/Out and reason (3–250 characters) are required."] });
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    var cashSession = await db.CashSessions.FromSqlInterpolated($"SELECT * FROM cash_sessions WHERE \"Id\" = {cashSessionId} AND \"ActiveOperatorId\" = {access.OperatorId} AND \"TerminalId\" = {access.TerminalId} AND \"Status\" = 'Open' FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
    if (cashSession is null) return Results.NotFound();
    if (request.Currency is null) currency = cashSession.Currency;
    var cashReceipts = await (from payment in db.Payments join sale in db.Sales on payment.SaleId equals sale.Id
                              where sale.CashSessionId == cashSessionId && (payment.Method.ToLower() == "cash" || payment.Method.ToLower() == "efectivo")
                              select new { payment.Amount, payment.Currency }).ToListAsync(cancellationToken);
    var priorMovements = await db.CashMovements.Where(x => x.CashSessionId == cashSessionId).ToListAsync(cancellationToken);
    var opening = currency == "USD"
        ? cashSession.OpeningBalanceUsd == 0m && cashSession.Currency == "USD" ? cashSession.OpeningBalance : cashSession.OpeningBalanceUsd
        : cashSession.OpeningBalanceVes == 0m && cashSession.Currency == "VES" ? cashSession.OpeningBalance : cashSession.OpeningBalanceVes;
    var expectedCash = CashSessionLedger.ExpectedBalance(opening,
        cashReceipts.Select(x => new CashReceipt(x.Currency, x.Amount)),
        priorMovements.Select(x => new CashMovement(x.Currency, x.Direction, x.Amount)), currency);
    if (direction == "Out" && request.Amount > expectedCash) return Results.Conflict(new { error = "insufficient_cash" });
    var movement = new CashMovementRecord { Id = Guid.NewGuid(), CashSessionId = cashSessionId, Amount = request.Amount,
        Direction = direction, Currency = currency, Reason = reason, ActorId = access.OperatorId, OccurredAtUtc = DateTime.UtcNow };
    db.CashMovements.Add(movement);
    db.AuditEvents.Add(new AuditRecord { Id = Guid.NewGuid(), ActorId = access.OperatorId, TerminalId = access.TerminalId,
        Action = $"cash-session.{direction.ToLowerInvariant()}", Payload = JsonSerializer.Serialize(new { cashSessionId, movement.Amount, movement.Reason }), OccurredAtUtc = DateTime.UtcNow });
    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return Results.Created($"/api/v1/cash-sessions/{cashSessionId}/movements/{movement.Id}", new { movement.Id, movement.Amount, movement.Direction, movement.Reason, movement.OccurredAtUtc });
});
app.MapPost("/api/v1/sales", async (HttpContext context, AuthenticationService auth, SupervisorAuthorizationStore approvals, SalesService sales, ConfirmSaleRequest request, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (request.Checkout is null) return Results.BadRequest(new { error = "Checkout is required." });
    if (!await AuthorizePermissionAsync(context, auth, approvals, access, Permission.ConfirmSale, cancellationToken)) return PermissionFailure(context);
    if (request.Checkout.Lines.Any(x => x.UnitPriceOverride.HasValue) &&
        !await AuthorizePermissionAsync(context, auth, approvals, access, Permission.OverrideSalePrice, cancellationToken)) return PermissionFailure(context);
    if (request.Checkout.Payments.Any(x => IsCreditMethod(x.Method)) &&
        !await AuthorizePermissionAsync(context, auth, approvals, access, Permission.CreateCreditSale, cancellationToken)) return PermissionFailure(context);
    var key = context.Request.Headers["Idempotency-Key"].ToString();
    try
    {
        var result = await sales.ConfirmAsync(access.OperatorId, access.TerminalId, request.CashSessionId, key, request.Checkout, cancellationToken);
        if (result.Error == "idempotency_conflict") return Results.Conflict(new { error = result.Error });
        return result.Replayed
            ? Results.Ok(new { saleId = result.SaleId, replayed = true })
            : Results.Created("/api/v1/sales/" + result.SaleId, new { saleId = result.SaleId, replayed = false });
    }
    catch (CheckoutRejectedException exception)
    {
        return exception.Code == "insufficient_stock"
            ? Results.Conflict(new { error = exception.Code })
            : Results.UnprocessableEntity(new { error = exception.Code });
    }
});
app.MapPost("/api/v1/sales/{saleId:guid}/returns", async (Guid saleId, HttpContext context, PosDbContext db,
    AuthenticationService auth, SupervisorAuthorizationStore approvals, SaleReturnService returns, SaleReturnRequest request, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await AuthorizePermissionAsync(context, auth, approvals, access, Permission.ReturnSale, cancellationToken)) return PermissionFailure(context);
    var cashSession = await db.CashSessions.AsNoTracking().SingleOrDefaultAsync(x => x.ActiveOperatorId == access.OperatorId &&
        x.TerminalId == access.TerminalId && x.Status == "Open", cancellationToken);
    if (cashSession is null) return Results.Conflict(new { error = "open_cash_session_required" });
    var key = context.Request.Headers["Idempotency-Key"].ToString();
    try
    {
        var result = await returns.ReturnAsync(access.OperatorId, access.TerminalId, cashSession.Id, saleId, key, request, cancellationToken);
        if (result.Error == "idempotency_conflict") return Results.Conflict(new { error = result.Error });
        return result.Replayed
            ? Results.Ok(new { returnId = result.ReturnId, result.Amount, result.Currency, replayed = true })
            : Results.Created($"/api/v1/sales/{saleId}/returns/{result.ReturnId}", new { returnId = result.ReturnId, result.Amount, result.Currency, replayed = false });
    }
    catch (CheckoutRejectedException exception)
    {
        if (exception.Code == "sale_not_found") return Results.NotFound(new { error = exception.Code });
        return exception.Code is "insufficient_cash_for_refund" or "refund_rounding_conflict" or "refund_exceeds_original_payments"
            or "sale_not_returnable"
            ? Results.Conflict(new { error = exception.Code })
            : Results.UnprocessableEntity(new { error = exception.Code });
    }
});
app.MapGet("/api/v1/products", async (HttpContext context, PosDbContext db, AuthenticationService auth, string? query, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null || await auth.FindValidSessionAsync(token, cancellationToken) is null) return Results.Unauthorized();
    var search = (query ?? string.Empty).Trim();
    if (search.Length > 100) return Results.BadRequest(new { error = "Search text is too long." });
    var products = await db.Products.AsNoTracking()
        .Where(x => x.Active && (search == "" || EF.Functions.ILike(x.Sku, search + "%") || EF.Functions.ILike(x.Name, "%" + search + "%")))
        .OrderBy(x => x.Name).Take(30)
        .Select(x => new { x.Id, x.Sku, x.Name, x.UnitPrice, x.Currency, hasImage = x.ImageFileName != null,
            quantity = db.Stocks.Where(stock => stock.ProductId == x.Id).Select(stock => (decimal?)stock.Quantity).FirstOrDefault() ?? 0m })
        .ToListAsync(cancellationToken);
    return Results.Ok(products);
});
app.MapGet("/api/v1/inventory", async (HttpContext context, PosDbContext db, AuthenticationService auth, SupervisorAuthorizationStore approvals, string? query, string? status, string? currency, string? stockStatus, bool? hasImage, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await AuthorizePermissionAsync(context, auth, approvals, access, Permission.AdjustStock, cancellationToken)) return PermissionFailure(context);
    var term = (query ?? string.Empty).Trim();
    var stateFilter = string.IsNullOrWhiteSpace(status) ? "active" : status.Trim().ToLowerInvariant();
    var currencyFilter = (currency ?? string.Empty).Trim().ToUpperInvariant();
    var stockFilter = (stockStatus ?? string.Empty).Trim().ToLowerInvariant();
    if (term.Length > 100 || stateFilter is not ("active" or "inactive" or "all") ||
        currencyFilter is not ("" or "VES" or "USD") || stockFilter is not ("" or "available" or "out-of-stock"))
        return Results.BadRequest(new { error = "invalid_inventory_filter" });
    var productsQuery = db.Products.AsNoTracking().AsQueryable();
    if (stateFilter == "active") productsQuery = productsQuery.Where(x => x.Active);
    else if (stateFilter == "inactive") productsQuery = productsQuery.Where(x => !x.Active);
    if (term.Length > 0) productsQuery = productsQuery.Where(x => EF.Functions.ILike(x.Sku, "%" + term + "%") || EF.Functions.ILike(x.Name, "%" + term + "%"));
    if (currencyFilter.Length > 0) productsQuery = productsQuery.Where(x => x.Currency == currencyFilter);
    if (hasImage.HasValue) productsQuery = productsQuery.Where(x => (x.ImageFileName != null) == hasImage.Value);
    var inventoryQuery = from product in productsQuery
                           join stock in db.Stocks.AsNoTracking() on product.Id equals stock.ProductId into stockRows
                           from stockRow in stockRows.DefaultIfEmpty()
                           select new { product.Id, product.Sku, product.Name, product.UnitPrice, product.Currency, Quantity = stockRow == null ? 0m : stockRow.Quantity, hasImage = product.ImageFileName != null, product.Active };
    if (stockFilter == "available") inventoryQuery = inventoryQuery.Where(x => x.Quantity > 0m);
    else if (stockFilter == "out-of-stock") inventoryQuery = inventoryQuery.Where(x => x.Quantity <= 0m);
    var inventory = await inventoryQuery.OrderBy(x => x.Name).ThenBy(x => x.Sku).ToListAsync(cancellationToken);
    return Results.Ok(inventory);
});
app.MapPost("/api/v1/inventory/{productId:guid}/adjustments", async (Guid productId, HttpContext context, PosDbContext db, AuthenticationService auth, SupervisorAuthorizationStore approvals, InventoryAdjustmentRequest request, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await AuthorizePermissionAsync(context, auth, approvals, access, Permission.AdjustStock, cancellationToken)) return PermissionFailure(context);
    if (request.QuantityDelta == 0m || decimal.Round(request.QuantityDelta, 3) != request.QuantityDelta ||
        string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 250)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["adjustment"] = ["A nonzero quantity with at most three decimals and a reason up to 250 characters is required."] });
    if (!await db.Products.AnyAsync(x => x.Id == productId && x.Active, cancellationToken)) return Results.NotFound();
    if (!await db.Stocks.AnyAsync(x => x.ProductId == productId, cancellationToken)) return Results.Conflict(new { error = "stock_record_missing" });
    StockMovement movement;
    try { movement = StockMovement.Create(productId, request.QuantityDelta, request.Reason, access.OperatorId, DateTimeOffset.UtcNow); }
    catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["adjustment"] = [exception.Message] }); }
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    var affected = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE stocks SET \"Quantity\" = \"Quantity\" + {movement.QuantityDelta}, \"Version\" = \"Version\" + 1 WHERE \"ProductId\" = {productId} AND \"Quantity\" + {movement.QuantityDelta} >= 0", cancellationToken);
    if (affected != 1) return Results.Conflict(new { error = "insufficient_stock" });
    var warehouseId = await WarehouseStockBridge.GetWarehouseForTerminalAsync(db, access.TerminalId, cancellationToken);
    if (warehouseId.HasValue)
    {
        try { await WarehouseStockBridge.ApplyDeltaAsync(db, warehouseId.Value, productId, movement.QuantityDelta, cancellationToken); }
        catch (InvalidOperationException) { return Results.Conflict(new { error = "insufficient_warehouse_stock" }); }
    }
    var record = new StockMovementRecord { Id = Guid.NewGuid(), ProductId = movement.ProductId, QuantityDelta = movement.QuantityDelta,
        Reason = movement.Reason, ActorId = movement.ActorId, TerminalId = access.TerminalId, OccurredAtUtc = movement.OccurredAtUtc.UtcDateTime };
    db.StockMovements.Add(record);
    db.AuditEvents.Add(new AuditRecord { Id = Guid.NewGuid(), ActorId = access.OperatorId, TerminalId = access.TerminalId,
        Action = "inventory.adjusted", Payload = JsonSerializer.Serialize(new { record.ProductId, record.QuantityDelta, record.Reason }), OccurredAtUtc = DateTime.UtcNow });
    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    var quantity = await db.Stocks.AsNoTracking().Where(x => x.ProductId == productId).Select(x => x.Quantity).SingleAsync(cancellationToken);
    return Results.Created($"/api/v1/inventory/{productId}/movements/{record.Id}", new { record.Id, record.ProductId, record.QuantityDelta, quantity, record.Reason, record.OccurredAtUtc });
});
app.MapGet("/api/v1/inventory/{productId:guid}/movements", async (Guid productId, HttpContext context, PosDbContext db, AuthenticationService auth, SupervisorAuthorizationStore approvals, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await AuthorizePermissionAsync(context, auth, approvals, access, Permission.AdjustStock, cancellationToken)) return PermissionFailure(context);
    var movements = await db.StockMovements.AsNoTracking().Where(x => x.ProductId == productId).OrderByDescending(x => x.OccurredAtUtc).Take(100)
        .Select(x => new { x.Id, x.QuantityDelta, x.Reason, x.ActorId, x.OccurredAtUtc }).ToListAsync(cancellationToken);
    return Results.Ok(movements);
});
app.MapHito4();
await ActivateInstalledModulesAsync();
app.Run();

async Task ActivateInstalledModulesAsync()
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Pos.ModuleActivation");
    var registry = new ModuleInstallationRegistry();
    IReadOnlyList<InstalledModuleRecord> installed;
    try { installed = await registry.GetAllAsync(connectionString); }
    catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
    {
        logger.LogError(exception, "No se pudo consultar el registro de módulos; el POS base continuará sin módulos.");
        return;
    }
    if (installed.Count == 0) return;

    var trustStore = app.Services.GetService<ModuleTrustKeyRingStore>();
    var storageRoot = ResolveConfiguredModulePath(builder.Configuration["Modules:StoragePath"], "Modules:StoragePath", "Modules");
    var staged = new List<(InstalledModuleRecord Record, LoadedPosModule Runtime)>();
    foreach (var record in installed)
    {
        try
        {
            if (string.Equals(record.Status, "Disabled", StringComparison.Ordinal))
                continue;
            if (trustStore is null)
            {
                await registry.SetActivationStatusAsync(connectionString, record.ModuleId, "ActivationFailed", "No está configurado el anillo de confianza de publicadores.");
                continue;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(record.ModuleId, "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
                !Version.TryParse(record.Version, out var storedVersion) || storedVersion.Revision != -1)
            {
                await registry.SetActivationStatusAsync(connectionString, record.ModuleId, "ActivationFailed", "El id o la versión guardada en el registro no son válidos.");
                continue;
            }
            var moduleDirectory = Path.GetFullPath(Path.Combine(storageRoot, record.ModuleId, storedVersion.ToString(3)));
            var safeRoot = Path.GetFullPath(storageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!moduleDirectory.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La ruta del módulo sale de la carpeta configurada.");
            var packagePath = Path.Combine(moduleDirectory, "package.zip");
            if (!File.Exists(packagePath))
                throw new FileNotFoundException("No se encontró el ZIP firmado guardado para el módulo.", packagePath);

            await using var packageStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var verified = ModulePackageVerifier.Verify(packageStream, trustStore.Current);
            if (!string.Equals(verified.Module.Id, record.ModuleId, StringComparison.Ordinal) || verified.Module.Version != storedVersion ||
                !string.Equals(verified.SignerKeyId, record.SignerKeyId, StringComparison.Ordinal))
                throw new InvalidOperationException("La firma, identidad o versión del ZIP no coincide con el registro instalado.");
            if (moduleHostApiVersion < verified.Module.HostApi.Min || moduleHostApiVersion > verified.Module.HostApi.Max)
            {
                await registry.SetActivationStatusAsync(connectionString, record.ModuleId, "IncompatibleHost",
                    $"El módulo requiere host API {verified.Module.HostApi.Min}–{verified.Module.HostApi.Max}; este servidor usa {moduleHostApiVersion}.");
                continue;
            }
            var terminalCheck = await EvaluateTerminalCompatibilityInScopeAsync(verified.Module.ClientApi!);
            if (!terminalCheck.IsCompatible)
            {
                var detail = $"{terminalCheck.UnreportedTerminalCount} terminal(es) sin versión reportada; versiones incompatibles: {string.Join(", ", terminalCheck.IncompatibleVersions)}.";
                await registry.SetActivationStatusAsync(connectionString, record.ModuleId, "WaitingForTerminals", detail);
                continue;
            }
            if (verified.Module.EntryAssembly is null)
            {
                await registry.SetActivationStatusAsync(connectionString, record.ModuleId, "NoRuntimeEntry", "El paquete contiene migraciones, pero no declara código ejecutable.");
                continue;
            }

            packageStream.Position = 0;
            var runtime = ModuleRuntimeLoader.Load(packageStream, trustStore.Current);
            staged.Add((record, runtime));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "El módulo {ModuleId} quedó inactivo durante la activación.", record.ModuleId);
            try { await registry.SetActivationStatusAsync(connectionString, record.ModuleId, "ActivationFailed", exception.Message); }
            catch (Exception statusException) when (statusException is NpgsqlException or InvalidOperationException)
            { logger.LogError(statusException, "No se pudo guardar el estado de activación fallida de {ModuleId}.", record.ModuleId); }
        }
    }

    foreach (var item in staged)
    {
        try
        {
            var moduleId = item.Record.ModuleId;
            var group = app.MapGroup("/api/v1/modules/" + moduleId).AddEndpointFilter(async (filterContext, next) =>
            {
                var http = filterContext.HttpContext;
                await using var operation = await moduleMaintenanceGate.EnterOperationAsync(http.RequestAborted);
                if (!activeModuleIds.ContainsKey(moduleId)) return Results.NotFound();
                var token = ReadBearerToken(http);
                if (token is null) return Results.Unauthorized();
                var auth = http.RequestServices.GetRequiredService<AuthenticationService>();
                var access = await auth.FindValidSessionAsync(token, http.RequestAborted);
                if (access is null) return Results.Unauthorized();
                return await next(filterContext);
            });
            foreach (var endpoint in item.Runtime.Endpoints)
            {
                var permission = endpoint.RequiredPermission switch
                {
                    null => Permission.ManageModules,
                    "Warehouse.View" => Permission.ViewWarehouses,
                    "Warehouse.Manage" => Permission.ManageWarehouses,
                    "Warehouse.Transfer" => Permission.TransferInventory,
                    _ => throw new InvalidOperationException($"El endpoint del módulo '{moduleId}' declara un permiso no compatible: '{endpoint.RequiredPermission}'.")
                };
                if (endpoint.RequiredPermission is not null && !item.Runtime.Package.Module.Permissions.Contains(endpoint.RequiredPermission, StringComparer.Ordinal))
                    throw new InvalidOperationException($"El endpoint del módulo '{moduleId}' usa un permiso que no declara en su manifiesto: '{endpoint.RequiredPermission}'.");
                group.MapMethods(endpoint.Route, [endpoint.Method.ToUpperInvariant()], endpoint.Handler)
                    .AddEndpointFilter(async (filterContext, next) =>
                    {
                        var http = filterContext.HttpContext;
                        var token = ReadBearerToken(http);
                        if (token is null) return Results.Unauthorized();
                        var auth = http.RequestServices.GetRequiredService<AuthenticationService>();
                        var access = await auth.FindValidSessionAsync(token, http.RequestAborted);
                        if (access is null) return Results.Unauthorized();
                        var approvals = http.RequestServices.GetRequiredService<SupervisorAuthorizationStore>();
                        if (!await AuthorizePermissionAsync(http, auth, approvals, access, permission, http.RequestAborted))
                            return PermissionFailure(http);
                        http.Items["Pos.OperatorId"] = access.OperatorId;
                        http.Items["Pos.TerminalId"] = access.TerminalId;
                        return await next(filterContext);
                    });
            }
            activeModuleRuntimes.Add(item.Runtime);
            activeModuleIds[moduleId] = 0;
            await registry.SetActivationStatusAsync(connectionString, moduleId, "Active", null);
            logger.LogInformation("Módulo {ModuleId} {Version} activado con {EndpointCount} endpoint(s).",
                moduleId, item.Runtime.Package.Module.Version.ToString(3), item.Runtime.Endpoints.Count);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "No se pudieron publicar las rutas del módulo {ModuleId}.", item.Record.ModuleId);
            try { await registry.SetActivationStatusAsync(connectionString, item.Record.ModuleId, "ActivationFailed", exception.Message); }
            catch (Exception statusException) when (statusException is NpgsqlException or InvalidOperationException)
            { logger.LogError(statusException, "No se pudo guardar el estado de activación fallida de {ModuleId}.", item.Record.ModuleId); }
        }
    }

    async Task<ModuleTerminalCompatibilityResult> EvaluateTerminalCompatibilityInScopeAsync(ModuleClientApiRange range)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await EvaluateTerminalCompatibilityAsync(scope.ServiceProvider.GetRequiredService<PosDbContext>(), range, CancellationToken.None);
    }
}

static string? ReadBearerToken(HttpContext context)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && authorization.Length > 7
        ? authorization[7..].Trim()
        : null;
}

static async Task<ModuleTerminalCompatibilityResult> EvaluateTerminalCompatibilityAsync(PosDbContext db,
    ModuleClientApiRange range, CancellationToken cancellationToken)
{
    var terminalVersions = new Dictionary<Guid, Version?>();
    foreach (var terminalId in await db.PairedTerminals.AsNoTracking().Where(terminal => terminal.Active)
                 .Select(terminal => terminal.TerminalId).ToListAsync(cancellationToken))
        terminalVersions[terminalId] = null;
    foreach (var record in await db.TerminalClientVersions.AsNoTracking().ToListAsync(cancellationToken))
        terminalVersions[record.TerminalId] = ClientApiVersionParser.TryParseOptional(record.ClientVersion, out var version) ? version : null;
    return new ModuleTerminalCompatibilityPolicy().Evaluate(range, terminalVersions.Values);
}

static async Task<bool> AuthorizePermissionAsync(HttpContext context, AuthenticationService auth,
    SupervisorAuthorizationStore approvals, AccessSessionRecord access, Permission permission, CancellationToken cancellationToken)
{
    var mode = await auth.GetPermissionAccessModeAsync(access.OperatorId, permission, cancellationToken);
    if (mode == PermissionAccessMode.Allow) return true;
    if (mode == PermissionAccessMode.Deny)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return false;
    }
    var operation = context.Request.Method + " " + context.Request.Path + context.Request.QueryString;
    var fingerprint = context.Items["permission.request_fingerprint"] as string ?? string.Empty;
    if (approvals.TryConsume(context.Request.Headers["X-Admin-Approval"].ToString(), access.OperatorId, access.TerminalId, permission, operation, fingerprint)) return true;
    context.Response.StatusCode = StatusCodes.Status428PreconditionRequired;
    context.Items["permission.challenge"] = new AdminPinChallenge("admin_pin_required", permission.ToString(), Guid.NewGuid(), operation, fingerprint);
    return false;
}

static IResult PermissionFailure(HttpContext context)
    => context.Items["permission.challenge"] is AdminPinChallenge challenge
        ? Results.Json(challenge, statusCode: StatusCodes.Status428PreconditionRequired)
        : Results.StatusCode(context.Response.StatusCode == StatusCodes.Status200OK ? StatusCodes.Status403Forbidden : context.Response.StatusCode);

static bool IsCreditMethod(string method) => string.Equals(method, "credit", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(method, "fiado", StringComparison.OrdinalIgnoreCase) || string.Equals(method, "credito", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(method, "crédito", StringComparison.OrdinalIgnoreCase);

static string ResolveConfiguredModulePath(string? configuredPath, string settingName, string folderName)
{
    var path = string.IsNullOrWhiteSpace(configuredPath)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "POS", folderName)
        : Environment.ExpandEnvironmentVariables(configuredPath);
    if (!Path.IsPathFullyQualified(path))
        throw new InvalidOperationException($"{settingName} debe ser una ruta absoluta.");
    return Path.GetFullPath(path);
}

static Permission? ResolveModulePermission(string permission) => permission switch
{
    "Warehouse.View" => Permission.ViewWarehouses,
    "Warehouse.Manage" => Permission.ManageWarehouses,
    "Warehouse.Transfer" => Permission.TransferInventory,
    _ => null
};

public sealed record CreateOperatorRequest(string? Username, string? Pin);
public sealed record OperatorCreateRequest(string? Username, string? Pin, string? Role);
public sealed record LoginRequest(string? Username, string? Pin, Guid TerminalId, string? ClientVersion);
public sealed record TerminalPairRequest(Guid TerminalId, string? AdministratorUsername, string? AdministratorPin);
public sealed record SupervisorAuthorizationRequest(string? AdministratorUsername, string? AdministratorPin, string? Permission, Guid OperationId, string? Operation, string? RequestFingerprint);
public sealed record OpenCashSessionRequest(decimal? OpeningBalance = null, string? Currency = null, decimal? OpeningBalanceUsd = null, decimal? OpeningBalanceVes = null);
public sealed record CloseCashSessionRequest(decimal? CountedClosingBalance = null, decimal? CountedClosingUsd = null, decimal? CountedClosingVes = null);
public sealed record CashMovementRequest(decimal Amount, string? Direction, string? Reason, string? Currency = null);
public sealed record InventoryAdjustmentRequest(decimal QuantityDelta, string? Reason);
public sealed record ConfirmSaleRequest(Guid CashSessionId, CheckoutRequest? Checkout);
