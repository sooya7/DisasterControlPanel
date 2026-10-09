using Colossal.Logging;
using Game;
using Game.Prefabs;
using Colossal.UI;

namespace DisasterControlPanel
{
    public sealed class Mod : Game.Modding.IMod
    {
        public static readonly ILog Log = LogManager.GetLogger("DisasterControlPanel").SetShowsErrorsInUI(false);
        public void OnLoad(UpdateSystem updateSystem)
        {
            Game.SceneFlow.GameManager.instance.localizationManager.AddSource("zh-HANS", new LocaleSource());
            updateSystem.UpdateBefore<DisasterCatalogSystem, PrefabInitializeSystem>(SystemUpdatePhase.PrefabUpdate);
            updateSystem.UpdateAt<DisasterEventSystem>(SystemUpdatePhase.Modification1);
            updateSystem.UpdateBefore<CustomDisasterSystem, Game.Simulation.WaterLevelChangeSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<WaterDisasterSystem, Game.Simulation.WaterSystem>(SystemUpdatePhase.PostSimulation);
            updateSystem.UpdateAt<CraterTerrainSystem>(SystemUpdatePhase.PostSimulation);
            updateSystem.UpdateAt<DisasterVisualSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateBefore<WeatherEffectsSystem, Game.Rendering.ClimateRenderSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAt<DisasterUISystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<DisasterPointTool>(SystemUpdatePhase.ToolUpdate);
#if DCP_DEV
            updateSystem.UpdateAt<DevServerSystem>(SystemUpdatePhase.UIUpdate);
#endif
            Log.Info("DisasterControlPanel 0.2.2 loaded");
        }
        public void OnDispose()
        {
#if DCP_DEV
            DevServerSystem.Instance?.Close();
#endif
            try { UIManager.defaultUISystem?.defaultUIView?.View?.ExecuteScript("window.__dcp && window.__dcp.dispose()"); }
            catch { }
            Log.Info("DisasterControlPanel disposed");
        }
    }
}
