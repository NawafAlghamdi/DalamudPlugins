using System;
using System.Collections.Generic;

namespace AnoMech.Core.Native.Interfaces;

// Holds the animation of the given actors on the frame it has reached; an actor left out of the
// next call animates on from there.
public interface IActorFreeze : IDisposable
{
    void Freeze(IReadOnlyCollection<uint> entityIds);
}
