using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

/// <summary>
/// <see cref="QueryFailureClassifier"/> is the one place a substring match against the native
/// engine's error text decides whether a caller sees a plain <see cref="LadybugException"/> or
/// the retryable <see cref="LadybugWriteConflictException"/>. A prior version matched only
/// <c>"one write transaction"</c> against a spec doc excerpt that turned out to be a truncated
/// quote of the real message, and shipped with nothing exercising either branch - these tests
/// exist so that branch can never again go untested.
/// </summary>
public class QueryFailureClassifierTests
{
    /// <summary>
    /// The exact message reported by the real engine (v0.18.3), confirmed against the real
    /// library by triggering a genuine write conflict - see
    /// <c>DatabaseLifecycleTests.ConcurrentWrite_ThrowsLadybugWriteConflictException</c> in the
    /// integration project.
    /// </summary>
    private const string RealEngineWriteConflictMessage =
        "Cannot start a new write transaction in the system. Only one write transaction at a time is allowed in the system.";

    /// <summary>
    /// The message as recorded (truncated) in this project's own design spec. Included so the
    /// exact quote that misled the original substring match stays covered even though the real
    /// message (above) is longer.
    /// </summary>
    private const string SpecExcerptWriteConflictMessage = "Cannot start a new write transaction in the system";

    [Test]
    public async Task RealEngineMessage_ClassifiesAsWriteConflict()
    {
        var ex = QueryFailureClassifier.Classify(RealEngineWriteConflictMessage, "CREATE (o:Obj {dbref: 2})");

        await Assert.That(ex).IsTypeOf<LadybugWriteConflictException>();
        await Assert.That(ex.Statement).IsEqualTo("CREATE (o:Obj {dbref: 2})");
        await Assert.That(ex.Message).Contains(RealEngineWriteConflictMessage);
    }

    [Test]
    public async Task SpecExcerptMessage_ClassifiesAsWriteConflict()
    {
        var ex = QueryFailureClassifier.Classify(SpecExcerptWriteConflictMessage, "CREATE (o:Obj {dbref: 2})");

        await Assert.That(ex).IsTypeOf<LadybugWriteConflictException>();
    }

    [Test]
    public async Task UnrelatedErrorMessage_ClassifiesAsPlainLadybugException()
    {
        var ex = QueryFailureClassifier.Classify(
            "Table Foo does not exist.", "MATCH (o:Foo) RETURN o");

        await Assert.That(ex).IsTypeOf<LadybugException>();
        await Assert.That(ex).IsNotTypeOf<LadybugWriteConflictException>();
        await Assert.That(ex.Message).Contains("Table Foo does not exist.");
    }

    [Test]
    public async Task EmptyMessage_ClassifiesAsPlainLadybugExceptionWithFallbackMessage()
    {
        var ex = QueryFailureClassifier.Classify(string.Empty, "MATCH (o) RETURN o");

        await Assert.That(ex).IsTypeOf<LadybugException>();
        await Assert.That(ex).IsNotTypeOf<LadybugWriteConflictException>();
        await Assert.That(ex.Message).Contains("Query failed.");
    }

    [Test]
    public async Task NullMessage_ClassifiesAsPlainLadybugExceptionWithFallbackMessage()
    {
        var ex = QueryFailureClassifier.Classify(null, "MATCH (o) RETURN o");

        await Assert.That(ex).IsTypeOf<LadybugException>();
        await Assert.That(ex).IsNotTypeOf<LadybugWriteConflictException>();
        await Assert.That(ex.Message).Contains("Query failed.");
    }
}
