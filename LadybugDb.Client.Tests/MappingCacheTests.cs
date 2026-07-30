using LadybugDb.Client.Mapping;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

/// <summary>
/// <see cref="MappingCache"/> keys a resolved projection by (<c>T</c>, column shape). Both halves of
/// that key earn their place here: dropping the shape makes two queries projecting the same
/// <c>T</c> collide - and a plan reads columns by index, so a collision returns real values from the
/// wrong columns rather than throwing - while dropping the cache entirely makes a streaming
/// projection reflect once per row.
/// </summary>
/// <remarks>
/// <see cref="NotInParallelAttribute"/> at the class level, with no constraint key:
/// <see cref="PlanIsBuiltOncePerShape_NotOncePerRow"/> measures
/// <see cref="MappingCache.PlansBuilt"/>, a process-wide counter with no way to scope it to one
/// test, so any other test resolving a plan concurrently inflates the delta. The same reasoning (and
/// the same fix) as <c>LeakTests</c> in the integration suite.
/// </remarks>
[NotInParallel]
public class MappingCacheTests
{
    private static LadybugRow Row(params (string Name, LadybugValue Value)[] columns) =>
        new([.. columns.Select(c => c.Value)], [.. columns.Select(c => c.Name)]);

    private static LadybugValue Int64(long value) => new(LadybugType.Int64, value);

    private static LadybugValue Str(string value) => new(LadybugType.String, value);

    /// <summary>Projected by two different queries below, with different column shapes.</summary>
    private record Shaped(long Dbref, string Name);

    /// <summary>Used only by <see cref="PlanIsBuiltOncePerShape_NotOncePerRow"/>, so the counter it measures moves only for it.</summary>
    private record CounterProbe(long Dbref);

    /// <summary>Used only by <see cref="TwoTypesOverTheSameColumns_GetTheirOwnPlans"/>.</summary>
    private record OtherType(long Dbref);

    /// <summary>
    /// The collision this key exists to prevent. Both queries project <see cref="Shaped"/>; one
    /// returns the columns in the opposite order, and one returns an extra column. Keyed on
    /// <c>T</c> alone, the second and third results would reuse the first's plan and read <c>Dbref</c>
    /// from a <c>Name</c> column - which would throw here, but returns a plausible wrong value for
    /// any two columns of the same type.
    /// </summary>
    [Test]
    public async Task TwoColumnShapesForTheSameType_DoNotCollide()
    {
        var forward = RowMapper.ResolvePlan<Shaped>(Row(("Dbref", Int64(1)), ("Name", Str("a"))));
        var reversed = RowMapper.ResolvePlan<Shaped>(Row(("Name", Str("b")), ("Dbref", Int64(2))));
        var withExtra = RowMapper.ResolvePlan<Shaped>(
            Row(("Dbref", Int64(3)), ("Extra", Int64(99)), ("Name", Str("c"))));

        await Assert.That(reversed).IsNotSameReferenceAs(forward);
        await Assert.That(withExtra).IsNotSameReferenceAs(forward);
        await Assert.That(withExtra).IsNotSameReferenceAs(reversed);

        // Each plan reads its own shape correctly - the point of not colliding.
        var a = forward.Map(Row(("Dbref", Int64(1)), ("Name", Str("a"))));
        var b = reversed.Map(Row(("Name", Str("b")), ("Dbref", Int64(2))));
        var c = withExtra.Map(Row(("Dbref", Int64(3)), ("Extra", Int64(99)), ("Name", Str("c"))));

        await Assert.That(a).IsEqualTo(new Shaped(1, "a"));
        await Assert.That(b).IsEqualTo(new Shaped(2, "b"));
        await Assert.That(c).IsEqualTo(new Shaped(3, "c"));
    }

    /// <summary>
    /// The same shape resolves to the same plan instance, from a separately-allocated name array -
    /// so the key compares its contents, not its reference.
    /// </summary>
    [Test]
    public async Task TheSameShapeResolvedTwice_ReturnsTheSamePlan()
    {
        var first = RowMapper.ResolvePlan<Shaped>(new[] { "Dbref", "Name" });
        var second = RowMapper.ResolvePlan<Shaped>(new List<string> { "Dbref", "Name" });

        await Assert.That(second).IsSameReferenceAs(first);
    }

    [Test]
    public async Task TwoTypesOverTheSameColumns_GetTheirOwnPlans()
    {
        var probe = RowMapper.ResolvePlan<OtherType>(new[] { "Dbref" });
        var scalar = RowMapper.ResolvePlan<long>(new[] { "Dbref" });

        await Assert.That(scalar.IsScalarUnwrap).IsTrue();
        await Assert.That(probe.IsScalarUnwrap).IsFalse();
        await Assert.That(probe.Constructor).IsNotNull();
    }

    /// <summary>
    /// Reflection must run once per (<c>T</c>, shape), not once per row: the consumer of this seam
    /// streams, so per-row work is per-row-of-the-whole-result work. Measured on the counter rather
    /// than asserted from the code's shape, because "resolve is cached" is exactly the kind of claim
    /// that stays true in a comment after it stops being true in the code.
    /// </summary>
    [Test]
    public async Task PlanIsBuiltOncePerShape_NotOncePerRow()
    {
        var columns = new[] { "Dbref" };
        var before = MappingCache.PlansBuilt;

        // Resolved per row, exactly as a streaming projection might if it did not hoist the call.
        for (var i = 0; i < 50; i++)
        {
            var plan = RowMapper.ResolvePlan<CounterProbe>(columns);
            var mapped = plan.Map(Row(("Dbref", Int64(i))));
            await Assert.That(mapped.Dbref).IsEqualTo((long)i);
        }

        await Assert.That(MappingCache.PlansBuilt - before).IsEqualTo(1);
    }

    /// <summary>
    /// A build that failed is not cached, so the mapping error is reported identically on every
    /// call. Caching the failure would be defensible; silently reporting it once and then handing
    /// back something else would not, and this pins which of the two happens.
    /// </summary>
    [Test]
    public async Task AFailedResolution_ThrowsTheSameWayEveryTime()
    {
        var columns = new[] { "Nmae" };

        var first = Assert.Throws<InvalidOperationException>(
            () => RowMapper.ResolvePlan<CounterProbe>(columns));
        var second = Assert.Throws<InvalidOperationException>(
            () => RowMapper.ResolvePlan<CounterProbe>(columns));

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsNotNull();
        await Assert.That(second!.Message).IsEqualTo(first!.Message);
    }
}
