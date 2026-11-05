using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace Fastranauts.Common
{
    /// <summary>
    /// Interface for an isolated, non-breaking optimization module.
    /// Each module can be individually toggled via BepInEx configuration and fails safely if a target changes.
    /// </summary>
    public interface IFastModule
    {
        string ModuleName { get; }
        bool IsEnabled { get; }
        void Initialize(ConfigFile config, ManualLogSource logger, Harmony harmony);
    }
}
