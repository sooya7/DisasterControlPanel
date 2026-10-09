using System;

namespace DisasterControlPanel
{
    internal static class DisasterVisualRules
    {
        public static int MeteorLead(int warning, float speed = 1) => Math.Max((int)Math.Ceiling(3*Math.Max(1,speed)), warning);
        public static float Flight(float time, float lead)
            => (float)Math.Pow(Math.Max(0d, Math.Min(1d, (time + lead) / Math.Max(.1f, lead))), 1.6d);
        public static float QuakeAge(float time, int pulse = 0) => time - Math.Max(0, Math.Min(2, pulse)) * 3f;
        public static float DustOpacity(float age, float life)
            => age < 0 || age >= life ? 0 : Math.Min(1, age / .4f) * Fade(age, life);
        public static float VisibleTime(float previous, float simulationDelta, float realDelta)
            => simulationDelta <= 0 ? previous : previous + Math.Min(simulationDelta, Math.Max(0, realDelta) * 2f);
        public static bool HailVisible(float cameraDistance, float viewedDistance, float radius)
            => Math.Min(cameraDistance, viewedDistance) < radius;
        public static float FoamOpacity(float depth, float slope)
            => depth <= .5f ? 0 : Math.Min(.65f, Math.Max(0, slope - .015f) * 4f);
        public static bool HitsGround(float height, float ground, float age) => age > .12f && height <= ground + .8f;
        // Seconds after impact a disaster's visuals may live: the fault stays open, the meteor
        // plume and cooling crater outlast the blast.
        public static float Lifetime(int kind) => kind == 1 ? 75 : kind == 2 ? 40 : 24;
        public static bool KeepTail(bool present, bool cancelled, int kind, uint frame, uint end, float age)
            => !present && !cancelled && kind>=1 && kind<=3 && frame>=end && age<Lifetime(kind);
        public static float Fade(float age, float life)
            => age < 0f || age >= life ? 0f : 1f - age / life;
        public static float DebrisHeight(float age, float speed)
            => Math.Max(0f, speed * age - 9f * age * age);
    }
}
