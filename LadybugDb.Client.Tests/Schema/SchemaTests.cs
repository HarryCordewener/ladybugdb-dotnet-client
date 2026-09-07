using System.Reflection;
using ExtendedNumerics;
using LadybugDb.Client.Mapping;
using LadybugDb.Client.Schema;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests.Schema;

[Node("Object")]
public sealed record Obj([property: Key] long Dbref, string Name, long? Loc);

[Node("Attr")]
public sealed record Attr([property: Key] string Akey, string Aname, string Aval);

[Rel("Has", From = typeof(Obj), To = typeof(Attr))]
public sealed record Has(long Since);

[Rel("Located", typeof(Obj), typeof(Obj))]
public sealed record Located;

[Node("Renamed")]
public sealed record Renamed([property: Key, Column("object_id")] long Id, [property: Column("display name")] string Name);

[Node("Keyless")]
public sealed record Keyless(long Dbref);

[Node("TwoKeys")]
public sealed record TwoKeys([property: Key] long A, [property: Key] long B);

[Node("Odd")]
public sealed record Odd([property: Key] long Id, Uri Home);

public sealed record NotMapped(long Id);

[Rel("Broken", typeof(NotMapped), typeof(Obj))]
public sealed record BrokenRel;

/// <summary>
/// Descriptors built from <c>[Node]</c>/<c>[Rel]</c> records, the CLR-to-engine type map, and the
/// DDL those descriptors render. No engine: <c>SchemaValidationTests</c> in the integration suite
/// takes the same records to a real catalog.
/// </summary>
public class SchemaTests
{
    [Test]
    public async Task Node_DescribesTableColumnsAndKey()
    {
        var schema = LadybugSchema.For(typeof(Obj));
        var obj = schema.Node<Obj>();
        await Assert.That(obj.Table).IsEqualTo("Object");
        await Assert.That(obj.Key.Column).IsEqualTo("dbref");
        await Assert.That(obj.Properties.Select(p => (p.Column, p.ClrName, p.EngineTypeName)))
            .IsEquivalentTo([("dbref", "Dbref", "INT64"), ("name", "Name", "STRING"), ("loc", "Loc", "INT64")]);
        await Assert.That(obj.Properties[2].ClrType).IsEqualTo(typeof(long?));
        await Assert.That(obj.Properties[2].EngineType).IsEqualTo(LadybugType.Int64);
        await Assert.That(obj.FindProperty("Loc")!.Column).IsEqualTo("loc");
        await Assert.That(obj.FindProperty("Nope")).IsNull();
    }

    [Test]
    public async Task Rel_DescribesEndpoints_AndPullsThemIntoTheSchema()
    {
        var schema = LadybugSchema.For(typeof(Has), typeof(Located));
        var has = schema.Rel<Has>();
        await Assert.That(has.Table).IsEqualTo("Has");
        await Assert.That(has.From.Table).IsEqualTo("Object");
        await Assert.That(has.To.Table).IsEqualTo("Attr");
        await Assert.That(has.Properties.Select(p => p.Column)).IsEquivalentTo(["since"]);
        await Assert.That(schema.Rel<Located>().Properties.Count).IsEqualTo(0);
        await Assert.That(schema.Nodes.Select(n => n.Table).Order()).IsEquivalentTo(["Attr", "Object"]);
        await Assert.That(schema.Rels.Select(r => r.Table).Order()).IsEquivalentTo(["Has", "Located"]);
    }

    [Test]
    public async Task Column_OverridesTheName()
    {
        var renamed = LadybugSchema.For(typeof(Renamed)).Node<Renamed>();
        await Assert.That(renamed.Key.Column).IsEqualTo("object_id");
        await Assert.That(renamed.Properties[1].Column).IsEqualTo("display name");
    }

