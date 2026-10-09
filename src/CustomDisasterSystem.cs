using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Events;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

namespace DisasterControlPanel
{
    public sealed class CustomDisasterSystem : GameSystemBase
    {
        private sealed class Work
        {
            public Entity[] Buildings;
            public int Cursor;
            public uint NextWarning;
            public readonly Dictionary<Entity, bool> WaterThreat = new Dictionary<Entity, bool>();
        }
        private readonly Dictionary<Entity, Work> _work = new Dictionary<Entity, Work>();
        private EntityQuery _events, _buildings, _danger;
        private EntityArchetype _endanger, _damage, _destroy, _ignite;
        private SimulationSystem _simulation;
        private TerrainSystem _terrain;
        private PrefabSystem _prefabs;
        private Texture2D _brush;
        private Entity _firePrefab;
        private bool _release;
        private bool _alert;
        public int LastDamaged { get; private set; }
        public int LastDestroyed { get; private set; }
        public int LastWarned { get; private set; }
        public int LastTerrainApplied { get; private set; }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            _simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            _terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            _prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            _events = GetEntityQuery(ComponentType.ReadWrite<CustomDisasterState>(), ComponentType.ReadOnly<Duration>(), ComponentType.Exclude<Deleted>());
            _buildings = GetEntityQuery(ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            _danger = GetEntityQuery(ComponentType.ReadWrite<InDanger>(), ComponentType.Exclude<Deleted>());
            _endanger = EntityManager.CreateArchetype(typeof(Game.Common.Event), typeof(Endanger));
            _damage = EntityManager.CreateArchetype(typeof(Game.Common.Event), typeof(Game.Objects.Damage));
            _destroy = EntityManager.CreateArchetype(typeof(Game.Common.Event), typeof(Game.Objects.Destroy));
            _ignite = EntityManager.CreateArchetype(typeof(Game.Common.Event), typeof(Ignite));
        }

        public void RequestAlert() { _alert = true; _release = false; }
        public void RequestRelease() { _release = true; _alert = false; }
        private Entity Create(EntityArchetype archetype)
        {
            using (var created = new NativeArray<Entity>(1, Allocator.Temp)) { EntityManager.CreateEntity(archetype, created); return created[0]; }
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            _work.Clear(); _alert = _release = false; _firePrefab = Entity.Null;
            LastDamaged = LastDestroyed = LastWarned = LastTerrainApplied = 0;
            base.OnGamePreload(purpose, mode);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (!World.GetOrCreateSystemManaged<DisasterUISystem>().InGame) return;
            if (_alert) { _alert = false; StartAlert(); }
            if (_release) { _release = false; ReleaseAll(); }
            uint frame = _simulation.frameIndex;
            using (var events = _events.ToEntityArray(Allocator.Temp))
            foreach (Entity e in events)
            {
                var s = EntityManager.GetComponentData<CustomDisasterState>(e);
                var duration = EntityManager.GetComponentData<Duration>(e);
                if (frame >= duration.m_EndFrame)
                {
                    LastDamaged = s.Damaged; LastDestroyed = s.Destroyed; LastWarned = s.Warned; LastTerrainApplied = s.TerrainApplied;
                    Release(e); _work.Remove(e);
                    EntityManager.AddComponent<Deleted>(e);
                    continue;
                }
                if (!_work.TryGetValue(e, out Work work))
                {
                    using (var buildings = _buildings.ToEntityArray(Allocator.Temp))
                        work = new Work { Buildings = buildings.ToArray(), NextWarning = frame };
                    _work.Add(e, work);
                }
                if (s.Kind == 10 || s.Kind == 11) UpdateWater(e, ref s, ref duration);
                Warn(e, ref s, duration, work);
                if (s.Kind >= 1 && s.Kind <= 3 && frame >= s.ImpactFrame)
                {
                    if (s.Pulses == 0 || (s.Kind == 1 && s.Pulses < 3 && frame >= s.NextPulse))
                    {
                        DamageBuildings(e, ref s, work);
                        s.Pulses++; s.NextPulse = frame + 180;
                        s.Phase = 1;
                    }
                }
                LastDamaged = s.Damaged; LastDestroyed = s.Destroyed; LastWarned = s.Warned; LastTerrainApplied = s.TerrainApplied;
                EntityManager.SetComponentData(e, s);
            }
            // Native EventTickSystem can delete a timed event before this system runs.
            var expired = new List<Entity>();
            foreach (var pair in _work)
                if (!EntityManager.Exists(pair.Key) || EntityManager.HasComponent<Deleted>(pair.Key)) { Release(pair.Key); expired.Add(pair.Key); }
            foreach (var e in expired) _work.Remove(e);
        }

