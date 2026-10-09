using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DisasterControlPanel
{
    // Presentation-only camera shake. The offset is applied after the game's camera
    // controller has run and removed again before the next frame, so it never feeds
    // back into camera state, saves, or picking. Paused simulation means no shake.
    [DefaultExecutionOrder(32000)]
    internal sealed class CameraShake : MonoBehaviour
    {
        private static CameraShake _instance;
        private static float _degrees, _frequency;
        private Camera _camera;
        private Quaternion _appliedRotation = Quaternion.identity, _setRotation;
        private Vector3 _appliedPosition, _setPosition;
        private bool _applied;

        internal static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("DCP camera shake") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<CameraShake>();
        }

        internal static void Clear() { _degrees = 0; _frequency = 0; }

        // Strongest request wins; frequency follows it (quake rumble ~8 Hz, impact ~14 Hz).
        internal static void Request(float degrees, float frequency)
        {
            if (degrees <= _degrees) return;
            _degrees = Mathf.Min(degrees, 2.2f); _frequency = frequency;
        }

        internal static void Shutdown()
        {
            if (_instance == null) return;
            _instance.Restore();
            Object.Destroy(_instance.gameObject); _instance = null; Clear();
        }

        private void OnEnable() { RenderPipelineManager.endContextRendering += AfterRender; }
        private void OnDisable() { RenderPipelineManager.endContextRendering -= AfterRender; Restore(); }
        // Undo right after the frame is rendered, before any system of the next frame reads the camera.
        private void AfterRender(ScriptableRenderContext context, List<Camera> cameras) { Restore(); }

        private void Restore()
        {
            if (!_applied || _camera == null) { _applied = false; return; }
            var t = _camera.transform;
            // Only undo our own offset; if the camera controller already rewrote the
            // transform this frame there is nothing of ours left on it.
            if (t.rotation == _setRotation && t.position == _setPosition)
            {
                t.rotation = _setRotation * Quaternion.Inverse(_appliedRotation);
                t.position = _setPosition - _appliedPosition;
            }
            _applied = false;
        }

        private void LateUpdate()
        {
            Restore();
            if (_degrees <= .001f) return;
            _camera = Camera.main;
            if (_camera == null) return;
            float time = Time.unscaledTime, f = Mathf.Max(1, _frequency);
            float Wave(float seed) =>
                (Mathf.PerlinNoise(time * f, seed) - .5f) * 2f * .75f + (Mathf.PerlinNoise(time * f * .27f, seed + 7.3f) - .5f) * 2f * .25f;
            _appliedRotation = Quaternion.Euler(Wave(1.7f) * _degrees, Wave(4.1f) * _degrees, Wave(9.9f) * _degrees * .5f);
            var t = _camera.transform;
            float height = Mathf.Clamp(t.position.y, 50, 3000);
            _appliedPosition = new Vector3(Wave(13.3f), Wave(17.9f) * .6f, Wave(21.1f)) * (_degrees * .0035f * height);
            t.rotation = t.rotation * _appliedRotation;
            t.position = t.position + _appliedPosition;
            _setRotation = t.rotation; _setPosition = t.position; _applied = true;
        }

        private void OnDestroy() { Restore(); if (_instance == this) _instance = null; }
    }
}
