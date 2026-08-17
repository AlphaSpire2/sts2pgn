namespace STS2PGN.Model;

/// <summary>
/// One PGN-style metadata tag. Unknown tags are first-class data and retain their order.
/// </summary>
public sealed record TagPair(string Name, string Value);
