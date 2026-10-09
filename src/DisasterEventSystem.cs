using Game;
using UnityEngine.Scripting;
namespace DisasterControlPanel
{
    // Creation must precede native event initialization (Modification2), not run in UIUpdate.
    public sealed class DisasterEventSystem : GameSystemBase
    {
        private DisasterUISystem _ui;
        [Preserve]
        protected override void OnCreate() { base.OnCreate(); _ui = World.GetOrCreateSystemManaged<DisasterUISystem>(); }
        [Preserve]
        protected override void OnUpdate() { _ui.ApplyQueuedSpawn(); }
    }
}