        private void UpdateWater(Entity e, ref CustomDisasterState s, ref Duration duration)
        {
            var water = EntityManager.GetComponentData<WaterLevelChange>(e);
            if (s.Phase == 0)
            {
                float angle = math.radians(s.Radius);
                water.m_Direction = new float2(math.sin(angle), math.cos(angle));
                water.m_MaxIntensity = s.Kind == 11 ? s.Level / 5f : s.Level * .2f;
                duration.m_StartFrame = s.ImpactFrame;
                duration.m_EndFrame = s.ImpactFrame + (uint)(s.Depth * 60) + (uint)(s.Kind == 11 ? WaterLevelChangeSystem.TsunamiEndDelay : 0);
                EntityManager.SetComponentData(e, duration);
                s.Phase = 1;
            }
            if (s.Kind == 10)
            {
                float ramp = WaterDisasterRules.FloodRamp(_simulation.frameIndex, s.ImpactFrame, duration.m_EndFrame);
                water.m_Intensity = water.m_MaxIntensity * ramp;
            }
            if (s.Kind == 11 && _simulation.frameIndex >= s.ImpactFrame + (uint)(s.Depth * 60)) s.Phase = 2;
            if (s.Kind == 11 && s.Phase == 2) water.m_MaxIntensity = water.m_Intensity = 0;
            water.m_DangerHeight=s.Position.y+WaterDisasterRules.PeakHeight(s.Kind,s.Level);
            var prefab=EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
            var data=EntityManager.GetComponentData<WaterLevelChangeData>(prefab);
            // The native predictor assumes waves begin at the map boundary. Local-source
            // disasters issue native Endanger below using the actual water footprint.
            var flags=World.GetOrCreateSystemManaged<WaterSystem>().UseLegacyWaterSources ? DangerFlags.Evacuate : (DangerFlags)0;
            if(data.m_DangerFlags!=flags){data.m_DangerFlags=flags;EntityManager.SetComponentData(prefab,data);}
            EntityManager.SetComponentData(e, water);
        }

        private void StartAlert()
        {
            using (var events = _events.ToEntityArray(Allocator.Temp))
            foreach (var e in events)
                if (EntityManager.GetComponentData<CustomDisasterState>(e).Kind == 4) return;
            var catalog = World.GetOrCreateSystemManaged<DisasterCatalogSystem>();
            var type = catalog.Find("Evacuation");
            if (type == null || !_prefabs.TryGetEntity(type.Levels[4], out var prefab)) return;
            var data = EntityManager.GetComponentData<EventData>(prefab);
            Entity e2 = Create(data.m_Archetype);
            EntityManager.SetComponentData(e2, new PrefabRef(prefab));
            EntityManager.SetComponentData(e2, new Duration { m_StartFrame = _simulation.frameIndex, m_EndFrame = _simulation.frameIndex + 108000 });
            EntityManager.SetComponentData(e2, new DangerLevel { m_DangerLevel = 1f });
            EntityManager.SetComponentData(e2, new CustomDisasterState { Kind = 4, Level = 5, Citywide = 1 });
            Mod.Log.Info("Citywide evacuation started event=" + e2.Index);
        }

        private bool ValidBuilding(Entity e)
            => EntityManager.Exists(e) && !EntityManager.HasComponent<Deleted>(e) && !EntityManager.HasComponent<Destroyed>(e)
               && !EntityManager.HasComponent<Game.Buildings.EmergencyShelter>(e) && !EntityManager.HasComponent<Game.Objects.OutsideConnection>(e);

