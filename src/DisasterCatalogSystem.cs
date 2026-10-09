using System;
using System.Collections.Generic;
using Game;
using Game.Prefabs;
using Game.Common;
using Game.Events;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace DisasterControlPanel
{
    public sealed class DisasterType
    {
        public string Id, Name, Kind, NativeName;
        public EventPrefab Original;
        public readonly EventPrefab[] Levels = new EventPrefab[10];
    }

    public sealed class DisasterCatalogSystem : GameSystemBase
    {
        internal const string Prefix = "DisasterControlPanel.";
        public readonly List<DisasterType> Types = new List<DisasterType>();
        private EntityQuery _query;
        private PrefabSystem _prefabs;
        private readonly HashSet<string> _registered = new HashSet<string>();
        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            _query = GetEntityQuery(ComponentType.ReadOnly<EventData>(), ComponentType.ReadOnly<PrefabData>(), ComponentType.Exclude<Deleted>());
        }
        [Preserve]
        protected override void OnUpdate()
        {
            RegisterCustom();
            using (var entities = _query.ToEntityArray(Allocator.Temp))
            foreach (Entity entity in entities)
            {
                EventPrefab prefab = _prefabs.GetPrefab<EventPrefab>(entity);
                if (prefab == null || prefab.name.StartsWith(Prefix, StringComparison.Ordinal) || _registered.Contains(prefab.name)) continue;
                string kind = null, name = null;
                if (prefab.TryGet<Game.Prefabs.WaterLevelChangeComponent>(out _))
                {
                    if (prefab.name == "Tsunami") { kind = "tsunami"; name = "海啸"; }
                    if (prefab.name == "Flood") { kind = "flood"; name = "洪水"; }
                }
                else if (prefab.name == "Lightning Strike") { kind = "weather"; name = "雷击"; }
                else if (prefab.TryGet<Game.Prefabs.Fire>(out var originalFire))
                {
                    // PrefabUpdate runs before native component initialization: ECS data can still be zero.
                    var target = originalFire.m_RandomTargetType;
                    if (target == EventTargetType.Building) { kind = "building"; name = "建筑火灾"; }
                    if (target == EventTargetType.WildTree) { kind = "forest"; name = "森林火灾"; }
                }
                else if (prefab.TryGet<Game.Prefabs.WeatherPhenomenon>(out _))
                {
                    kind = "weather";
                    var n = prefab.name.ToLowerInvariant();
                    name = n.Contains("tornado") ? "龙卷风" : n.Contains("hail") ? "冰雹" : n.Contains("thunder") ? "雷暴" : prefab.name;
                }
                if (kind == null) continue;
                // Tutorial fire prefabs can target the same object type. Keep the actual simulation events.
                if (prefab.name.IndexOf("tutorial", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var type = new DisasterType { Id = prefab.name, Name = name, Kind = kind, NativeName = prefab.name, Original = prefab };
                for (int level = 1; level <= 10; level++)
                {
                    var clone = (EventPrefab)prefab.Clone(Prefix + prefab.name + ".L" + level);
                    float scale = level / 5f;
                    if (clone.TryGet<Game.Prefabs.Fire>(out var fire))
                    {
                        if (prefab.name != "Lightning Strike") fire.m_StartProbability = 0f;
                        fire.m_StartIntensity *= scale;
                        fire.m_EscalationRate *= scale;
                        fire.m_SpreadProbability *= scale;
                        fire.m_SpreadRange *= Unity.Mathematics.math.sqrt(scale);
                    }
                    if (clone.TryGet<Game.Prefabs.WeatherPhenomenon>(out var weather))
                    {
                        weather.m_OccurrenceProbability = 0f;
                        weather.m_DamageSeverity *= scale;
                        weather.m_PhenomenonRadius.min *= Unity.Mathematics.math.sqrt(scale);
                        weather.m_PhenomenonRadius.max *= Unity.Mathematics.math.sqrt(scale);
                        weather.m_DangerLevel = Unity.Mathematics.math.saturate(weather.m_DangerLevel * scale);
                        if (prefab.name == "Lightning Strike")
                            weather.m_LightningInterval = new Colossal.Mathematics.Bounds1(3f, 8f);
                    }
                    if (clone.TryGet<WaterLevelChangeComponent>(out var water))
                    {
                        water.m_Evacuate = true; water.m_DangerLevel = 1f;
                        // A manual flood must not join SoilWaterSystem's singleton rain-controlled flood.
                        if (kind == "flood") water.m_ChangeType = WaterLevelChangeType.None;
                    }
                    clone.m_ConcurrentLimit = 4;
                    _prefabs.AddPrefab(clone);
                    type.Levels[level - 1] = clone;
                }
                Types.Add(type);
                _registered.Add(prefab.name);
                Mod.Log.Info("Registered disaster " + prefab.name + " (" + kind + ") with 10 manual intensity levels");
            }
        }
        private void RegisterCustom()
        {
            foreach (string id in new[] { "Earthquake", "Meteor", "Sinkhole", "Evacuation" })
            {
                if (_registered.Contains(id)) continue;
                string name = id == "Earthquake" ? "地震" : id == "Meteor" ? "陨石" : id == "Sinkhole" ? "地面塌陷" : "全城避难";
                var type = new DisasterType { Id = id, Name = name, Kind = id == "Evacuation" ? "alert" : id.ToLowerInvariant(), NativeName = "自定义 · " + name };
                for (int level = 1; level <= 10; level++)
                {
                    var prefab = UnityEngine.ScriptableObject.CreateInstance<CustomEventPrefab>();
                    prefab.name = Prefix + id + ".L" + level; prefab.m_ConcurrentLimit = 8;
                    var journal = prefab.AddComponent<JournalEventComponent>();
                    journal.m_Icon = "Media/Game/Notifications/BuildingCollapsed.svg";
                    journal.m_TrackedData = new[] { EventDataTrackingType.Damages, EventDataTrackingType.Casualties };
                    journal.m_TrackedCityEffects = new EventCityEffectTrackingType[0];
                    _prefabs.AddPrefab(prefab); type.Levels[level - 1] = prefab;
                }
                Types.Add(type); _registered.Add(id);
                Mod.Log.Info("Registered custom disaster " + id);
            }
        }
        public DisasterType Find(string id) => Types.Find(t => t.Id == id);
    }
}
