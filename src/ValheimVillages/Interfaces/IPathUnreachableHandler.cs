using UnityEngine;

namespace ValheimVillages.Interfaces
{
    /// <summary>
    ///     Optional capability for <see cref="IBehavior" /> implementations.
    ///     VillagerAI invokes <see cref="OnPathUnreachable" /> when its
    ///     recovery flow (retreat to known location + retry with backoff)
    ///     has exhausted its attempt budget and the target still cannot be
    ///     reached via a complete NavMesh path. The behavior decides what
    ///     to do next — typically AbandonWork and let the work scanner
    ///     pick fresh work. Behaviors that don't care about pathing
    ///     failures simply don't implement this interface.
    ///     Optional-interface pattern is used (rather than a default-impl
    ///     method on IBehavior) because the project targets net472, which
    ///     lacks runtime support for default interface methods.
    /// </summary>
    public interface IPathUnreachableHandler
    {
        void OnPathUnreachable(Vector3 target);
    }
}