        private void Warn(Entity source, ref CustomDisasterState s, Duration duration, Work work)
        {
            bool localWater=s.Kind>=10 && s.Citywide==0;
            var waterSystem=World.GetOrCreateSystemManaged<WaterDisasterSystem>();
            if(localWater && (World.GetOrCreateSystemManaged<WaterSystem>().UseLegacyWaterSources || !waterSystem.HasSources(source)))return;
            uint frame = _simulation.frameIndex;
            if (frame < work.NextWarning) return;
            int end = Math.Min(work.Cursor + (localWater ? 32 : 512), work.Buildings.Length);
            for (; work.Cursor < end; work.Cursor++)
            {
                Entity building = work.Buildings[work.Cursor];
                if (!ValidBuilding(building)) continue;
                var position = EntityManager.GetComponentData<Game.Objects.Transform>(building).m_Position;
                if(localWater)
                {
                    if(!work.WaterThreat.TryGetValue(building,out bool threatened))
                        work.WaterThreat[building]=threatened=waterSystem.Threatens(source,s,position);
                    if(!threatened)continue;
                }
                else if (s.Citywide == 0 && math.distance(position.xz, s.Position.xz) > s.Radius * 1.5f) continue;
                if (EntityManager.HasComponent<InDanger>(building))
                {
                    var old = EntityManager.GetComponentData<InDanger>(building);
                    if ((old.m_Flags & DangerFlags.Evacuate) != 0 && old.m_EndFrame > frame + 64) continue;
                }
                Entity warning = Create(_endanger);
                EntityManager.SetComponentData(warning, new Endanger { m_Event = source, m_Target = building, m_Flags = DangerFlags.Evacuate, m_EndFrame = duration.m_EndFrame });
                s.Warned++;
            }
            if (work.Cursor >= work.Buildings.Length)
            {
                work.Cursor = 0; work.NextWarning = frame + 120;
            }
        }

        private void DamageBuildings(Entity source, ref CustomDisasterState s, Work work)
        {
            foreach (Entity e in work.Buildings)
            {
                if (!ValidBuilding(e)) continue;
                float distance = math.distance(EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position.xz, s.Position.xz);
                if (distance >= s.Radius) continue;
                float damage = DisasterRules.Damage(s.Kind, s.Level, distance, s.Radius);
                bool collapse = s.Kind == 3 ? distance < s.Radius * .75f : damage >= .8f;
                Entity d = Create(_damage);
                EntityManager.SetComponentData(d, new Game.Objects.Damage { m_Object = e, m_Delta = new float3(s.Kind == 1 ? damage / 3f : damage, 0, 0) });
                if (s.Pulses == 0) s.Damaged++;
                if (collapse && (s.Kind != 1 || s.Pulses == 2))
                {
                    Entity destroyed = Create(_destroy);
                    EntityManager.SetComponentData(destroyed, new Game.Objects.Destroy(e, source)); s.Destroyed++;
                }
                else if (s.Kind == 2 && distance < s.Radius * .75f) IgniteBuilding(e, s.Level);
                if (EntityManager.HasBuffer<TargetElement>(source) && s.Pulses == 0) EntityManager.GetBuffer<TargetElement>(source).Add(new TargetElement(e));
            }
            Mod.Log.Info("Custom impact kind=" + s.Kind + " pulse=" + s.Pulses + " damaged=" + s.Damaged + " collapsed=" + s.Destroyed);
        }

        private void IgniteBuilding(Entity target, int level)
        {
            if (EntityManager.HasComponent<OnFire>(target)) return;
            var type = World.GetOrCreateSystemManaged<DisasterCatalogSystem>().Find("Building Fire");
            if (type == null || !_prefabs.TryGetEntity(type.Levels[Math.Min(9, level - 1)], out _firePrefab)) return;
            Entity fire = Create(EntityManager.GetComponentData<EventData>(_firePrefab).m_Archetype);
            EntityManager.SetComponentData(fire, new PrefabRef(_firePrefab));
            EntityManager.GetBuffer<TargetElement>(fire).Add(new TargetElement(target));
        }

