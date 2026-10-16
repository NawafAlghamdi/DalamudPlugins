using AnoMech.Core.Native.Interfaces;

namespace AnoMech.Tests;

// Who the sim last asked to hold still.
internal sealed class FakeActorFreeze : IActorFreeze
{
    public IReadOnlySet<uint> Frozen { get; private set; } = new HashSet<uint>();

    public void Freeze(IReadOnlyCollection<uint> entityIds) => Frozen = entityIds.ToHashSet();

    public void Dispose() { }
}
