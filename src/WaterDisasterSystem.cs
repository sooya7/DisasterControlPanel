using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.Common;
using Game.Events;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace DisasterControlPanel
{
    // 1.6.2 new water no longer consumes WaterLevelChange or retains sea-source entities.
    // Adjust only the freshly rebuilt simulation cache; ECS sources and SeaLevel stay intact.
    public sealed class WaterDisasterSystem : GameSystemBase
    {
        private static readonly FieldInfo SourceHandle = typeof(WaterSystem).GetField("m_SourceHandle", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo SourceCache = typeof(WaterSystem).GetProperty("CurrentJobSourceCache", BindingFlags.Instance | BindingFlags.NonPublic);
        private EntityQuery _events, _sources;
        private WaterSystem _water;
        private TerrainSystem _terrain;
        private SimulationSystem _simulation;
        private struct Source
        {
            public float3 Position;
            public float Surface, Radius, Progress;
        }
        private readonly Dictionary<Entity, List<Source>> _tsunamiSources = new Dictionary<Entity, List<Source>>();
        private readonly List<Entity> _expired = new List<Entity>();
        private readonly Dictionary<Entity, List<List<Source>>> _walls = new Dictionary<Entity, List<List<Source>>>();
        private readonly Dictionary<Entity, List<float4>> _crests = new Dictionary<Entity, List<float4>>();
        public int MatchedSources { get; private set; }
        public float MaximumOffset { get; private set; }
        public bool Available => SourceHandle?.FieldType == typeof(JobHandle) && SourceCache?.PropertyType == typeof(NativeList<WaterSourceCache>);

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            _water = World.GetOrCreateSystemManaged<WaterSystem>();
            _terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            _simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            _events = GetEntityQuery(ComponentType.ReadOnly<CustomDisasterState>(), ComponentType.ReadOnly<WaterLevelChange>(), ComponentType.ReadOnly<Duration>(), ComponentType.Exclude<Deleted>());
            _sources = GetEntityQuery(ComponentType.ReadOnly<WaterSourceData>(), ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            if (!Available) Mod.Log.Error("This game version does not expose the expected water source cache; manual water forcing is unavailable.");
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            _tsunamiSources.Clear(); _walls.Clear(); _crests.Clear(); MatchedSources = 0; MaximumOffset = 0;
            base.OnGamePreload(purpose, mode);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            MatchedSources = 0; MaximumOffset = 0;
            if (_events.IsEmptyIgnoreFilter) { _tsunamiSources.Clear(); _walls.Clear(); _crests.Clear(); return; }
            if (_simulation.selectedSpeed <= 0 || !Available || _water.UseLegacyWaterSources || !World.GetOrCreateSystemManaged<DisasterUISystem>().InGame) return;
            _expired.Clear();
            foreach (var pair in _tsunamiSources)
                if (!EntityManager.Exists(pair.Key) || EntityManager.HasComponent<Deleted>(pair.Key)) _expired.Add(pair.Key);
            foreach (var e in _expired) { _tsunamiSources.Remove(e); _walls.Remove(e); _crests.Remove(e); }
            ((JobHandle)SourceHandle.GetValue(_water)).Complete();
            var cache = (NativeList<WaterSourceCache>)SourceCache.GetValue(_water);
            using (var events = _events.ToEntityArray(Allocator.Temp))
            foreach (var e in events)
            {
                var state = EntityManager.GetComponentData<CustomDisasterState>(e);
                var change = EntityManager.GetComponentData<WaterLevelChange>(e);
                var duration = EntityManager.GetComponentData<Duration>(e);
                uint forcingEnd = math.min(duration.m_EndFrame, state.ImpactFrame + (uint)(state.Depth * 60));
                if (!_tsunamiSources.TryGetValue(e, out var positions))
                {
                    if (state.Kind == 10) positions = FloodArea(state.Position, state.Level);
                    else { var wall = WaveWall(state.Position, change.m_Direction, state.Level, out positions); _walls[e] = wall; }
                    _tsunamiSources.Add(e, positions);
                    if(positions.Count==0)
                    {
                        duration.m_EndFrame=_simulation.frameIndex;EntityManager.SetComponentData(e,duration);
                        World.GetOrCreateSystemManaged<DisasterUISystem>().WaterSourceUnavailable();
                    }
                    Mod.Log.Info("Prepared safe water sources event=" + e.Index + ":" + e.Version + " count=" + positions.Count + " kind=" + state.Kind);
                }
                if (state.Kind == 11 && _walls.TryGetValue(e, out var columns)) { ForceWall(e, state, forcingEnd, columns, cache); continue; }
                foreach (var source in positions)
                {
                    float offset = state.Kind == 10
                        ? WaterDisasterRules.HeightOffset(10, state.Level, _simulation.frameIndex, state.ImpactFrame, forcingEnd, state.Phase)
                        : WaterDisasterRules.TravelingOffset(state.Level, _simulation.frameIndex, state.ImpactFrame, forcingEnd, state.Phase, source.Progress);
                    if (offset == 0 || source.Radius < 7) continue;
                    cache.Add(new WaterSourceCache { m_ConstantDepth = 2, m_Position = source.Position - _terrain.positionOffset,
                        m_Height = math.max(.01f, source.Surface + offset - source.Position.y), m_Radius = source.Radius });
                    MatchedSources++; MaximumOffset = math.max(MaximumOffset, math.abs(offset));
                }
            }
        }

        internal bool TryResolveTarget(int kind, float3 target, out float3 origin, out int direction)
        {
            origin = target; direction = 0;
            if (!_water.UseLegacyWaterSources && !Available) return false;
            if (!_water.UseLegacyWaterSources)
            {
                var terrain = _terrain.GetHeightData();
                var surface = _water.GetSurfaceData(out var dependencies); dependencies.Complete();
                float half = WaterSystem.kMapSize * .5f - WaterSystem.kCellSize;
                var water = WaterDisasterRules.NearestWater(target.x, target.z, half, kind == 11, _water.SeaLevel, (x,z) =>
                {
                    var p = new float3(x,0,z);
                    return (TerrainUtils.SampleHeight(ref terrain,p), WaterUtils.SampleDepth(ref surface,p));
                });
                if (!water.HasValue) return false;
                var point = water.Value; origin = new float3(point.x, point.surface, point.z);
                direction = WaterDisasterRules.AutoDirection(point.x, point.z, target.x, target.z, half,
                    (x,z) => WaterUtils.SampleDepth(ref surface,new float3(x,0,z)) <= .5f);
                return true;
            }
            float nearest = float.MaxValue; bool found = false;
            using (var sources = _sources.ToEntityArray(Allocator.Temp))
            foreach (var e in sources)
            {
                var source = EntityManager.GetComponentData<WaterSourceData>(e);
                var transform = EntityManager.GetComponentData<Game.Objects.Transform>(e);
                float distance = math.distancesq(transform.m_Position.xz, target.xz);
                if (source.m_ConstantDepth != (kind == 10 ? 2 : 3) || source.m_Polluted > 0f || source.m_Radius <= 0f || distance >= nearest) continue;
                nearest = distance; origin = transform.m_Position; found = true;
            }
            if (found) direction = WaterDisasterRules.DirectionToward(-origin.x, -origin.z);
            return found;
        }

        internal bool Threatens(Entity e, CustomDisasterState state, float3 building)
        {
            if(!_tsunamiSources.TryGetValue(e,out var sources) || sources.Count==0)return false;
            var terrain=_terrain.GetHeightData();
            float peak=WaterDisasterRules.PeakHeight(state.Kind,state.Level);
            // Evaluate only the nearest viable sources, rather than casting from every source
            // to every building every update. Existing Work caches each building's result.
            var nearest=new Source[3];var distances=new[] {float.MaxValue,float.MaxValue,float.MaxValue};
            foreach(var source in sources)
            {
                if(building.y>=source.Surface+peak)continue;
                float d=math.distancesq(building.xz,source.Position.xz);
                for(int i=0;i<3;i++) if(d<distances[i])
                {
                    for(int j=2;j>i;j--){distances[j]=distances[j-1];nearest[j]=nearest[j-1];}
                    distances[i]=d;nearest[i]=source;break;
                }
            }
            for(int i=0;i<3;i++) if(distances[i]<float.MaxValue && WaterDisasterRules.IsThreatened(nearest[i].Position.x,nearest[i].Position.z,
                nearest[i].Surface+peak,building.x,building.z,building.y,1800+state.Level*220,
                (x,z)=>TerrainUtils.SampleHeight(ref terrain,new float3(x,0,z))))return true;
            return false;
        }
        internal bool HasSources(Entity e) => _tsunamiSources.TryGetValue(e,out var sources) && sources.Count>0;

#if DCP_DEV
        internal object Diagnostics()
        {
            var sources = new List<object>();
            using (var entities = _sources.ToEntityArray(Allocator.Temp))
            foreach (var e in entities)
            {
                var s = EntityManager.GetComponentData<WaterSourceData>(e);
                var p = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
                sources.Add(new { id = e.Index, version = e.Version, x = p.x, y = p.y, z = p.z, type = s.m_ConstantDepth, height = s.m_Height, radius = s.m_Radius, modifier = s.m_Modifier, pollution = s.m_Polluted });
            }
            return new { useLegacyWaterSources = _water.UseLegacyWaterSources, seaLevel = _water.SeaLevel, available = Available, matchedSources = MatchedSources, maximumOffset = MaximumOffset, sources };
        }
#endif


        // Current crest points (x, water surface + 0.4, z, height of the wave) for visuals.
        internal bool TryGetCrest(Entity e, out List<float4> crest) => _crests.TryGetValue(e, out crest) && crest.Count > 0;

        private void ForceWall(Entity e, CustomDisasterState state, uint forcingEnd, List<List<Source>> columns, NativeList<WaterSourceCache> cache)
        {
            WaterDisasterRules.WaveWall(state.Level, _simulation.frameIndex, state.ImpactFrame, forcingEnd, state.Phase,
                out float mainTravel, out float mainHeight, out float tailTravel, out float tailHeight, out float drawdown);
            if (!_crests.TryGetValue(e, out var crest)) _crests[e] = crest = new List<float4>();
            crest.Clear();
            float mainCrest = mainTravel < 0 ? float.MaxValue : WaterDisasterRules.CrestDistance(state.Level, mainTravel);
            float tailCrest = tailTravel < 0 ? float.MaxValue : WaterDisasterRules.CrestDistance(state.Level, tailTravel);
            void Add(Source source, float offset)
            {
                if (offset == 0 || source.Radius < 7) return;
                cache.Add(new WaterSourceCache { m_ConstantDepth = 2, m_Position = source.Position - _terrain.positionOffset,
                    m_Height = math.max(.01f, source.Surface + offset - source.Position.y), m_Radius = source.Radius });
                MatchedSources++; MaximumOffset = math.max(MaximumOffset, math.abs(offset));
            }
            void Crest(List<Source> column, float distance, float height)
            {
                int i = -1;
                if (column[0].Progress + WaterDisasterRules.PathStep * .5f >= distance)
                {
                    i = column.Count - 1;
                    for (int k = 0; k < column.Count; k++) if (column[k].Progress <= distance + WaterDisasterRules.PathStep * .5f) { i = k; break; }
                }
                if (i < 0 || height <= 0) return;
                Add(column[i], height);
                // A lower body behind the crest gives the wall its mass instead of a thin ridge.
                if (i > 0) Add(column[i - 1], height * .6f);
                if (i > 1) Add(column[i - 2], height * .3f);
                crest.Add(new float4(column[i].Position.x, column[i].Surface + .4f, column[i].Position.z, height));
            }
            foreach (var column in columns)
            {
                Crest(column, mainCrest, mainHeight);
                Crest(column, tailCrest, tailHeight);
                // Shore pulls back before the wall arrives.
                if (drawdown < 0 && (mainTravel < 0 || mainCrest > column[column.Count - 1].Progress + 600)) Add(column[column.Count - 1], drawdown);
            }
        }

        private List<List<Source>> WaveWall(float3 target, float2 travel, int level, out List<Source> shore)
        {
            var columns = new List<List<Source>>(); shore = new List<Source>();
            var terrain = _terrain.GetHeightData();
            var surface = _water.GetSurfaceData(out var dependencies); dependencies.Complete();
            float half = WaterSystem.kMapSize * .5f - WaterSystem.kCellSize;
            var paths = WaterDisasterRules.WallPaths(target.x, target.z, travel.x, travel.y, level, half, (x, z) =>
            {
                var p = new float3(x, 0f, z);
                return WaterDisasterRules.IsSea(TerrainUtils.SampleHeight(ref terrain, p), WaterUtils.SampleDepth(ref surface, p), _water.SeaLevel);
            });
            foreach (var path in paths)
            {
                var column = new List<Source>();
                foreach (var cell in path)
                {
                    var p = new float3(cell.x, 0, cell.z); p.y = TerrainUtils.SampleHeight(ref terrain, p);
                    float radius = WaterDisasterRules.SafeSourceRadius(p.x, p.z, WaterDisasterRules.CrestRadius, half,
                        (x, z) => WaterUtils.SampleDepth(ref surface, new float3(x, 0, z)) > .5f);
                    // Progress holds the cell's offshore distance for the wall.
                    if (radius >= 7) column.Add(new Source { Position = p, Surface = _water.SeaLevel, Radius = radius, Progress = cell.distance });
                }
                if (column.Count == 0) continue;
                columns.Add(column); shore.Add(column[column.Count - 1]);
            }
            return columns;
        }

        private List<Source> FloodArea(float3 target, int level)
        {
            var positions = new List<Source>();
            var terrain = _terrain.GetHeightData();
            var surface = _water.GetSurfaceData(out var dependencies); dependencies.Complete();
            var area = WaterDisasterRules.FloodArea(target.x, target.z, level, WaterSystem.kMapSize * .5f - WaterSystem.kCellSize, target.y, (x, z) =>
            {
                var p = new float3(x, 0f, z);
                return (TerrainUtils.SampleHeight(ref terrain, p), WaterUtils.SampleDepth(ref surface, p));
            });
            foreach (var point in area)
            {
                var p = new float3(point.x, 0f, point.z);
                p.y = TerrainUtils.SampleHeight(ref terrain, p);
                float radius = WaterDisasterRules.SafeSourceRadius(p.x, p.z, WaterDisasterRules.SourceRadius(level), WaterSystem.kMapSize*.5f-WaterSystem.kCellSize,
                    (x,z) => WaterUtils.SampleDepth(ref surface,new float3(x,0,z))>.5f);
                if(radius>=7) positions.Add(new Source {Position=p,Surface=p.y+WaterUtils.SampleDepth(ref surface,p),Radius=radius});
            }
            return positions;
        }

        private List<Source> SeaFront(float3 target, float2 travel, int level)
        {
            var positions = new List<Source>();
            var terrain = _terrain.GetHeightData();
            var surface = _water.GetSurfaceData(out var dependencies); dependencies.Complete();
            var line = WaterDisasterRules.TsunamiFront(target.x, target.z, travel.x, travel.y, level, WaterSystem.kMapSize * .5f - WaterSystem.kCellSize, (x, z) =>
            {
                var p = new float3(x, 0f, z);
                return WaterDisasterRules.IsSea(TerrainUtils.SampleHeight(ref terrain, p), WaterUtils.SampleDepth(ref surface, p), _water.SeaLevel);
            });
            foreach (var point in line)
            for(int row=0;row<3;row++)
            {
                float progress=row*.5f;
                var p=new float3(point.x,0,point.z)+new float3(travel.x,0,travel.y)*(WaterDisasterRules.OffshoreDistance(level)*progress);
                float half=WaterSystem.kMapSize*.5f-WaterSystem.kCellSize;
                if(math.abs(p.x)>half || math.abs(p.z)>half)continue;
                if(row>0 && !WaterDisasterRules.WetSegment(point.x,point.z,p.x,p.z,(x,z)=>WaterUtils.SampleDepth(ref surface,new float3(x,0,z))>.5f))continue;
                p.y=TerrainUtils.SampleHeight(ref terrain,p);
                if(!WaterDisasterRules.IsSea(p.y,WaterUtils.SampleDepth(ref surface,p),_water.SeaLevel))continue;
                float radius=WaterDisasterRules.SafeSourceRadius(p.x,p.z,WaterDisasterRules.SourceRadius(level),half,
                    (x,z)=>WaterUtils.SampleDepth(ref surface,new float3(x,0,z))>.5f);
                if(radius>=7)positions.Add(new Source {Position=p,Surface=_water.SeaLevel,Radius=radius,Progress=progress});
            }
            return positions;
        }
    }
}
