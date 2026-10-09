using System;
using System.Collections.Generic;

namespace DisasterControlPanel
{
    internal static class WaterDisasterRules
    {
        public static float FloodRamp(uint frame, uint impact, uint end)
            => (float)Math.Max(0d, Math.Min(1d, ((long)frame - impact) / Math.Max(1d, ((long)end - impact) * .65d)));

        private static float Scale(int level) => (float)Math.Pow((Math.Max(1, Math.Min(10, level)) - 1) / 9d, 2);
        public static float PeakHeight(int kind, int level) => kind == 10 ? 1.5f + 88.5f * Scale(level) : 3f + 97f * Scale(level);
        public static float SourceRadius(int level) => 192f + 144f * Scale(level);
        public static int FloodExtent(int level) => (int)Math.Round(6f * Scale(level)) * 448;
        public static int FrontHalfWidth(int level) => (int)Math.Round(10f * Scale(level)) * 448;
        public static int OffshoreDistance(int level) => 224 + (int)Math.Round(7f * Scale(level)) * 224;

        public static float HeightOffset(int kind, int level, uint frame, uint impact, uint end, int phase)
        {
            if (frame < impact || frame >= end || phase == 2 || end <= impact) return 0f;
            float peak = PeakHeight(kind, level);
            if (kind == 10) return peak * FloodRamp(frame, impact, end);
            double t = ((long)frame - impact) / (double)(end - impact);
            // A short drawdown, one broad main crest, then a smaller trailing wave.
            if (t < .1) return -(float)Math.Sin(Math.PI * t / .1) * Math.Min(4f, peak * .08f);
            double pulse = t < .65 ? Math.Sin(Math.PI * (t - .1) / .55)
                : t >= .7 && t < .95 ? .5 * Math.Sin(Math.PI * (t - .7) / .25) : 0;
            return peak * (float)(pulse * pulse);
        }

