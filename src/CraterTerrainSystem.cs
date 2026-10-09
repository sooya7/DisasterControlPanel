using Game;
using Game.Common;
using Game.Events;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace DisasterControlPanel
{
    // TerrainSystem.ApplyBrush writes the heightmap synchronously, but the ApplyTool phase only
    // ticks while the player actively applies a tool (ToolOutputSystem), never during normal play.
    // PostSimulation runs after all impact pulses and applies each crater once per rendered frame.
    public sealed class CraterTerrainSystem : GameSystemBase
    {
        private EntityQuery _query;
#if DCP_DEV
        internal int UpdateCount { get; private set; }
        internal int EventCount => _query.CalculateEntityCount();
#endif
        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            _query = GetEntityQuery(ComponentType.ReadWrite<CustomDisasterState>(), ComponentType.ReadOnly<Duration>(), ComponentType.Exclude<Deleted>());
            RequireForUpdate(_query);
        }
        [Preserve]
        protected override void OnUpdate()
        {
#if DCP_DEV
            UpdateCount++;
#endif
            using (var events = _query.ToEntityArray(Allocator.Temp))
            foreach (var e in events)
            {
                var s = EntityManager.GetComponentData<CustomDisasterState>(e);
                if ((s.Kind != 2 && s.Kind != 3) || s.Pulses == 0 || s.TerrainApplied != 0) continue;
                World.GetOrCreateSystemManaged<CustomDisasterSystem>().ApplyCrater(ref s);
                EntityManager.SetComponentData(e, s);
            }
        }
    }
}
