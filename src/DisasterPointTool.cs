using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine.Scripting;

namespace DisasterControlPanel
{
    public sealed class DisasterPointTool : ToolBaseSystem
    {
        public override string toolID => "DisasterControlPanel.Point";
        private DisasterUISystem _panel;
        private ToolBaseSystem _previous;
        private int _earliest;
        public override PrefabBase GetPrefab() => null;
        public override bool TrySetPrefab(PrefabBase prefab) => false;
        [Preserve]
        protected override void OnCreate() { base.OnCreate(); _panel = World.GetOrCreateSystemManaged<DisasterUISystem>(); }
        internal void Begin()
        {
            if (m_ToolSystem.activeTool != this) _previous = m_ToolSystem.activeTool;
            _earliest = UnityEngine.Time.frameCount + 3;
            m_ToolSystem.activeTool = this;
            _panel.Picking = true;
        }
        internal void Finish()
        {
            _panel.Picking = false;
            if (m_ToolSystem.activeTool == this)
                m_ToolSystem.activeTool = _previous != null && _previous != this ? _previous : m_DefaultToolSystem;
        }
        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Terrain | TypeMask.StaticObjects;
        }
        [Preserve]
        protected override void OnStopRunning()
        {
            _panel.Picking = false;
            base.OnStopRunning();
        }
        [Preserve]
        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            applyAction.shouldBeEnabled = true;
            cancelAction.shouldBeEnabled = true;
            if (cancelAction.WasPressedThisFrame()) { Finish(); return inputDeps; }
            if (!_panel.PointerOverPanel && UnityEngine.Time.frameCount >= _earliest && applyAction.WasPressedThisFrame()
                && GetRaycastResult(out Entity entity, out RaycastHit hit))
            {
                if (_panel.SetTarget(entity, hit.m_Position)) _panel.Spawn();
            }
            return inputDeps;
        }
    }
}
