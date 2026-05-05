using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XivMarket;

#pragma warning disable CS8618    // initialized via Dalamud injection at plugin load
internal class Service
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; }
    [PluginService] internal static ICommandManager CommandManager { get; private set; }
    [PluginService] internal static IClientState ClientState { get; private set; }
    [PluginService] internal static IPlayerState PlayerState { get; private set; }
    [PluginService] internal static IDataManager DataManager { get; private set; }
    [PluginService] internal static IFramework Framework { get; private set; }
    [PluginService] internal static IGameGui GameGui { get; private set; }
    [PluginService] internal static IKeyState KeyState { get; private set; }
    [PluginService] internal static IPluginLog PluginLog { get; private set; }
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; }
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; }

    internal static void Initialize(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Service>();
    }
}
#pragma warning restore CS8618
