namespace PinBot.Agent.Tests;

/// <summary>
/// Tests that point PINBOT_HOME at a temp folder. It is one process-wide setting, so these never run
/// alongside any other test: one test's folder swapped in mid-way through another made it read an empty state.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PinBotHomeCollection
{
    public const string Name = "PINBOT_HOME";
}
