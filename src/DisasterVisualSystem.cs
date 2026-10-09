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
    // The packaged HDRP build has no transparent Unlit/Lit variants, so soft volumes come
    // from the game's own VFX graphs and every custom mesh is opaque: glowing parts are
    // exposure-independent emission, solid parts are lit geometry.
    public sealed class DisasterVisualSystem : GameSystemBase
    {
        private sealed class Visual
        {
            public GameObject Root, Meteor, TailRoot, Flash, Fault, CraterHot, CraterRim;
            public readonly List<MeshRenderer> Tail = new List<MeshRenderer>();
            public readonly List<Mesh> Meshes = new List<Mesh>();
            public readonly List<NativeDisasterVfx> Effects = new List<NativeDisasterVfx>();
            public Light ImpactLight;
            public HDAdditionalLightData HdLight;
            public AudioSource Sound, Roar;
            public bool PlayedSound, PlayedRoar;
            public float Time, LastSimulationTime, FlightTime = 3;
            public int CraterSamples;
            public CustomDisasterState Snapshot;
            public Duration SnapshotDuration;
            public Vector3 Approach;
            public readonly List<Vector3> Landed = new List<Vector3>();
            public readonly List<bool> HasLanded = new List<bool>();
            public readonly List<float> LandingTimes = new List<float>();
            public NativeDisasterVfx GroundDust, Column, Flame, Explosion, Foam, Wake, Spray;
            public NativeDisasterVfx BigFire, Smoke, Embers, Sparks, EjectaTrail, BuildingDust, CrackTip, Shock, TrailSmoke, TrailDust;
            public readonly List<Vector3> BuildingPoints = new List<Vector3>();
            public readonly List<float> BuildingFalloff = new List<float>();
            public readonly List<Vector3> EjectaDir = new List<Vector3>();
            public readonly List<float> EjectaVertical = new List<float>();
            public MaterialPropertyBlock Block;
            public readonly List<GameObject> Fragments = new List<GameObject>();
            // Fault rupture: fissure traces, terrain under each trace point, dust anchors.
            public List<DisasterVfxRules.Fissure> Fissures;
            public int[] FissureStart;
            public float[] FaultGround, FaultSlope;
            public float FaultReach, FaultFront = -1, FaultWiden = -1;
            public Mesh FaultMesh;
            public readonly List<int> DustAnchors = new List<int>();
            public void Dispose()
            {
                foreach (var fx in Effects) fx.Dispose();
                foreach (var mesh in Meshes) if (mesh != null) Object.Destroy(mesh);
                Object.Destroy(Root);
            }
        }
        private readonly Dictionary<Entity, Visual> _visuals = new Dictionary<Entity, Visual>();
        private readonly HashSet<Entity> _cancelled = new HashSet<Entity>();
        private EntityQuery _query;
        private SimulationSystem _simulation;
        private Material _orange, _dust, _ejecta, _plasma, _molten, _fissure, _soil;
        private readonly List<Mesh> _rocks = new List<Mesh>();
        private readonly List<Mesh> _tailMeshes = new List<Mesh>();
        private Mesh _dome;
        private EntityQuery _buildings;
        private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
        private Texture2D _rockTexture, _soilTexture;
        private AudioClip _impactSound, _quakeSound, _roarSound, _waveSound;
        private EntityQuery _vfxQuery;
        private WindTextureSystem _wind;
        private readonly Dictionary<string, VisualEffectAsset> _assets = new Dictionary<string, VisualEffectAsset>();
        private readonly HashSet<string> _missingAssets = new HashSet<string>();
        private WaterSystem _water;
        private TerrainSystem _terrain;
        private readonly List<Vector3> _faultVertices = new List<Vector3>();
        private readonly List<Vector2> _faultUv = new List<Vector2>();
        private readonly List<int> _faultSoil = new List<int>(), _faultDark = new List<int>();
        // Plasma tail, head to end: white-hot core cooling to deep red.
        private static readonly Color[] TailColors = { new Color(1, .93f, .78f) * 14, new Color(1, .72f, .32f) * 8, new Color(1, .46f, .13f) * 4.5f, new Color(.85f, .26f, .05f) * 2.2f };
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
            Shader lit = Shader.Find("HDRP/Lit") ?? Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color");
            if (lit == null) { Mod.Log.Warn("No supported disaster visual shader"); return; }
            _rockTexture=new Texture2D(128,64,TextureFormat.RGBA32,false) {name="DCP fractured hot rock",wrapMode=TextureWrapMode.Repeat};
            for(int y=0;y<64;y++)for(int x=0;x<128;x++)
            {
                float n=Mathf.PerlinNoise(x*.17f,y*.17f),vein=Mathf.Pow(Mathf.Clamp01(1-Mathf.Abs(n-.48f)*24),3);
                _rockTexture.SetPixel(x,y,Color.Lerp(new Color(.09f+n*.18f,.055f+n*.09f,.025f),new Color(1,.42f,.055f),vein));
            }
            _rockTexture.Apply(false,true);
            // Torn soil: clods and darker cracks, tiles along the fissure banks.
            _soilTexture=new Texture2D(128,128,TextureFormat.RGBA32,false) {name="DCP torn soil",wrapMode=TextureWrapMode.Repeat};
            for(int y=0;y<128;y++)for(int x=0;x<128;x++)
            {
                float n=Mathf.PerlinNoise(x*.09f,y*.09f)*.6f+Mathf.PerlinNoise(x*.31f+17,y*.31f)*.4f;
                float clod=Mathf.Clamp01((Mathf.PerlinNoise(x*.21f+40,y*.21f+9)-.55f)*6);
                var c=Color.Lerp(new Color(.15f,.115f,.08f),new Color(.33f,.26f,.19f),n);
                _soilTexture.SetPixel(x,y,Color.Lerp(c,new Color(.07f,.055f,.04f),clod*.7f));
            }
            _soilTexture.Apply(false,true);
            _orange = Lit("DCP incandescent material", new Color(1f, .3f, .04f), .08f, _rockTexture);
            _dust = Lit("DCP dust material", new Color(.4f, .33f, .26f), .12f, _rockTexture);
            _ejecta = Lit("DCP cooling ejecta", new Color(.12f, .09f, .07f), .08f, _rockTexture);
            _plasma = Lit("DCP plasma", Color.black, 0, null);
            _molten = Lit("DCP molten crater", new Color(.32f, .28f, .25f), .05f, _rockTexture);
            _fissure = Lit("DCP fissure depth", new Color(.018f, .014f, .011f), 0, null);
            _soil = Lit("DCP torn soil", Color.white, .04f, _soilTexture);
            foreach (var hot in new[] { _orange, _molten })
                if (hot.HasProperty("_EmissiveColorMap")) { hot.SetTexture("_EmissiveColorMap", _rockTexture); hot.EnableKeyword("_EMISSIVE_COLOR_MAP"); }
            if (_orange.HasProperty("_UnlitColorMap")) _orange.SetTexture("_UnlitColorMap", _rockTexture);
            // Exposure weight 0: emission is screen-relative, so the fireball, plasma tail and
            // molten crater glow the same at noon and at night instead of reading as dull orange.
            foreach (var hot in new[] { _orange, _ejecta, _plasma, _molten })
                if (hot.HasProperty("_EmissiveExposureWeight")) hot.SetFloat("_EmissiveExposureWeight", 0);
            if (_orange.HasProperty("_EmissiveColor")) _orange.SetColor("_EmissiveColor", new Color(1f, .55f, .2f) * 7f);
            for (int variant = 0; variant < 4; variant++) _rocks.Add(MakeRock(variant));
            for (int k = 0; k < TailColors.Length; k++) _tailMeshes.Add(MakeTailSegment(k, TailColors.Length));
            _dome = MakeDome();
            _impactSound = MakeSound("DCP meteor impact", 0); _quakeSound = MakeSound("DCP seismic rumble", 1);
            _roarSound = MakeSound("DCP meteor entry roar", 2); _waveSound = MakeSound("DCP tsunami roar", 3);
        }
        private static Material Lit(string name, Color color, float smoothness, Texture texture)
        {
            var shader = Shader.Find("HDRP/Lit") ?? Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color");
            var material = new Material(shader) { name = name, color = color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", color);
            if (texture != null && material.HasProperty("_BaseColorMap")) material.SetTexture("_BaseColorMap", texture);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0);
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", Color.black);
            return material;
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
            if (asset != null)
            {
                var fx = new NativeDisasterVfx(visual.Root.transform, asset, (uint)visual.Root.GetInstanceID() + (uint)visual.Effects.Count * 7919u);
                visual.Effects.Add(fx);
                return fx;
            }
            if (_missingAssets.Add(name)) Mod.Log.Warn("Native disaster visual asset unavailable: " + name);
            return null;
        }
        // Make every triangle face away from the given interior point or axis.
        private static void Orient(Vector3[] v, int[] f, System.Func<Vector3, Vector3> outward)
        {
            for (int i = 0; i < f.Length; i += 3)
            {
                Vector3 a = v[f[i]], b = v[f[i + 1]], c = v[f[i + 2]];
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward((a + b + c) / 3)) < 0) { int swap = f[i + 1]; f[i + 1] = f[i + 2]; f[i + 2] = swap; }
            }
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
            var verts=new Vector3[f.Count];var uv=new Vector2[f.Count];var tris=new int[f.Count];
            for(int i=0;i<f.Count;i++){verts[i]=v[f[i]];tris[i]=i;var d=v[f[i]].normalized;uv[i]=new Vector2(math.atan2(d.z,d.x)/(2*math.PI)+.5f,d.y*.5f+.5f);}
            Orient(verts,tris,c=>c);
            var mesh=new Mesh{name="DCP fractured rock "+seed,vertices=verts,uv=uv,triangles=tris};
            mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
        }
        // One stretch of the plasma tail in unit space: +z points back up the entry path,
        // radius tapers from just under the head's size to a point.
        private static Mesh MakeTailSegment(int k, int count)
        {
            const int sides = 16, rings = 5;
            var v = new List<Vector3>(); var f = new List<int>();
            for (int j = 0; j < rings; j++)
            {
                float u = (k + j / (float)(rings - 1)) / count, r = .95f * math.pow(1 - u, 1.35f) + .015f;
                for (int i = 0; i < sides; i++) { float a = i * math.PI * 2 / sides; v.Add(new Vector3(math.cos(a) * r, math.sin(a) * r, u)); }
            }
            for (int j = 0; j + 1 < rings; j++) for (int i = 0; i < sides; i++)
            {
                int a = j * sides + i, b = j * sides + (i + 1) % sides, c = a + sides, d = b + sides;
                f.AddRange(new[] { a, b, c, b, d, c });
            }
            var verts = v.ToArray(); var tris = f.ToArray();
            Orient(verts, tris, c => new Vector3(c.x, c.y, 0));
            var mesh = new Mesh { name = "DCP plasma tail " + k, vertices = verts, triangles = tris };
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }
        // Unit hemisphere for the impact fireball.
        private static Mesh MakeDome()
        {
            const int rows = 8, cols = 24;
            var v = new List<Vector3>(); var f = new List<int>();
            for (int r = 0; r <= rows; r++) for (int c = 0; c <= cols; c++)
            {
                float lat = r * math.PI * .5f / rows, lon = c * math.PI * 2 / cols;
                v.Add(new Vector3(math.cos(lat) * math.cos(lon), math.sin(lat), math.cos(lat) * math.sin(lon)));
            }
            for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++)
            {
                int a = r * (cols + 1) + c, b = a + 1, d = a + cols + 1, e = d + 1;
                f.AddRange(new[] { a, b, d, b, e, d });
            }
            var verts = v.ToArray(); var tris = f.ToArray();
            Orient(verts, tris, c => c);
            var mesh = new Mesh { name = "DCP fireball dome", vertices = verts, triangles = tris };
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }
        private static GameObject MeshObject(string name, Transform parent, Mesh mesh, Material material, bool shadows)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }
        private GameObject Rock(Transform parent, Material material, int variant)
            => MeshObject("DCP rock", parent, _rocks[(variant < 0 ? 0 : variant) % _rocks.Count], material, true);
        // Each sound gets its own child object so moving it never drags the VFX graphs along.
        private static AudioSource Speaker(Transform parent, AudioClip clip, bool loop, float near, float far)
        {
            var owner = new GameObject("DCP sound"); owner.transform.SetParent(parent, false);
            var source = owner.AddComponent<AudioSource>();
            source.playOnAwake = false; source.spatialBlend = 1; source.minDistance = near; source.maxDistance = far;
            source.rolloffMode = AudioRolloffMode.Linear; source.dopplerLevel = 0; source.loop = loop; source.clip = clip;
            return source;
        }
        private Visual Create(CustomDisasterState state, float lead)
        {
            var v = new Visual { Root = new GameObject("DCP disaster visuals"), Block = new MaterialPropertyBlock() };
            v.Root.transform.position = state.Position;
            float bearing=math.frac(state.Position.x*.0131f+state.Position.z*.0173f)*math.PI*2, elevation=DisasterVfxRules.MeteorElevation;
            v.Approach=new Vector3(math.cos(elevation)*math.cos(bearing),math.sin(elevation),math.cos(elevation)*math.sin(bearing));
            v.FlightTime=DisasterVfxRules.MeteorFlightTime(lead,_simulation.selectedSpeed);
            v.Sound=Speaker(v.Root.transform,state.Kind==1 ? _quakeSound : state.Kind==2 ? _impactSound : null,false,150,7000);
            var lightObject=new GameObject("DCP impact illumination");lightObject.transform.SetParent(v.Root.transform,false);lightObject.transform.localPosition=Vector3.up*35;
            v.ImpactLight=lightObject.AddComponent<Light>();v.ImpactLight.type=UnityEngine.LightType.Point;v.ImpactLight.color=new Color(1,.55f,.2f);v.ImpactLight.range=650+state.Level*80;v.ImpactLight.enabled=false;
            v.HdLight=lightObject.AddComponent<HDAdditionalLightData>();
            v.Meteor = Rock(v.Root.transform, _orange, 0);
            v.Meteor.SetActive(false);
            v.GroundDust = Native(v, "DustcloudSmall");
            v.Column = Native(v, "Dustcloud");
            if(state.Kind==2) CreateMeteor(v,state);
            if(state.Kind==3) for(int i=0;i<36;i++)
            {
                v.Fragments.Add(Rock(v.Root.transform,_dust,i));v.Landed.Add(Vector3.zero);v.HasLanded.Add(false);v.LandingTimes.Add(0);
            }
            if(state.Kind==1) CreateQuake(v,state);
            return v;
        }
        private void CreateMeteor(Visual v, CustomDisasterState state)
        {
            v.Flame = Native(v, "FireMovingMedium");
            v.Explosion = Native(v, "ExplosionTimed");
            v.BigFire = Native(v, "FireBig");
            v.Smoke = Native(v, "smokeFromFire");
            v.Embers = Native(v, "FireEmbers");
            v.Sparks = Native(v, "Sparks");
            v.EjectaTrail = Native(v, "DustcloudSmall");
            v.Shock = Native(v, "DustcloudSmall");
            v.TrailSmoke = Native(v, "smokeFromFire");
            v.TrailDust = Native(v, "Dustcloud");
            v.Roar = Speaker(v.Root.transform, _roarSound, false, 250, 9000);
            for(int i=0;i<36;i++)
            {
                v.Fragments.Add(Rock(v.Root.transform,_ejecta,i));v.Landed.Add(Vector3.zero);v.HasLanded.Add(false);v.LandingTimes.Add(0);
                DisasterVfxRules.Ejecta(i,state.Level,out float azimuth,out float horizontal,out float vertical);
                v.EjectaDir.Add(new Vector3(math.cos(azimuth),0,math.sin(azimuth))*horizontal);v.EjectaVertical.Add(vertical);
                v.Fragments[i].SetActive(false);
            }
            v.TailRoot = new GameObject("DCP plasma tail"); v.TailRoot.transform.SetParent(v.Root.transform, false);
            foreach (var mesh in _tailMeshes)
                v.Tail.Add(MeshObject("DCP plasma tail segment", v.TailRoot.transform, mesh, _plasma, false).GetComponent<MeshRenderer>());
            v.TailRoot.SetActive(false);
            v.Flash = MeshObject("DCP impact fireball", v.Root.transform, _dome, _plasma, false); v.Flash.SetActive(false);
            v.CraterHot = MeshObject("DCP molten crater", v.Root.transform, null, _molten, false); v.CraterHot.SetActive(false);
            v.CraterRim = MeshObject("DCP scorched rim", v.Root.transform, null, _molten, false); v.CraterRim.SetActive(false);
        }
        private void CreateQuake(Visual v, CustomDisasterState state)
        {
            v.BuildingDust = Native(v, "Dustcloud");
            v.CrackTip = Native(v, "DustcloudSmall");
            int seed=(int)(math.frac(state.Position.x*.00731f+state.Position.z*.01193f)*100000);
            v.Fissures=DisasterVfxRules.QuakeFault(seed,state.Level,state.Radius);
            v.FaultReach=DisasterVfxRules.FaultReach(v.Fissures);
            int total=0;v.FissureStart=new int[v.Fissures.Count];
            for(int i=0;i<v.Fissures.Count;i++){v.FissureStart[i]=total;total+=v.Fissures[i].X.Count;}
            v.FaultGround=new float[total];v.FaultSlope=new float[total];
            // Terrain under every trace point and its slope across the fissure, sampled once.
            var terrain=_terrain.GetHeightData();
            for(int fi=0;fi<v.Fissures.Count;fi++)
            {
                var f=v.Fissures[fi];int n=f.X.Count;float spacing=0;
                for(int i=0;i<n;i++)
                {
                    Normal(f,i,out float nx,out float nz);
                    float reach=f.Width[i]*1.4f+4;
                    float3 c=state.Position+new float3(f.X[i],0,f.Z[i]);
                    float y=TerrainUtils.SampleHeight(ref terrain,c);
                    float yl=TerrainUtils.SampleHeight(ref terrain,c-new float3(nx,0,nz)*reach),yr=TerrainUtils.SampleHeight(ref terrain,c+new float3(nx,0,nz)*reach);
                    v.FaultGround[v.FissureStart[fi]+i]=y;v.FaultSlope[v.FissureStart[fi]+i]=(yr-yl)/(2*reach);
                    // Dust anchors every ~40 m along the main fault, ~30 m along splays.
                    if(i>0)spacing+=math.distance(new float2(f.X[i],f.Z[i]),new float2(f.X[i-1],f.Z[i-1]));
                    if((i==0 || spacing>=(f.Main ? 40 : 30)) && v.DustAnchors.Count<64){v.DustAnchors.Add(fi<<16|i);spacing=0;}
                }
            }
            v.FaultMesh=new Mesh{name="DCP fault rupture"};v.FaultMesh.MarkDynamic();v.Meshes.Add(v.FaultMesh);
            v.Fault=MeshObject("DCP fault rupture",v.Root.transform,v.FaultMesh,_soil,true);
            v.Fault.GetComponent<MeshRenderer>().sharedMaterials=new[]{_soil,_fissure};
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
        private static void Normal(DisasterVfxRules.Fissure f,int i,out float nx,out float nz)
        {
            int a=math.max(0,i-1),b=math.min(f.X.Count-1,i+1);
            float tx=f.X[b]-f.X[a],tz=f.Z[b]-f.Z[a],len=math.max(.001f,math.sqrt(tx*tx+tz*tz));
            nx=-tz/len;nz=tx/len;
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
                float life=DisasterVisualRules.Lifetime(s.Kind);
                float simulationTime = ((long)_simulation.frameIndex - s.ImpactFrame) / 60f;
                float lead=math.max(3f,((long)s.ImpactFrame-duration.m_StartFrame)/60f);
                if(simulationTime < (s.Kind==2 ? -lead : 0))continue;
                if (!_visuals.TryGetValue(e, out var v))
                {
                    if(_visuals.Count>=8 || simulationTime>=life)continue;
                    v = Create(s,lead);v.Time=simulationTime;v.LastSimulationTime=simulationTime;_visuals[e] = v;
                }
                v.Snapshot=s;v.SnapshotDuration=duration;
                float delta=math.max(0,simulationTime-v.LastSimulationTime);v.LastSimulationTime=simulationTime;
                float oldTime=v.Time;
                v.Time=simulationTime<0 ? simulationTime : v.Time<0 ? 0 : DisasterVisualRules.VisibleTime(v.Time,delta,UnityEngine.Time.unscaledDeltaTime);
                float t=v.Time, presentationDelta=math.max(0,t-oldTime);
                if(t>=life)continue;
                live.Add(e);
                bool paused=_simulation.selectedSpeed<=0;
                UpdateSound(v,s,t,paused);
                foreach(var fx in v.Effects)fx.Begin();
                Vector3 p = s.Position;
                var terrainDebris=_terrain.GetHeightData();
                float cameraFalloff=CameraFalloff(p,s.Radius);
                if (s.Kind == 2) UpdateMeteor(v,s,t,p,ref terrainDebris,paused,cameraFalloff);
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
        private void Glow(Renderer renderer, Visual v, Color color)
        {
            v.Block.SetColor(EmissiveColorId, color);
            renderer.SetPropertyBlock(v.Block);
        }
        private void UpdateMeteor(Visual v,CustomDisasterState s,float t,Vector3 p,ref TerrainHeightData terrain,bool paused,float cameraFalloff)
        {
            float flight=v.FlightTime, distance=DisasterVfxRules.MeteorEntryDistance;
            float progress=DisasterVfxRules.MeteorProgress(t,flight);
            bool flying=progress>=0 && t<0;
            var head=p+v.Approach*distance*(1-math.max(0,progress));
            float size=7+s.Level*2.6f;
            float flicker=.9f+.2f*Mathf.PerlinNoise(UnityEngine.Time.unscaledTime*11,s.Position.x*.01f);
            v.Meteor.SetActive(flying);
            v.TailRoot.SetActive(flying);
            v.Roar.transform.position=head;
            if(flying)
            {
                v.Meteor.transform.position=head;
                v.Meteor.transform.localScale=new Vector3(1.15f,.9f,1)*size;
                v.Meteor.transform.rotation=Quaternion.Euler(t*90,t*40,t*70);
                // Glowing plasma sheath streaming behind the head, white core to red end.
                float length=DisasterVfxRules.TailLength(distance*progress,size)*(.92f+.16f*flicker);
                v.TailRoot.transform.position=head;
                v.TailRoot.transform.rotation=Quaternion.LookRotation(v.Approach);
                v.TailRoot.transform.localScale=new Vector3(size*flicker,size*flicker,math.max(1,length));
                float trail=DisasterVfxRules.TrailEmission(t,flight);
                for(int k=0;k<v.Tail.Count;k++)Glow(v.Tail[k],v,TailColors[k]*trail*flicker);
                v.BigFire?.Emit(head+v.Approach*(size*.6f),Vector3.one*(1.6f+s.Level*.28f),trail);
                for(int i=0;i<8;i++)
                    v.Flame?.Emit(head+v.Approach*(size*.8f+i*length/9f),Vector3.one*(1.2f+s.Level*.18f)*(1-i*.09f),trail*(1-i*.1f));
                v.Sparks?.Emit(head+v.Approach*size,Vector3.one*(1+s.Level*.15f),trail*.8f);
            }
            // Smoke trail drawn across the sky: each point puffs as the head passes and keeps
            // smoking, drifting on the wind, long after the impact.
            if(progress>=0)
            {
                const int smokePoints=64;
                for(int k=0;k<smokePoints;k++)
                {
                    float along=DisasterVfxRules.TrailPoint(k,smokePoints),since=t-DisasterVfxRules.MeteorPassTime(along,flight);
                    float puff=DisasterVfxRules.TrailSmoke(since);
                    if(puff<=0)continue;
                    var at=p+v.Approach*distance*along;
                    float grow=1+since*.22f;
                    v.TrailSmoke?.Emit(at,Vector3.one*(1.6f+s.Level*.3f)*grow,puff*(.6f+.4f*(1-along)));
                    if(k%2==0)v.TrailDust?.Emit(at+Vector3.up*3,Vector3.one*(1.4f+s.Level*.24f)*grow,puff*.55f);
                }
            }
            // Light travels with the fireball, flashes on impact, then glows from the molten crater.
            v.ImpactLight.enabled=progress>=0 && t<24;
            if(v.ImpactLight.enabled)
            {
                if(t<0)
                {
                    v.ImpactLight.transform.position=head;v.ImpactLight.color=new Color(1,.72f,.45f);
                    v.HdLight.SetIntensity((50000+s.Level*15000)*math.sqrt(progress));
                }
                else
                {
                    v.ImpactLight.transform.position=p+Vector3.up*(35+s.Level*4);
                    float flash=DisasterVisualRules.Fade(t,1.2f);
                    v.ImpactLight.color=Color.Lerp(new Color(1,.4f,.12f),new Color(1,.85f,.7f),flash);
                    v.HdLight.SetIntensity((420000+s.Level*90000)*flash*flash+(30000+s.Level*6000)*math.max(DisasterVfxRules.CraterFire(t),DisasterVfxRules.CraterGlow(t)));
                }
            }
            if(t>=0 && v.Roar.isPlaying)v.Roar.Stop();
            float crater=DisasterRules.CraterRadius(2,s.Level);
            // Fireball dome: snaps out white-hot, cools through orange and sinks into the dust.
            float flashRadius=DisasterVfxRules.FlashRadius(t,crater);
            v.Flash.SetActive(flashRadius>0);
            if(flashRadius>0)
            {
                v.Flash.transform.localPosition=Vector3.down*2;
                v.Flash.transform.localScale=new Vector3(flashRadius,flashRadius*DisasterVfxRules.FlashHeight(t),flashRadius);
                var hue=t<.35f ? Color.Lerp(new Color(1,.96f,.88f),new Color(1,.62f,.22f),t/.35f) : Color.Lerp(new Color(1,.62f,.22f),new Color(.8f,.2f,.05f),(t-.35f)/.55f);
                Glow(v.Flash.GetComponent<MeshRenderer>(),v,hue*DisasterVfxRules.FlashEmission(t));
            }
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
            // Shock front racing out over the ground, kicking up a ragged ring of dust.
            float shock=DisasterVfxRules.ShockRadius(t,s.Level,crater);
            if(shock>0)
                for(int i=0;i<48;i++)
                {
                    float a=i*.1308997f+DisasterVfxRules.Hash(i,131)*.1f,r=shock*(.92f+.16f*DisasterVfxRules.Hash(i,133));
                    var at=p+new Vector3(math.cos(a)*r,0,math.sin(a)*r);at.y=TerrainUtils.SampleHeight(ref terrain,at)+1.5f;
                    v.Shock?.Emit(at,new Vector3(2.2f+s.Level*.3f,.6f,2.2f+s.Level*.3f)*(1+t*.6f),DisasterVfxRules.ShockEmission(t)*.8f,Vector3.up*a*57.29578f);
                }
            float fire=DisasterVfxRules.CraterFire(t),smoke=DisasterVfxRules.ImpactSmoke(t);
            for(int i=0;i<10;i++)
            {
                float a=i*2.399963f,r=crater*(.2f+.75f*DisasterVfxRules.Hash(i,91));
                var at=p+new Vector3(math.cos(a)*r,0,math.sin(a)*r);at.y=TerrainUtils.SampleHeight(ref terrain,at)+.5f;
                if(i<6)v.BigFire?.Emit(at,Vector3.one*(1+s.Level*.15f)*(.6f+DisasterVfxRules.Hash(i,93)*.6f),fire*(.5f+DisasterVfxRules.Hash(i,95)*.5f));
                v.Embers?.Emit(at+Vector3.up*2,Vector3.one*(1.2f+s.Level*.15f),fire);
                v.Smoke?.Emit(at+Vector3.up*(4+i*2),Vector3.one*(1.6f+s.Level*.25f),smoke*.9f);
            }
            for(int i=0;i<24;i++)
            {
                var emitter=DisasterVfxRules.MeteorGround(i,t,s.Level);
                float3 pos=s.Position+new float3(emitter.X,0,emitter.Z); pos.y=TerrainUtils.SampleHeight(ref terrain,pos)+emitter.Y;
                v.GroundDust?.Emit(pos,new Vector3(emitter.Width,emitter.Height,emitter.Width)*1.25f,emitter.Intensity,Vector3.up*emitter.Yaw);
            }
            // Rising plume with a spreading cap.
            for(int i=0;i<24;i++)
            {
                var emitter=DisasterVfxRules.MeteorPlume(i,t,s.Level);
                v.Column?.Emit(p+new Vector3(emitter.X,emitter.Y,emitter.Z),new Vector3(emitter.Width,emitter.Height,emitter.Width)*1.2f,emitter.Intensity,Vector3.up*emitter.Yaw);
            }
            UpdateCrater(v,s,t,crater,ref terrain);
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
                float scale=(1.2f+s.Level*.35f)*(.35f+DisasterVfxRules.Hash(i,97)*DisasterVfxRules.Hash(i,98)*2.2f);
                fragment.transform.localScale=Vector3.one*scale*math.min(1,(14-t)*.5f);
                if(!v.HasLanded[i])fragment.transform.rotation=Quaternion.Euler(t*(90+i*7),t*(60+i*3),t*45+i*19);
                Glow(fragment.GetComponent<MeshRenderer>(),v,new Color(1,.5f,.16f)*DisasterVfxRules.EjectaGlow(t));
                if(!v.HasLanded[i])v.EjectaTrail?.Emit(next,Vector3.one*(.35f+scale*.12f),.55f);
                else v.GroundDust?.Emit(v.Landed[i],Vector3.one*(.5f+scale*.1f),DisasterVfxRules.LandingEmission(t-v.LandingTimes[i])*.7f);
            }
            if(!paused)CameraShake.Request(DisasterVfxRules.MeteorShake(t,s.Level)*cameraFalloff,14);
        }
        // Molten crater floor and scorched rim, draped on the freshly dug terrain. The heightmap
        // is resampled a few times while the crater brush lands, then the meshes stay put.
        private static readonly float[] CraterResample = { .25f, .8f, 1.6f, 3f, 5f };
        private void UpdateCrater(Visual v,CustomDisasterState s,float t,float crater,ref TerrainHeightData terrain)
        {
            bool shown=t>=.25f;
            v.CraterHot.SetActive(shown);v.CraterRim.SetActive(shown);
            if(!shown)return;
            if(v.CraterSamples<CraterResample.Length && t>=CraterResample[v.CraterSamples])
            {
                while(v.CraterSamples<CraterResample.Length && t>=CraterResample[v.CraterSamples])v.CraterSamples++;
                DrapeDisk(v,v.CraterHot,0,crater*.45f,4,s.Position,ref terrain);
                DrapeDisk(v,v.CraterRim,crater*.45f,crater*.85f,3,s.Position,ref terrain);
            }
            float sink=DisasterVfxRules.CraterSink(t,DisasterVisualRules.Lifetime(2));
            v.CraterHot.transform.localPosition=v.CraterRim.transform.localPosition=Vector3.down*sink;
            float pulse=.85f+.3f*Mathf.PerlinNoise(UnityEngine.Time.unscaledTime*1.7f,s.Position.z*.01f);
            Glow(v.CraterHot.GetComponent<MeshRenderer>(),v,new Color(1,.45f,.12f)*10*DisasterVfxRules.CraterGlow(t)*pulse);
            Glow(v.CraterRim.GetComponent<MeshRenderer>(),v,new Color(1,.3f,.07f)*4*DisasterVfxRules.CraterGlow(t*1.5f)*pulse);
        }
        private void DrapeDisk(Visual v,GameObject target,float inner,float outer,int rings,float3 centre,ref TerrainHeightData terrain)
        {
            const int segments=28;
            var filter=target.GetComponent<MeshFilter>();
            var mesh=filter.sharedMesh;
            if(mesh==null){mesh=new Mesh{name="DCP crater drape"};mesh.MarkDynamic();v.Meshes.Add(mesh);filter.sharedMesh=mesh;}
            var verts=new List<Vector3>();var uv=new List<Vector2>();var tris=new List<int>();
            for(int r=0;r<=rings;r++)for(int c=0;c<segments;c++)
            {
                float radius=math.lerp(inner,outer,r/(float)rings),a=c*math.PI*2/segments;
                // Ragged edge so the glow does not read as a perfect disc.
                if(r==rings)radius*=.88f+.24f*DisasterVfxRules.Hash(c,141);
                float3 at=centre+new float3(math.cos(a)*radius,0,math.sin(a)*radius);
                verts.Add(new Vector3(at.x-centre.x,TerrainUtils.SampleHeight(ref terrain,at)-centre.y+.35f,at.z-centre.z));
                uv.Add(new Vector2(at.x*.02f,at.z*.02f));
            }
            for(int r=0;r<rings;r++)for(int c=0;c<segments;c++)
            {
                int a=r*segments+c,b=r*segments+(c+1)%segments,d=a+segments,e=b+segments;
                // Rings grow outward and angles turn counter-clockwise seen from above: this order faces up.
                tris.AddRange(new[]{a,b,d,b,e,d});
            }
            mesh.Clear();mesh.SetVertices(verts);mesh.SetUVs(0,uv);mesh.SetTriangles(tris,0);mesh.RecalculateNormals();mesh.RecalculateBounds();
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
            float front=DisasterVfxRules.RuptureFront(t,v.FaultReach),widen=DisasterVfxRules.FissureWiden(t);
            if(math.abs(front-v.FaultFront)>.5f || math.abs(widen-v.FaultWiden)>.004f){BuildFault(v,s,front,widen);v.FaultFront=front;v.FaultWiden=widen;}
            v.Fault.transform.localPosition=Vector3.down*DisasterVfxRules.FissureSink(t,DisasterVisualRules.Lifetime(1));
            bool tearing=false;
            for(int k=0;k<3;k++){float age=t-k*3f;tearing|=age>=0 && age<1.6f;}
            // Dust bursts from the running rupture tips.
            if(tearing && presentationDelta>0)
                for(int fi=0;fi<v.Fissures.Count;fi++)
                {
                    var f=v.Fissures[fi];int n=f.X.Count,tip=-1;
                    for(int i=0;i<n;i++)if(f.Reach[i]<=front)tip=i;
                    if(tip<0 || tip>=n-1)continue;
                    var at=new Vector3(p.x+f.X[tip],v.FaultGround[v.FissureStart[fi]+tip]+1,p.z+f.Z[tip]);
                    v.CrackTip?.Emit(at,new Vector3(.9f+s.Level*.15f,.6f,.9f+s.Level*.15f)*(f.Main ? 1 : .6f),.9f);
                }
            // Freshly torn ground spurts, every aftershock shakes dust out of the whole fault.
            for(int i=0;i<v.DustAnchors.Count;i++)
            {
                int fi=v.DustAnchors[i]>>16,pi=v.DustAnchors[i]&0xFFFF;var f=v.Fissures[fi];
                float intensity=DisasterVfxRules.FissureDust(t,front,f.Reach[pi],v.FaultReach);
                if(intensity<=0)continue;
                var at=new Vector3(p.x+f.X[pi],v.FaultGround[v.FissureStart[fi]+pi]+.8f,p.z+f.Z[pi]);
                float size=(1+s.Level*.18f)*(f.Main ? 1 : .7f);
                v.GroundDust?.Emit(at,new Vector3(size,.6f,size),intensity,Vector3.up*DisasterVfxRules.Hash(i,151)*360);
                if(i%3==0)v.Column?.Emit(at+Vector3.up,Vector3.one*(.6f+s.Level*.1f),intensity*.35f);
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
        // Fissure cross-section, left to right: outer bank, raised lip, inner edge, dark
        // depth, inner edge, lip, outer bank. On the main fault the +n lip is a scarp
        // that rises with every pulse. Unopened points collapse to zero width.
        private void BuildFault(Visual v,CustomDisasterState s,float front,float widen)
        {
            var verts=_faultVertices;var uv=_faultUv;verts.Clear();uv.Clear();_faultSoil.Clear();_faultDark.Clear();
            float w0=DisasterVfxRules.FissureWidth(s.Level),scarp=DisasterVfxRules.ScarpHeight(s.Level)*widen;
            var offsets=new float[7];var heights=new float[7];
            for(int fi=0;fi<v.Fissures.Count;fi++)
            {
                var f=v.Fissures[fi];int n=f.X.Count,first=verts.Count;float along=0;
                for(int i=0;i<n;i++)
                {
                    int k=v.FissureStart[fi]+i;
                    Normal(f,i,out float nx,out float nz);
                    if(i>0)along+=math.distance(new float2(f.X[i],f.Z[i]),new float2(f.X[i-1],f.Z[i-1]));
                    float open=DisasterVfxRules.FissureOpen(front,f.Reach[i],v.FaultReach);
                    float w=f.Width[i]*open*widen,h=w*.5f,shown=math.saturate(w/.4f);
                    float lip=(.15f+.12f*w)*shown,bank=(.7f*w+1.2f)*shown;
                    float up=f.Main ? scarp*(f.Width[i]/w0)*open : 0,bankUp=bank+up*2.5f;
                    offsets[0]=-(h+bank);offsets[1]=-(h+.35f*bank);offsets[2]=-h;offsets[3]=0;offsets[4]=h;offsets[5]=h+.35f*bankUp;offsets[6]=h+bankUp;
                    heights[0]=0;heights[1]=lip;heights[2]=lip*.45f;heights[3]=-.18f*shown;heights[4]=(lip+up)*.45f;heights[5]=lip+up;heights[6]=0;
                    for(int c=0;c<7;c++)
                    {
                        float o=offsets[c];
                        // .32 m above the sampled terrain: clear of the ground, close to road decks.
                        float y=v.FaultGround[k]+v.FaultSlope[k]*o+.32f+heights[c];
                        verts.Add(new Vector3(f.X[i]+nx*o,y-s.Position.y,f.Z[i]+nz*o));
                        uv.Add(new Vector2(along/6f,c/6f));
                    }
                }
                for(int i=0;i+1<n;i++)for(int c=0;c<6;c++)
                {
                    int a=first+i*7+c,b=a+1,cc=a+7,d=cc+1;
                    var list=c==2||c==3 ? _faultDark : _faultSoil;
                    // Offsets grow along n = (-t.z, t.x), so this order faces up.
                    list.Add(a);list.Add(b);list.Add(cc);list.Add(b);list.Add(d);list.Add(cc);
                }
            }
            var mesh=v.FaultMesh;
            mesh.Clear();mesh.SetVertices(verts);mesh.SetUVs(0,uv);mesh.subMeshCount=2;
            mesh.SetTriangles(_faultSoil,0);mesh.SetTriangles(_faultDark,1);
            mesh.RecalculateNormals();mesh.RecalculateBounds();
        }
        private void Commit(Visual visual,float delta)
        {
            var wind=_wind.WindTexture; var map=_terrain.mapOffsetScale;
            foreach(var fx in visual.Effects)fx.Commit(delta,wind,map);
        }
        private static AudioClip MakeSound(string name,int kind)
        {
            const int rate=22050;
            float seconds=kind==0 ? 4 : kind==1 ? 10 : kind==2 ? 8 : 12;
            int length=(int)(rate*seconds);var samples=new float[length];uint noise=127+(uint)kind*977;
            float filtered=0,hiss=0;
            for(int i=0;i<length;i++)
            {
                noise=noise*1664525+1013904223;float n=((noise>>8)/(float)0xFFFFFF)*2-1;filtered=filtered*.97f+n*.03f;hiss=hiss*.8f+n*.2f;
                float t=i/(float)rate,value;
                if(kind<=1)
                {
                    bool quake=kind==1;
                    float envelope=quake ? math.saturate(t*3)*math.saturate((10-t)*.5f) : math.exp(-t*1.7f)*(1-math.exp(-t*80));
                    float pulse=quake ? .55f+.45f*math.sin(t*2.1f)*math.sin(t*2.1f) : 1;
                    value=(filtered*2.5f+math.sin(t*(quake ? 160 : 110))*.13f+n*(quake ? .035f : .08f)*math.exp(-t*4))*envelope*pulse;
                }
                else if(kind==2)
                {
                    // Entry roar: builds from a distant rumble to a tearing hiss right before impact.
                    float x=t/seconds,envelope=math.pow(x,2.2f)*math.saturate((seconds-t)*8);
                    value=(filtered*2.2f+hiss*.35f*(.4f+.6f*x)+math.sin(t*(70+t*9))*.08f)*envelope;
                }
                else
                {
                    // Seamless loop of surf thunder: three surges per loop.
                    float surge=.65f+.35f*math.sin(t*math.PI*2*3/seconds);
                    value=(filtered*2.6f+hiss*.25f*surge)*surge;
                }
                samples[i]=math.clamp(value,-.6f,.6f);
            }
            var clip=AudioClip.Create(name,length,1,rate,false);clip.SetData(samples,0);return clip;
        }
        private static float Volume(int count)
        {
            var settings=Game.Settings.SharedSettings.instance.audio;
            return .55f/math.sqrt(math.max(1,count))*settings.masterVolume*settings.ingameVolume*settings.disastersVolume;
        }
        private void UpdateSound(Visual v,CustomDisasterState state,float time,bool paused)
        {
            if(v.Sound.clip!=null)
            {
                v.Sound.volume=Volume(_visuals.Count);
                if(!v.PlayedSound && time>=0){v.PlayedSound=true;v.Sound.Play();}
                if(paused)v.Sound.Pause();else if(v.PlayedSound)v.Sound.UnPause();
            }
            if(v.Roar==null)return;
            // The roar clip ends at the impact; long flights start it late, short ones part-way in.
            float length=v.Roar.clip.length;
            v.Roar.volume=Volume(_visuals.Count)*.8f;
            if(!v.PlayedRoar && time<0 && time>=-math.min(v.FlightTime,length))
            {v.PlayedRoar=true;v.Roar.Play();v.Roar.time=math.clamp(length+time,0,length-.05f);}
            if(paused)v.Roar.Pause();else if(v.PlayedRoar && time<0)v.Roar.UnPause();
        }
        private void UpdateWaterVisual(Entity e,CustomDisasterState s,HashSet<Entity> live)
        {
            live.Add(e);
            if(!_visuals.TryGetValue(e,out var v))
            {
                if(_visuals.Count>=8)return;
                v=new Visual {Root=new GameObject("DCP water disaster visuals")};
                v.Foam=Native(v,"BoatFoam01");v.Wake=Native(v,"BoatFoam01");v.LastSimulationTime=_simulation.frameIndex/60f;
                if(s.Kind==11){v.Spray=Native(v,"WaterVaporHuge");v.Roar=Speaker(v.Root.transform,_waveSound,true,400,9000);}
                _visuals[e]=v;
            }
            var surface=_water.GetSurfaceData(out var deps);deps.Complete();var terrain=_terrain.GetHeightData();
            float angle=math.radians(s.Radius);var forward=new float3(math.sin(angle),0,math.cos(angle));var right=new float3(forward.z,0,-forward.x);
            foreach(var fx in v.Effects)fx.Begin();
            var water=World.GetOrCreateSystemManaged<WaterDisasterSystem>();
            bool paused=_simulation.selectedSpeed<=0;
            // Tsunami: white water rides the moving crest of the wall, a churning wake trails it
            // and spray blows off its top, over the sea and as it rolls inland.
            bool crestShown=false;float loudest=0;
            if(s.Kind==11 && water.TryGetCrest(e,out var crest))
            {
                crestShown=true;int step=math.max(1,(crest.Count+63)/64);
                Vector3 centre=Vector3.zero;float weight=0;
                for(int i=0;i<crest.Count;i+=step)
                {
                    var c=crest[i];float3 at=new float3(c.x,0,c.z);
                    float depth=WaterUtils.SampleDepth(ref surface,at),ground=TerrainUtils.SampleHeight(ref terrain,at);
                    at.y=math.max(c.y,ground+depth+.4f);
                    float size=math.clamp(c.w/20f,.6f,3f);
                    v.Foam?.Emit(at,new Vector3(3+size*1.6f,1,2+size),.95f,Vector3.up*(s.Radius+90));
                    float3 behind=at-forward*(30+c.w*1.5f);
                    behind.y=math.max(at.y-c.w*.3f,TerrainUtils.SampleHeight(ref terrain,behind)+WaterUtils.SampleDepth(ref surface,behind)+.4f);
                    v.Wake?.Emit(behind,new Vector3(2.5f+size,1,2+size*.8f),.7f,Vector3.up*(s.Radius+90+DisasterVfxRules.Hash(i,161)*40));
                    if((i/step)%2==0)v.Spray?.Emit((Vector3)at+Vector3.up*(1+c.w*.25f),Vector3.one*(.8f+size*.6f),math.saturate(c.w/10f)*.85f);
                    centre+=(Vector3)at*c.w;weight+=c.w;loudest=math.max(loudest,c.w);
                }
                if(v.Roar!=null && weight>0)v.Roar.transform.position=centre/weight;
            }
            if(v.Roar!=null)
            {
                v.Roar.volume=Volume(_visuals.Count)*math.saturate(loudest/25f);
                if(loudest>0 && !v.Roar.isPlaying && !paused)v.Roar.Play();
                if(paused || loudest<=0)v.Roar.Pause();
            }
            // Flood: foam and churn along the edge where the rising water is spreading into
            // streets right now.
            if(s.Kind==10 && water.TryGetFloodFront(e,out var front))
                for(int i=0;i<front.Count;i++)
                {
                    var c=front[i];float3 at=new float3(c.x,0,c.z);
                    float ground=TerrainUtils.SampleHeight(ref terrain,at),depth=WaterUtils.SampleDepth(ref surface,at);
                    if(depth<=.2f)continue;
                    at.y=ground+depth+.3f;
                    v.Wake?.Emit(at,new Vector3(2.2f+s.Level*.2f,1,1.6f+s.Level*.1f),.25f+.5f*c.w,Vector3.up*DisasterVfxRules.Hash(i,163)*360);
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
            Clear();CameraShake.Shutdown();
            foreach(var material in new[]{_orange,_dust,_ejecta,_plasma,_molten,_fissure,_soil})if(material!=null)Object.Destroy(material);
            foreach(var mesh in _rocks)if(mesh!=null)Object.Destroy(mesh);_rocks.Clear();
            foreach(var mesh in _tailMeshes)if(mesh!=null)Object.Destroy(mesh);_tailMeshes.Clear();
            if(_dome!=null)Object.Destroy(_dome);
            foreach(var texture in new[]{_rockTexture,_soilTexture})if(texture!=null)Object.Destroy(texture);
            foreach(var clip in new[]{_impactSound,_quakeSound,_roarSound,_waveSound})if(clip!=null)Object.Destroy(clip);
            base.OnDestroy();
        }
    }
}
