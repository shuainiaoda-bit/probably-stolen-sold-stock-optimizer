using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace SoldStockChecks
{
    sealed partial class Runner
    {
        static int gridCopies, itemCopies, stockRefreshes;
        static GameGridInventory observedStock;
        static void GridCopyProbe() { gridCopies++; }
        static void ItemCopyProbe() { itemCopies++; }
        static void StockVisualProbe(GameItemElement __instance)
        { if (ReferenceEquals(__instance.parentInventory, observedStock)) stockRefreshes++; }
        static string Pose(GameItem item) => item.modifiedShape.minX + ":" + item.modifiedShape.minY + ":" + item.modifiedShape.orientation + ":" + item.modifiedShape.flipped;

        IEnumerator InventoryScenario()
        {
            phase = "inventory-performance";
            bool smoke = Environment.GetCommandLineArgs().Contains("-sold-stock-smoke");
            var stock = EmporiumEntry.Instance.invElement;
            var originalShape = stock.inventoryShape;
            var originalItems = stock.childItems;
            var holding = new GameGridInventory(180, 900);
            var stockBackground = (TreeNodeRender)AccessTools.Field(typeof(GameGridInventory), "background").GetValue(stock);
            var originalBackgroundType = stockBackground.image.type;
            stockBackground.image.type = UnityEngine.UI.Image.Type.Simple;
            ((TreeNodeRender)AccessTools.Field(typeof(GameGridInventory), "background").GetValue(holding)).image.type = UnityEngine.UI.Image.Type.Simple;
            foreach (var item in originalItems) { stock.Expel(item); holding.UncheckedAccept(item); }
            stock.SetShape(180, 900);
            var samples = new List<GameItem>();
            var probe = new Harmony("soldstock.inventoryprobe");
            var timer = new Stopwatch();
            try
            {
                foreach (int count in smoke ? new[] { 40 } : new[] { 1000, 2000 })
                {
                    Mode(true);
                    for (int n = 0; n < count; n++)
                    {
                        var item = DirectoryMaster.Item(n % 5 == 0 ? "wire" : "common_ore");
                        GeneralHelper.SetItemOwned(item, n % 4 != 0);
                        item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetPosition((n % 20) * 8, (n / 20) * 8).Build();
                        if (!stock.UncheckedAccept(item)) throw new InvalidOperationException("Query fixture refused " + n);
                        samples.Add(item);
                        if (n % 100 == 99) yield return .01f;
                    }
                    var crate = PreBuiltItemHelper.LootCrateMedical(); GeneralHelper.SetItemOwned(crate, true);
                    crate.modifiedShape = new GridShapeBuilder(crate.modifiedShape).SetPosition(165, 820).Build();
                    Check(stock.UncheckedAccept(crate), "Query fixture includes nested contents"); samples.Add(crate);
                    Mode(false);
                    var all = EmporiumEntry.Instance.GetAllItems();
                    var owned = EmporiumEntry.Instance.GetAllOwnedItems();
                    var saved = PlayerStore.Instance.FindAllItem(false);
                    var ownedSaved = PlayerStore.Instance.FindAllItem(true);
                    for (int repeat = 0; repeat < (smoke ? 1 : 3); repeat++)
                    foreach (bool enabled in repeat % 2 == 0 ? new[] { false, true } : new[] { true, false })
                    {
                        Mode(enabled);
                        Check(EmporiumEntry.Instance.GetAllItems().SequenceEqual(all), "Full traversal order matches baseline: " + count + "/" + enabled);
                        Check(EmporiumEntry.Instance.GetAllOwnedItems().SequenceEqual(owned), "Ownership filtering matches baseline: " + count + "/" + enabled);
                        Check(PlayerStore.Instance.FindAllItem(false).SequenceEqual(saved) && PlayerStore.Instance.FindAllItem(true).SequenceEqual(ownedSaved), "Save traversal matches baseline: " + count + "/" + enabled);
                        for (int i = 0; i < 8; i++) { EmporiumEntry.Instance.GetAllItems(); PlayerStore.Instance.FindAllItem(false); }
                        int queries = smoke ? 15 : 200;
                        timer.Restart(); for (int i = 0; i < queries; i++) EmporiumEntry.Instance.GetAllItems(); timer.Stop(); double allMs = timer.Elapsed.TotalMilliseconds;
                        timer.Restart(); for (int i = 0; i < queries; i++) EmporiumEntry.Instance.GetAllOwnedItems(); timer.Stop(); double ownedMs = timer.Elapsed.TotalMilliseconds;
                        timer.Restart(); for (int i = 0; i < queries; i++) PlayerStore.Instance.FindAllItem(false); timer.Stop(); double saveScanMs = timer.Elapsed.TotalMilliseconds;
                        measurements.Add(new { kind = "world-queries", count, repeat, optimized = enabled, queries, allMs, ownedMs, saveScanMs });
                        UnityEngine.Debug.Log("[SoldStockCheck] EXTRA " + Newtonsoft.Json.JsonConvert.SerializeObject(measurements.Last()));
                        yield return .05f;
                    }
                    probe.Patch(AccessTools.PropertyGetter(typeof(GameGridInventory), "children"), prefix: new HarmonyMethod(typeof(Runner), nameof(GridCopyProbe)));
                    probe.Patch(AccessTools.PropertyGetter(typeof(GameItem), "children"), prefix: new HarmonyMethod(typeof(Runner), nameof(ItemCopyProbe)));
                    foreach (bool enabled in new[] { false, true })
                    {
                        Mode(enabled); gridCopies = itemCopies = 0; EmporiumEntry.Instance.GetAllItems();
                        measurements.Add(new { kind = "traversal-list-copies", count, optimized = enabled, gridCopies, itemCopies });
                    }
                    probe.UnpatchAll(probe.Id);
                    Mode(true);
                    var moved = samples[0]; stock.Expel(moved);
                    Check(!EmporiumEntry.Instance.GetAllItems().Contains(moved), "Traversal reflects removal in the same frame");
                    Check(stock.UncheckedAccept(moved) && EmporiumEntry.Instance.GetAllItems().Contains(moved), "Traversal reflects reinsertion in the same frame");
                    var snapshot = EmporiumEntry.Instance.GetAllItems(); snapshot.Clear();
                    Check(EmporiumEntry.Instance.GetAllItems().Count > count, "Callers can mutate their returned snapshot without changing inventory");
                    foreach (var item in samples) item.Destroy(); samples.Clear(); yield return .1f;
                }

                foreach (int count in smoke ? new[] { 24 } : new[] { 300, 600 })
                {
                    Mode(true);
                    for (int n = 0; n < count; n++)
                    {
                        var item = DirectoryMaster.Item(n % 3 == 0 ? "wire" : "common_ore"); GeneralHelper.SetItemOwned(item, true);
                        item.modifiedShape = new GridShapeBuilder(item.modifiedShape).SetPosition((n % 20) * 8, (n / 20) * 8).Build();
                        if (!stock.UncheckedAccept(item)) throw new InvalidOperationException("Sorting fixture refused " + n); samples.Add(item);
                        if (n % 100 == 99) yield return .01f;
                    }
                    ItemPinHelper.SetPinned(samples[0], true);
                    var positions = stock.childItems.ToDictionary(i => i.uniqueId, Pose);
                    var roots = stock.childItems.ToDictionary(i => i.uniqueId, i => i.unitCount);
                    Dictionary<int, string> expected = null;
                    var events = new List<string>(); List<string> expectedEvents = null;
                    Action<GameItem, GameInventory, SlotMarker> added = (i, g, s) => events.Add("A" + i.uniqueId);
                    Action<GameItem, GameInventory, SlotMarker> removed = (i, g, s) => events.Add("R" + i.uniqueId);
                    stock.onSlotAddItemLateFunc += added; stock.onSlotRemoveItemLateFunc += removed;
                    try
                    {
                        for (int repeat = 0; repeat < (smoke ? 1 : 3); repeat++)
                        foreach (bool enabled in repeat % 2 == 0 ? new[] { false, true } : new[] { true, false })
                        {
                            Mode(enabled); events.Clear();
                            timer.Restart(); bool sorted = InventorySortHelper.Sort(stock, true, false); timer.Stop();
                            double sortMs = timer.Elapsed.TotalMilliseconds;
                            Check(sorted, "Native sorting succeeds: " + count + "/" + enabled);
                            if (expected == null) { expected = stock.childItems.ToDictionary(i => i.uniqueId, Pose); expectedEvents = events.ToList(); }
                            Check(stock.childItems.Count == roots.Count && stock.childItems.All(i => roots[i.uniqueId] == i.unitCount && expected[i.uniqueId] == Pose(i)), "Sorting preserves native layout, identities and quantities: " + enabled);
                            Check(events.SequenceEqual(expectedEvents), "Sorting preserves add/remove callback order: " + enabled);
                            Check(Pose(samples[0]) == positions[samples[0].uniqueId] && ItemPinHelper.IsPinned(samples[0]), "Pinned item stays in place: " + enabled);
                            timer.Restart(); bool undone = InventorySortHelper.Undo(stock); timer.Stop(); double undoMs = timer.Elapsed.TotalMilliseconds;
                            Check(undone && stock.childItems.All(i => positions[i.uniqueId] == Pose(i)), "Native undo restores all original positions: " + enabled);
                            measurements.Add(new { kind = "native-sorting", count, repeat, optimized = enabled, sortMs, undoMs });
                            UnityEngine.Debug.Log("[SoldStockCheck] EXTRA " + Newtonsoft.Json.JsonConvert.SerializeObject(measurements.Last()));
                            yield return .05f;
                        }
                    }
                    finally { stock.onSlotAddItemLateFunc -= added; stock.onSlotRemoveItemLateFunc -= removed; }
                    observedStock = stock;
                    probe.Patch(AccessTools.Method(typeof(GameItemElement), "Validate"), prefix: new HarmonyMethod(typeof(Runner), nameof(StockVisualProbe)));
                    foreach (bool enabled in new[] { false, true })
                    {
                        Mode(enabled); stockRefreshes = 0; InventorySortHelper.Sort(stock, true, false);
                        measurements.Add(new { kind = "sorting-refresh-count", count, optimized = enabled, stockRefreshes });
                        InventorySortHelper.Undo(stock);
                    }
                    probe.UnpatchAll(probe.Id);
                    Mode(true);
                    bool threw = false;
                    Action<GameItem, GameInventory, SlotMarker> fail = (i, g, s) => { stock.Validate(); throw new InvalidOperationException("inventory-test-exception"); };
                    stock.onSlotRemoveItemFunc += fail;
                    try { InventorySortHelper.Sort(stock, true, false); }
                    catch (InvalidOperationException error) { threw = error.Message == "inventory-test-exception"; }
                    finally { stock.onSlotRemoveItemFunc -= fail; }
                    Check(threw && stock.childItems.Count == roots.Count, "A callback exception propagates without changing item membership");
                    observedStock = stock; stockRefreshes = 0;
                    probe.Patch(AccessTools.Method(typeof(GameItemElement), "Validate"), prefix: new HarmonyMethod(typeof(Runner), nameof(StockVisualProbe)));
                    stock.Validate(); Check(stockRefreshes > 0, "Exception finalizer restores immediate visual validation"); probe.UnpatchAll(probe.Id);
                    ItemPinHelper.SetPinned(samples[0], false);
                    foreach (var item in samples) item.Destroy(); samples.Clear(); yield return .1f;
                }
                foreach (bool enabled in new[] { false, true })
                {
                    Mode(enabled);
                    buyer = StoreClientList.CreateUpperLowerVisitor(); buyerInstance = StoreClientInstance.CreateClientInstance(buyer); Seed();
                    buyer.clientIntent = StoreClient.ClientIntent.SELL; buyer.acceptDealDialogue = null;
                    var purchased = new List<GameItem>();
                    for (int n = 0; n < (smoke ? 6 : 40); n++)
                    {
                        var item = Sample("common_ore"); GeneralHelper.SetItemOwned(item, false); purchased.Add(item);
                    }
                    int cost = purchased.Sum(i => (int)i.GetNegociatedValue()), beforeCash = PlayerStore.Instance.playerCash;
                    NegociationUIManager.Instance.BuyMultipleItem(purchased);
                    Check(PlayerStore.Instance.playerCash == beforeCash - cost && purchased.All(i => GeneralHelper.IsItemOwned(i) && i.parentInventory != null), "Native bulk buying preserves payment, ownership and placement: " + enabled);
                    foreach (var item in purchased) item.Destroy();
                    buyer = StoreClientList.CreateUpperLowerVisitor(); buyerInstance = StoreClientInstance.CreateClientInstance(buyer); Seed();
                    buyer.clientIntent = StoreClient.ClientIntent.BUY; buyer.acceptDealDialogue = null;
                    buyer.clientBuyingIdList = new List<string> { "common_ore" };
                    var offered = new List<GameItem>();
                    for (int n = 0; n < (smoke ? 6 : 40); n++) offered.Add(Sample("common_ore"));
                    Check(offered.All(i => PlayerStore.Instance.CanSellThisItem(i, true)), "Bulk selling fixtures satisfy native eligibility: " + enabled);
                    int proceeds = offered.Sum(i => (int)i.GetNegociatedValue()); beforeCash = PlayerStore.Instance.playerCash;
                    NegociationUIManager.Instance.SellMultiple(offered, true);
                    Check(PlayerStore.Instance.playerCash == beforeCash + proceeds && PlayerStore.Instance.soldItemsTracker.Count == offered.Count && offered.All(i => ReferenceEquals(i.parentInventory, EmporiumEntry.Instance.soldElement)), "Native bulk selling preserves proceeds and records: " + enabled);
                    SoldItem.HandleSoldItem(); yield return .1f;
                }
            }
            finally
            {
                probe.UnpatchAll(probe.Id); observedStock = null; Mode(true);
                foreach (var item in samples) item.Destroy();
                stock.SetShape(originalShape.width, originalShape.height);
                stockBackground.image.type = originalBackgroundType;
                foreach (var item in originalItems) { holding.Expel(item); stock.UncheckedAccept(item); }
                UnityEngine.Object.Destroy(holding.handler.gameObject);
            }
        }
    }
}
