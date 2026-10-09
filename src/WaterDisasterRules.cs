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
    
        // ---- 0.2.1 travelling wave wall (Cities: Skylines 1 style) ----
        // The crest starts far offshore and is carried coastward as a narrow line of
        // moving sources, so the native water sim produces a wall of water rolling in.
        public const float CrestRadius = 110f, CrossSpacing = 160f, PathStep = 112f, BeyondOrigin = 900f;
        public static int TsunamiStart(int level) => 800 + (int)Math.Round(8f * Scale(level)) * 448;
        public static int WallHalfWidth(int level) => Math.Max(448, FrontHalfWidth(level));
        private static float Sat(double x) => (float)Math.Max(0, Math.Min(1, x));

        // t in 0..1 of the forcing window. Main crest: travels 6%..45%, breaks on the
        // coast and holds to 55%, dies by 72%. Tail wave at half height: 60%..97%.
        // Drawdown at the shore first, the sea pulling back before the wall arrives.
        public static void WaveWall(int level, uint frame, uint impact, uint end, int phase,
            out float mainTravel, out float mainHeight, out float tailTravel, out float tailHeight, out float drawdown)
        {
            mainTravel = tailTravel = -1; mainHeight = tailHeight = drawdown = 0;
            if (end <= impact || frame < impact || frame >= end || phase == 2) return;
            double t = ((long)frame - impact) / (double)(end - impact);
            float peak = PeakHeight(11, level);
            if (t < .3) drawdown = -(float)Math.Sin(Math.PI * Math.Min(1, t / .3)) * Math.Min(4f, peak * .08f);
            if (t >= .06 && t < .72)
            {
                double x = Sat((t - .06) / .39);
                mainTravel = (float)(1 - Math.Pow(1 - x, 1.4));
                mainHeight = peak * Sat((t - .06) / .05) * (t < .55 ? 1 : 1 - Sat((t - .55) / .17));
            }
            if (t >= .6 && t < .97)
            {
                tailTravel = (float)(1 - Math.Pow(1 - Sat((t - .6) / .25), 1.4));
                tailHeight = peak * .5f * Sat((t - .6) / .04) * (t < .85 ? 1 : 1 - Sat((t - .85) / .12));
            }
        }

        // Distance (positive = offshore of the clicked coast point) the crest has reached.
        public static float CrestDistance(int level, float travel) => TsunamiStart(level) - travel * (TsunamiStart(level) + BeyondOrigin);

        // Columns perpendicular to the coast, each a list of sea cells ordered offshore to
        // shore with their offshore distance. Cells over land are skipped, so islands and
        // headlands break the wall like real coastline does.
        public static List<List<(float x, float z, float distance)>> WallPaths(float x, float z, float dx, float dz, int level, float halfMap, Func<float, float, bool> isSea)
        {
            bool SeaAt(float sx, float sz) => Math.Abs(sx) <= halfMap && Math.Abs(sz) <= halfMap && isSea(sx, sz);
            var columns = new List<List<(float x, float z, float distance)>>();
            int width = WallHalfWidth(level), start = TsunamiStart(level);
            for (float cross = -width; cross <= width + .1f; cross += CrossSpacing)
            {
                var column = new List<(float x, float z, float distance)>();
                for (float d = start; d >= -BeyondOrigin; d -= PathStep)
                {
                    float sx = x - dx * d + dz * cross, sz = z - dz * d - dx * cross;
                    if (SeaAt(sx, sz)) column.Add((sx, sz, d));
                    else if (column.Count > 0 && d < 0) break; // reached this column's shore
                }
                if (column.Count > 0) columns.Add(column);
            }
            return columns;
        }

        // Index of the cell carrying the crest in a column, or -1 when the crest has not
        // reached the column yet. Past the last sea cell the shore cell keeps pushing.
        public static int CrestCell(List<(float x, float z, float distance)> column, float crest)
        {
            if (column.Count == 0 || crest > column[0].distance + PathStep * .5f) return -1;
            int best = column.Count - 1;
            for (int i = 0; i < column.Count; i++) if (column[i].distance <= crest + PathStep * .5f) { best = i; break; }
            return best;
        }
    }
}
