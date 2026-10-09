using System;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game.Events;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace DisasterControlPanel
{
    public enum CustomDisasterKind { Earthquake = 1, Meteor = 2, Sinkhole = 3, Evacuation = 4 }

    // Save all progression, not just a managed list: reloading must not repeat an impact.
    public struct CustomDisasterState : IComponentData, ISerializable
    {
        public int Kind, Level, Citywide, Phase, Pulses, Damaged, Destroyed, Warned, TerrainApplied;
        public uint ImpactFrame, NextPulse;
        public float3 Position;
        public float Radius, Depth;
        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(Kind); writer.Write(Level); writer.Write(Citywide); writer.Write(Phase);
            writer.Write(Pulses); writer.Write(Damaged); writer.Write(Destroyed); writer.Write(Warned); writer.Write(TerrainApplied);
            writer.Write(ImpactFrame); writer.Write(NextPulse); writer.Write(Position); writer.Write(Radius); writer.Write(Depth);
        }
        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out Kind); reader.Read(out Level); reader.Read(out Citywide); reader.Read(out Phase);
            reader.Read(out Pulses); reader.Read(out Damaged); reader.Read(out Destroyed); reader.Read(out Warned); reader.Read(out TerrainApplied);
            reader.Read(out ImpactFrame); reader.Read(out NextPulse); reader.Read(out Position); reader.Read(out Radius); reader.Read(out Depth);
        }
    }

    public sealed class CustomEventPrefab : EventPrefab
    {
        public override void GetArchetypeComponents(HashSet<ComponentType> components)
        {
            base.GetArchetypeComponents(components);
            components.Add(ComponentType.ReadWrite<CustomDisasterState>());
            components.Add(ComponentType.ReadWrite<Duration>());
            components.Add(ComponentType.ReadWrite<DangerLevel>());
            components.Add(ComponentType.ReadWrite<Game.Objects.Transform>());
            components.Add(ComponentType.ReadWrite<TargetElement>());
        }
    }

    internal static class DisasterRules
    {
        public static float Radius(int kind, int level) => kind == 1 ? 200 + level * 150 : kind == 2 ? 50 + level * 28 : 18 + level * 6;
        public static float CraterRadius(int kind, int level) => kind == 2 ? 25 + level * 11 : 18 + level * 6;
        public static float Depth(int kind, int level) => kind == 2 ? 8 + level * 3 : kind == 3 ? 15 + level * 3 : 0;
        public static float Falloff(float distance, float radius) => math.saturate(1f - distance / math.max(1f, radius));
        public static float Damage(int kind, int level, float distance, float radius)
            => math.saturate((kind == 1 ? .15f + level * .18f : .35f + level * .25f) * Falloff(distance, radius));
    }
}
