using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

/// <summary>
/// <see cref="TransactionStatement"/> decides whether a raw statement is transaction control, which
/// is what stops the client from sending a nested <c>BEGIN TRANSACTION</c> that the engine answers by
/// destroying the transaction already in flight.
/// </summary>
/// <remarks>
/// The failure modes are not symmetric, and these tests are written around that. A missed
/// transaction statement leaves the caller exactly where they were before this class existed. A
/// <em>false</em> match refuses a legitimate query that would have worked. So the spellings the
/// engine accepts must all be recognized, and anything merely containing those keywords must not be.
/// The accepted spellings below were measured against the real engine, not assumed - see
/// <c>TransactionGuardTests</c> in the integration project.
/// </remarks>
public class TransactionStatementTests
{
    [Test]
    [Arguments("BEGIN TRANSACTION")]
    [Arguments("BEGIN TRANSACTION READ ONLY")]
    [Arguments("begin transaction")]
    [Arguments("begin transaction read only")]
    [Arguments("BeGiN tRaNsAcTiOn")]
    [Arguments("  BEGIN TRANSACTION  ")]
    [Arguments("BEGIN   TRANSACTION")]
    [Arguments("BEGIN TRANSACTION;")]
    [Arguments("BEGIN TRANSACTION ;")]
    [Arguments("\tBEGIN\nTRANSACTION\r\n")]
    [Arguments("BEGIN TRANSACTION READ    ONLY ;")]
    public async Task RecognizedBeginSpellings_ClassifyAsBegin(string cypher)
    {
        await Assert.That(TransactionStatement.Classify(cypher)).IsEqualTo(TransactionEffect.Begin);
    }

    [Test]
    [Arguments("COMMIT")]
    [Arguments("commit")]
    [Arguments("  COMMIT ;  ")]
    [Arguments("ROLLBACK")]
    [Arguments("rollback")]
    [Arguments("\tROLLBACK\n")]
    public async Task RecognizedEndSpellings_ClassifyAsEnd(string cypher)
    {
        await Assert.That(TransactionStatement.Classify(cypher)).IsEqualTo(TransactionEffect.End);
    }

    /// <summary>
    /// The false-positive cases. Every one of these is a legitimate statement that must reach the
    /// engine untouched; classifying any of them would refuse a query that works today. The string
    /// literals matter most - a naive "does it contain BEGIN TRANSACTION" check fails exactly here.
    /// </summary>
    [Test]
    [Arguments("CREATE (n:S {s: 'BEGIN TRANSACTION'})")]
    [Arguments("CREATE (n:S {s: 'COMMIT'})")]
    [Arguments("CREATE (n:S {s: 'ROLLBACK'})")]
    [Arguments("MATCH (n:S) WHERE n.s = 'BEGIN TRANSACTION' RETURN n")]
    [Arguments("MATCH (n:S) RETURN n.commit")]
    [Arguments("CREATE NODE TABLE Commit(id INT64, PRIMARY KEY(id))")]
    [Arguments("RETURN 'COMMIT'")]
    [Arguments("BEGIN TRANSACTION READ WRITE")]   // a parser rejection; not a transaction we opened
    [Arguments("BEGIN")]                          // likewise
    [Arguments("COMMITTED")]
    [Arguments("ROLLBACKS")]
    [Arguments("MATCH (n) RETURN n")]
    public async Task StatementsThatMerelyMentionTransactionControl_ClassifyAsNone(string cypher)
    {
        await Assert.That(TransactionStatement.Classify(cypher)).IsEqualTo(TransactionEffect.None);
    }

    /// <summary>
    /// A multi-statement script is deliberately not classified. Tracking only its leading keyword
    /// would record a transaction as open that the script's own <c>COMMIT</c> already closed, leaving
    /// the connection's bookkeeping wrong in the direction that refuses valid work. Passing it
    /// through leaves the caller in the pre-existing manual regime instead, which is the documented
    /// and safer of the two.
    /// </summary>
    [Test]
    [Arguments("BEGIN TRANSACTION; CREATE (n:T {id: 1}); COMMIT")]
    [Arguments("BEGIN TRANSACTION; CREATE (n:T {id: 1})")]
    public async Task MultiStatementScripts_ClassifyAsNone(string cypher)
    {
        await Assert.That(TransactionStatement.Classify(cypher)).IsEqualTo(TransactionEffect.None);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task NullOrBlank_ClassifiesAsNone(string? cypher)
    {
        await Assert.That(TransactionStatement.Classify(cypher)).IsEqualTo(TransactionEffect.None);
    }
}
