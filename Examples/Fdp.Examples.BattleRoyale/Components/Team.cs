namespace Fdp.Examples.BattleRoyale.Components;

/// <summary>
/// Managed component - MUST be immutable record for thread safety
/// Demonstrates rich data (strings, arrays) in GDB architecture
/// </summary>
public record Team(string TeamName, int TeamId, string[] MemberNames);
