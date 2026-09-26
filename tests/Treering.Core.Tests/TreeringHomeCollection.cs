namespace Treering.Core.Tests;

/// <summary>
/// Tests that point <c>TREERING_HOME</c> somewhere else. It is one variable for the whole process,
/// so they run alone: in parallel, one class would read or delete another's home, and anything that
/// writes under <see cref="Projects.Home"/> meanwhile would land in the wrong folder.
/// </summary>
[CollectionDefinition("TREERING_HOME", DisableParallelization = true)]
public sealed class TreeringHomeCollection;
