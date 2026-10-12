using System.Text;
using Npgsql;

namespace Pos.Server;

public static class ModulePlanFailureLog
{
    public static string Write(string diagnosticId, string moduleId, Exception exception,
        string? connectionString = null, string? logDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticId);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(exception);

        logDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PuntoDeVenta", "Logs");
        Directory.CreateDirectory(logDirectory);
        var path = Path.Combine(logDirectory, "errores-validacion-modulos.log");
        var safeException = exception.ToString();
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            safeException = safeException.Replace(connectionString, "[conexión oculta]", StringComparison.Ordinal);
            try
            {
                var password = new NpgsqlConnectionStringBuilder(connectionString).Password;
                if (!string.IsNullOrEmpty(password))
                    safeException = safeException.Replace(password, "[contraseña oculta]", StringComparison.Ordinal);
            }
            catch (ArgumentException) { }
        }

        var entry = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("O"))
            .Append(" | Diagnóstico: ").Append(diagnosticId)
            .Append(" | Módulo: ").Append(moduleId)
            .AppendLine()
            .AppendLine(safeException)
            .AppendLine(new string('-', 80))
            .ToString();
        File.AppendAllText(path, entry, Encoding.UTF8);
        return path;
    }
}
