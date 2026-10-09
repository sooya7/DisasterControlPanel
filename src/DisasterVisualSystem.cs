using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Events;
using Game.Rendering;
using Game.Simulation;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.VFX;

namespace DisasterControlPanel
{
    // Visuals never own gameplay. Independent pulse lifetimes pause with simulation;
    // post-impact animation is capped at 2x presentation speed for readability.
    public sealed class DisasterVisualSystem : GameSystemBase
    {
        private sealed class Visual
        {
            public GameObject Root, Meteor;
            public Light ImpactLight;
            public HDAdditionalLightData HdLight;
            public AudioSource Sound;
            public bool PlayedSound;
            public float Time, LastSimulationTime;
            public CustomDisasterState Snapshot;
            public Duration SnapshotDuration;
            public Vector3 Approach;
            public readonly List<Vector3> Landed = new List<Vector3>();
            public readonly List<bool> HasLanded = new List<bool>();
            public readonly List<float> LandingTimes = new List<float>();
            public NativeDisasterVfx GroundDust, Column, Flame, Explosion, Foam;
            public NativeDisasterVfx BigFire, Smoke, Embers, Sparks, EjectaTrail, QuakeWave, BuildingDust, CrackTip;
            public readonly List<LineRenderer> CrackEdges = new List<LineRenderer>();
            public readonly List<float> CrackShown = new List<float>();
            public readonly List<Vector3> BuildingPoints = new List<Vector3>();
            public readonly List<float> BuildingFalloff = new List<float>();
            public readonly List<Vector3> EjectaDir = new List<Vector3>();
            public readonly List<float> EjectaVertical = new List<float>();
            public MaterialPropertyBlock Block;
            public readonly List<GameObject> Fragments = new List<GameObject>();
            public readonly List<LineRenderer> Cracks = new List<LineRenderer>();
            public readonly List<Vector3[]> CrackPoints = new List<Vector3[]>();
            public readonly List<Vector3> DustGround = new List<Vector3>();
            public void Dispose()
            {
                GroundDust?.Dispose(); Column?.Dispose(); Flame?.Dispose(); Explosion?.Dispose(); Foam?.Dispose();
                BigFire?.Dispose(); Smoke?.Dispose(); Embers?.Dispose(); Sparks?.Dispose(); EjectaTrail?.Dispose();
                QuakeWave?.Dispose(); BuildingDust?.Dispose(); CrackTip?.Dispose();
                Object.Destroy(Root);
            }
        }
        private readonly Dictionary<Entity, Visual> _visuals = new Dictionary<Entity, Visual>();
        private readonly HashSet<Entity> _cancelled = new HashSet<Entity>();
        private EntityQuery _query;
        private SimulationSystem _simulation;
        private Material _orange, _dust, _crack, _crackEdge, _ejecta;
        private Mesh _mesh;
        private readonly List<Mesh> _rocks = new List<Mesh>();
        private AnimationCurve _crackTaper;
        private EntityQuery _buildings;
        private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
        private Texture2D _rockTexture;
        private AudioClip _impactSound, _quakeSound;
        private EntityQuery _vfxQuery;
        private WindTextureSystem _wind;
        private readonly Dictionary<string, VisualEffectAsset> _assets = new Dictionary<string, VisualEffectAsset>();
        private readonly HashSet<string> _missingAssets = new HashSet<string>();
        private WaterSystem _water;
        private TerrainSystem _terrain;
        public int VisualCount => _visuals.Count;
        public bool ShaderAvailable => _orange != null;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate(); _simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            _terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            _water = World.GetOrCreateSystemManaged<WaterSystem>();
            _wind = World.GetOrCreateSystemManaged<WindTextureSystem>();
            _vfxQuery = GetEntityQuery(ComponentType.ReadOnly<VFXData>());
            _query = GetEntityQuery(ComponentType.ReadOnly<CustomDisasterState>(), ComponentType.ReadOnly<Duration>(), ComponentType.Exclude<Deleted>());
            _buildings = GetEntityQuery(ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<Game.Objects.Transform>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Game.Tools.Temp>());
        }

