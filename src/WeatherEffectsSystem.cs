using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Events;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;
using UnityEngine.VFX;

namespace DisasterControlPanel
{
    // Native weather simulation owns damage and targets. This adapter only drives native VFX.
    public sealed class WeatherEffectsSystem : GameSystemBase
    {
        private EntityQuery _events, _targets;
        private SimulationSystem _simulation;
        private PrefabSystem _prefabs;
        private CameraUpdateSystem _camera;
        private ClimateRenderSystem _climateRender;
        private TerrainSystem _terrain;
        private WindTextureSystem _wind;
        private VisualEffect _hail;
        private bool _hailing, _assetChecked;
        private sealed class HailField
        {
            public GameObject Root;public Mesh Mesh;
            public readonly Vector3[] Vertices=new Vector3[192*4];
        }
        private readonly Dictionary<Entity,HailField> _fields=new Dictionary<Entity,HailField>();
        private Material _hailMaterial;
        private readonly MaterialPropertyBlock _hailTint=new MaterialPropertyBlock();
        private readonly Dictionary<Entity, float> _lightningTimers = new Dictionary<Entity, float>();
        public bool HailActive => _hailing;
        public bool HailAssetAvailable => _hail != null && _hail.visualEffectAsset != null;
        public int GroundLightningCount { get; private set; }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            _simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            _prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            _camera = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            _climateRender = World.GetOrCreateSystemManaged<ClimateRenderSystem>();
            _terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            _wind = World.GetOrCreateSystemManaged<WindTextureSystem>();
            _events = GetEntityQuery(ComponentType.ReadOnly<Game.Events.WeatherPhenomenon>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Duration>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            _targets = GetEntityQuery(new EntityQueryDesc {
                Any = new[] { ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<Game.Objects.Tree>() },
                All = new[] { ComponentType.ReadOnly<Game.Objects.Transform>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Destroyed>() }
            });
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (!World.GetOrCreateSystemManaged<DisasterUISystem>().InGame) { Clear(); return; }
            float hailIntensity = 0f;
            var live = new HashSet<Entity>();
            var hailLive = new HashSet<Entity>();
            using (var events = _events.ToEntityArray(Allocator.Temp))
            foreach (var e in events)
            {
                var prefab = _prefabs.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab);
                if (prefab == null || !prefab.name.StartsWith(DisasterCatalogSystem.Prefix, System.StringComparison.Ordinal)) continue;
                var duration = EntityManager.GetComponentData<Duration>(e);
                if (_simulation.frameIndex < duration.m_StartFrame || _simulation.frameIndex >= duration.m_EndFrame) continue;
                var weather = EntityManager.GetComponentData<Game.Events.WeatherPhenomenon>(e);
                if (prefab.name.StartsWith(DisasterCatalogSystem.Prefix + "Hail Storm.", System.StringComparison.Ordinal) && _camera.activeViewer != null && weather.m_Intensity>0)
                {
                    float cameraDistance=math.distance(_camera.position.xz,weather.m_PhenomenonPosition.xz);
                    float3 direction=_camera.direction;
                    float distanceToGround=direction.y<-.001f ? math.max(0,(weather.m_PhenomenonPosition.y-_camera.position.y)/direction.y) : 0;
                    float3 viewed=_camera.position+direction*distanceToGround;
                    bool visible=DisasterVisualRules.HailVisible(cameraDistance,math.distance(viewed.xz,weather.m_PhenomenonPosition.xz),weather.m_PhenomenonRadius);
                    if(_camera.activeCamera!=null)visible|=GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(_camera.activeCamera),
                        new Bounds((Vector3)weather.m_PhenomenonPosition+Vector3.up*85,new Vector3(weather.m_PhenomenonRadius*2,340,weather.m_PhenomenonRadius*2)));
                    if(visible){hailLive.Add(e);UpdateHailField(e,weather);}
                    if(cameraDistance<weather.m_PhenomenonRadius)hailIntensity=math.max(hailIntensity,weather.m_Intensity);
                }
                if (!prefab.name.StartsWith(DisasterCatalogSystem.Prefix + "Lightning Strike.", System.StringComparison.Ordinal)) continue;
                live.Add(e);
                // A rising timer means the native simulator attempted a strike. Its VFX queue
                // is empty for bare ground; fill that gap without igniting or damaging anything.
                if (_camera.activeViewer != null && _lightningTimers.TryGetValue(e, out float previous) && weather.m_LightningTimer > previous + .001f && weather.m_Intensity > 0f && !HasLightningTarget(weather))
                {
                    var queue = _climateRender.GetLightningStrikeQueue(out var dependencies); dependencies.Complete();
                    queue.Enqueue(new LightningStrike { m_Position = weather.m_HotspotPosition });
                    GroundLightningCount++;
                }
                _lightningTimers[e] = weather.m_LightningTimer;
            }
            var stale = new List<Entity>();
            foreach (var e in _lightningTimers.Keys) if (!live.Contains(e)) stale.Add(e);
            foreach (var e in stale) _lightningTimers.Remove(e);
            stale.Clear();foreach(var e in _fields.Keys)if(!hailLive.Contains(e))stale.Add(e);
            foreach(var e in stale){Object.Destroy(_fields[e].Root);Object.Destroy(_fields[e].Mesh);_fields.Remove(e);}
            UpdateHail(hailIntensity);
        }

        private void UpdateHailField(Entity entity,Game.Events.WeatherPhenomenon weather)
        {
            if(_camera.activeCamera==null)return;
            if(!_fields.TryGetValue(entity,out var field))
            {
                if(_fields.Count>=8)return;
                if(_hailMaterial==null)
                {
                    var shader=Shader.Find("HDRP/Unlit");if(shader==null)return;
                    _hailMaterial=new Material(shader){name="DCP hail curtain",renderQueue=3000};
                    _hailMaterial.SetFloat("_SurfaceType",1);_hailMaterial.SetFloat("_BlendMode",0);
                    _hailMaterial.SetInt("_SrcBlend",(int)UnityEngine.Rendering.BlendMode.One);_hailMaterial.SetInt("_DstBlend",(int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    _hailMaterial.SetInt("_AlphaSrcBlend",(int)UnityEngine.Rendering.BlendMode.One);_hailMaterial.SetInt("_AlphaDstBlend",(int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    _hailMaterial.SetInt("_ZWrite",0);_hailMaterial.SetInt("_CullMode",0);_hailMaterial.SetInt("_ZTestDepthEqualForOpaque",(int)UnityEngine.Rendering.CompareFunction.LessEqual);
                    _hailMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");_hailMaterial.SetOverrideTag("RenderType","Transparent");
                    _hailMaterial.SetShaderPassEnabled("DepthForwardOnly",false);_hailMaterial.SetShaderPassEnabled("MotionVectors",false);
                    _hailMaterial.SetColor("_UnlitColor",new Color(.8f,.9f,1,.5f));
                }
                field=new HailField{Root=new GameObject("DCP world hail field"),Mesh=new Mesh{name="DCP falling hail and ground splashes"}};
                field.Mesh.MarkDynamic();field.Root.AddComponent<MeshFilter>().sharedMesh=field.Mesh;
                var renderer=field.Root.AddComponent<MeshRenderer>();renderer.sharedMaterial=_hailMaterial;renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
                var indices=new int[192*6];for(int i=0;i<192;i++){int k=i*6,v=i*4;indices[k]=v;indices[k+1]=v+1;indices[k+2]=v+2;indices[k+3]=v;indices[k+4]=v+2;indices[k+5]=v+3;}
                field.Mesh.vertices=field.Vertices;field.Mesh.triangles=indices;_fields.Add(entity,field);
            }
            var terrain=_terrain.GetHeightData();float time=_simulation.frameIndex/60f;
            Vector3 side=_camera.activeCamera.transform.right;float size=math.clamp(math.distance(_camera.position,weather.m_PhenomenonPosition)/1600,.3f,2.5f);
            for(int i=0;i<192;i++)
            {
                float angle=i*2.39996f,r=math.sqrt((i+.5f)/192)*weather.m_PhenomenonRadius;
                float3 p=weather.m_PhenomenonPosition+new float3(math.cos(angle)*r,0,math.sin(angle)*r);
                float half=WaterSystem.kMapSize*.5f-WaterSystem.kCellSize;p.xz=math.clamp(p.xz,-half,half);
                p.y=TerrainUtils.SampleHeight(ref terrain,p);float cycle=math.frac(time*.65f+i*.618f);
                Vector3 bottom=p+new float3(0,math.max(0,1-cycle/.88f)*170+.3f,0);
                Vector3 top=bottom+Vector3.up*(cycle<.88f ? 5+size*4 : .3f);
                Vector3 width=side*(cycle<.88f ? size*.45f : size*(1+(cycle-.88f)*20));
                int v=i*4;field.Vertices[v]=bottom-width;field.Vertices[v+1]=bottom+width;field.Vertices[v+2]=top+width;field.Vertices[v+3]=top-width;
            }
            field.Mesh.vertices=field.Vertices;field.Mesh.RecalculateBounds();
            _hailTint.SetColor("_UnlitColor",new Color(.8f,.9f,1,.5f*math.saturate(weather.m_Intensity)));
            field.Root.GetComponent<MeshRenderer>().SetPropertyBlock(_hailTint);
        }

        private bool HasLightningTarget(Game.Events.WeatherPhenomenon weather)
        {
            using (var targets = _targets.ToEntityArray(Allocator.Temp))
            foreach (var e in targets)
                if (math.distance(EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position.xz, weather.m_HotspotPosition.xz) < weather.m_HotspotRadius) return true;
            return false;
        }

        private void UpdateHail(float intensity)
        {
            if (intensity > 0f && !_assetChecked)
            {
                _assetChecked = true;
                var asset = Resources.Load<VisualEffectAsset>("Precipitation/PrecipitationVFX");
                if (asset == null) { Mod.Log.Warn("Native hail precipitation VFX asset unavailable"); return; }
                _hail = new GameObject("DCP native hail VFX").AddComponent<VisualEffect>();
                _hail.visualEffectAsset = asset;
            }
            if (_hail == null) return;
            bool active = intensity > 0f;
            if (_hailing != active) { _hail.SendEvent(active ? "OnHailStart" : "OnHailStop"); _hailing = active; }
            if (_camera.activeViewer == null) return;
            _hail.SetVector3("CameraPosition", _camera.position);
            _hail.SetVector3("CameraDirection", _camera.direction);
            _hail.SetVector3("VolumeScale", Vector3.one * 30f);
            _hail.SetTexture("WindTexture", _wind.WindTexture);
            _hail.SetFloat("CloudsAltitude", 800f);
            _hail.SetVector4("MapOffsetScale", _terrain.mapOffsetScale);
            _hail.SetFloat("RainStrength", intensity);
            _hail.SetFloat("SnowStrength", intensity);
            _hail.pause = _simulation.selectedSpeed <= 0f;
            _hail.playRate = math.max(0f, _simulation.smoothSpeed);
        }

        private void Clear()
        {
            if (_hail != null) Object.Destroy(_hail.gameObject);
            _hail = null; _hailing = false; _assetChecked = false; _lightningTimers.Clear();
            foreach(var field in _fields.Values){Object.Destroy(field.Root);Object.Destroy(field.Mesh);}_fields.Clear();
            if(_hailMaterial!=null)Object.Destroy(_hailMaterial);_hailMaterial=null;
        }
        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        { Clear(); GroundLightningCount = 0; base.OnGamePreload(purpose, mode); }
        [Preserve]
        protected override void OnDestroy() { Clear(); base.OnDestroy(); }
    }
}
