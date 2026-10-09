using System;

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

        public static Emitter MeteorColumn(int i, float time, int level)
        {
            float age = time - .35f - Hash(i, 23) * 1.4f;
            float angle = i * 2.399963f, radius = (2 + level) * (float)Math.Sqrt(Hash(i, 31));
            return new Emitter { X = (float)Math.Cos(angle) * radius + Math.Max(0, age) * 1.6f,
                Z = (float)Math.Sin(angle) * radius, Y = 3 + Math.Max(0, age) * (1.5f + Hash(i, 17) * 2),
                Width = (1.1f + level * .16f) * (.7f + Hash(i, 19)), Height = 1.5f + level * .1f,
                Intensity = Envelope(age, 5.5f) * .5f, Yaw = angle * 57.29578f };
        }

        public static float QuakeEmission(float time, int pulse, float distance, int level)
            => Envelope(time - pulse * 3f - distance / (300 + level * 35), .8f);

        public static float CrackAngle(int branch)
            => (branch % 8) * 2.399963f + (branch < 8 ? Hash(branch, 41) * .3f : (branch % 2 == 0 ? .45f : -.55f));

        public static float CrackLength(int branch)
            => (branch < 8 ? .038f : .022f) * (.65f + Hash(branch, 43) * .7f);

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

        // Radius of the visible surface wave of a pulse, or -1 when it is not travelling.
        public static float QuakeWaveRadius(float time, int pulse, int level, float radius)
        {
            float r = (time - pulse * 3f) * (300 + level * 35);
            return r < 0 || r > radius ? -1 : r;
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
        public static float CraterFire(float time) => time < .1f || time >= 12 ? 0 : Clamp((time - .1f) / .4f) * (1 - time / 12);
        public static float ImpactSmoke(float time) => time < .3f || time >= 16 ? 0 : Clamp((time - .3f) / 1.5f) * (1 - time / 16);
        public static float ImpactSparks(float time) => time >= 0 && time < .45f ? 1 : 0;
    }
}
