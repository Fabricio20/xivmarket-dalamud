using System;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using XivMarket.Models;

namespace XivMarket.Services;

public sealed class MarketBoardSpliceHook : IDisposable
{
    private readonly Plugin plugin;

    private int lastRequestId = -1;
    private bool firedNq;
    private bool firedHq;
    private bool disposed;

    public MarketBoardSpliceHook(Plugin plugin)
    {
        this.plugin = plugin;
        Service.MarketBoard.OfferingsReceived += this.OnOfferingsReceived;
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        if (this.disposed) return;
        try
        {
            var listings = offerings.ItemListings;
            if (listings.Count == 0) return;

            var requestId = offerings.RequestId;
            var itemId = (int)listings[0].ItemId;

            if (requestId != this.lastRequestId)
            {
                this.lastRequestId = requestId;
                this.firedNq = false;
                this.firedHq = false;

                if (this.plugin.Configuration.DebugLogging)
                    Service.PluginLog.Information(
                        "[XivMarket] mb splice: new request id={RequestId} item={Item} listings={Count}",
                        requestId, itemId, listings.Count);
            }

            if (this.firedNq && this.firedHq) return;

            var worldId = this.GetHomeWorldId();
            if (worldId is null) return;
            var worldName = this.GetHomeWorldName();
            if (worldName is null) return;

            var now = DateTimeOffset.UtcNow;

            foreach (var listing in listings)
            {
                if (!this.firedNq && !listing.IsHq)
                {
                    this.firedNq = true;
                    var update = new SpliceUpdate(
                        itemId, worldId.Value, worldName,
                        IsHq: false,
                        (long)listing.PricePerUnit,
                        (int)listing.ItemQuantity,
                        now);
                    this.plugin.Cache.Splice(update);

                    if (this.plugin.Configuration.DebugLogging)
                        Service.PluginLog.Information(
                            "[XivMarket] mb splice: fired NQ {Price}g x{Qty}",
                            listing.PricePerUnit, listing.ItemQuantity);
                }
                else if (!this.firedHq && listing.IsHq)
                {
                    this.firedHq = true;
                    var update = new SpliceUpdate(
                        itemId, worldId.Value, worldName,
                        IsHq: true,
                        (long)listing.PricePerUnit,
                        (int)listing.ItemQuantity,
                        now);
                    this.plugin.Cache.Splice(update);

                    if (this.plugin.Configuration.DebugLogging)
                        Service.PluginLog.Information(
                            "[XivMarket] mb splice: fired HQ {Price}g x{Qty}",
                            listing.PricePerUnit, listing.ItemQuantity);
                }

                if (this.firedNq && this.firedHq) break;
            }
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "MarketBoardSpliceHook.OnOfferingsReceived failed");
        }
    }

    private int? GetHomeWorldId()
    {
        try
        {
            if (!Service.ClientState.IsLoggedIn) return null;
            var id = (int)Service.PlayerState.HomeWorld.RowId;
            return id > 0 ? id : null;
        }
        catch { return null; }
    }

    private string? GetHomeWorldName()
    {
        try
        {
            var id = this.GetHomeWorldId();
            if (id is null) return null;
            return this.plugin.Worlds.Lookup(id.Value)?.Name;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        Service.MarketBoard.OfferingsReceived -= this.OnOfferingsReceived;
    }
}