        private void Materials()
        {
            if (_orange != null) return;
            Shader shader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color");
            if (shader == null) { Mod.Log.Warn("No supported disaster visual shader"); return; }
            _orange = new Material(Shader.Find("HDRP/Lit") ?? shader) { name = "DCP incandescent material" };
            _dust = new Material(Shader.Find("HDRP/Lit") ?? shader) { name = "DCP dust material" };
            SetColor(_orange, new Color(1f, .3f, .04f)); SetColor(_dust, new Color(.4f, .33f, .26f));
            _crack = new Material(shader) { name = "DCP ground fissure" }; SetColor(_crack, new Color(.02f,.015f,.012f));
            _crackEdge = new Material(shader) { name = "DCP fissure torn soil" }; SetColor(_crackEdge, new Color(.15f,.12f,.09f));
            _ejecta = new Material(Shader.Find("HDRP/Lit") ?? shader) { name = "DCP cooling ejecta" }; SetColor(_ejecta, new Color(.12f,.09f,.07f));
            _crackTaper = new AnimationCurve(new Keyframe(0,1), new Keyframe(.55f,.7f), new Keyframe(.85f,.35f), new Keyframe(1,.04f));
            _rockTexture=new Texture2D(128,64,TextureFormat.RGBA32,false) {name="DCP fractured hot rock",wrapMode=TextureWrapMode.Repeat};
            for(int y=0;y<64;y++)for(int x=0;x<128;x++)
            {
                float n=Mathf.PerlinNoise(x*.17f,y*.17f),vein=Mathf.Pow(Mathf.Clamp01(1-Mathf.Abs(n-.48f)*24),3);
                _rockTexture.SetPixel(x,y,Color.Lerp(new Color(.09f+n*.18f,.055f+n*.09f,.025f),new Color(1,.42f,.055f),vein));
            }
            _rockTexture.Apply(false,true);
            foreach(var rockMaterial in new[]{_orange,_dust})
            {
                if(rockMaterial.HasProperty("_BaseColorMap"))rockMaterial.SetTexture("_BaseColorMap",_rockTexture);
                if(rockMaterial.HasProperty("_Smoothness"))rockMaterial.SetFloat("_Smoothness",.12f);
            }
            if(_orange.HasProperty("_EmissiveColorMap")){_orange.SetTexture("_EmissiveColorMap",_rockTexture);_orange.EnableKeyword("_EMISSIVE_COLOR_MAP");}
            if(_dust.HasProperty("_EmissiveColor"))_dust.SetColor("_EmissiveColor",Color.black);
            if(_ejecta.HasProperty("_BaseColorMap"))_ejecta.SetTexture("_BaseColorMap",_rockTexture);
            // Exposure weight 0: emission is screen-relative, so the fireball and hot ejecta
            // glow the same at noon and at night instead of reading as a dull orange ball.
            foreach(var hot in new[]{_orange,_ejecta})
            {
                if(hot.HasProperty("_EmissiveExposureWeight"))hot.SetFloat("_EmissiveExposureWeight",0);
                if(hot.HasProperty("_Smoothness"))hot.SetFloat("_Smoothness",.08f);
            }
            if(_orange.HasProperty("_EmissiveColor"))_orange.SetColor("_EmissiveColor",new Color(1f,.55f,.2f)*7f);
            if(_ejecta.HasProperty("_EmissiveColor"))_ejecta.SetColor("_EmissiveColor",Color.black);
            for(int variant=0;variant<4;variant++)_rocks.Add(MakeRock(variant));
            if(_orange.HasProperty("_UnlitColorMap"))_orange.SetTexture("_UnlitColorMap",_rockTexture);
            _impactSound=MakeSound("DCP meteor impact",false);_quakeSound=MakeSound("DCP seismic rumble",true);
            _mesh = new Mesh { name = "DCP rock" };
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            var uv=new List<Vector2>();
            for (int row=0;row<=12;row++) for(int col=0;col<=24;col++)
            {
                float latitude=row*math.PI/12, longitude=col*math.PI*2/24;
                var p=new Vector3(math.sin(latitude)*math.cos(longitude),math.cos(latitude),math.sin(latitude)*math.sin(longitude));
                vertices.Add(p*(.85f+.15f*math.sin(p.x*13+p.y*7+p.z*11)));
                uv.Add(new Vector2(col/24f,row/12f));
                if(row<12 && col<24) {int i=row*25+col;triangles.AddRange(new[]{i,i+1,i+25,i+1,i+26,i+25});}
            }
            _mesh.vertices = vertices.ToArray(); _mesh.triangles = triangles.ToArray(); _mesh.uv=uv.ToArray();
            _mesh.RecalculateNormals(); _mesh.RecalculateBounds();
        }
        private NativeDisasterVfx Native(Visual visual, string name)
        {
            if (!_assets.TryGetValue(name, out var asset))
            {
                var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
                using (var entities = _vfxQuery.ToEntityArray(Allocator.Temp)) foreach (var entity in entities)
                {
                    var prefab = prefabs.GetPrefab<EffectPrefab>(entity);
                    var effect = prefab.GetComponent<VFX>().m_Effect;
                    if (effect != null) _assets[effect.name] = effect;
                }
                _assets.TryGetValue(name, out asset);
            }
            if (asset != null) return new NativeDisasterVfx(visual.Root.transform, asset, (uint)visual.Root.GetInstanceID());
            if (_missingAssets.Add(name)) Mod.Log.Warn("Native disaster visual asset unavailable: " + name);
            return null;
        }
        private static void SetColor(Material material, Color color)
        {
            material.color = color;
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", color);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", color);
        }
        // Faceted boulder: subdivided icosahedron, layered noise, random planar fractures,
        // flat shading. Replaces the smooth lumpy sphere that read as an old-game placeholder.
        private static Mesh MakeRock(int seed)
        {
            float t=(1+math.sqrt(5f))/2;
            var v=new List<Vector3>{new Vector3(-1,t,0),new Vector3(1,t,0),new Vector3(-1,-t,0),new Vector3(1,-t,0),new Vector3(0,-1,t),new Vector3(0,1,t),
                new Vector3(0,-1,-t),new Vector3(0,1,-t),new Vector3(t,0,-1),new Vector3(t,0,1),new Vector3(-t,0,-1),new Vector3(-t,0,1)};
            var f=new List<int>{0,11,5,0,5,1,0,1,7,0,7,10,0,10,11,1,5,9,5,11,4,11,10,2,10,7,6,7,1,8,3,9,4,3,4,2,3,2,6,3,6,8,3,8,9,4,9,5,2,4,11,6,2,10,8,6,7,9,8,1};
            for(int i=0;i<v.Count;i++)v[i]=v[i].normalized;
            for(int level=0;level<2;level++)
            {
                var cache=new Dictionary<long,int>();var next=new List<int>();
                int Mid(int a,int b){long key=a<b?((long)a<<32)|(uint)b:((long)b<<32)|(uint)a;if(cache.TryGetValue(key,out int m))return m;v.Add(((v[a]+v[b])*.5f).normalized);cache[key]=v.Count-1;return v.Count-1;}
                for(int i=0;i<f.Count;i+=3){int a=f[i],b=f[i+1],c=f[i+2],ab=Mid(a,b),bc=Mid(b,c),ca=Mid(c,a);next.AddRange(new[]{a,ab,ca,b,bc,ab,c,ca,bc,ab,bc,ca});}
                f=next;
            }
            var stretch=new Vector3(1+DisasterVfxRules.Hash(seed,61)*.5f,.75f+DisasterVfxRules.Hash(seed,62)*.3f,.85f+DisasterVfxRules.Hash(seed,63)*.4f);
            var cuts=new List<Vector4>();
            for(int c=0;c<7;c++)
            {
                float az=DisasterVfxRules.Hash(seed*13+c,71)*6.2831853f,el=(DisasterVfxRules.Hash(seed*13+c,73)-.5f)*3.1415926f;
                cuts.Add(new Vector4(math.cos(el)*math.cos(az),math.sin(el),math.cos(el)*math.sin(az),.62f+DisasterVfxRules.Hash(seed*13+c,79)*.3f));
            }
            for(int i=0;i<v.Count;i++)
            {
                var p=v[i];
                float n=Mathf.PerlinNoise(p.x*1.7f+seed*3.1f,p.z*1.7f+p.y*.9f)*.28f+Mathf.PerlinNoise(p.y*4.3f+seed,p.x*4.3f-p.z*2.1f)*.12f;
                p*=.82f+n;
                foreach(var c in cuts){var nrm=new Vector3(c.x,c.y,c.z);float d=Vector3.Dot(p,nrm);if(d>c.w)p-=nrm*(d-c.w);}
                v[i]=Vector3.Scale(p,stretch);
            }
            for(int i=0;i<f.Count;i+=3)
            {
                Vector3 a=v[f[i]],b=v[f[i+1]],c=v[f[i+2]];
                if(Vector3.Dot(Vector3.Cross(b-a,c-a),a+b+c)<0){int swap=f[i+1];f[i+1]=f[i+2];f[i+2]=swap;}
            }
            var verts=new Vector3[f.Count];var uv=new Vector2[f.Count];var tris=new int[f.Count];
            for(int i=0;i<f.Count;i++){verts[i]=v[f[i]];tris[i]=i;var d=v[f[i]].normalized;uv[i]=new Vector2(math.atan2(d.z,d.x)/(2*math.PI)+.5f,d.y*.5f+.5f);}
            var mesh=new Mesh{name="DCP fractured rock "+seed,vertices=verts,uv=uv,triangles=tris};
            mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
        }
        private GameObject Rock(Transform parent, Material material, int variant = -1)
        {
            var go = new GameObject("DCP rock"); go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = variant < 0 || _rocks.Count == 0 ? _rocks.Count > 0 ? _rocks[0] : _mesh : _rocks[variant % _rocks.Count];
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }
        private Visual Create(CustomDisasterState state)
        {
            var v = new Visual { Root = new GameObject("DCP disaster visuals"), Block = new MaterialPropertyBlock() };
            float bearing=math.frac(state.Position.x*.0131f+state.Position.z*.0173f)*math.PI*2;
            // Shallower entry (about 37 degrees) gives a long, readable streak across the sky.
            v.Approach=new Vector3(math.cos(bearing)*1250,950,math.sin(bearing)*1250);
            v.Sound=v.Root.AddComponent<AudioSource>();v.Sound.playOnAwake=false;v.Sound.spatialBlend=1;v.Sound.minDistance=150;v.Sound.maxDistance=7000;v.Sound.rolloffMode=AudioRolloffMode.Linear;v.Sound.dopplerLevel=0;
            v.Sound.clip=state.Kind==1 ? _quakeSound : state.Kind==2 ? _impactSound : null;v.Root.transform.position=state.Position;
            var lightObject=new GameObject("DCP impact illumination");lightObject.transform.SetParent(v.Root.transform,false);lightObject.transform.localPosition=Vector3.up*35;
            v.ImpactLight=lightObject.AddComponent<Light>();v.ImpactLight.type=UnityEngine.LightType.Point;v.ImpactLight.color=new Color(1,.55f,.2f);v.ImpactLight.range=650+state.Level*80;v.ImpactLight.enabled=false;
            v.HdLight=lightObject.AddComponent<HDAdditionalLightData>();
            v.Meteor = Rock(v.Root.transform, _orange, 0);
            v.GroundDust = Native(v, "DustcloudSmall");
            v.Column = Native(v, "Dustcloud");
            var terrain=_terrain.GetHeightData();
            if(state.Kind==2)
            {
                v.Flame = Native(v, "FireMovingMedium");
                v.Explosion = Native(v, "ExplosionTimed");
                v.BigFire = Native(v, "FireBig");
                v.Smoke = Native(v, "smokeFromFire");
                v.Embers = Native(v, "FireEmbers");
                v.Sparks = Native(v, "Sparks");
                v.EjectaTrail = Native(v, "DustcloudSmall");
                for(int i=0;i<36;i++)
                {
                    v.Fragments.Add(Rock(v.Root.transform,_ejecta,i));v.Landed.Add(Vector3.zero);v.HasLanded.Add(false);v.LandingTimes.Add(0);
                    DisasterVfxRules.Ejecta(i,state.Level,out float azimuth,out float horizontal,out float vertical);
                    v.EjectaDir.Add(new Vector3(math.cos(azimuth),0,math.sin(azimuth))*horizontal);v.EjectaVertical.Add(vertical);
                }
            }
            if(state.Kind==3) for(int i=0;i<36;i++)
            {
                v.Fragments.Add(Rock(v.Root.transform,_dust,i));v.Landed.Add(Vector3.zero);v.HasLanded.Add(false);v.LandingTimes.Add(0);
            }
            if(state.Kind==1)
            {
                v.QuakeWave = Native(v, "DustcloudSmall");
                v.BuildingDust = Native(v, "Dustcloud");
                v.CrackTip = Native(v, "DustcloudSmall");
                for(int i=0;i<24;i++)
                {
                    var crack=new GameObject("DCP ground fissure");crack.transform.SetParent(v.Root.transform,false);
                    // Lie flat on the ground instead of turning to face the camera.
                    crack.transform.rotation=Quaternion.Euler(-90,0,0);
                    var points=new Vector3[24];float a=DisasterVfxRules.CrackAngle(i);
                    Vector3 anchor=i<8 ? (Vector3)state.Position : v.CrackPoints[i%8][i/8*6];
                    for(int j=0;j<24;j++)
                    {
                        float r=j*state.Radius*DisasterVfxRules.CrackLength(i)*12f/23f;
                        float jag=(DisasterVfxRules.Hash(i*31+j,83)-.5f)*(4+state.Level*.8f)*math.min(1,j/3f);
                        float bend=math.sin(j*1.6f+i)*14*j/23f+jag;
                        float3 p=(float3)anchor+new float3(math.cos(a)*r+math.sin(a)*bend,0,math.sin(a)*r-math.cos(a)*bend);
                        p.y=TerrainUtils.SampleHeight(ref terrain,p)+.18f;points[j]=p;
                    }
                    var edgeObject=new GameObject("DCP fissure edge");edgeObject.transform.SetParent(crack.transform,false);
                    var edge=edgeObject.AddComponent<LineRenderer>();
                    var line=crack.AddComponent<LineRenderer>();
                    foreach(var lr in new[]{edge,line})
                    {
                        lr.useWorldSpace=true;lr.alignment=LineAlignment.TransformZ;lr.numCornerVertices=2;lr.numCapVertices=0;
                        lr.widthCurve=_crackTaper;lr.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;lr.receiveShadows=false;lr.positionCount=0;
                    }
                    line.sharedMaterial=_crack;edge.sharedMaterial=_crackEdge;
                    v.Cracks.Add(line);v.CrackEdges.Add(edge);v.CrackPoints.Add(points);v.CrackShown.Add(0);
                }
                for(int i=0;i<48;i++)
                    v.DustGround.Add(v.CrackPoints[i%24][4+(i/24)*8]);
                // Buildings that will shake: stratified by distance so dust comes from the
                // whole affected area, not only the centre.
                var near=new List<(float d,Vector3 p)>();
                using(var entities=_buildings.ToEntityArray(Allocator.Temp)) foreach(var b in entities)
                {
                    var pos=EntityManager.GetComponentData<Game.Objects.Transform>(b).m_Position;
                    float d=math.distance(pos.xz,state.Position.xz);
                    if(d<state.Radius)near.Add((d,pos));
                }
                near.Sort((x,y)=>x.d.CompareTo(y.d));
                int take=math.min(48,near.Count);
                for(int i=0;i<take;i++)
                {
                    var item=near[take==near.Count ? i : (int)((long)i*near.Count/take)];
                    v.BuildingPoints.Add(item.p);v.BuildingFalloff.Add(DisasterRules.Falloff(item.d,state.Radius));
                }
            }
            return v;
        }
        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        { Clear(); base.OnGamePreload(purpose, mode); }
        [Preserve]
        protected override void OnUpdate()
        {
            if (!World.GetOrCreateSystemManaged<DisasterUISystem>().InGame) { CameraShake.Clear(); if (_visuals.Count > 0) Clear(); return; }
            Materials(); if (_orange == null) return;
            CameraShake.Ensure(); CameraShake.Clear();
            var live = new HashSet<Entity>();
            var frames=new List<(Entity entity,CustomDisasterState state,Duration duration)>();
            var present=new HashSet<Entity>();
            using(var entities=_query.ToEntityArray(Allocator.Temp)) foreach(var e in entities)
            {
                if(_cancelled.Contains(e))continue;
                var duration=EntityManager.GetComponentData<Duration>(e);
                if(_simulation.frameIndex>=duration.m_EndFrame)continue;
                present.Add(e);frames.Add((e,EntityManager.GetComponentData<CustomDisasterState>(e),duration));
            }
            foreach(var pair in _visuals)
                if(DisasterVisualRules.KeepTail(present.Contains(pair.Key),_cancelled.Contains(pair.Key),pair.Value.Snapshot.Kind,
                    _simulation.frameIndex,pair.Value.SnapshotDuration.m_EndFrame,pair.Value.Time))
                    frames.Add((pair.Key,pair.Value.Snapshot,pair.Value.SnapshotDuration));
            foreach(var frame in frames)
            {
                var e=frame.entity;var s=frame.state;var duration=frame.duration;
                if(s.Kind>=10){UpdateWaterVisual(e,s,live);continue;}
                if (s.Kind < 1 || s.Kind > 3) continue;
                float simulationTime = ((long)_simulation.frameIndex - s.ImpactFrame) / 60f;
                float lead=math.max(3f,((long)s.ImpactFrame-duration.m_StartFrame)/60f);
                if(simulationTime < (s.Kind==2 ? -lead : 0))continue;
                if (!_visuals.TryGetValue(e, out var v))
                {
                    if(_visuals.Count>=8 || simulationTime>=24)continue;
                    v = Create(s);v.Time=simulationTime;v.LastSimulationTime=simulationTime;_visuals[e] = v;
                }
                v.Snapshot=s;v.SnapshotDuration=duration;
                float delta=math.max(0,simulationTime-v.LastSimulationTime);v.LastSimulationTime=simulationTime;
                float oldTime=v.Time;
                v.Time=simulationTime<0 ? simulationTime : v.Time<0 ? 0 : DisasterVisualRules.VisibleTime(v.Time,delta,UnityEngine.Time.unscaledDeltaTime);
                float t=v.Time, presentationDelta=math.max(0,t-oldTime);
                if(t>=24)continue;
                live.Add(e);
                bool paused=_simulation.selectedSpeed<=0;
                UpdateSound(v,s,t,paused);
                foreach(var fx in new[]{v.GroundDust,v.Column,v.Flame,v.Explosion,v.BigFire,v.Smoke,v.Embers,v.Sparks,v.EjectaTrail,v.QuakeWave,v.BuildingDust,v.CrackTip})fx?.Begin();
                Vector3 p = s.Position;
                var terrainDebris=_terrain.GetHeightData();
                float cameraFalloff=CameraFalloff(p,s.Radius);
                if (s.Kind == 2) UpdateMeteor(v,s,t,lead,p,ref terrainDebris,paused,cameraFalloff);
                else if (s.Kind == 1) UpdateQuake(v,s,t,p,presentationDelta,paused,cameraFalloff);
                else UpdateSinkhole(v,s,t,p,ref terrainDebris);
                Commit(v,presentationDelta);
            }
            var stale = new List<Entity>();
            foreach (var pair in _visuals) if (!live.Contains(pair.Key)) { pair.Value.Dispose(); stale.Add(pair.Key); }
            foreach (var e in stale) _visuals.Remove(e);
            stale.Clear();foreach(var e in _cancelled)if(!EntityManager.Exists(e)||EntityManager.HasComponent<Deleted>(e))stale.Add(e);
            foreach(var e in stale)_cancelled.Remove(e);
        }
        private static float CameraFalloff(Vector3 epicentre,float radius)
        {
            var camera=Camera.main;if(camera==null)return 1;
            var c=camera.transform.position;float d=new Vector2(c.x-epicentre.x,c.z-epicentre.z).magnitude/math.max(50,radius);
            return 1/(1+d*d);
        }
        private void UpdateMeteor(Visual v,CustomDisasterState s,float t,float lead,Vector3 p,ref TerrainHeightData terrain,bool paused,float cameraFalloff)
        {
            float flight=DisasterVisualRules.Flight(t,lead);
            v.Meteor.SetActive(t>=-lead && t<0);
            var head=p+v.Approach*(1-flight);
            var back=v.Approach.normalized;
            if(t<0)
            {
                v.Meteor.transform.position=head;
                v.Meteor.transform.localScale=new Vector3(1.15f,.9f,1)*(8+s.Level*3);
                v.Meteor.transform.rotation=Quaternion.Euler(t*90,t*40,t*70);
                float trail=DisasterVfxRules.TrailEmission(t,lead);
                // Plasma head and a tapering fire tail behind it.
                v.BigFire?.Emit(head+back*(4+s.Level),Vector3.one*(1.6f+s.Level*.28f),trail);
                for(int i=0;i<8;i++)
                    v.Flame?.Emit(head+back*i*(7+s.Level*1.2f),Vector3.one*(1.2f+s.Level*.18f)*(1-i*.09f),trail*(1-i*.1f));
                v.Sparks?.Emit(head+back*(6+s.Level),Vector3.one*(1+s.Level*.15f),trail*.8f);
            }
            // Smoke trail left in the sky: each point along the path puffs as the meteor
            // passes and the native smoke lingers and drifts on the wind afterwards.
            for(int k=0;k<20;k++)
            {
                float tk=-lead+lead*(k+.5f)/20f, since=t-tk;
                if(since<0 || since>.9f)continue;
                var at=p+v.Approach*(1-DisasterVisualRules.Flight(tk,lead));
                v.Smoke?.Emit(at,Vector3.one*(1.2f+s.Level*.22f),(1-since/.9f)*.8f);
            }
            // Light travels with the fireball, flashes on impact, then glows from the burning crater.
            v.ImpactLight.enabled=t>=-lead && t<12;
            if(v.ImpactLight.enabled)
            {
                if(t<0)
                {
                    v.ImpactLight.transform.position=head;v.ImpactLight.color=new Color(1,.72f,.45f);
                    v.HdLight.SetIntensity((50000+s.Level*15000)*math.sqrt(flight));
                }
                else
                {
                    v.ImpactLight.transform.position=p+Vector3.up*(35+s.Level*4);
                    float flash=DisasterVisualRules.Fade(t,1.2f);
                    v.ImpactLight.color=Color.Lerp(new Color(1,.4f,.12f),new Color(1,.85f,.7f),flash);
                    v.HdLight.SetIntensity((420000+s.Level*90000)*flash*flash+(30000+s.Level*6000)*DisasterVfxRules.CraterFire(t));
                }
            }
            float crater=DisasterRules.CraterRadius(2,s.Level);
            float burst=DisasterVfxRules.ImpactEmission(t);
            v.Explosion?.Emit(p+Vector3.up*3,Vector3.one*(2.2f+s.Level*.5f),burst);
            for(int i=0;i<6;i++)
            {
                float a=i*1.0471976f+.4f,r=crater*.45f;
                var at=p+new Vector3(math.cos(a)*r,0,math.sin(a)*r);at.y=TerrainUtils.SampleHeight(ref terrain,at)+2;
                v.Explosion?.Emit(at,Vector3.one*(1.4f+s.Level*.3f),DisasterVfxRules.ImpactEmission(t-.08f*(i%3)));
            }
            for(int i=0;i<10;i++)
                v.Sparks?.Emit(p+Vector3.up*(2+i%3*3),Vector3.one*(1.4f+s.Level*.25f),DisasterVfxRules.ImpactSparks(t),Vector3.up*i*36);
            float fire=DisasterVfxRules.CraterFire(t),smoke=DisasterVfxRules.ImpactSmoke(t);
            for(int i=0;i<10;i++)
            {
                float a=i*2.399963f,r=crater*(.2f+.75f*DisasterVfxRules.Hash(i,91));
                var at=p+new Vector3(math.cos(a)*r,0,math.sin(a)*r);at.y=TerrainUtils.SampleHeight(ref terrain,at)+.5f;
                if(i<6)v.BigFire?.Emit(at,Vector3.one*(1+s.Level*.15f)*(.6f+DisasterVfxRules.Hash(i,93)*.6f),fire*(.5f+DisasterVfxRules.Hash(i,95)*.5f));
                v.Embers?.Emit(at+Vector3.up*2,Vector3.one*(1.2f+s.Level*.15f),fire);
                if(i<6)v.Smoke?.Emit(at+Vector3.up*(4+i*2),Vector3.one*(1.6f+s.Level*.25f),smoke*.9f);
            }
            for(int i=0;i<24;i++)
            {
                var emitter=DisasterVfxRules.MeteorGround(i,t,s.Level);
                float3 pos=s.Position+new float3(emitter.X,0,emitter.Z); pos.y=TerrainUtils.SampleHeight(ref terrain,pos)+emitter.Y;
                v.GroundDust?.Emit(pos,new Vector3(emitter.Width,emitter.Height,emitter.Width)*1.25f,emitter.Intensity,Vector3.up*emitter.Yaw);
            }
            for(int i=0;i<8;i++)
            {
                var emitter=DisasterVfxRules.MeteorColumn(i,t,s.Level);
                v.Column?.Emit(p+new Vector3(emitter.X,emitter.Y,emitter.Z),new Vector3(emitter.Width,emitter.Height,emitter.Width)*1.2f,emitter.Intensity);
            }
            // Ejecta: faceted rocks thrown out glowing, cooling as they fly, trailing dust.
            for(int i=0;i<v.Fragments.Count;i++)
            {
                var fragment=v.Fragments[i];
                if(t<0 || t>=14){fragment.SetActive(false);continue;}
                fragment.SetActive(true);
                Vector3 next=p+Vector3.up*3+v.EjectaDir[i]*t+Vector3.up*(v.EjectaVertical[i]*t-4.9f*t*t);
                float groundY=TerrainUtils.SampleHeight(ref terrain,next);
                if(!v.HasLanded[i] && DisasterVisualRules.HitsGround(next.y,groundY,t))
                {v.HasLanded[i]=true;v.Landed[i]=new Vector3(next.x,groundY+.4f,next.z);v.LandingTimes[i]=t;}
                fragment.transform.position=v.HasLanded[i] ? v.Landed[i] : next;
                float size=(1.2f+s.Level*.35f)*(.35f+DisasterVfxRules.Hash(i,97)*DisasterVfxRules.Hash(i,98)*2.2f);
                fragment.transform.localScale=Vector3.one*size*math.min(1,(14-t)*.5f);
                if(!v.HasLanded[i])fragment.transform.rotation=Quaternion.Euler(t*(90+i*7),t*(60+i*3),t*45+i*19);
                float glow=DisasterVfxRules.EjectaGlow(t);
                v.Block.SetColor(EmissiveColorId,new Color(1,.5f,.16f)*glow);
                fragment.GetComponent<MeshRenderer>().SetPropertyBlock(v.Block);
                if(!v.HasLanded[i])v.EjectaTrail?.Emit(next,Vector3.one*(.35f+size*.12f),.55f);
                else v.GroundDust?.Emit(v.Landed[i],Vector3.one*(.5f+size*.1f),DisasterVfxRules.LandingEmission(t-v.LandingTimes[i])*.7f);
            }
            if(!paused)CameraShake.Request(DisasterVfxRules.MeteorShake(t,s.Level)*cameraFalloff,14);
        }
        // Sinkhole keeps its 0.2.0 debris behaviour; only the rock shape changed.
        private void UpdateSinkhole(Visual v,CustomDisasterState s,float t,Vector3 p,ref TerrainHeightData terrain)
        {
            v.Meteor.SetActive(false);v.ImpactLight.enabled=false;
            for(int i=0;i<v.Fragments.Count;i++)
            {
                var fragment=v.Fragments[i];float fade=DisasterVisualRules.DustOpacity(t,12f);fragment.SetActive(fade>0);
                if(fade<=0)continue;
                float a=i*2.39996f,r=t*(15+s.Level*4)*(1+i%4*.2f);
                Vector3 next=p+new Vector3(math.cos(a)*r,(30+s.Level*3)*t-9*t*t+3,math.sin(a)*r);
                float groundY=TerrainUtils.SampleHeight(ref terrain,next);
                if(!v.HasLanded[i] && DisasterVisualRules.HitsGround(next.y,groundY,t))
                {v.HasLanded[i]=true;v.Landed[i]=new Vector3(next.x,groundY+.8f,next.z);v.LandingTimes[i]=t;}
                fragment.transform.position=v.HasLanded[i] ? v.Landed[i] : next;
                fragment.transform.localScale=Vector3.one*(1+s.Level*.35f)*(1+i%3*.4f)*math.min(1,fade*4);
                if(!v.HasLanded[i])fragment.transform.rotation=Quaternion.Euler(t*130+i*19,t*80,t*65);
                if(v.HasLanded[i])v.GroundDust?.Emit(v.Landed[i],Vector3.one*.4f,DisasterVfxRules.LandingEmission(t-v.LandingTimes[i])*.6f);
            }
        }
        private void UpdateQuake(Visual v,CustomDisasterState s,float t,Vector3 p,float presentationDelta,bool paused,float cameraFalloff)
        {
            v.Meteor.SetActive(false);v.ImpactLight.enabled=false;
            float growth=DisasterVfxRules.CrackGrowth(t);
            float width=(1.2f+s.Level*.32f)*(.55f+.45f*growth)*DisasterVisualRules.CrackOpacity(t);
            for(int i=0;i<v.Cracks.Count;i++)
            {
                // Branches open once the parent fissure has torn past their junction.
                float start=i<8 ? 0 : (i/8*6)/23f, local=i<8 ? growth : math.saturate((growth-start)/(1-start));
                var points=v.CrackPoints[i];var line=v.Cracks[i];var edge=v.CrackEdges[i];
                float shown=local*23f;int full=(int)math.floor(shown);
                if(local<=0 || width<=0){line.positionCount=0;edge.positionCount=0;continue;}
                int count=math.min(24,full+2);
                line.positionCount=count;edge.positionCount=count;
                for(int j=0;j<count;j++)
                {
                    Vector3 q=j<=full ? points[math.min(j,23)] : Vector3.Lerp(points[math.min(full,23)],points[math.min(full+1,23)],shown-full);
                    line.SetPosition(j,q);edge.SetPosition(j,q-Vector3.up*.08f);
                }
                float branch=i<8 ? 1 : .6f;
                line.widthMultiplier=width*branch;edge.widthMultiplier=width*branch*2.6f;
                // Dust spurts from the tip while it is tearing.
                if(shown>v.CrackShown[i]+.01f && presentationDelta>0)
                {
                    var tip=line.GetPosition(count-1);
                    v.CrackTip?.Emit(tip+Vector3.up,new Vector3(.9f+s.Level*.15f,.6f,.9f+s.Level*.15f),.9f);
                }
                v.CrackShown[i]=shown;
            }
            for(int i=0;i<v.DustGround.Count;i++)
            {
                float d=Vector3.Distance(v.DustGround[i],p),intensity=0;
                for(int k=0;k<3;k++)intensity=math.max(intensity,DisasterVfxRules.QuakeEmission(t,k,d,s.Level));
                if(4+(i/24)*8>v.CrackShown[i%24])intensity=0;
                v.GroundDust?.Emit(v.DustGround[i]+Vector3.up,new Vector3(1.2f+s.Level*.2f,.7f,1.2f+s.Level*.2f),intensity);
                if(i%3==0)v.Column?.Emit(v.DustGround[i]+Vector3.up,Vector3.one*(.6f+s.Level*.1f),intensity*.3f);
            }
            // Visible surface wave: a low ring of dust racing outward with each pulse.
            var terrain=_terrain.GetHeightData();
            for(int k=0;k<3;k++)
            {
                float r=DisasterVfxRules.QuakeWaveRadius(t,k,s.Level,s.Radius);
                if(r<0)continue;
                for(int i=0;i<30;i++)
                {
                    float a=i*.2094395f+k*.1f;
                    float3 at=s.Position+new float3(math.cos(a)*r,0,math.sin(a)*r);at.y=TerrainUtils.SampleHeight(ref terrain,at)+.6f;
                    v.QuakeWave?.Emit(at,new Vector3(1.6f+s.Level*.25f,.35f,1.6f+s.Level*.25f),.55f*(1-r/s.Radius),Vector3.up*a*57.29578f);
                }
            }
            // Buildings shed dust as the wave reaches them.
            for(int i=0;i<v.BuildingPoints.Count;i++)
            {
                float d=Vector3.Distance(v.BuildingPoints[i],p);
                float intensity=DisasterVfxRules.BuildingDust(t,d,s.Level)*math.saturate(.25f+v.BuildingFalloff[i]);
                v.BuildingDust?.Emit(v.BuildingPoints[i]+Vector3.up*2,Vector3.one*(1.1f+s.Level*.14f),intensity*.75f);
            }
            if(!paused)CameraShake.Request(DisasterVfxRules.QuakeShake(t,s.Level)*cameraFalloff,8);
        }
        private void Commit(Visual visual,float delta)
        {
            var wind=_wind.WindTexture; var map=_terrain.mapOffsetScale;
            visual.GroundDust?.Commit(delta,wind,map); visual.Column?.Commit(delta,wind,map);
            visual.Flame?.Commit(delta,wind,map); visual.Explosion?.Commit(delta,wind,map); visual.Foam?.Commit(delta,wind,map);
            foreach(var fx in new[]{visual.BigFire,visual.Smoke,visual.Embers,visual.Sparks,visual.EjectaTrail,visual.QuakeWave,visual.BuildingDust,visual.CrackTip})fx?.Commit(delta,wind,map);
        }
        private AudioClip MakeSound(string name,bool quake)
        {
            const int rate=22050;int length=rate*(quake ? 10 : 4);var samples=new float[length];uint noise=127;
            float filtered=0;
            for(int i=0;i<length;i++)
            {
                noise=noise*1664525+1013904223;float n=((noise>>8)/(float)0xFFFFFF)*2-1;filtered=filtered*.97f+n*.03f;
                float t=i/(float)rate, envelope=quake ? math.saturate(t*3)*math.saturate((10-t)*.5f) : math.exp(-t*1.7f)*(1-math.exp(-t*80));
                float pulse=quake ? .55f+.45f*math.sin(t*2.1f)*math.sin(t*2.1f) : 1;
                samples[i]=math.clamp((filtered*2.5f+math.sin(t*(quake ? 160 : 110))*.13f+n*(quake ? .035f : .08f)*math.exp(-t*4))*envelope*pulse,-.6f,.6f);
            }
            var clip=AudioClip.Create(name,length,1,rate,false);clip.SetData(samples,0);return clip;
        }
        private void UpdateSound(Visual v,CustomDisasterState state,float time,bool paused)
        {
            if(v.Sound.clip==null)return;
            var settings=Game.Settings.SharedSettings.instance.audio;
            v.Sound.volume=.55f/math.sqrt(math.max(1,_visuals.Count))*settings.masterVolume*settings.ingameVolume*settings.disastersVolume;
            if(!v.PlayedSound && time>=0){v.PlayedSound=true;v.Sound.Play();}
            if(paused)v.Sound.Pause();else if(v.PlayedSound)v.Sound.UnPause();
        }
        private void UpdateWaterVisual(Entity e,CustomDisasterState s,HashSet<Entity> live)
        {
            live.Add(e);
            if(!_visuals.TryGetValue(e,out var v))
            {
                if(_visuals.Count>=8)return;
                v=new Visual {Root=new GameObject("DCP measured water foam")};
                v.Foam=Native(v,"BoatFoam01");v.LastSimulationTime=_simulation.frameIndex/60f;
                if(s.Kind==11)v.Column=Native(v,"WaterVaporHuge");
                _visuals[e]=v;
            }
            var surface=_water.GetSurfaceData(out var deps);deps.Complete();var terrain=_terrain.GetHeightData();
            float angle=math.radians(s.Radius);var forward=new float3(math.sin(angle),0,math.cos(angle));var right=new float3(forward.z,0,-forward.x);
            v.Foam?.Begin();v.Column?.Begin();
            // Tsunami: white water rides the moving crest of the wall, spray blows off its top.
            bool crestShown=false;
            if(s.Kind==11 && World.GetOrCreateSystemManaged<WaterDisasterSystem>().TryGetCrest(e,out var crest))
            {
                crestShown=true;int step=math.max(1,(crest.Count+47)/48);
                for(int i=0;i<crest.Count;i+=step)
                {
                    var c=crest[i];float3 at=new float3(c.x,0,c.z);
                    float depth=WaterUtils.SampleDepth(ref surface,at),ground=TerrainUtils.SampleHeight(ref terrain,at);
                    at.y=math.max(c.y,ground+depth+.4f);
                    float size=math.clamp(c.w/20f,.6f,3f);
                    v.Foam?.Emit(at,new Vector3(3+size*1.5f,1,2+size),.9f,Vector3.up*(s.Radius+90));
                    if((i/step)%3==0)v.Column?.Emit((Vector3)at+Vector3.up*(2+c.w*.15f),Vector3.one*(.8f+size*.5f),math.saturate(c.w/12f)*.8f);
                }
            }
            for(int i=0;i<(crestShown ? 0 : 32);i++)
            {
                float cross=(i%8-3.5f)/3.5f*(s.Kind==11 ? WaterDisasterRules.FrontHalfWidth(s.Level) : 200+s.Level*220);
                float along=math.lerp(-WaterDisasterRules.OffshoreDistance(s.Level)-350,1000,(i/8)/3f);
                float3 p=s.Position+right*cross+forward*along;
                float half=WaterSystem.kMapSize*.5f-35;
                if(math.abs(p.x)>half || math.abs(p.z)>half)continue;
                float depth=WaterUtils.SampleDepth(ref surface,p),ground=TerrainUtils.SampleHeight(ref terrain,p);
                float3 a=p+forward*28,b=p-forward*28;
                float da=WaterUtils.SampleDepth(ref surface,a),db=WaterUtils.SampleDepth(ref surface,b);
                float slope=math.abs((TerrainUtils.SampleHeight(ref terrain,a)+da)-(TerrainUtils.SampleHeight(ref terrain,b)+db))/56;
                float alpha=da>.5f && db>.5f ? DisasterVisualRules.FoamOpacity(depth,slope) : depth>.5f && depth<8 ? .25f : 0;
                p.y=ground+depth+.4f;
                v.Foam?.Emit(p,new Vector3(2+s.Level*.3f,1,1+s.Level*.1f),alpha,Vector3.up*s.Radius);
            }
            float simulationTime=_simulation.frameIndex/60f;
            Commit(v,math.min(math.max(0,simulationTime-v.LastSimulationTime),UnityEngine.Time.unscaledDeltaTime*2));
            v.LastSimulationTime=simulationTime;
        }
        internal void CancelAll() { foreach(var e in new List<Entity>(_visuals.Keys))Cancel(e); }
        internal void Cancel(Entity e)
        {
            _cancelled.Add(e);
            if(_visuals.TryGetValue(e,out var visual)){visual.Dispose();_visuals.Remove(e);}
        }
        private void Clear() { foreach (var v in _visuals.Values) v.Dispose(); _visuals.Clear(); _cancelled.Clear(); _assets.Clear(); _missingAssets.Clear(); }
        [Preserve]
        protected override void OnDestroy()
        {
            Clear();CameraShake.Shutdown();foreach(var material in new[]{_orange,_dust,_crack,_crackEdge,_ejecta})if(material!=null)Object.Destroy(material);
            foreach(var rock in _rocks)if(rock!=null)Object.Destroy(rock);_rocks.Clear();
            if(_mesh!=null)Object.Destroy(_mesh);if(_rockTexture!=null)Object.Destroy(_rockTexture);if(_impactSound!=null)Object.Destroy(_impactSound);if(_quakeSound!=null)Object.Destroy(_quakeSound);base.OnDestroy();
        }
    }
}
