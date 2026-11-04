// Every database test joins this one collection, so the run pays for one migrated database and
// the tests in it run one at a time — which a per-test ResetBusinessTablesAsync relies on.
//
// Ships from skills-db-postgres (assets/Tests/Database/). Never hand-edited.

namespace RouteGo.Payment.Api.Tests.Database;

/// <summary>The xunit collection that shares <see cref="DatabaseFixture"/> across every database test.</summary>
[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    /// <summary>The collection name a test class opts into: <c>[Collection(DatabaseCollection.Name)]</c>.</summary>
    public const string Name = "Database";
}
