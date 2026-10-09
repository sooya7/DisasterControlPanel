#if DCP_DEV
using System;
using System.Net;
using System.IO;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Assets;
using Game.Common;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Settings;
using Game.Tools;
using Colossal.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Scripting;
using Colossal.IO.AssetDatabase;
using System.Linq;

namespace DisasterControlPanel
{
    // Verification-only local interface. This entire type is absent from Release.
    public sealed class DevServerSystem : GameSystemBase
    {
        internal static DevServerSystem Instance;
        private HttpListener _listener;
        private readonly ConcurrentQueue<HttpListenerContext> _queue = new ConcurrentQueue<HttpListenerContext>();
        private DisasterUISystem _ui;
        private bool _test, _autoSave;
        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate(); Instance = this;
            _ui = World.GetOrCreateSystemManaged<DisasterUISystem>();
            _test = Array.Exists(Environment.GetCommandLineArgs(), s => s == "-dcp-test");
            if (_test) { _autoSave = SharedSettings.instance.general.autoSave; SharedSettings.instance.general.autoSave = false; Mod.Log.Info("Test session: autosave disabled in memory"); }
            _listener = new HttpListener(); _listener.Prefixes.Add("http://127.0.0.1:8644/");
            _listener.Start();
            var thread = new Thread(() => {
                while (_listener.IsListening)
                    try { _queue.Enqueue(_listener.GetContext()); } catch { break; }
            });
            thread.IsBackground = true; thread.Start();
            Mod.Log.Info("Debug interface on 127.0.0.1:8644");
        }
        internal void Close()
        {
            _listener?.Close();
            if (_test) SharedSettings.instance.general.autoSave = _autoSave;
        }
        [Preserve]
        protected override void OnDestroy() { Close(); Instance = null; base.OnDestroy(); }
        [Preserve]
        protected override void OnUpdate()
        {
            if (!_queue.TryDequeue(out var context)) return;
            object result;
            int status = 200;
            try
            {
                string path = context.Request.Url.AbsolutePath;
                if (path == "/state") result = _ui.Snapshot();
                else if (path == "/diagnostics" && _test) result = Diagnostics();
                else if (path == "/evacuation" && _test && _ui.InGame) result = Evacuation();
                else if (path == "/water" && _test && _ui.InGame) result = World.GetOrCreateSystemManaged<WaterDisasterSystem>().Diagnostics();
                else if (path == "/terrain" && _test && _ui.InGame)
                {
                    var terrain = World.GetOrCreateSystemManaged<Game.Simulation.TerrainSystem>();
                    var data = terrain.GetHeightData(true);
                    var p = new float3(float.Parse(context.Request.QueryString["x"], System.Globalization.CultureInfo.InvariantCulture), 0, float.Parse(context.Request.QueryString["z"], System.Globalization.CultureInfo.InvariantCulture));
                    result = new { height = Game.Simulation.TerrainUtils.SampleHeight(ref data, p), sizeX = terrain.playableArea.x, sizeZ = terrain.playableArea.y, minX = terrain.playableOffset.x, minZ = terrain.playableOffset.y };
                }
                else if (path == "/inspect" && _test) result = Inspect(new Entity { Index = int.Parse(context.Request.QueryString["index"]), Version = int.Parse(context.Request.QueryString["version"]) });
                else if (path == "/saves" && _test) result = AssetDatabase.global.GetAssets(default(SearchFilter<SaveGameMetadata>)).Where(a => SafeTestSave(a.ToString())).Select(a => new { id = a.id.guid.ToString(), name = a.ToString(), population = a.target.population }).ToArray();
                else if (path == "/targets" && _ui.InGame) result = Targets(context.Request.QueryString["kind"]);
                else if (path == "/command" && context.Request.HttpMethod == "POST" && _test)
                {
                    JObject body;
                    using (var reader = new System.IO.StreamReader(context.Request.InputStream)) body = JObject.Parse(reader.ReadToEnd());
                    switch ((string)body["action"])
                    {
                        case "loadFixture":
                            string fixture = (string)body["name"];
                            if (!SafeTestSave(fixture)) throw new Exception("Not a whitelisted test fixture");
                            var fixtureSave = AssetDatabase.global.GetAssets(default(SearchFilter<SaveGameMetadata>)).FirstOrDefault(a => a.ToString().Contains(fixture));
                            if (fixtureSave == null) throw new Exception("Test fixture not found");
                            GameManager.instance.Load(GameMode.Game, Colossal.Serialization.Entities.Purpose.LoadGame, fixtureSave);
                            break;
                        case "loadTest":
                            if (GameManager.instance.gameMode != GameMode.MainMenu) throw new Exception("Load allowed only from MainMenu");
                            var save = AssetDatabase.global.GetAssets(default(SearchFilter<SaveGameMetadata>)).FirstOrDefault(a => a.ToString().Contains("CS2MCP-LiveTest-20260917-070704"));
                            if (save == null) throw new Exception("Test save not found");
                            GameManager.instance.Load(GameMode.Game, Colossal.Serialization.Entities.Purpose.LoadGame, save);
                            break;
                        case "toggle": _ui.Toggle(); break;
                        case "quit": UnityEngine.Application.Quit(); break;
                        case "select": _ui.Select((string)body["id"]); break;
                        case "pick": _ui.BeginPick(); break;
                        case "target":
                            int index = (int?)body["index"] ?? 0, version = (int?)body["version"] ?? 0;
                            _ui.SetTarget(new Entity { Index = index, Version = version }, new float3((float?)body["x"] ?? 0, (float?)body["y"] ?? 0, (float?)body["z"] ?? 0));
                            break;
                        case "spawn": _ui.Spawn(); break;
                        case "stop": _ui.Stop(); break;
                        case "evacuate": World.GetOrCreateSystemManaged<CustomDisasterSystem>().RequestAlert(); break;
                        case "release": World.GetOrCreateSystemManaged<CustomDisasterSystem>().RequestRelease(); break;
                        case "ui":
                            // Exercises the actual mounted panel's controls, rather than a separate mock.
                            string selector = (string)body["selector"];
                            string js = "(function(){var e=document.querySelector(" + JsonConvert.SerializeObject(selector) + ");if(e){";
                            if (body["value"] != null) js += "e.value=" + JsonConvert.SerializeObject((string)body["value"]) + ";e.dispatchEvent(new Event('change',{bubbles:true}));";
                            else js += "e.dispatchEvent(new Event('click',{bubbles:true}));";
                            UIManager.defaultUISystem.defaultUIView.View.ExecuteScript(js + "}})()");
                            break;
                        default: throw new Exception("Unknown action");
                    }
                    result = _ui.Snapshot();
                }
                else { status = 404; result = new { error = "Unavailable" }; }
            }
            catch (Exception ex)
            {
                status = 500;
                result = new { error = ex.ToString() };
                Mod.Log.Error(ex);
            }
            // A timed-out client can disconnect while its request waits for UIUpdate.
            // Send once: a write failure must never try to change committed headers.
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(result));
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is HttpListenerException || ex is IOException || ex is ObjectDisposedException)
            {
                Mod.Log.Warn("Debug response disconnected: " + ex.Message);
            }
            finally
            {
                try { context.Response.Close(); }
                catch (Exception ex) when (ex is HttpListenerException || ex is IOException || ex is ObjectDisposedException)
                { Mod.Log.Warn("Debug response close: " + ex.Message); }
            }
        }
        private static bool SafeTestSave(string name) => name != null && (name.Contains("CS2MCP-LiveTest-20260917-070704") || name.Contains("ATL-P0-clean-verified-20260923") || name.StartsWith("DCP-test-", StringComparison.Ordinal));
        private object Inspect(Entity e)
        {
            if (!EntityManager.Exists(e)) return new { exists = false };
            bool burning = EntityManager.HasComponent<Game.Events.OnFire>(e);
            var fire = burning ? EntityManager.GetComponentData<Game.Events.OnFire>(e) : default;
            object water = null;
            if (EntityManager.HasComponent<Game.Events.WaterLevelChange>(e)) { var w = EntityManager.GetComponentData<Game.Events.WaterLevelChange>(e); water = new { intensity = w.m_Intensity, maximum = w.m_MaxIntensity, x = w.m_Direction.x, z = w.m_Direction.y }; }
            object weather = null;
            if (EntityManager.HasComponent<Game.Events.WeatherPhenomenon>(e)) { var w = EntityManager.GetComponentData<Game.Events.WeatherPhenomenon>(e); weather = new { intensity = w.m_Intensity, lightningTimer = w.m_LightningTimer, x = w.m_HotspotPosition.x, y = w.m_HotspotPosition.y, z = w.m_HotspotPosition.z }; }
            float damage = EntityManager.HasComponent<Game.Objects.Damaged>(e) ? EntityManager.GetComponentData<Game.Objects.Damaged>(e).m_Damage.x : 0;
            return new { exists = true, burning, intensity = fire.m_Intensity, fireEvent = fire.m_Event.Index,
                damage, water, weather,
                created = EntityManager.HasComponent<Created>(e),
                destroyed = EntityManager.HasComponent<Destroyed>(e),
                hotspotFrames = EntityManager.HasBuffer<Game.Events.HotspotFrame>(e) ? EntityManager.GetBuffer<Game.Events.HotspotFrame>(e).Length : 0 };
        }
        private object Evacuation()
        {
            int endangered = 0, controlled = 0, toShelter = 0, sheltered = 0, capacity = 0, occupants = 0, shelters = 0, buses = 0;
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Events.InDanger>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (var e in entities) { endangered++; var source = EntityManager.GetComponentData<Game.Events.InDanger>(e).m_Event; if (EntityManager.Exists(source) && EntityManager.HasComponent<CustomDisasterState>(source)) controlled++; }
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Citizens.Citizen>(), ComponentType.Exclude<Deleted>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (var e in entities)
                {
                    if (EntityManager.HasComponent<Game.Citizens.TravelPurpose>(e)) { var p = EntityManager.GetComponentData<Game.Citizens.TravelPurpose>(e).m_Purpose; if (p == Game.Citizens.Purpose.InEmergencyShelter) sheltered++; if (p == Game.Citizens.Purpose.EmergencyShelter) toShelter++; }
                    else if (EntityManager.HasBuffer<Game.Citizens.TripNeeded>(e)) { var trips = EntityManager.GetBuffer<Game.Citizens.TripNeeded>(e); if (trips.Length > 0 && trips[0].m_Purpose == Game.Citizens.Purpose.EmergencyShelter) toShelter++; }
                }
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Buildings.EmergencyShelter>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Deleted>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (var e in entities)
                {
                    shelters++;
                    var p = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                    if (EntityManager.HasComponent<EmergencyShelterData>(p)) capacity += EntityManager.GetComponentData<EmergencyShelterData>(p).m_ShelterCapacity;
                    if (EntityManager.HasBuffer<Game.Buildings.Occupant>(e)) occupants += EntityManager.GetBuffer<Game.Buildings.Occupant>(e).Length;
                }
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Vehicles.PublicTransport>(), ComponentType.Exclude<Deleted>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (var e in entities) if ((EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(e).m_State & Game.Vehicles.PublicTransportFlags.Evacuating) != 0) buses++;
            var custom = World.GetOrCreateSystemManaged<CustomDisasterSystem>();
            var visual = World.GetOrCreateSystemManaged<DisasterVisualSystem>();
            var crater = World.GetOrCreateSystemManaged<CraterTerrainSystem>();
            var weatherEffects = World.GetOrCreateSystemManaged<WeatherEffectsSystem>();
            return new { endangered, controlled, toShelter, sheltered, capacity, occupants, shelters, buses, lastDamaged = custom.LastDamaged, lastDestroyed = custom.LastDestroyed, lastWarned = custom.LastWarned, lastTerrainApplied = custom.LastTerrainApplied, visualCount = visual.VisualCount, shaderAvailable = visual.ShaderAvailable,
                craterEnabled = crater.Enabled, craterUpdates = crater.UpdateCount, craterEvents = crater.EventCount,
                hailActive = weatherEffects.HailActive, hailAssetAvailable = weatherEffects.HailAssetAvailable, groundLightningCount = weatherEffects.GroundLightningCount };
        }
        private object Diagnostics()
        {
            var ps = World.GetOrCreateSystemManaged<PrefabSystem>();
            return World.GetOrCreateSystemManaged<DisasterCatalogSystem>().Types.Select(t => new {
                t.Id, levels = t.Levels.Select(v => {
                    ps.TryGetEntity(v, out Entity e);
                    object fire = null;
                    if (EntityManager.HasComponent<FireData>(e)) { var d = EntityManager.GetComponentData<FireData>(e); fire = new { target = d.m_RandomTargetType.ToString(), start = d.m_StartIntensity }; }
                    return new { name = v.name, fire };
                }).ToArray()
            }).ToArray();
        }
        private object Targets(string kind)
        {
            var rows = new List<object>();
            var component = kind == "forest" ? ComponentType.ReadOnly<Game.Objects.Tree>() : ComponentType.ReadOnly<Game.Buildings.Building>();
            using (var query = EntityManager.CreateEntityQuery(component, ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity e in entities)
                {
                    if (kind == "forest" && EntityManager.HasComponent<Owner>(e)) continue;
                    var p = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
                    if (math.lengthsq(p.xz) > 3000f * 3000f) continue;
                    rows.Add(new { index = e.Index, version = e.Version, x = p.x, y = p.y, z = p.z,
                        burning = EntityManager.HasComponent<Game.Events.OnFire>(e) });
                    if (rows.Count >= 12) break;
                }
            }
            return rows;
        }
    }
}
#endif