        internal void ApplyCrater(ref CustomDisasterState s)
        {
            if (s.TerrainApplied != 0) return;
            if (_brush == null)
            {
                _brush = new Texture2D(64, 64, TextureFormat.RGBA32, false, true) { name = "DCP crater falloff", wrapMode = TextureWrapMode.Clamp };
                var pixels = new Color[4096];
                for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                {
                    float r = math.length(new float2(x - 31.5f, y - 31.5f)) / 31.5f;
                    float value = math.pow(math.saturate(1f - r * r), 2f); pixels[y * 64 + x] = new Color(value, value, value, value);
                }
                _brush.SetPixels(pixels); _brush.Apply();
            }
            float radius = DisasterRules.CraterRadius(s.Kind, s.Level);
            var area = new Bounds2(s.Position.xz - radius, s.Position.xz + radius);
            // ApplyBrush clamps its time step to >= .05 and Shift speed is 2000 m/s.
            float delta = math.clamp(UnityEngine.Time.unscaledDeltaTime, .05f, 1f);
            var brush = new Game.Tools.Brush { m_Position = s.Position, m_Start = s.Position, m_Target = s.Position, m_Size = radius * 2, m_Opacity = 1, m_Strength = -s.Depth / (2000f * delta) };
            _terrain.ApplyBrush(TerraformingType.Shift, area, brush, _brush);
            s.TerrainApplied = 1;
            Mod.Log.Info("Applied crater radius=" + radius + " depth=" + s.Depth + " position=" + s.Position);
        }

        private void Release(Entity source)
        {
            using (var targets = _danger.ToEntityArray(Allocator.Temp))
            foreach (Entity e in targets)
            {
                var danger = EntityManager.GetComponentData<InDanger>(e);
                if (danger.m_Event != source) continue;
                Entity replacement = Entity.Null; uint end = 0;
                using (var remaining = _events.ToEntityArray(Allocator.Temp))
                foreach (Entity other in remaining)
                {
                    if (other == source) continue;
                    var state = EntityManager.GetComponentData<CustomDisasterState>(other);
                    var duration = EntityManager.GetComponentData<Duration>(other);
                    if (duration.m_EndFrame <= _simulation.frameIndex) continue;
                    if (state.Citywide == 0 && EntityManager.HasComponent<Game.Objects.Transform>(e))
                    {
                        var p=EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
                        if(state.Kind>=10 ? !World.GetOrCreateSystemManaged<WaterDisasterSystem>().Threatens(other,state,p) : math.distance(p.xz,state.Position.xz)>state.Radius*1.5f)continue;
                    }
                    replacement = other; end = duration.m_EndFrame; break;
                }
                if (replacement == Entity.Null) EntityManager.RemoveComponent<InDanger>(e);
                else { danger.m_Event = replacement; danger.m_EndFrame = end; EntityManager.SetComponentData(e, danger); }
                if (!EntityManager.HasComponent<EffectsUpdated>(e)) EntityManager.AddComponent<EffectsUpdated>(e);
            }
        }

        private void ReleaseAll()
        {
            var sources = new List<Entity>();
            using (var events = _events.ToEntityArray(Allocator.Temp))
            foreach (var e in events)
            {
                var state = EntityManager.GetComponentData<CustomDisasterState>(e);
                state.Citywide = 0;
                EntityManager.SetComponentData(e, state);
                sources.Add(e);
                if (state.Kind == 4) { var d = EntityManager.GetComponentData<Duration>(e); d.m_EndFrame = _simulation.frameIndex; EntityManager.SetComponentData(e, d); EntityManager.AddComponent<Deleted>(e); }
            }
            foreach (var source in sources)
            {
                Release(source);
                if (_work.TryGetValue(source, out var work)) { work.Cursor = 0; work.NextWarning = 0; }
            }
            Mod.Log.Info("Standalone citywide evacuation released");
        }

        [Preserve]
        protected override void OnDestroy() { if (_brush != null) UnityEngine.Object.Destroy(_brush); base.OnDestroy(); }
    }
}