    [Test]
    public async Task Node_WithoutKey_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LadybugSchema.For(typeof(Keyless)));
        await Assert.That(ex!.Message).Contains("Keyless");
        await Assert.That(ex.Message).Contains("[Key]");
    }

    [Test]
    public async Task Node_WithTwoKeys_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LadybugSchema.For(typeof(TwoKeys)));
        await Assert.That(ex!.Message).Contains("TwoKeys");
        await Assert.That(ex.Message).Contains("'A'");
        await Assert.That(ex.Message).Contains("'B'");
    }

    [Test]
    public async Task Property_OfAnUnmappedClrType_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LadybugSchema.For(typeof(Odd)));
        await Assert.That(ex!.Message).Contains("Odd");
        await Assert.That(ex.Message).Contains("'Home'");
        await Assert.That(ex.Message).Contains("Uri");
    }

    [Test]
    public async Task Rel_WhoseEndpointIsNotANode_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LadybugSchema.For(typeof(BrokenRel)));
        await Assert.That(ex!.Message).Contains("BrokenRel");
        await Assert.That(ex.Message).Contains("NotMapped");
        await Assert.That(ex.Message).Contains("[Node]");
    }

    [Test]
    public async Task Type_WithNeitherAttribute_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LadybugSchema.For(typeof(NotMapped)));
        await Assert.That(ex!.Message).Contains("NotMapped");
    }

    [Test]
    public async Task Node_NotInTheSchema_IsReportedByName()
    {
        var schema = LadybugSchema.For(typeof(Obj));
        var ex = Assert.Throws<InvalidOperationException>(() => schema.Node<Attr>());
        await Assert.That(ex!.Message).Contains("Attr");
        await Assert.That(schema.TryGetNode(typeof(Attr), out _)).IsFalse();
    }

    [Test]
    public async Task FromAssembly_FindsEveryAnnotatedType()
    {
        // The refused shapes above live in this assembly too, so a scan of all of it must throw on
        // them - the assembly here is filtered to the well-formed ones through a predicate.
        var schema = LadybugSchema.FromAssembly(typeof(SchemaTests).Assembly,
            t => t.Namespace == typeof(SchemaTests).Namespace && t.Name is "Obj" or "Attr" or "Has" or "Located");
        await Assert.That(schema.Nodes.Count).IsEqualTo(2);
        await Assert.That(schema.Rels.Count).IsEqualTo(2);
    }

    [Test]
    public async Task CreateTableStatements_RenderNodesBeforeRels()
    {
        var schema = LadybugSchema.For(typeof(Has), typeof(Located));
        await Assert.That(schema.CreateTableStatements()).IsEquivalentTo([
            "CREATE NODE TABLE Object(dbref INT64, name STRING, loc INT64, PRIMARY KEY(dbref))",
            "CREATE NODE TABLE Attr(akey STRING, aname STRING, aval STRING, PRIMARY KEY(akey))",
            "CREATE REL TABLE Has(FROM Object TO Attr, since INT64)",
            "CREATE REL TABLE Located(FROM Object TO Object)",
        ]);
    }

    [Test]
    public async Task EngineTypeName_ParsesToTheLadybugType()
    {
        await Assert.That(EngineTypeMap.Parse("INT64")).IsEqualTo(LadybugType.Int64);
        await Assert.That(EngineTypeMap.Parse("DECIMAL(38, 10)")).IsEqualTo(LadybugType.Decimal);
        await Assert.That(EngineTypeMap.Parse("STRING[]")).IsEqualTo(LadybugType.List);
        await Assert.That(EngineTypeMap.Parse("INT64[3]")).IsEqualTo(LadybugType.List);
        await Assert.That(EngineTypeMap.Parse("TIMESTAMP_MS")).IsEqualTo(LadybugType.Timestamp);
        await Assert.That(EngineTypeMap.Parse("TIMESTAMP_TZ")).IsEqualTo(LadybugType.TimestampTz);
        await Assert.That(EngineTypeMap.Parse("SERIAL")).IsEqualTo(LadybugType.Int64);
        await Assert.That(EngineTypeMap.Parse("FLOAT")).IsEqualTo(LadybugType.Single);
        await Assert.That(EngineTypeMap.Parse("NODE")).IsEqualTo(LadybugType.Node);
        await Assert.That(EngineTypeMap.Parse("no such type")).IsNull();
    }

    private static readonly (LadybugType Type, object Payload)[] ColumnSamples =
    [
        (LadybugType.Boolean, true), (LadybugType.Int8, sbyte.MinValue), (LadybugType.Int16, short.MinValue),
        (LadybugType.Int32, int.MinValue), (LadybugType.Int64, long.MinValue), (LadybugType.UInt8, byte.MaxValue),
        (LadybugType.UInt16, ushort.MaxValue), (LadybugType.UInt32, uint.MaxValue), (LadybugType.UInt64, ulong.MaxValue),
        (LadybugType.Int128, Int128.MinValue), (LadybugType.Single, float.MaxValue), (LadybugType.Double, double.MaxValue),
        (LadybugType.Decimal, "1.5"), (LadybugType.String, "s"), (LadybugType.Blob, new byte[] { 1 }),
        (LadybugType.Uuid, Guid.NewGuid()), (LadybugType.Date, DateOnly.MinValue), (LadybugType.Timestamp, DateTime.UnixEpoch),
        (LadybugType.TimestampTz, DateTimeOffset.UnixEpoch), (LadybugType.Interval, TimeSpan.Zero),
    ];

    /// <summary>
    /// The schema's widening table says which engine column types each CLR type can be read from.
    /// That is <see cref="RowMapper"/>'s decision, made in code; this pins the table to it in both
    /// directions, for every (CLR type, column type) pair, so the two cannot drift apart.
    /// </summary>
    [Test]
    public async Task WideningTable_MatchesRowMapper_ForEveryPair()
    {
        var map = typeof(RowMapper).GetMethod(nameof(RowMapper.Map), BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var clr in EngineTypeMap.ClrTypes)
        {
            var mapper = map.MakeGenericMethod(clr);
            foreach (var (type, payload) in ColumnSamples)
            {
                var row = new LadybugRow([new LadybugValue(type, payload)], ["c"]);
                var readable = true;
                try { mapper.Invoke(null, [row]); }
                catch (TargetInvocationException) { readable = false; }

                await Assert.That(EngineTypeMap.CanRead(clr, type)).IsEqualTo(readable)
                    .Because($"{clr.Name} reading a {type} column");
            }
        }
    }

    [Test]
    public async Task DdlTypes_CoverThePlannedMap()
    {
        await Assert.That(EngineTypeMap.DdlTypeName(typeof(BigDecimal))).IsEqualTo("DECIMAL(38, 10)");
        await Assert.That(EngineTypeMap.DdlTypeName(typeof(int?))).IsEqualTo("INT32");
        await Assert.That(EngineTypeMap.DdlTypeName(typeof(byte[]))).IsEqualTo("BLOB");
        await Assert.That(EngineTypeMap.DdlTypeName(typeof(DateTimeOffset))).IsEqualTo("TIMESTAMP_TZ");
        await Assert.That(EngineTypeMap.DdlTypeName(typeof(Uri))).IsNull();
    }
}
