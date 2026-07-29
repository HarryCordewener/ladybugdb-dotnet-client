using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class ContainerValueTests
{
    [Test]
    public async Task ListAndMap_RoundTrip()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE C(id INT64, tags STRING[], attrs MAP(STRING,STRING), PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (n:C {id: 1, tags: ['a','b','c'], attrs: map(['k1','k2'],['v1','v2'])})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:C) RETURN n.tags, n.attrs");
            await using var e = r.GetAsyncEnumerator();
            await e.MoveNextAsync();
            var row = e.Current;

            var list = row.GetValue(0).AsList();
            await Assert.That(list.Count).IsEqualTo(3);
            await Assert.That(list[1].AsString()).IsEqualTo("b");

            var map = row.GetValue(1).AsMap();
            await Assert.That(map.Count).IsEqualTo(2);
            await Assert.That(map[0].Key.AsString()).IsEqualTo("k1");
            await Assert.That(map[0].Value.AsString()).IsEqualTo("v1");
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task Node_ExposesIdLabelAndProperties()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE P(id INT64, name STRING, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync("CREATE (n:P {id: 7, name: 'Limbo'})")) { }

            await using var r = await conn.QueryAsync("MATCH (n:P) RETURN n");
            await using var e = r.GetAsyncEnumerator();
            await e.MoveNextAsync();
            var row = e.Current;

            var node = row.GetValue(0).AsNode();
            await Assert.That(node.Label).IsEqualTo("P");
            await Assert.That(node.Properties["name"].AsString()).IsEqualTo("Limbo");
            await Assert.That(node.Properties["id"].AsInt64()).IsEqualTo(7L);
        }
        finally { TestDatabase.Cleanup(path); }
    }

    [Test]
    public async Task Rel_ExposesLabelEndpointsAndProperties()
    {
        var path = TestDatabase.NewPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Person(id INT64, name STRING, PRIMARY KEY(id))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE REL TABLE Knows(FROM Person TO Person, since INT64)")) { }
            await using (var _ = await conn.QueryAsync("CREATE (:Person {id: 1, name: 'Ada'})")) { }
            await using (var _ = await conn.QueryAsync("CREATE (:Person {id: 2, name: 'Grace'})")) { }
            await using (var _ = await conn.QueryAsync(
                "MATCH (a:Person {id: 1}), (b:Person {id: 2}) CREATE (a)-[:Knows {since: 1990}]->(b)")) { }

            await using var r = await conn.QueryAsync("MATCH (a:Person)-[k:Knows]->(b:Person) RETURN a, k, b");
            await using var e = r.GetAsyncEnumerator();
            await e.MoveNextAsync();
            var row = e.Current;

            var source = row.GetValue(0).AsNode();
            var rel = row.GetValue(1).AsRel();
            var destination = row.GetValue(2).AsNode();

            await Assert.That(rel.Label).IsEqualTo("Knows");
            await Assert.That(rel.SourceId).IsEqualTo(source.Id);
            await Assert.That(rel.DestinationId).IsEqualTo(destination.Id);
            await Assert.That(rel.Properties["since"].AsInt64()).IsEqualTo(1990L);
        }
        finally { TestDatabase.Cleanup(path); }
    }
}
