using Pos.Modules.Publisher;

try
{
    var options = PublisherArguments.Parse(args);
    PublisherRunner.Run(options);
    return 0;
}
catch (PublisherUsageException exception)
{
    Console.Error.WriteLine($"Error de argumentos: {exception.Message}");
    Console.Error.WriteLine("Uso:");
    Console.Error.WriteLine("  Pos.Modules.Publisher build --manifest <archivo> --stage <carpeta> --files <lista.json> --output <paquete.zip> --key-id <id> --thumbprint <huella> [--store CurrentUser|LocalMachine]");
    Console.Error.WriteLine("  Pos.Modules.Publisher export-public-key --thumbprint <huella> --output <clave-publica.pem> [--store CurrentUser|LocalMachine]");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"No se pudo publicar el módulo: {exception.Message}");
    return 1;
}
