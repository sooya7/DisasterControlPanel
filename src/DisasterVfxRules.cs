using System;
using System.Collections.Generic;

namespace DisasterControlPanel
{
    // Emission positions and timings, shared by runtime and offline choreography checks.
    // Particle shading, flipbooks, turbulence and fading belong to the native VFX graphs.
    internal static class DisasterVfxRules
    {
        internal struct Emitter
        {
            public float X, Y, Z, Width, Height, Intensity, Yaw;
        }
        internal static float Hash(int i, int salt)
        {
            uint n = unchecked((uint)(i * 747796405 + salt * 2891336453L));
            n = ((n >> (int)((n >> 28) + 4)) ^ n) * 277803737u;
            return ((n >> 22) ^ n) / (float)uint.MaxValue;
        }
        private static float Clamp(float x) => Math.Max(0, Math.Min(1, x));
        private static float Envelope(float age, float duration)
            => age < 0 || age >= duration ? 0 : Clamp(age / .12f) * Clamp((duration - age) / .6f);

        public static Emitter MeteorGround(int i, float time, int level)
        {
            float angle = i * 2.399963f, variation = Hash(i, 7);
            float age = time - Hash(i, 3) * .5f;
            // Uneven filled fan of emitters, decelerating quickly; never a uniform ring.
            float speed = (14 + level * 3) * (.12f + variation * 1.1f);
            float radius = speed * (float)(1 - Math.Exp(-Math.Max(0, age) * .65)) / .65f;
            return new Emitter { X = (float)Math.Cos(angle) * radius, Z = (float)Math.Sin(angle) * radius,
                Y = 1, Width = (1.2f + level * .22f) * (.7f + Hash(i, 11)), Height = .5f + Hash(i, 13) * .45f,
                Intensity = Envelope(age, 2.8f + variation) * .7f, Yaw = angle * 57.29578f };
        }

        public static float QuakeEmission(float time, int pulse, float distance, int level)
            => Envelope(time - pulse * 3f - distance / (300 + level * 35), .8f);

        public static float ImpactEmission(float time)
            => time >= 0 && time < .28f ? 1 : 0;

        public static float LandingEmission(float age)
            => age >= 0 && age < .18f ? 1 : 0;

        public static float TrailEmission(float time, float lead)
            => time < -lead || time >= 0 ? 0 : Clamp((time + lead) * 3);

        // ---- 0.2.1 cinematic pass: shared timings for camera shake, crack growth, impact staging ----
        static readonly float[] QuakePulseWeight = { 1f, .8f, .65f };
        static readonly float[] CrackTarget = { .55f, .82f, 1f };

        // Seismic shake envelope for one pulse: sharp onset, sustained rumble, long tail.
        public static float QuakePulseShake(float age, int level)
        {
            float hold = 1.1f + level * .12f;
            if (age < 0 || age >= hold + 1.8f) return 0;
            return Clamp(age / .15f) * (age < hold ? 1 : 1 - (age - hold) / 1.8f);
        }

        // Camera shake in degrees at the epicentre, summed over the three pulses.
        public static float QuakeShake(float time, int level)
        {
            float a = 0;
            for (int k = 0; k < 3; k++) a = Math.Max(a, QuakePulseShake(time - k * 3f, level) * QuakePulseWeight[k]);
            return a * (.05f + level * .045f);
        }

        // Impact kick with exponential decay; a faint rumble during the last second of flight.
        public static float MeteorShake(float time, int level)
        {
            float amp = .15f + level * .08f;
            if (time < 0) return time > -1 ? amp * .12f * (1 + time) : 0;
            return time > 4 ? 0 : amp * (float)Math.Exp(-time * 2.2);
        }

        // Fraction of each fissure that has opened: every aftershock tears it further.
        public static float CrackGrowth(float time)
        {
            float g = 0;
            for (int k = 0; k < 3; k++)
            {
                float age = time - k * 3f;
                if (age < 0) break;
                float from = k == 0 ? 0 : CrackTarget[k - 1], e = Clamp(age / 1.2f);
                g = from + (CrackTarget[k] - from) * (1 - (1 - e) * (1 - e));
            }
            return g;
        }

