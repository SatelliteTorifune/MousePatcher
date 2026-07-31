using Brutal.Logging;
using KSA;
using StarMap.API;
using HarmonyLib;
namespace KSAModding
{
    [StarMapMod]
    public class MousePatcher
    {
        public void Log(string msg)
        {
            DefaultCategory.Log.Debug("[MousePatcher]" + msg);
        }
        [StarMapImmediateLoad]
        public void Init(KSA.Mod definingMod)
        {

        }

        [StarMapAllModsLoaded]
        public void OnFullyLoaded()
        {
            Log("OnFullyLoaded");
            var _harmony = new Harmony("com.satelliteTorifune.MousePatcher");
            _harmony.PatchAll();

        }
    }
}
