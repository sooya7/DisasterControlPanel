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
            public float Surface, Radius, Progress, Inland;
            public bool Land;
        }
        private struct Plain
        {
            public float3 Position;
            public float Radius;
            public WaterDisasterRules.PlainCell Cell;
        }
        private readonly Dictionary<Entity, List<Source>> _tsunamiSources = new Dictionary<Entity, List<Source>>();
        private readonly List<Entity> _expired = new List<Entity>();
        private readonly Dictionary<Entity, List<List<Source>>> _walls = new Dictionary<Entity, List<List<Source>>>();
        private readonly Dictionary<Entity, List<float4>> _crests = new Dictionary<Entity, List<float4>>();
        private readonly Dictionary<Entity, List<Plain>> _plains = new Dictionary<Entity, List<Plain>>();
        private readonly Dictionary<Entity, List<float4>> _fronts = new Dictionary<Entity, List<float4>>();
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
            ClearPlans(); MatchedSources = 0; MaximumOffset = 0;
            base.OnGamePreload(purpose, mode);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            MatchedSources = 0; MaximumOffset = 0;
            if (_events.IsEmptyIgnoreFilter) { ClearPlans(); return; }
            if (_simulation.selectedSpeed <= 0 || !Available || _water.UseLegacyWaterSources || !World.GetOrCreateSystemManaged<DisasterUISystem>().InGame) return;
            _expired.Clear();
            foreach (var pair in _tsunamiSources)
                if (!EntityManager.Exists(pair.Key) || EntityManager.HasComponent<Deleted>(pair.Key)) _expired.Add(pair.Key);
            foreach (var e in _expired) { _tsunamiSources.Remove(e); _walls.Remove(e); _crests.Remove(e); _plains.Remove(e); _fronts.Remove(e); }
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
                    if (state.Kind == 10) { positions = FloodArea(state.Position, state.Level); if (positions.Count > 0) _plains[e] = FloodPlain(state.Position, state.Level); }
                    else { var wall = WaveWall(state.Position, change.m_Direction, state.Level, out positions); _walls[e] = wall; }
                    _tsunamiSources.Add(e, positions);
                    if(positions.Count==0)
                    {
                        duration.m_EndFrame=_simulation.frameIndex;EntityManager.SetComponentData(e,duration);
                        World.GetOrCreateSystemManaged<DisasterUISystem>().WaterSourceUnavailable();
                    }
                    Mod.Log.Info("Prepared safe water sources event=" + e.Index + ":" + e.Version + " count=" + positions.Count + " kind=" + state.Kind
                        + (_plains.TryGetValue(e, out var planned) ? " plain=" + planned.Count : "")
                        + (_walls.TryGetValue(e, out var wallPlan) ? " columns=" + wallPlan.Count + " land=" + LandCells(wallPlan) : ""));
                }
                if (state.Kind == 11 && _walls.TryGetValue(e, out var columns)) { ForceWall(e, state, forcingEnd, columns, cache); continue; }
                if (state.Kind == 10 && _plains.TryGetValue(e, out var plain)) ForcePlain(e, state, forcingEnd, plain, cache);
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
            // Ground the rising flood reaches through the plain search is in danger too.
            if(_plains.TryGetValue(e,out var plain))
                foreach(var cell in plain)
                    if(building.y<cell.Cell.Base+peak && math.distancesq(building.xz,cell.Position.xz)<(cell.Radius+40)*(cell.Radius+40))return true;
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
        // Freshly flooded ground at the edge of a rising flood (x, surface, z, freshness 0..1).
        internal bool TryGetFloodFront(Entity e, out List<float4> front) => _fronts.TryGetValue(e, out front) && front.Count > 0;

        private void ClearPlans() { _tsunamiSources.Clear(); _walls.Clear(); _crests.Clear(); _plains.Clear(); _fronts.Clear(); }

        private static int LandCells(List<List<Source>> columns)
        {
            int count = 0;
            foreach (var column in columns) foreach (var source in column) if (source.Land) count++;
            return count;
        }

        private void AddSource(NativeList<WaterSourceCache> cache, float3 position, float radius, float surface)
        {
            if (radius < 7 || surface - position.y < .05f) return;
            cache.Add(new WaterSourceCache { m_ConstantDepth = 2, m_Position = position - _terrain.positionOffset,
                m_Height = surface - position.y, m_Radius = radius });
            MatchedSources++; MaximumOffset = math.max(MaximumOffset, math.abs(surface - _water.SeaLevel));
        }

        private void ForceWall(Entity e, CustomDisasterState state, uint forcingEnd, List<List<Source>> columns, NativeList<WaterSourceCache> cache)
        {
            int level = state.Level;
            float sea = _water.SeaLevel;
            WaterDisasterRules.WaveWall(level, _simulation.frameIndex, state.ImpactFrame, forcingEnd, state.Phase,
                out float mainTravel, out float mainHeight, out float tailTravel, out float tailHeight, out float drawdown);
            if (!_crests.TryGetValue(e, out var crest)) _crests[e] = crest = new List<float4>();
            crest.Clear();
            float mainCrest = mainTravel < 0 ? float.MaxValue : WaterDisasterRules.CrestDistance(level, mainTravel);
            float tailCrest = tailTravel < 0 ? float.MaxValue : WaterDisasterRules.CrestDistance(level, tailTravel);
            float Height(Source source, float height) => WaterDisasterRules.CrestHeight(level, source.Land, source.Inland, source.Progress, height);
            void Crest(List<Source> column, float distance, float height)
            {
                if (height <= 0 || column[0].Progress + WaterDisasterRules.PathStep * .5f < distance) return;
                int i = column.Count - 1;
                for (int k = 0; k < column.Count; k++) if (column[k].Progress < distance) { i = math.max(0, k - 1); break; }
                var a = column[i];
                float3 at = a.Position; float radius = a.Radius, h = Height(a, height);
                // Between two neighbouring cells the crest glides instead of jumping 112 m at a time.
                if (i + 1 < column.Count && a.Progress >= distance)
                {
                    var b = column[i + 1];
                    float gap = a.Progress - b.Progress;
                    if (gap > 0 && gap <= WaterDisasterRules.PathStep * 1.05f)
                    {
                        float f = math.saturate((a.Progress - distance) / gap);
                        at = math.lerp(a.Position, b.Position, f);
                        radius = WaterDisasterRules.BlendRadius(a.Radius, b.Radius, gap, f);
                        h = math.lerp(h, Height(b, height), f);
                    }
                }
                AddSource(cache, at, radius, sea + h);
                // A lower body behind the crest gives the wall its mass instead of a thin ridge.
                if (i > 0) AddSource(cache, column[i - 1].Position, column[i - 1].Radius, sea + Height(column[i - 1], height) * .6f);
                if (i > 1) AddSource(cache, column[i - 2].Position, column[i - 2].Radius, sea + Height(column[i - 2], height) * .3f);
                // Visuals ride the measured water surface; this is only the floor for that height.
                if (h > .3f) crest.Add(new float4(at.x, math.max(sea, at.y) + .4f, at.z, h));
            }
            foreach (var column in columns)
            {
                Crest(column, mainCrest, mainHeight);
                Crest(column, tailCrest, tailHeight);
                // Shore pulls back before the wall arrives.
                int shore = column.Count - 1;
                while (shore > 0 && column[shore].Land) shore--;
                if (drawdown < 0 && !column[shore].Land && (mainTravel < 0 || mainCrest > column[shore].Progress + 600))
                    AddSource(cache, column[shore].Position, column[shore].Radius, column[shore].Surface + drawdown);
            }
        }

        private void ForcePlain(Entity e, CustomDisasterState state, uint forcingEnd, List<Plain> plain, NativeList<WaterSourceCache> cache)
        {
            if (!_fronts.TryGetValue(e, out var front)) _fronts[e] = front = new List<float4>();
            front.Clear();
            float rise = WaterDisasterRules.HeightOffset(10, state.Level, _simulation.frameIndex, state.ImpactFrame, forcingEnd, state.Phase);
            if (rise <= 0) return;
            foreach (var cell in plain)
            {
                if (!WaterDisasterRules.PlainActive(cell.Cell, rise)) continue;
                float surface = cell.Cell.Base + rise;
                AddSource(cache, cell.Position, cell.Radius, surface);
                float edge = WaterDisasterRules.PlainFront(cell.Cell, rise);
                if (edge > 0 && front.Count < 64) front.Add(new float4(cell.Position.x, surface, cell.Position.z, edge));
            }
        }

        private List<List<Source>> WaveWall(float3 target, float2 travel, int level, out List<Source> shore)
        {
            var columns = new List<List<Source>>(); shore = new List<Source>();
            var terrain = _terrain.GetHeightData();
            var surface = _water.GetSurfaceData(out var dependencies); dependencies.Complete();
            float half = WaterSystem.kMapSize * .5f - WaterSystem.kCellSize;
            var paths = WaterDisasterRules.WallPaths(target.x, target.z, travel.x, travel.y, level, half, _water.SeaLevel, (x, z) =>
            {
                var p = new float3(x, 0f, z);
                return WaterDisasterRules.IsSea(TerrainUtils.SampleHeight(ref terrain, p), WaterUtils.SampleDepth(ref surface, p), _water.SeaLevel);
            }, (x, z) => TerrainUtils.SampleHeight(ref terrain, new float3(x, 0f, z)));
            foreach (var path in paths)
            {
                var column = new List<Source>();
                foreach (var cell in path)
                {
                    var p = new float3(cell.X, 0, cell.Z); p.y = TerrainUtils.SampleHeight(ref terrain, p);
                    // Sea cells keep their footprint on existing sea; run-up cells keep theirs
                    // entirely below the wave surface, so nothing is poured behind a ridge.
                    float limit = cell.Surface - .3f;
                    float radius = cell.Land
                        ? WaterDisasterRules.SafeSourceRadius(p.x, p.z, WaterDisasterRules.LandRadius, half,
                            (x, z) => TerrainUtils.SampleHeight(ref terrain, new float3(x, 0, z)) < limit)
                        : WaterDisasterRules.SafeSourceRadius(p.x, p.z, WaterDisasterRules.CrestRadius, half,
                            (x, z) => WaterUtils.SampleDepth(ref surface, new float3(x, 0, z)) > .5f);
                    // Progress holds the cell's offshore distance for the wall.
                    if (radius >= 7) column.Add(new Source { Position = p, Surface = _water.SeaLevel, Radius = radius, Progress = cell.Distance, Inland = cell.Inland, Land = cell.Land });
                }
                if (column.Count == 0 || column[0].Land) continue;
                columns.Add(column);
                for (int i = column.Count - 1; i >= 0; i--) if (!column[i].Land) { shore.Add(column[i]); break; }
            }
            return columns;
        }

        private List<Plain> FloodPlain(float3 target, int level)
        {
            var plain = new List<Plain>();
            var terrain = _terrain.GetHeightData();
            var surface = _water.GetSurfaceData(out var dependencies); dependencies.Complete();
            float half = WaterSystem.kMapSize * .5f - WaterSystem.kCellSize;
            var cells = WaterDisasterRules.FloodPlain(target.x, target.z, level, half, (x, z) =>
            {
                var p = new float3(x, 0f, z);
                return (TerrainUtils.SampleHeight(ref terrain, p), WaterUtils.SampleDepth(ref surface, p));
            });
            // One source per 128 m bin, lowest ground first; each disk lies wholly below the
            // level at which its cell floods, so it never fills a hollow the water cannot reach.
            var bins = new HashSet<(int, int)>();
            int attempts = 0;
            foreach (var cell in cells)
            {
                if (plain.Count >= WaterDisasterRules.PlainLimit || ++attempts > 2400) break;
                var bin = ((int)math.floor(cell.X / 128f), (int)math.floor(cell.Z / 128f));
                if (bins.Contains(bin)) continue;
                var p = new float3(cell.X, 0, cell.Z); p.y = TerrainUtils.SampleHeight(ref terrain, p);
                float level0 = cell.Level + .05f;
                float radius = WaterDisasterRules.SafeSourceRadius(cell.X, cell.Z, 96f, half,
                    (x, z) => TerrainUtils.SampleHeight(ref terrain, new float3(x, 0, z)) <= level0);
                if (radius < 7) continue;
                bins.Add(bin);
                plain.Add(new Plain { Position = p, Radius = radius, Cell = cell });
            }
            return plain;
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
    }
}
