using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMarket.Services;
using XivMarket.Windows;

namespace XivMarket;

public sealed class Plugin : IDalamudPlugin
{
    /// <summary>Convenience access for Configuration.Save() (which calls back through here).</summary>
    public static IDalamudPluginInterface PluginInterface => Service.PluginInterface;

    private const string MainCommand = "/xivmarket";

    public Configuration Configuration { get; }
    public XivMarketClient Client { get; }
    public ItemPriceCache Cache { get; }
    public WorldsService Worlds { get; }
    public MarketabilityProvider Marketability { get; }
    public TooltipInjector Injector { get; }
    public MarketBoardSpliceHook MarketBoardSplice { get; }
    public RetainerSellListHighlighter RetainerHighlighter { get; }
    public InventoryPreloadService InventoryPreload { get; }
    public ItemDetailHook Hook { get; }
    public WindowSystem WindowSystem { get; } = new("XivMarket");

    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly RetainerSellWindow retainerSellWindow;
    private readonly CancellationTokenSource startupCts = new();
    private bool disposed;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        Service.Initialize(pluginInterface);

        try
        {
            this.Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

            this.Client = new XivMarketClient(() => this.Configuration.ApiBaseUrl);
            this.Worlds = new WorldsService(this.Client, Service.PluginLog);
            this.Marketability = new MarketabilityProvider(Service.DataManager, Service.PluginLog);

            this.Cache = new ItemPriceCache(
                this.Client,
                this.Marketability.IsMarketable,
                () => this.Configuration.CacheTtl);
            this.Cache.DebugLog = (msg, args) =>
            {
                if (this.Configuration.DebugLogging)
                    Service.PluginLog.Information(msg, args!);
            };

            this.Injector = new TooltipInjector(Service.GameGui, Service.PluginLog);
            this.MarketBoardSplice = new MarketBoardSpliceHook(this);
            this.RetainerHighlighter = new RetainerSellListHighlighter(this);
            this.InventoryPreload = new InventoryPreloadService(this);
            this.Hook = new ItemDetailHook(this);

            this.configWindow = new ConfigWindow(this);
            this.mainWindow = new MainWindow(this);
            this.retainerSellWindow = new RetainerSellWindow(this);
            this.WindowSystem.AddWindow(this.configWindow);
            this.WindowSystem.AddWindow(this.mainWindow);
            this.WindowSystem.AddWindow(this.retainerSellWindow);

            Service.CommandManager.AddHandler(MainCommand, new CommandInfo(this.OnCommand)
            {
                HelpMessage = "Toggle the XIV Market window.",
            });

            pluginInterface.UiBuilder.Draw += this.WindowSystem.Draw;
            pluginInterface.UiBuilder.OpenMainUi += this.ToggleMainUi;
            pluginInterface.UiBuilder.OpenConfigUi += this.ToggleConfigUi;

            // Background world map load - non-blocking. Renderer skips region rows until it lands.
            _ = Task.Run(() => this.Worlds.LoadAsync(this.startupCts.Token));

            Service.PluginLog.Information("XivMarket loaded. API: {Url}", this.Configuration.ApiBaseUrl);
        }
        catch (Exception)
        {
            // Constructor failed mid-way - best-effort cleanup of whatever was already created
            // before letting the exception propagate (Dalamud will mark the plugin as failed).
            this.SafeDispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        this.SafeDispose();
        GC.SuppressFinalize(this);
    }

    private void SafeDispose()
    {
        // Order matters: stop new events first (hook), then clean visual state (injector),
        // then cancel async work (cache, worlds), then UI.
        TryRun("startup cancellation", () => { this.startupCts.Cancel(); this.startupCts.Dispose(); });
        TryRun("hook unregister", () => this.Hook?.Dispose());
        TryRun("mb splice unregister", () => this.MarketBoardSplice?.Dispose());
        TryRun("retainer highlighter unregister", () => this.RetainerHighlighter?.Dispose());
        TryRun("inventory preload unregister", () => this.InventoryPreload?.Dispose());

        TryRun("framework-thread injector cleanup", () =>
        {
            // Injector.Dispose internally tries to remove the node, but unsafe pointer ops
            // MUST happen on the framework thread. Marshal explicitly.
            if (this.Injector is null) return;
            if (Service.Framework is null)
            {
                this.Injector.Dispose();
                return;
            }
            try
            {
                Service.Framework.RunOnFrameworkThread(() =>
                {
                    try { this.Injector.CleanupNode(); }
                    catch (Exception ex) { Service.PluginLog?.Error(ex, "Framework-thread cleanup failed"); }
                }).Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Service.PluginLog?.Warning(ex, "Could not marshal cleanup; falling back to direct dispose");
            }
            this.Injector.Dispose();
        });

        TryRun("cache dispose", () => this.Cache?.Dispose());
        TryRun("client dispose", () => this.Client?.Dispose());

        TryRun("UI dispose", () =>
        {
            if (Service.PluginInterface is { } pi)
            {
                pi.UiBuilder.Draw -= this.WindowSystem.Draw;
                pi.UiBuilder.OpenMainUi -= this.ToggleMainUi;
                pi.UiBuilder.OpenConfigUi -= this.ToggleConfigUi;
            }
            this.WindowSystem.RemoveAllWindows();
            this.retainerSellWindow?.Dispose();
            this.mainWindow?.Dispose();
            this.configWindow?.Dispose();
        });

        TryRun("command remove", () => Service.CommandManager?.RemoveHandler(MainCommand));
    }

    private static void TryRun(string label, Action a)
    {
        try { a(); }
        catch (Exception ex) { Service.PluginLog?.Error(ex, "Dispose step '{Label}' failed", label); }
    }

    private void OnCommand(string command, string args) => this.mainWindow.Toggle();
    public void ToggleMainUi() => this.mainWindow.Toggle();
    public void ToggleConfigUi() => this.configWindow.Toggle();
}
