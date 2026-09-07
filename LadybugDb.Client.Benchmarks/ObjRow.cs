namespace LadybugDb.Client.Benchmarks;

/// <summary>The projection shape every read-path benchmark materializes: one <c>Obj</c> row.</summary>
public sealed record ObjRow(long Dbref, string Name, long Loc);