        // Dust shaken off a building as the surface wave reaches it, once per pulse.
        public static float BuildingDust(float time, float distance, int level)
        {
            float speed = 300 + level * 35, best = 0;
            for (int k = 0; k < 3; k++)
                best = Math.Max(best, Envelope(time - k * 3f - distance / speed, 1.6f) * QuakePulseWeight[k]);
            return best;
        }

        // Ejecta launch: azimuth, elevation 35-75 degrees, speed scaled with level.
        public static void Ejecta(int i, int level, out float azimuth, out float horizontal, out float vertical)
        {
            azimuth = Hash(i, 51) * 6.2831853f;
            float elevation = .61f + Hash(i, 53) * .7f, speed = (22 + level * 5) * (.45f + Hash(i, 57) * .85f);
            horizontal = speed * (float)Math.Cos(elevation); vertical = speed * (float)Math.Sin(elevation);
        }

        // Ejecta cooling: white-orange glow fading to dark rock over about four seconds.
        public static float EjectaGlow(float age) => age < 0 ? 0 : 6f * (float)Math.Exp(-age * .95);

        // Burning crater, ember and smoke plume lifetimes after impact.
        public static float CraterFire(float time) => time < .1f || time >= 18 ? 0 : Clamp((time - .1f) / .4f) * (1 - time / 18);
        public static float ImpactSmoke(float time) => time < .3f || time >= 30 ? 0 : Clamp((time - .3f) / 1.5f) * (1 - time / 30);
        public static float ImpactSparks(float time) => time >= 0 && time < .45f ? 1 : 0;

        // ---- 0.2.2 meteor: long entry streak, flash, shock ring, rising plume, cooling crater ----
        public const float MeteorEntryDistance = 5200f, MeteorElevation = .84f;

        // Visible fall in simulation seconds: about seven real seconds at any speed, never
        // longer than the warning that precedes the impact.
        public static float MeteorFlightTime(float lead, float speed)
            => Math.Max(.5f, Math.Min(lead, Math.Max(3f, 7f * Math.Max(1f, speed))));

        // Fraction of the entry path covered (near-constant speed); -1 while not yet visible.
        public static float MeteorProgress(float time, float flight)
            => time < -flight ? -1 : time >= 0 ? 1 : (float)Math.Pow(Clamp((time + flight) / flight), 1.18);

        // When the head passed the point at this fraction of the path, measured from the impact.
        public static float MeteorPassTime(float fromImpact, float flight)
            => -flight + flight * (float)Math.Pow(Clamp(1 - fromImpact), 1 / 1.18);

        // Smoke trail sample positions, denser near the ground where the camera usually is.
        public static float TrailPoint(int k, int count) => (float)Math.Pow((k + .5f) / count, 1.7);
        public static float TrailSmoke(float since) => since < 0 || since >= 18 ? 0 : Clamp(since / .2f) * (1 - since / 18);

        // Glowing plasma tail length behind the head, limited by how far it has travelled.
        public static float TailLength(float travelled, float size) => Math.Max(0, Math.Min(travelled, size * 9));

        // Impact fireball dome: snaps out to about 1.7 crater radii, then flattens and sinks.
        public static float FlashRadius(float time, float crater)
            => time < 0 || time >= .9f ? 0 : crater * (1.7f * (1 - (1 - Clamp(time / .14f)) * (1 - Clamp(time / .14f))) + .35f * Clamp((time - .14f) / .76f));
        public static float FlashHeight(float time) => 1 - .85f * Clamp((time - .14f) / .7f);
        public static float FlashEmission(float time) => time < 0 || time >= .9f ? 0 : 40f * (float)Math.Exp(-time * 5.2) * Clamp((.9f - time) / .25f);

        // Ground shock front: a fast, decelerating dust ring, gone after three seconds.
        public static float ShockRadius(float time, int level, float crater)
            => time < 0 || time >= 3 ? -1 : crater * (3.2f + level * .25f) * (float)((1 - Math.Exp(-time * 1.9)) / (1 - Math.Exp(-3 * 1.9)));
        public static float ShockEmission(float time) => time < 0 || time >= 3 ? 0 : Clamp(time / .08f) * (1 - time / 3);

