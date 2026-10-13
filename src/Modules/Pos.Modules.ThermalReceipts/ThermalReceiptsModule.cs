using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Modules.Abstractions;
using Pos.Shared.Contracts;

namespace Pos.Modules.ThermalReceipts;

public sealed class ThermalReceiptsModule : IPosServerModule
{
    public const string Id = "thermal-receipts";
    private static readonly Guid TemplateId = new("52406e74-3d47-4dd2-a8be-7d96c026b100");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string Schema = "m_thermal_receipts_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Id)))[..12].ToLowerInvariant();
    private static string Table(string name) => $"\"{Schema}\".\"{name}\"";

    public string ModuleId => Id;
    public IReadOnlyList<PosModuleEndpoint> GetEndpoints() =>
    [
        new("GET", "template", GetTemplateAsync, "ThermalReceipts.Print"),
        new("PUT", "template", SaveTemplateAsync, "ThermalReceipts.Configure")
    ];

    private static async Task<IResult> GetTemplateAsync(HttpContext context)
    {
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var command = new NpgsqlCommand($"SELECT template_json,revision FROM {Table("receipt_templates")} WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", TemplateId);
        await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
        if (!await reader.ReadAsync(context.RequestAborted))
            return Results.Ok(new ThermalReceiptTemplateSnapshot(ThermalReceiptTemplatePolicy.Default, 0));
        var json = reader.GetString(0);
        var revision = reader.GetInt64(1);
        var template = JsonSerializer.Deserialize<ThermalReceiptTemplate>(json, JsonOptions);
        if (template is null || ThermalReceiptTemplatePolicy.Validate(template) is not null)
            return Results.Problem("La plantilla guardada no es válida; un administrador debe volver a guardarla.", statusCode: StatusCodes.Status500InternalServerError);
        return Results.Ok(new ThermalReceiptTemplateSnapshot(template, revision));
    }

    private static async Task<IResult> SaveTemplateAsync(HttpContext context)
    {
        var request = await context.Request.ReadFromJsonAsync<SaveThermalReceiptTemplateRequest>(cancellationToken: context.RequestAborted);
        var validation = ThermalReceiptTemplatePolicy.Validate(request?.Template);
        if (validation is not null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["template"] = [validation] });
        var template = ThermalReceiptTemplatePolicy.Normalize(request!.Template!);
        var operatorId = context.Items["Pos.OperatorId"] is Guid id ? id : Guid.Empty;
        if (operatorId == Guid.Empty) return Results.Unauthorized();
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {Table("receipt_templates")} AS target (id,template_json,revision,updated_by,updated_at_utc)
            VALUES (@id,@json,1,@operator,@now)
            ON CONFLICT (id) DO UPDATE SET template_json=EXCLUDED.template_json,
                revision=target.revision+1, updated_by=EXCLUDED.updated_by,
                updated_at_utc=EXCLUDED.updated_at_utc
            RETURNING revision
            """, connection);
        command.Parameters.AddWithValue("id", TemplateId);
        command.Parameters.AddWithValue("json", JsonSerializer.Serialize(template, JsonOptions));
        command.Parameters.AddWithValue("operator", operatorId);
        command.Parameters.AddWithValue("now", DateTime.UtcNow);
        var revision = (long)(await command.ExecuteScalarAsync(context.RequestAborted) ?? 1L);
        return Results.Ok(new ThermalReceiptTemplateSnapshot(template, revision));
    }

    private static async Task<NpgsqlConnection> OpenAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var connectionString = context.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Pos");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("Falta ConnectionStrings:Pos.");
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
