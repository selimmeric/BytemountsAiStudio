using BytemountsAiStudio.TestSupport;

namespace BytemountsAiStudio.Persistence.Tests;

/// xUnit koleksiyon tanımı derleme başına gerekli; fixture ortak.
[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "postgres";
}
