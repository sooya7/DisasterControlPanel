using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Colossal.UI;
using Colossal.UI.Binding;
using Game;
using Game.City;
using Game.Common;
using Game.Events;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

namespace DisasterControlPanel
{
    public sealed class DisasterUISystem : UISystemBase
    {
        internal bool Open, Picking, PointerOverPanel;
        private DisasterCatalogSystem _catalog;
        private PrefabSystem _prefabs;
        private SimulationSystem _simulation;
        private ToolSystem _tools;
        private EntityQuery _events, _burning;
        private string _selected, _message = "先选择灾难，再在地图上选点。";
        private int _level = 6, _seconds = 60;
        private bool _repeat, _messageIsError;
        private float _messageAt;
        private float _nextMount;
        // Each disaster remembers its own strength and warning.
        private readonly Dictionary<string, int> _levels = new Dictionary<string, int>(), _warnings = new Dictionary<string, int>();
        private int _warning = 30, _direction;
        private bool _citywide;
        private bool _hasTarget;
        private Entity _target, _city;
        private float3 _position;
        private float _nextUpdate, _lastSpawn = -10f;
        private string _script;
        private bool _mounted;
        private UIView _lastView;
        private sealed class SpawnRequest
        {
            public DisasterType Type;
            public int Level, Seconds, Warning, Direction;
            public bool Citywide;
            public Entity Target;
            public float3 Position;
        }
        private SpawnRequest _pending;
        private readonly HashSet<Entity> _stopping = new HashSet<Entity>();
        internal bool InGame => GameManager.instance != null && GameManager.instance.gameMode == GameMode.Game && !GameManager.instance.isGameLoading;
        private DisasterPointTool PointTool => World.GetOrCreateSystemManaged<DisasterPointTool>();

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            _catalog = World.GetOrCreateSystemManaged<DisasterCatalogSystem>();
            _prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            _simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            _tools = World.GetOrCreateSystemManaged<ToolSystem>();
            _events = GetEntityQuery(ComponentType.ReadOnly<Game.Events.Event>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            _burning = GetEntityQuery(ComponentType.ReadWrite<OnFire>(), ComponentType.Exclude<Deleted>());
            AddBinding(new TriggerBinding("dcp", "toggle", Toggle));
            AddBinding(new TriggerBinding<string>("dcp", "select", Select));
            AddBinding(new TriggerBinding<int>("dcp", "level", n => { _level = math.clamp(n, 1, 10); if (_selected != null) _levels[_selected] = _level; Refresh(); }));
            AddBinding(new TriggerBinding<bool>("dcp", "repeat", v => { _repeat = v; Refresh(); }));
            AddBinding(new TriggerBinding<string>("dcp", "focus", Focus));
            AddBinding(new TriggerBinding<string>("dcp", "stopOne", StopOne));
            AddBinding(new TriggerBinding<int>("dcp", "seconds", n => { _seconds = math.clamp(n, 15, 180); Refresh(); }));
            AddBinding(new TriggerBinding<int>("dcp", "warning", n => { _warning = math.clamp(n, 0, 120); if (_selected != null) _warnings[_selected] = _warning; Refresh(); }));
            AddBinding(new TriggerBinding<int>("dcp", "direction", n => { _direction = ((n % 360) + 360) % 360; Refresh(); }));
            AddBinding(new TriggerBinding<bool>("dcp", "citywide", n => { _citywide = n; Refresh(); }));
            AddBinding(new TriggerBinding("dcp", "evacuate", () => { World.GetOrCreateSystemManaged<CustomDisasterSystem>().RequestAlert(); Info("已请求全城避难，居民将通过原生系统前往庇护所。"); }));
            AddBinding(new TriggerBinding("dcp", "release", () => { World.GetOrCreateSystemManaged<CustomDisasterSystem>().RequestRelease(); Info("已请求解除独立全城警报；受灾区域仍保持避难。"); }));
            AddBinding(new TriggerBinding<bool>("dcp", "pointer", v => PointerOverPanel = v));
            AddBinding(new TriggerBinding("dcp", "pick", BeginPick));
            AddBinding(new TriggerBinding("dcp", "cancel", () => { PointTool.Finish(); Refresh(); }));
            AddBinding(new TriggerBinding("dcp", "spawn", () => Spawn()));
            AddBinding(new TriggerBinding("dcp", "stop", () => Stop()));
            AddBinding(new TriggerBinding("dcp", "ready", () => { _mounted = true; Mod.Log.Info("Panel mounted in game UI"); Refresh(); }));
            _script = "window.__dcpCss=" + JsonConvert.SerializeObject(Resource("DCP.panel.css")) + ";" + Resource("DCP.panel.js");
        }
        private static string Resource(string name)
        {
            using (var stream = typeof(Mod).Assembly.GetManifestResourceStream(name))
            using (var reader = new System.IO.StreamReader(stream)) return reader.ReadToEnd();
        }
        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            Open = Picking = PointerOverPanel = _hasTarget = false;
            _mounted = false; _nextMount = 0;
            _target = _city = Entity.Null;
            _stopping.Clear();
            _pending = null;
            _lastSpawn = -10f;
            _message = "先选择灾难，再在地图上选点。";
            base.OnGamePreload(purpose, mode);
            Refresh();
        }
        private void Refresh() => _nextUpdate = 0f;
        internal void Toggle()
        {
            if (!InGame) return;
            Open = !Open;
            if (!Open) { PointTool.Finish(); PointerOverPanel = false; }
            Refresh();
        }
        internal void Select(string id)
        {
            if (_catalog.Find(id) == null) return;
            if (_pending != null) { Fail("上一场灾难正在初始化，请稍候。"); return; }
            PointTool.Finish();
            _selected = id;
            _level = _levels.TryGetValue(id, out var level) ? level : 6;
            _warning = _warnings.TryGetValue(id, out var warning) ? warning : 30;
            _hasTarget = false;
            _target = Entity.Null;
            BeginPick();
        }
        internal void BeginPick()
        {
            if (!InGame || _catalog.Find(_selected) == null) return;
            if (_pending != null) { Fail("上一场灾难正在初始化，请稍候。"); return; }
            _hasTarget = false;
            _target = Entity.Null;
            PointTool.Begin();
            _message = "";
            Refresh();
        }
        internal bool SetTarget(Entity entity, float3 position)
        {
            var type = _catalog.Find(_selected);
            if (!InGame || type == null || !math.all(math.isfinite(position))) return false;
            if (type.Kind == "building" || type.Kind == "forest")
            {
                entity = ResolveTarget(entity, type.Kind);
                if (entity == Entity.Null)
                {
                    _message = type.Kind == "building" ? "请点击实际建筑。" : "请点击野生树木，园林树木不能作为森林火灾起点。";
                    Refresh(); return false;
                }
                position = EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position;
            }
            _target = type.Kind == "building" || type.Kind == "forest" ? entity : Entity.Null;
            _position = position;
            _hasTarget = true;
            _message = "位置已选定。";
            Refresh();
            return true;
        }
        private Entity ResolveTarget(Entity entity, string kind)
        {
            for (int depth = 0; depth < 8 && entity != Entity.Null && EntityManager.Exists(entity); depth++)
            {
                if (EntityManager.HasComponent<Temp>(entity) || EntityManager.HasComponent<Deleted>(entity) || EntityManager.HasComponent<Destroyed>(entity)) return Entity.Null;
                if (EntityManager.HasComponent<Game.Objects.Transform>(entity))
                {
                    if (kind == "building" && EntityManager.HasComponent<Game.Buildings.Building>(entity)) return entity;
                    if (kind == "forest" && EntityManager.HasComponent<Game.Objects.Tree>(entity) && !EntityManager.HasComponent<Owner>(entity)) return entity;
                }
                if (!EntityManager.HasComponent<Owner>(entity)) break;
                entity = EntityManager.GetComponentData<Owner>(entity).m_Owner;
            }
            return Entity.Null;
        }
        internal bool Spawn()
        {
            var type = _catalog.Find(_selected);
            if (!InGame || type == null || !_hasTarget) return Fail("请先在地图上选点。");
            if (UnityEngine.Time.realtimeSinceStartup - _lastSpawn < 0.8f) return Fail("请稍候再触发下一场灾难。");
            if (_pending != null) return Fail("上一场灾难正在初始化，请稍候。");
            bool waterDisaster = type.Kind == "flood" || type.Kind == "tsunami";
            float3 position = _position; int direction = _direction;
            if (waterDisaster && !World.GetOrCreateSystemManaged<WaterDisasterSystem>().TryResolveTarget(type.Kind == "flood" ? 10 : 11, _position, out position, out direction))
                return Fail(type.Kind == "tsunami" ? "这里离海太远，请点靠近海岸的位置。" : "这里附近没有水，请点靠近河流、湖泊或海岸的位置。");
            if (ControlledEvents().Count >= 8) return Fail("本 Mod 最多同时运行 8 场灾难；请先停止现有灾害。");
            if ((type.Kind == "building" || type.Kind == "forest") && ResolveTarget(_target, type.Kind) == Entity.Null)
                return Fail("目标已失效，请重新选点。");
            if ((type.Kind == "building" || type.Kind == "forest") && EntityManager.HasComponent<OnFire>(_target)) return Fail("目标已经着火，请选择其他位置。");
            _pending = new SpawnRequest { Type = type, Level = _level, Seconds = FixedSeconds(type.Kind), Target = _target, Position = position,
                Warning = type.Kind == "flood" ? 0 : type.Kind == "meteor" ? DisasterVisualRules.MeteorLead(_warning, _simulation.selectedSpeed) : _warning,
                Citywide = !waterDisaster && _citywide, Direction = direction };
            _lastSpawn = UnityEngine.Time.realtimeSinceStartup;
            _hasTarget = false;
            if (!_repeat) PointTool.Finish();
            Info("正在初始化“" + type.Name + "”…");
            Refresh(); return true;
        }
        internal void ApplyQueuedSpawn()
        {
            var request = _pending;
            if (request == null) return;
            _pending = null;
            if (!InGame) return;
            var type = request.Type;
            if ((type.Kind == "building" || type.Kind == "forest") && ResolveTarget(request.Target, type.Kind) == Entity.Null) { Fail("目标已失效，请重新选点。"); return; }
            var variant = type.Levels[request.Level - 1];
            if (!_prefabs.TryGetEntity(variant, out Entity prefab) || !EntityManager.HasComponent<EventData>(prefab)) { Fail("灾难正在初始化，请稍候。"); return; }
            Entity result = Entity.Null;
            try
            {
                using (var created = new NativeArray<Entity>(1, Allocator.Temp))
                {
                    EntityManager.CreateEntity(EntityManager.GetComponentData<EventData>(prefab).m_Archetype, created);
                    result = created[0];
                }
                EntityManager.SetComponentData(result, new PrefabRef(prefab));
                if (type.Kind == "weather")
                {
                    float3 p = request.Position;
                    if (p.Equals(default(float3))) p.y = 0.001f; // Native initialization interprets the zero vector as random positioning.
                    var data = EntityManager.GetComponentData<WeatherPhenomenonData>(prefab);
                    float radius = (data.m_PhenomenonRadius.min + data.m_PhenomenonRadius.max) * 0.5f;
                    EntityManager.SetComponentData(result, new Game.Events.WeatherPhenomenon
                    {
                        m_PhenomenonPosition = p, m_HotspotPosition = p,
                        m_PhenomenonRadius = radius,
                        m_HotspotRadius = radius * (data.m_HotspotRadius.min + data.m_HotspotRadius.max) * 0.5f,
                        m_LightningTimer = type.Id == "Lightning Strike" ? 1f : 0f
                    });
                    uint frame = math.max(1u, _simulation.frameIndex + 1u);
                    EntityManager.SetComponentData(result, new Duration { m_StartFrame = frame, m_EndFrame = frame + (uint)(request.Seconds * 60) });
                }
                else if (type.Kind == "building" || type.Kind == "forest") EntityManager.GetBuffer<TargetElement>(result).Add(new TargetElement(request.Target));
                else
                {
                    int kind = type.Kind == "earthquake" ? 1 : type.Kind == "meteor" ? 2 : type.Kind == "sinkhole" ? 3 : type.Kind == "flood" ? 10 : 11;
                    uint start = _simulation.frameIndex;
                    uint impact = start + (uint)(request.Warning * 60);
                    var custom = new CustomDisasterState { Kind = kind, Level = request.Level, Citywide = request.Citywide ? 1 : 0, Position = request.Position, ImpactFrame = impact, NextPulse = impact,
                        Radius = DisasterRules.Radius(kind, request.Level), Depth = DisasterRules.Depth(kind, request.Level) };
                    EntityManager.SetComponentData(result, new Duration { m_StartFrame = start, m_EndFrame = impact + (uint)(request.Seconds * 60) + (uint)(kind == 11 ? WaterLevelChangeSystem.TsunamiEndDelay : 0) });
                    EntityManager.SetComponentData(result, new Game.Events.DangerLevel { m_DangerLevel = 1f });
                    if (EntityManager.HasComponent<CustomDisasterState>(result)) EntityManager.SetComponentData(result, custom);
                    else EntityManager.AddComponentData(result, custom);
                    if (EntityManager.HasComponent<Game.Objects.Transform>(result)) EntityManager.SetComponentData(result, new Game.Objects.Transform(request.Position, quaternion.identity));
                    if (kind >= 10)
                    {
                        if(!World.GetOrCreateSystemManaged<WaterSystem>().UseLegacyWaterSources)
                        {
                            var waterData=EntityManager.GetComponentData<WaterLevelChangeData>(prefab);
                            waterData.m_DangerFlags=(DangerFlags)0;EntityManager.SetComponentData(prefab,waterData);
                        }
                        float angle = math.radians(request.Direction);
                        var water = new WaterLevelChange { m_Direction = new float2(math.sin(angle), math.cos(angle)), m_DangerHeight = request.Position.y + WaterDisasterRules.PeakHeight(kind, request.Level), m_MaxIntensity = kind == 11 ? request.Level / 5f : request.Level * .2f };
                        EntityManager.SetComponentData(result, water);
                        custom.Radius = request.Direction; custom.Depth = request.Seconds;
                        EntityManager.SetComponentData(result, custom);
                    }
                }
                if (type.Kind == "weather" && request.Citywide)
                    EntityManager.AddComponentData(result, new CustomDisasterState { Kind = 0, Level = request.Level, Citywide = 1 });
                _lastSpawn = UnityEngine.Time.realtimeSinceStartup;
                _hasTarget = false; // A second click must never repeat the same operation.
                Info("已触发“" + type.Name + "”" + (_repeat ? "，可继续点击地图。" : "。"));
                Mod.Log.Info("Created " + variant.name + " event=" + result.Index + ":" + result.Version + " at=" + request.Position);
                Refresh();
            }
            catch (Exception ex)
            {
                if (result != Entity.Null && EntityManager.Exists(result)) EntityManager.DestroyEntity(result);
                Mod.Log.Error(ex);
                Fail("触发失败，详情已写入 Mod 日志。");
            }
        }
        // Duration is no longer a panel choice: each kind runs for a length that fits its animation.
        // Tsunami needs time for the wave wall to travel in from offshore.
        internal static int FixedSeconds(string kind) => kind == "tsunami" ? 150 : kind == "flood" ? 120 : 60;
        private bool Fail(string message) { _message = message; _messageIsError = true; _messageAt = UnityEngine.Time.realtimeSinceStartup; Refresh(); return false; }
        // Success notes clear themselves after a few seconds; errors stay until the next action.
        private void Info(string message) { _message = message; _messageIsError = false; _messageAt = UnityEngine.Time.realtimeSinceStartup; Refresh(); }
        private Entity FindControlled(string id)
        {
            foreach (var e in ControlledEvents()) if (e.Index + ":" + e.Version == id) return e;
            return Entity.Null;
        }
        private void Focus(string id)
        {
            var e = FindControlled(id);
            if (e == Entity.Null) return;
            float3 p = default; bool found = false;
            if (EntityManager.HasComponent<CustomDisasterState>(e)) { p = EntityManager.GetComponentData<CustomDisasterState>(e).Position; found = !p.Equals(default(float3)); }
            if (!found && EntityManager.HasComponent<Game.Events.WeatherPhenomenon>(e)) { p = EntityManager.GetComponentData<Game.Events.WeatherPhenomenon>(e).m_PhenomenonPosition; found = true; }
            if (!found && EntityManager.HasBuffer<TargetElement>(e))
                foreach (var t in EntityManager.GetBuffer<TargetElement>(e))
                    if (EntityManager.HasComponent<Game.Objects.Transform>(t.m_Entity)) { p = EntityManager.GetComponentData<Game.Objects.Transform>(t.m_Entity).m_Position; found = true; break; }
            if (!found) return;
            var controller = World.GetOrCreateSystemManaged<Game.Rendering.CameraUpdateSystem>().activeCameraController;
            if (controller != null) controller.pivot = p;
        }
        private void StopOne(string id)
        {
            var e = FindControlled(id);
            if (e == Entity.Null || !InGame) return;
            World.GetOrCreateSystemManaged<DisasterVisualSystem>().Cancel(e);
            StopEvent(e); Extinguish();
            Info("已停止这场灾害，已造成的损伤不会恢复。");
        }
        internal List<Entity> ControlledEvents()
        {
            var result = new List<Entity>();
            using (var entities = _events.ToEntityArray(Allocator.Temp))
            foreach (Entity e in entities)
            {
                var p = _prefabs.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab);
                if (p != null && p.name.StartsWith(DisasterCatalogSystem.Prefix, StringComparison.Ordinal)) result.Add(e);
            }
            return result;
        }
        internal void WaterSourceUnavailable()
        {
            _message="这里没有足够开阔的连续水面，无法安全造水；请点更开阔的水面。";Refresh();
        }
        internal int Stop()
        {
            if (!InGame) return 0;
            bool queued = _pending != null;
            _pending = null;
            World.GetOrCreateSystemManaged<DisasterVisualSystem>().CancelAll();
            var events = ControlledEvents();
            foreach (Entity e in events)
            {
                World.GetOrCreateSystemManaged<DisasterVisualSystem>().Cancel(e);
                StopEvent(e);
            }
            Extinguish();
            _messageIsError = false; _messageAt = UnityEngine.Time.realtimeSinceStartup;
            _message = events.Count > 0 || queued ? "已请求停止 " + (events.Count + (queued ? 1 : 0)) + " 场灾害。暂停时需要恢复模拟；已造成的损伤不会恢复。" : "当前没有本 Mod 触发的灾害。";
            Mod.Log.Info("Stop requested for " + events.Count + " controlled events");
            Refresh(); return events.Count;
        }
        private void StopEvent(Entity e)
        {
            if (EntityManager.HasComponent<CustomDisasterState>(e))
            {
                var custom = EntityManager.GetComponentData<CustomDisasterState>(e);
                var d = EntityManager.GetComponentData<Duration>(e);
                if (custom.Kind == 11)
                {
                    var water = EntityManager.GetComponentData<WaterLevelChange>(e);
                    water.m_MaxIntensity = water.m_Intensity = 0;
                    EntityManager.SetComponentData(e, water);
                    custom.Phase = 2; custom.Citywide = 0;
                    EntityManager.SetComponentData(e, custom);
                    d.m_EndFrame = math.min(d.m_EndFrame, _simulation.frameIndex + (uint)WaterLevelChangeSystem.TsunamiEndDelay);
                }
                else d.m_EndFrame = _simulation.frameIndex;
                d.m_StartFrame = math.min(d.m_StartFrame, d.m_EndFrame);
                EntityManager.SetComponentData(e, d);
            }
            if (EntityManager.HasComponent<Game.Events.WeatherPhenomenon>(e))
            {
                var duration = EntityManager.GetComponentData<Duration>(e);
                duration.m_EndFrame = _simulation.frameIndex;
                duration.m_StartFrame = math.min(duration.m_StartFrame, duration.m_EndFrame);
                EntityManager.SetComponentData(e, duration);
            }
            if (EntityManager.HasComponent<Game.Events.Fire>(e)) _stopping.Add(e);
            // A created event has not ignited yet. Cancel it before the native initialization stage.
            if (EntityManager.HasComponent<Created>(e)) EntityManager.AddComponent<Deleted>(e);
        }
        private void Extinguish()
        {
            if (_stopping.Count == 0) return;
            _stopping.RemoveWhere(e => !EntityManager.Exists(e) || EntityManager.HasComponent<Deleted>(e));
            using (var entities = _burning.ToEntityArray(Allocator.Temp))
            foreach (Entity e in entities)
            {
                var fire = EntityManager.GetComponentData<OnFire>(e);
                if (_stopping.Contains(fire.m_Event)) { fire.m_Intensity = 0f; EntityManager.SetComponentData(e, fire); }
            }
        }
        internal object Snapshot()
        {
            var type = _catalog.Find(_selected);
            var active = new List<object>();
            foreach (Entity e in ControlledEvents())
            {
                var prefab = _prefabs.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab);
                var t = _catalog.Types.Find(x => prefab.name.StartsWith(DisasterCatalogSystem.Prefix + x.Id + ".L", StringComparison.Ordinal));
                object weather = null;
                float remaining = -1f;
                if (EntityManager.HasComponent<Duration>(e))
                {
                    var d = EntityManager.GetComponentData<Duration>(e);
                    remaining = d.m_EndFrame > _simulation.frameIndex ? (d.m_EndFrame - _simulation.frameIndex) / 60f : 0f;
                }
                if (EntityManager.HasComponent<Game.Events.WeatherPhenomenon>(e))
                {
                    var w = EntityManager.GetComponentData<Game.Events.WeatherPhenomenon>(e);
                    weather = new { x = w.m_PhenomenonPosition.x, y = w.m_PhenomenonPosition.y, z = w.m_PhenomenonPosition.z, radius = w.m_PhenomenonRadius };
                }
                object custom = null;
                if (EntityManager.HasComponent<CustomDisasterState>(e))
                {
                    var c = EntityManager.GetComponentData<CustomDisasterState>(e);
                    custom = new { kind = c.Kind, phase = c.Phase, warningSeconds = c.ImpactFrame > _simulation.frameIndex ? math.ceil((c.ImpactFrame - _simulation.frameIndex) / 60f) : 0,
                        waveSeconds = c.Kind == 11 ? Math.Ceiling(Math.Max(0d, (double)c.ImpactFrame + c.Depth * 60d - _simulation.frameIndex) / 60d) : 0,
                        damaged = c.Damaged, destroyed = c.Destroyed, warned = c.Warned, terrainApplied = c.TerrainApplied, radius = c.Radius, depth = c.Depth };
                }
                int level = 0; int dot = prefab.name.LastIndexOf(".L", StringComparison.Ordinal);
                if (EntityManager.HasComponent<CustomDisasterState>(e)) level = EntityManager.GetComponentData<CustomDisasterState>(e).Level;
                else if (dot >= 0) int.TryParse(prefab.name.Substring(dot + 2), out level);
                active.Add(new { id = e.Index, version = e.Version, name = t?.Name ?? "灾难", level, variant = prefab.name, seconds = math.ceil(remaining), weather, custom });
            }
            return new
            {
                inGame = InGame, open = Open, picking = Picking, repeat = _repeat, selected = _selected, level = _level, seconds = _seconds, warning = _warning, citywide = _citywide, direction = _direction,
                message = _message, frame = _simulation.frameIndex,
                types = _catalog.Types.Where(t => t.Kind != "alert").Select(t => new { id = t.Id, name = t.Name, kind = t.Kind, nativeName = t.NativeName }).ToArray(),
                target = _hasTarget ? new { x = math.round(_position.x), z = math.round(_position.z), entity = _target.Index } : null,
                canSpawn = InGame && _hasTarget && type != null && !Picking && _pending == null,
                pending = _pending != null,
                active
            };
        }
        [Preserve]
        protected override void OnUpdate()
        {
            base.OnUpdate();
            if (InGame && Input.GetKeyDown(KeyCode.F9)) Toggle();
            if (InGame)
            {
                Entity city = World.GetOrCreateSystemManaged<CitySystem>().City;
                if (city != _city) { _city = city; _hasTarget = false; _target = Entity.Null; }
                Extinguish();
            }
            if (UnityEngine.Time.realtimeSinceStartup < _nextUpdate) return;
            _nextUpdate = UnityEngine.Time.realtimeSinceStartup + (Open ? 0.25f : 1f);
            try
            {
                var view = UIManager.defaultUISystem?.defaultUIView?.View;
                if (view == null) return;
                var currentView = UIManager.defaultUISystem.defaultUIView;
                if (currentView != _lastView) { _lastView = currentView; _mounted = false; }
                float now = UnityEngine.Time.realtimeSinceStartup;
                // Re-inject the whole panel script at most every 3 s while waiting for its ready signal.
                if (!_mounted && now >= _nextMount) { view.ExecuteScript(_script); _nextMount = now + 3f; }
                if (!_messageIsError && _message.Length > 0 && now - _messageAt > 5f) _message = "";
                // A closed panel only needs to know whether to show the launcher.
                object state = Open ? Snapshot() : new { inGame = InGame, open = false };
                view.ExecuteScript("window.__dcp && window.__dcp.update(" + JsonConvert.SerializeObject(state) + ")");
            }
            catch (Exception ex) { Mod.Log.Warn("Panel update: " + ex.Message); }
        }
    }
}