        // Rising dust plume: 16 emitters in a column that climbs and drifts, 8 in the spreading cap.
        public static Emitter MeteorPlume(int i, float time, int level)
        {
            float age = time - .25f - Hash(i, 23) * .5f, a = Math.Max(0, age);
            float height = (70 + level * 22) * (float)(1 - Math.Exp(-a * .42));
            float fade = age < 0 ? 0 : Clamp(age / .5f) * Clamp((20 - age) / 6f);
            if (i < 16)
            {
                float f = (i / 2 + .5f) / 8f, angle = i * 2.399963f;
                float r = (6 + level * 2.2f) * (.5f + .5f * f) * (.3f + .7f * Hash(i, 31));
                return new Emitter { X = (float)Math.Cos(angle) * r + a * 1.2f * f, Z = (float)Math.Sin(angle) * r, Y = 2 + height * (float)Math.Pow(f, .85),
                    Width = (1.4f + level * .22f) * (.8f + .5f * f) * (1 + a * .06f), Height = 1.6f + level * .12f,
                    Intensity = fade * .55f, Yaw = angle * 57.29578f };
            }
            int k = i - 16;
            float ring = k * .785f + Hash(i, 37), rc = 4 + (12 + level * 5) * (float)(1 - Math.Exp(-a * .3));
            return new Emitter { X = (float)Math.Cos(ring) * rc + a * 1.2f, Z = (float)Math.Sin(ring) * rc, Y = 2 + height + Hash(i, 41) * 8,
                Width = (1.8f + level * .28f) * (1 + a * .08f), Height = 1.4f + level * .1f,
                Intensity = fade * .5f * Clamp(age / 1.2f), Yaw = ring * 57.29578f };
        }

        // Molten crater floor cooling to scorched rock, then sinking out of sight.
        public static float CraterGlow(float time) => time < .1f ? 0 : Clamp((time - .1f) / .3f) * (float)Math.Exp(-(time - .1f) / 6.5);
        public static float CraterSink(float time, float life) => Clamp((time - (life - 5)) / 4) * 3f;

        // ---- 0.2.2 earthquake: one fault rupture through the epicentre instead of a radial star ----
        internal sealed class Fissure
        {
            public readonly List<float> X = new List<float>(), Z = new List<float>(), Reach = new List<float>(), Width = new List<float>();
            public bool Main;
        }

        public static float FaultHalfLength(int level, float radius) => radius * (.42f + level * .025f);
        public static float FissureWidth(int level) => 1.1f + level * .42f;
        public static float ScarpHeight(int level) => .15f + level * .11f;

