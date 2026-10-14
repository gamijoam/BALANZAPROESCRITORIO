namespace Pos.Integration.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedPostgresCollection
{
    public const string Name = "shared PostgreSQL integration database";
}