        public static List<(float x, float z)> FloodArea(float x, float z, int level, float halfMap, float baseline, Func<float, float, (float terrain, float depth)> sample)
        {
            const int step = 56;
            bool Wet(float sx, float sz, out float surface)
            {
                surface = 0;
                if (Math.Abs(sx) > halfMap || Math.Abs(sz) > halfMap) return false;
                var p = sample(sx, sz); surface = p.terrain + p.depth;
                return p.depth > .5f;
            }
            var result = new List<(float x, float z)>();
            if (!Wet(x, z, out _)) return result;
            int extent = FloodExtent(level);
            var queue = new Queue<(int x, int z)>();
            var visited = new HashSet<(int x, int z)> { (0, 0) };
            var bins = new HashSet<(int x, int z)>();
            queue.Enqueue((0, 0));
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                if (bins.Add(((int)Math.Round(p.x / 224d), (int)Math.Round(p.z / 224d)))) result.Add((x + p.x, z + p.z));
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    var n = (x: p.x + dx * step, z: p.z + dz * step);
                    if (n.x * n.x + n.z * n.z > extent * extent || visited.Contains(n)) continue;
                    Wet(x + p.x, z + p.z, out float previous);
                    bool connected = true;
                    // Small steps and diagonals follow bends; compare adjacent water surfaces,
                    // not every upstream cell against a single downstream sea-level baseline.
                    for (int i = 1; i <= 12; i++)
                    {
                        if (!Wet(x + p.x + dx * step * i / 12f, z + p.z + dz * step * i / 12f, out float height)
                            || Math.Abs(height - previous) > 2f) { connected = false; break; }
                        previous = height;
                    }
                    if (connected) { visited.Add(n); queue.Enqueue(n); }
                }
            }
            if (result.Count <= 256) return result;
            var bounded = new List<(float x, float z)>();
            for (int i = 0; i < 256; i++) bounded.Add(result[i * (result.Count - 1) / 255]);
            return bounded;
        }

        // Restrict the complete native circular footprint to previously wet cells.
        // Centers alone are insufficient: AddConstant can otherwise inject behind a ridge.
        public static float SafeSourceRadius(float x, float z, float requested, float halfMap, Func<float, float, bool> wet)
        {
            if (!wet(x, z)) return 0;
            float safe = Math.Min(requested, Math.Min(halfMap - Math.Abs(x), halfMap - Math.Abs(z)));
            for (int iz = (int)Math.Floor((z - requested) / 7); iz <= (int)Math.Ceiling((z + requested) / 7); iz++)
            for (int ix = (int)Math.Floor((x - requested) / 7); ix <= (int)Math.Ceiling((x + requested) / 7); ix++)
            {
                float sx = (ix + .5f) * 7, sz = (iz + .5f) * 7;
                float distance = (float)Math.Sqrt((sx-x)*(sx-x)+(sz-z)*(sz-z));
                if (distance >= safe + 10) continue;
                if (Math.Abs(sx)>halfMap || Math.Abs(sz)>halfMap || !wet(sx, sz)) safe = Math.Min(safe, distance - 10);
            }
            return safe >= 7 ? safe : 0;
        }

        public static bool WetSegment(float x,float z,float targetX,float targetZ,Func<float,float,bool> wet)
        {
            float dx=targetX-x,dz=targetZ-z;int steps=Math.Max(1,(int)Math.Ceiling(Math.Sqrt(dx*dx+dz*dz)/7));
            for(int i=0;i<=steps;i++)if(!wet(x+dx*i/steps,z+dz*i/steps))return false;
            return true;
        }

        public static float TravelingOffset(int level, uint frame, uint impact, uint end, int phase, float progress)
        {
            if (end <= impact || frame < impact || frame >= end || phase == 2) return 0;
            // Three offshore strips share a waveform with a coastward phase delay.
            double t = ((long)frame-impact)/(double)(end-impact);
            double local = (t - .18 * progress) / .82;
            if (local < 0 || local >= 1) return 0;
            return HeightOffset(11, level, (uint)(local*60000), 0, 60000, phase);
        }

        public static bool IsThreatened(float sourceX, float sourceZ, float surface, float targetX, float targetZ, float targetHeight, float reach, Func<float,float,float> terrain)
        {
            float dx=targetX-sourceX, dz=targetZ-sourceZ;
            float distance=(float)Math.Sqrt(dx*dx+dz*dz);
            if (targetHeight >= surface || distance > reach) return false;
            int steps=Math.Max(1,(int)Math.Ceiling(distance/7));
            for(int i=1;i<=steps;i++) if(terrain(sourceX+dx*i/steps,sourceZ+dz*i/steps)>=surface) return false;
            return true;
        }

        public static bool Upstream(float x, float z, float dx, float dz)
            => x * dx + z * dz < 0f;

        public static bool IsSea(float terrain, float depth, float seaLevel)
            => depth > .5f && terrain < seaLevel && Math.Abs(terrain + depth - seaLevel) < 2f;

        public static bool IsFloodSource(int type, float height, float radius, float pollution)
            => type != 3 && height > 0f && radius > 0f && pollution <= 0f;

        public static (float x, float z, float surface)? NearestWater(float x, float z, float halfMap, bool seaOnly, float seaLevel, Func<float, float, (float terrain, float depth)> sample)
        {
            if (Math.Abs(x) > halfMap || Math.Abs(z) > halfMap) return null;
            (float x, float z, float surface)? best = null;
            float distance = 4096f * 4096f + 1f;
            void Try(float sx, float sz)
            {
                float d = (sx-x)*(sx-x) + (sz-z)*(sz-z);
                if (d >= distance || Math.Abs(sx) > halfMap || Math.Abs(sz) > halfMap) return;
                var water = sample(sx, sz);
                if (water.depth <= .5f || (seaOnly && !IsSea(water.terrain, water.depth, seaLevel))) return;
                distance = d; best = (sx, sz, water.terrain + water.depth);
            }
            Try(x, z);
            if (distance == 0f) return best;
            for (int dz = -4096; dz <= 4096; dz += 128)
                for (int dx = -4096; dx <= 4096; dx += 128) Try(x + dx, z + dz);
            if (best.HasValue)
            {
                var p = best.Value;
                for (int dz = -128; dz <= 128; dz += 32)
                    for (int dx = -128; dx <= 128; dx += 32) Try(p.x + dx, p.z + dz);
            }
            return best;
        }

        public static int DirectionToward(float dx, float dz)
            => ((int)Math.Round(Math.Atan2(dx, dz) * 180 / Math.PI) + 360) % 360;

        public static int AutoDirection(float x, float z, float targetX, float targetZ, float halfMap, Func<float, float, bool> isDry)
        {
            if ((targetX-x)*(targetX-x)+(targetZ-z)*(targetZ-z) > 64f*64f)
                return DirectionToward(targetX-x, targetZ-z);
            float distance = float.MaxValue; int direction = 0;
            for (int dz = -1024; dz <= 1024; dz += 64)
                for (int dx = -1024; dx <= 1024; dx += 64)
                {
                    float d = dx*dx + dz*dz;
                    if (d == 0f || d >= distance || Math.Abs(x+dx) > halfMap || Math.Abs(z+dz) > halfMap || !isDry(x+dx,z+dz)) continue;
                    distance = d; direction = DirectionToward(dx,dz);
                }
            return direction;
        }

        public static List<(float x, float z)> TsunamiFront(float x, float z, float dx, float dz, int level, float halfMap, Func<float, float, bool> isSea)
        {
            bool SeaAt(float sx, float sz) => Math.Abs(sx) <= halfMap && Math.Abs(sz) <= halfMap && isSea(sx, sz);
            var result = new List<(float x, float z)>();
            if (!SeaAt(x, z)) return result;
            int width = FrontHalfWidth(level), offshore = OffshoreDistance(level);
            for (int cross = -width; cross <= width; cross += 448)
            {
                // Search each column offshore, allowing curved coasts without sources on land.
                for (int distance = offshore; distance >= 0; distance -= 224)
                {
                    float sx = x - dx * distance + dz * cross;
                    float sz = z - dz * distance - dx * cross;
                    if (!SeaAt(sx, sz)) continue;
                    result.Add((sx, sz)); break;
                }
            }
            return result;
        }
    
        // ---- 0.2.2 travelling wave wall with run-up (Cities: Skylines 1 style) ----
        // The crest starts far offshore and is carried coastward as a narrow line of moving
        // sources, steepening as it nears the coast, then keeps rolling inland over low ground
        // until the terrain rises above the decaying wave. High ground stays dry.
        public const float CrestRadius = 110f, CrossSpacing = 160f, PathStep = 112f, BeyondOrigin = 900f, LandStep = 56f, LandRadius = 64f;
        public static int TsunamiStart(int level) => 800 + (int)Math.Round(8f * Scale(level)) * 448;
        public static int WallHalfWidth(int level) => Math.Max(448, FrontHalfWidth(level));
        public static float RunUp(int level) => 250f + 2750f * Scale(level);
        // Share of the crest height left after this far inland (fraction of the run-up).
        public static float LandDecay(float inland) => inland <= 0 ? 1 : inland >= 1 ? 0 : (float)Math.Pow(1 - inland, 1.3);
        // Waves steepen as they reach shallow water: 60% of full height far out, 100% at the coast.
        public static float Shoal(int level, float distance) => .6f + .4f * Sat(1 - distance / Math.Max(1f, TsunamiStart(level)));
        private static float Sat(double x) => (float)Math.Max(0, Math.Min(1, x));

        internal struct WallCell
        {
            public float X, Z, Distance, Inland, Surface;
            public bool Land;
        }

        // t in 0..1 of the forcing window. Main crest: travels 4%..50% (decelerating over land),
        // holds its reach to 58%, dies by 72%. Tail wave at half height: 60%..97%.
        // Drawdown at the shore first, the sea pulling back before the wall arrives.
        public static void WaveWall(int level, uint frame, uint impact, uint end, int phase,
            out float mainTravel, out float mainHeight, out float tailTravel, out float tailHeight, out float drawdown)
        {
            mainTravel = tailTravel = -1; mainHeight = tailHeight = drawdown = 0;
            if (end <= impact || frame < impact || frame >= end || phase == 2) return;
            double t = ((long)frame - impact) / (double)(end - impact);
            float peak = PeakHeight(11, level);
            if (t < .3) drawdown = -(float)Math.Sin(Math.PI * Math.Min(1, t / .3)) * Math.Min(4f, peak * .08f);
            if (t >= .04 && t < .72)
            {
                mainTravel = (float)(1 - Math.Pow(1 - Sat((t - .04) / .46), 1.5));
                mainHeight = peak * Sat((t - .04) / .04) * (t < .58 ? 1 : 1 - Sat((t - .58) / .14));
            }
            if (t >= .6 && t < .97)
            {
                tailTravel = (float)(1 - Math.Pow(1 - Sat((t - .6) / .25), 1.5));
                tailHeight = peak * .5f * Sat((t - .6) / .04) * (t < .85 ? 1 : 1 - Sat((t - .85) / .12));
            }
        }

        // Distance (positive = offshore of the clicked coast point) the crest has reached.
        public static float CrestDistance(int level, float travel) => TsunamiStart(level) - travel * (TsunamiStart(level) + BeyondOrigin + RunUp(level));

        // Columns perpendicular to the coast, each a list of cells ordered offshore to inland.
        // Sea cells over land are skipped, so islands and headlands break the wall like real
        // coastline does. From the column's last sea cell the path continues inland in short
        // steps while every 7 m sample stays below the decaying wave surface.
        public static List<List<WallCell>> WallPaths(float x, float z, float dx, float dz, int level, float halfMap, float seaLevel,
            Func<float, float, bool> isSea, Func<float, float, float> terrain)
        {
            bool Inside(float sx, float sz) => Math.Abs(sx) <= halfMap && Math.Abs(sz) <= halfMap;
            var columns = new List<List<WallCell>>();
            int width = WallHalfWidth(level), start = TsunamiStart(level);
            float peak = PeakHeight(11, level), runUp = RunUp(level);
            for (float cross = -width; cross <= width + .1f; cross += CrossSpacing)
            {
                var column = new List<WallCell>();
                for (float d = start; d >= -BeyondOrigin; d -= PathStep)
                {
                    float sx = x - dx * d + dz * cross, sz = z - dz * d - dx * cross;
                    if (Inside(sx, sz) && isSea(sx, sz)) column.Add(new WallCell { X = sx, Z = sz, Distance = d, Surface = seaLevel });
                    else if (column.Count > 0 && d < 0) break; // reached this column's shore
                }
                if (column.Count == 0) continue;
                var shore = column[column.Count - 1];
                for (float inland = 7; inland <= runUp; inland += 7)
                {
                    float sx = shore.X + dx * inland, sz = shore.Z + dz * inland;
                    float surface = seaLevel + peak * LandDecay(inland / runUp);
                    if (!Inside(sx, sz) || terrain(sx, sz) >= surface - .3f) break; // blocked by rising ground
                    if (inland % LandStep < 7)
                        column.Add(new WallCell { X = sx, Z = sz, Distance = shore.Distance - inland, Inland = inland, Surface = surface, Land = true });
                }
                columns.Add(column);
            }
            return columns;
        }

        // Last cell the crest has reached in a column (the crest lies between it and the next),
        // or -1 when the crest has not reached the column yet. Past the end the last cell keeps pushing.
        public static int CrestCell(List<WallCell> column, float crest)
        {
            if (column.Count == 0 || crest > column[0].Distance + PathStep * .5f) return -1;
            for (int i = 0; i < column.Count; i++) if (column[i].Distance < crest) return Math.Max(0, i - 1);
            return column.Count - 1;
        }

        // Smooth crest motion between two neighbouring cells: the interpolated disk stays inside
        // the union of the two verified footprints when its radius is reduced by the half gap.
        // Near either end a disk shrunk by the distance moved also stays inside that end's disk.
        public static float BlendRadius(float radiusA, float radiusB, float gap, float t)
        {
            float r = Math.Min(radiusA, radiusB), h = gap * .5f;
            float middle = r > h ? (float)Math.Sqrt(r * r - h * h) : 0;
            return Math.Max(middle, Math.Max(radiusA - t * gap, radiusB - (1 - t) * gap));
        }

        // Crest height carried by a cell: shoaling at sea, decaying with distance over land.
        public static float CrestHeight(int level, bool land, float inland, float distance, float height)
            => land ? height * LandDecay(inland / RunUp(level)) : height * Shoal(level, distance);

        // ---- 0.2.2 flood plain: water creeps into connected low ground as the level rises ----
        public static float LandReach(int level) => 250f + 1750f * Scale(level);
        public const int PlainStep = 64, PlainLimit = 320;

        internal struct PlainCell
        {
            public float X, Z, Level, Base;
        }

        // Priority flood (minimax path) from the clicked water: each dry node records the
        // lowest water level that reaches it along any path, and the water surface it is
        // fed from. Only nodes a full-height flood can reach are kept; ridges higher than the
        // flood protect everything behind them.
        public static List<PlainCell> FloodPlain(float x, float z, int level, float halfMap, Func<float, float, (float terrain, float depth)> sample)
        {
            var result = new List<PlainCell>();
            if (Math.Abs(x) > halfMap || Math.Abs(z) > halfMap) return result;
            var origin = sample(x, z);
            if (origin.depth <= .5f) return result;
            float peak = PeakHeight(10, level), waterLimit = FloodExtent(level) + 224, landLimit = FloodExtent(level) + LandReach(level);
            var best = new Dictionary<(int, int), float>();
            var bases = new Dictionary<(int, int), float>();
            var dry = new HashSet<(int, int)>();
            var heap = new MinHeap();
            // Keys are heights above the feeding water surface: the order in which ground floods.
            var levels = new Dictionary<(int, int), float>();
            float surface = origin.terrain + origin.depth;
            best[(0, 0)] = 0; bases[(0, 0)] = surface; levels[(0, 0)] = surface;
            heap.Push(0, 0, 0);
            var done = new HashSet<(int, int)>();
            while (heap.Count > 0)
            {
                var (_, gx, gz) = heap.Pop();
                if (!done.Add((gx, gz))) continue;
                float px = x + gx * PlainStep, pz = z + gz * PlainStep, baseHere = bases[(gx, gz)], levelHere = levels[(gx, gz)];
                bool viaLand = dry.Contains((gx, gz));
                if (viaLand) result.Add(new PlainCell { X = px, Z = pz, Level = levelHere, Base = baseHere });
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    var n = (gx + dx, gz + dz);
                    if (done.Contains(n)) continue;
                    float nx = x + n.Item1 * PlainStep, nz = z + n.Item2 * PlainStep;
                    float distance = (float)Math.Sqrt((nx - x) * (nx - x) + (nz - z) * (nz - z));
                    if (Math.Abs(nx) > halfMap || Math.Abs(nz) > halfMap || distance > landLimit) continue;
                    // Highest ground or water surface crossed on the way, sampled at the native 7 m grid.
                    float crossing = levelHere, previous = baseHere; bool wet = !viaLand;
                    for (int i = 1; i <= 9; i++)
                    {
                        var p = sample(px + dx * PlainStep * i / 9f, pz + dz * PlainStep * i / 9f);
                        float top = p.terrain + Math.Max(0, p.depth);
                        // Water stays "the same body" while it is wet and level with its neighbour.
                        wet = wet && p.depth > .5f && Math.Abs(top - previous) <= 2f;
                        previous = top;
                        crossing = Math.Max(crossing, wet ? Math.Min(top, levelHere) : top);
                    }
                    if (wet && distance > waterLimit) continue;
                    if (!wet && crossing >= baseHere + peak - .5f) continue;
                    float nLevel = wet ? previous : crossing, nBase = wet ? previous : baseHere, key = nLevel - nBase;
                    if (best.TryGetValue(n, out float known) && known <= key) continue;
                    best[n] = key; bases[n] = nBase; levels[n] = nLevel;
                    if (wet) dry.Remove(n); else dry.Add(n);
                    heap.Push(key, n.Item1, n.Item2);
                }
            }
            // Lowest (first flooded) cells first; the cap keeps the native source count bounded.
            result.Sort((a, b) => (a.Level - a.Base).CompareTo(b.Level - b.Base));
            return result;
        }

        private sealed class MinHeap
        {
            private readonly List<(float key, int x, int z)> _items = new List<(float, int, int)>();
            public int Count => _items.Count;
            public void Push(float key, int x, int z)
            {
                _items.Add((key, x, z));
                for (int i = _items.Count - 1; i > 0;)
                {
                    int parent = (i - 1) / 2;
                    if (_items[parent].key <= _items[i].key) break;
                    var swap = _items[parent]; _items[parent] = _items[i]; _items[i] = swap; i = parent;
                }
            }
            public (float key, int x, int z) Pop()
            {
                var top = _items[0];
                _items[0] = _items[_items.Count - 1]; _items.RemoveAt(_items.Count - 1);
                for (int i = 0; ;)
                {
                    int l = i * 2 + 1, r = l + 1, m = i;
                    if (l < _items.Count && _items[l].key < _items[m].key) m = l;
                    if (r < _items.Count && _items[r].key < _items[m].key) m = r;
                    if (m == i) break;
                    var swap = _items[m]; _items[m] = _items[i]; _items[i] = swap; i = m;
                }
                return top;
            }
        }

        // A plain cell joins the flood once the rising water stands this far above its crossing level.
        public static bool PlainActive(PlainCell cell, float rise) => cell.Base + rise >= cell.Level + .4f;
        // Freshly reached ground marks the advancing edge of the flood (foam and debris there).
        public static float PlainFront(PlainCell cell, float rise)
        {
            float over = cell.Base + rise - cell.Level - .4f;
            return over < 0 || over >= 2.5f ? 0 : 1 - over / 2.5f;
        }
    }
}