        // Main fault: en-echelon segments on both sides of the epicentre, each slightly turned,
        // stepped sideways and overlapping the previous one, with a meandering trace. Splay
        // cracks branch forward from segment ends. Offsets are relative to the epicentre.
        public static List<Fissure> QuakeFault(int seed, int level, float radius)
        {
            var fissures = new List<Fissure>();
            var splays = new List<Fissure>();
            float half = FaultHalfLength(level, radius), w0 = FissureWidth(level);
            float heading = Hash(seed, 101) * (float)Math.PI;
            for (int side = 0; side < 2; side++)
            {
                float angle = heading + side * (float)Math.PI, along = 0, x = 0, z = 0;
                float phase = Hash(seed + side, 102) * 6.283f;
                for (int segment = 0; along < half - 1 && segment < 64; segment++)
                {
                    int salt = seed * 131 + side * 67 + segment * 7;
                    float length = Math.Min(half - along, 140 + Hash(salt, 103) * 230);
                    float a = angle + (Hash(salt, 105) - .5f) * .14f, ca = (float)Math.Cos(a), sa = (float)Math.Sin(a);
                    float overlap = 0, step = 0;
                    if (segment > 0)
                    {
                        overlap = 10 + Hash(salt, 107) * 22;
                        step = (Hash(salt, 109) > .5f ? 1 : -1) * (3 + level * .6f + Hash(salt, 111) * 5);
                    }
                    float sx = x - ca * overlap - sa * step, sz = z - sa * overlap + ca * step, total = length + overlap;
                    int count = Math.Max(2, (int)Math.Ceiling(total / 6f) + 1);
                    var f = new Fissure { Main = true };
                    for (int i = 0; i < count; i++)
                    {
                        float u = total * i / (count - 1), g = along - overlap + u;
                        float meander = 4.5f * (float)Math.Sin(g / 97f + phase) + 2f * (float)Math.Sin(g / 29f + phase * 1.7f)
                            + (i == 0 || i == count - 1 ? 0 : (Hash(salt * 31 + i, 113) - .5f) * 1.4f);
                        if (segment == 0) meander *= Clamp(u / 30f);
                        float reach = Math.Max(0, g);
                        float ends = segment == 0 ? .25f + .75f * Clamp((total - u) / 22f) : .25f + .75f * Clamp(Math.Min(u, total - u) / 22f);
                        f.X.Add(sx + ca * u - sa * meander); f.Z.Add(sz + sa * u + ca * meander); f.Reach.Add(reach);
                        f.Width.Add(w0 * (float)Math.Pow(Clamp(1 - reach / half), .55) * ends * (.8f + .4f * Hash(salt * 31 + i, 117)));
                    }
                    fissures.Add(f);
                    if (Hash(salt, 119) < .75f)
                    {
                        int at = (int)((count - 1) * (.55f + .4f * Hash(salt, 121)));
                        float turn = (Hash(salt, 123) > .5f ? 1 : -1) * (.35f + .4f * Hash(salt, 125));
                        float bl = (25 + Hash(salt, 127) * 110) * (.5f + level * .06f), ba = a + turn;
                        float bc = (float)Math.Cos(ba), bs = (float)Math.Sin(ba), bw = f.Width[at] * .45f;
                        int bn = Math.Max(2, (int)Math.Ceiling(bl / 5f) + 1);
                        var b = new Fissure();
                        for (int i = 0; i < bn; i++)
                        {
                            float u = bl * i / (bn - 1), jag = i == 0 ? 0 : (Hash(salt * 17 + i, 129) - .5f) * 1.6f + 2.5f * (float)Math.Sin(u / 23f + phase);
                            b.X.Add(f.X[at] + bc * u - bs * jag); b.Z.Add(f.Z[at] + bs * u + bc * jag);
                            b.Reach.Add(f.Reach[at] + u); b.Width.Add(bw * (1 - .92f * u / bl));
                        }
                        splays.Add(b);
                    }
                    x = sx + ca * total; z = sz + sa * total; along += length;
                }
            }
            fissures.AddRange(splays);
            return fissures;
        }

        public static float FaultReach(List<Fissure> fissures)
        {
            float best = 1;
            foreach (var f in fissures) foreach (var r in f.Reach) best = Math.Max(best, r);
            return best;
        }

        // The rupture tip runs out from the epicentre; each aftershock tears it further.
        public static float RuptureFront(float time, float maxReach) => maxReach * 1.02f * CrackGrowth(time);

        // A point opens over a short distance behind the passing tip, so the tip stays sharp.
        public static float FissureOpen(float front, float reach, float maxReach) => Clamp((front - reach) / (18 + maxReach * .05f));

        // Every pulse jolts the fissure wider: a quick shove with a slight overshoot.
        public static float FissureWiden(float time)
        {
            float w = 0;
            for (int k = 0; k < 3; k++)
            {
                float age = time - k * 3f;
                if (age < 0) break;
                float e = Clamp(age / .5f);
                w += QuakePulseWidth[k] * ((1 - (1 - e) * (1 - e)) + .18f * (float)Math.Sin(Math.PI * Clamp(age / .45f)));
            }
            return w;
        }
        static readonly float[] QuakePulseWidth = { .62f, .23f, .15f };

        // The fissure stays after the shaking and sinks under the terrain at the end of its life.
        public static float FissureSink(float time, float life) => Clamp((time - (life - 4)) / 4) * 2.5f;

        // Dust from a fissure point: a spurt as it tears open, a puff with every later pulse.
        public static float FissureDust(float time, float front, float reach, float maxReach)
        {
            if (front <= reach) return 0;
            float band = 60 + maxReach * .04f, behind = front - reach, dust = 0;
            for (int k = 0; k < 3; k++)
            {
                float age = time - k * 3f;
                if (age < 0 || age >= 1.6f) continue;
                // While a pulse is tearing, the freshly opened stretch behind the tip spurts.
                if (behind < band) dust = Math.Max(dust, .9f * (1 - behind / band) * (1 - age / 1.6f));
                if (k > 0 && age < 1.4f) dust = Math.Max(dust, .55f * (1 - age / 1.4f));
            }
            return dust;
        }
    }
}
