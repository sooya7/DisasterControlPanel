using System;
using Game.Rendering.Utilities;
using UnityEngine;
using UnityEngine.VFX;

namespace DisasterControlPanel
{
    // Same texture contract as Game.Effects.VFXSystem. All resources here are private;
    // the game's shared VFX instances and their emitters are never modified.
    internal sealed class NativeDisasterVfx : IDisposable
    {
        private const int Capacity = 64;
        private readonly VisualEffect _effect;
        private readonly Texture2D _instances;
        private readonly Color[] _data = new Color[Capacity * 3];
        private int _count;
        private bool _started;

        public NativeDisasterVfx(Transform parent, VisualEffectAsset asset, uint seed)
        {
            var go = new GameObject("DCP native " + asset.name);
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            // Native graphs consume world-space positions, not the disaster root's offset.
            go.transform.position = Vector3.zero;
            _effect = go.AddComponent<VisualEffect>();
            _effect.visualEffectAsset = asset;
            _effect.resetSeedOnPlay = false;
            _effect.startSeed = seed;
            _instances = new Texture2D(Capacity, 3, TextureFormat.RGBAFloat, false, true)
            { name = "DCP private VFX instances", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            _instances.SetPixels(_data); _instances.Apply(false, false);
            _effect.SetTexture("InstanceData", _instances);
            _effect.SetInt("Count", 0);
            go.SetActive(true);
        }

        public void Begin() { _count = 0; Array.Clear(_data, 0, _data.Length); }

        public void Emit(Vector3 position, Vector3 scale, float intensity, Vector3 euler = default)
        {
            if (_count >= Capacity || intensity <= .001f) return;
            _data[_count] = new Color(position.x, position.y, position.z, Mathf.Clamp01(intensity));
            _data[_count + Capacity] = new Color(euler.x * Mathf.Deg2Rad, euler.y * Mathf.Deg2Rad, euler.z * Mathf.Deg2Rad, 0);
            _data[_count + Capacity * 2] = new Color(scale.x, scale.y, scale.z, 0);
            _count++;
        }

        public void Commit(float presentationDelta, Texture wind, Vector4 mapOffsetScale)
        {
            _instances.SetPixels(_data); _instances.Apply(false, false);
            _effect.SetInt("Count", _count);
            _effect.SetCheckedTexture(Shader.PropertyToID("WindTexture"), wind);
            _effect.SetCheckedTexture(Shader.PropertyToID("WindTexture 1"), wind);
            _effect.SetCheckedVector4(Shader.PropertyToID("MapOffsetScale"), mapOffsetScale);
            if (_count > 0 && !_started) { _effect.Reinit(); _started = true; }
            _effect.pause = presentationDelta <= 0;
            _effect.playRate = Mathf.Clamp(presentationDelta / Mathf.Max(.0001f, Time.deltaTime), 0, 8);
        }

        public void Dispose()
        {
            if (_effect != null) { _effect.Stop(); UnityEngine.Object.Destroy(_effect.gameObject); }
            if (_instances != null) UnityEngine.Object.Destroy(_instances);
        }
    }
}
