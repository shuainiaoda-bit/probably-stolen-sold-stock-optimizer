using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Localization.Settings;
using UnityEngine.SceneManagement;
using Optimizer = SoldStockOptimizer.ModEntry;

namespace SoldStockChecks
{
    [HarmonyPatch(typeof(ChainStores.Native.ModEntry), "OnEnable")]
    static class Boot
    {
        static void Postfix()
        {
            if (!Environment.GetCommandLineArgs().Contains("-sold-stock-check")) return;
            if (!Application.isBatchMode) throw new InvalidOperationException("The benchmark requires a silent background player.");
            var go = new GameObject("SoldStock.DisposableBenchmark");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Runner>();
        }
    }

    sealed class Runner : MonoBehaviour
    {
        IEnumerator flow;
        float resume;
        bool finished;
        string phase = "startup";
        readonly Optimizer optimizer = new Optimizer();
        readonly List<object> measurements = new List<object>();
        readonly List<string> checks = new List<string>(), errors = new List<string>();
        static int visualRefreshes;
        StoreClient buyer;
        StoreClientInstance buyerInstance;
        Dictionary<string, double> startingReputation;
        void Start()
        {
            Application.runInBackground = true;
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            Application.logMessageReceived += Log;
            flow = Flow();
        }
        void Log(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                errors.Add(message + "\n" + stack);
        }
        void Update()
        {
            if (finished || Time.realtimeSinceStartup < resume) return;
            try
            {
                if (Time.realtimeSinceStartup > 1500) throw new TimeoutException(phase);
                if (!flow.MoveNext()) { Finish(errors.Count == 0, "Benchmark completed"); return; }
                resume = Time.realtimeSinceStartup + (flow.Current is float delay ? delay : .02f);
            }
            catch (Exception ex) { Finish(false, ex.ToString()); }
        }
        void Finish(bool pass, string detail)
        {
            finished = true;
            optimizer.OnDisable();
            UnityEngine.Debug.Log("[SoldStockCheck] RESULT " + JsonConvert.SerializeObject(new {
                status = pass ? "PASS" : "FAIL", detail, phase, checks, errors, measurements,
                gameVersion = Application.version, graphics = SystemInfo.graphicsDeviceType.ToString(),
                loadedMods = ModLoader.Mods.Where(m => m.IsEnabled && m.LoadError == null).Select(m => m.Manifest.ID).ToArray(),
                batch = Application.isBatchMode, audioDisabled = Environment.GetCommandLineArgs().Contains("-noaudio"),
                harness = "Exact shipping optimizer DLL loaded by the native loader in the existing approved diagnostic host"
            }));
            Application.Quit(pass ? 0 : 2);
        }
        void Check(bool pass, string label)
        {
            if (!pass) throw new InvalidOperationException(label);
            checks.Add(label);
            UnityEngine.Debug.Log("[SoldStockCheck] PASS " + label);
        }
        void Mode(bool enabled)
        {
            optimizer.OnDisable();
            if (enabled) optimizer.OnEnable();
        }
        void ClearSold()
        {
            foreach (var item in EmporiumEntry.Instance.soldElement.childItems) item.Destroy();
            PlayerStore.Instance.soldItemsTracker.Clear();
        }
        void Seed()
        {
            UnityEngine.Random.InitState(20461117);
            RNG.random = new System.Random(20461117);
            buyer.useClientBudget = false;
            PlayerStore.Instance.currentClientInstance = buyerInstance;
            PlayerStore.Instance.playerCash = 1000000;
            if (startingReputation != null)
                foreach (var rep in PlayerStore.Instance.storeReputations) rep.SetReputation(startingReputation[rep.factionId]);
        }
        GameItem Sample(string definition)
        {
            var item = DirectoryMaster.Item(definition) ?? throw new Exception("Missing fixture " + definition);
            GeneralHelper.SetItemOwned(item, true);
            if (!EmporiumEntry.Instance.backInvinvElement.UncheckedAccept(item)) throw new Exception("Counter refused fixture");
            return item;
        }
        static void CountVisual(GameItemElement __instance)
        {
            if (ReferenceEquals(__instance.parentInventory, EmporiumEntry.Instance.soldElement)) visualRefreshes++;
        }
        IEnumerator Flow()
        {
            while (MainMenuUIController.Instance == null || NewGameData.Instance == null || !LocalizationSettings.InitializationOperation.IsDone || Time.realtimeSinceStartup < 4) yield return .25f;
            var chain = ModLoader.Mods.Single(m => m.Manifest.ID == "chain_stores" && m.Instance is ChainStores.Native.ModEntry);
            chain.Instance.OnDisable();
            new ChainStores.Native.ModEntry().OnDisable();
            optimizer.OnDisable();
            new Harmony("mod.chain_stores").UnpatchAll("mod.chain_stores");
            NewGameData.Instance.startType = NewGameData.StartType.WATER_MERCHANT;
            NewGameData.Instance.skipIntro = true;
            NewGameData.Instance.disableTutorialHint = true;
            NewGameData.Instance.endlessMode = true;
            NewGameData.Instance.isInMainMenu = false;
            NewGameData.Instance.saveSlot = -1;
            NewGameData.Instance.isLoadFromMainMenu = false;
            SceneManager.LoadScene("InventoryScene");
            yield return 2f;
            while (!PlayerStore.HasInstance || EmporiumEntry.Instance == null || PlayerStore.Instance.saveSlotId < 0) yield return .25f;
            yield return 3f;
            PlayerStore.Instance.IsPropertyPaid = true;
            buyer = StoreClientList.CreateUpperLowerVisitor();
            buyerInstance = StoreClientInstance.CreateClientInstance(buyer);
            startingReputation = PlayerStore.Instance.storeReputations.ToDictionary(r => r.factionId, r => r.GetReputationExact());
            Check(EmporiumEntry.Instance.soldElement.childItems.Count == 0, "Fresh disposable game has an empty sold bag");
            Seed();
            // Warm both paths before recording measurements.
            foreach (bool enabled in new[] { false, true })
            {
                Mode(enabled);
                for (int n = 0; n < 12; n++) PlayerStore.Instance.SellItem(Sample("common_ore"));
                ClearSold();
            }
            yield return .1f;
            int[] counts = Environment.GetCommandLineArgs().Contains("-sold-stock-smoke") ? new[] { 40 } : new[] { 1000, 2000 };
            int repetitions = Environment.GetCommandLineArgs().Contains("-sold-stock-smoke") ? 1 : 3;
            for (int repeat = 0; repeat < repetitions; repeat++)
            foreach (int count in counts)
            foreach (bool enabled in repeat % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                phase = (enabled ? "optimized" : "baseline") + "-" + count + "-r" + repeat;
                Mode(enabled); Seed();
                PlayerStore.Instance.storeClientManager.chemicalFeedbackStack.Clear();
                GC.Collect(); yield return .15f;
                double saleMs = 0, firstMs = 0, lastMs = 0;
                long allocated = 0;
                int expectedCash = PlayerStore.Instance.playerCash;
                var ids = new List<int>();
                var watch = new Stopwatch();
                for (int n = 0; n < count; n++)
                {
                    var item = Sample(n % 5 == 0 ? "wire" : "common_ore");
                    ids.Add(item.uniqueId);
                    expectedCash += (int)item.GetNegociatedValue();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    watch.Restart(); PlayerStore.Instance.SellItem(item); watch.Stop();
                    double ms = watch.Elapsed.TotalMilliseconds;
                    allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                    saleMs += ms;
                    if (n < 100) firstMs += ms;
                    if (n >= count - 100) lastMs += ms;
                    if ((n + 1) % 100 == 0) { UnityEngine.Debug.Log("[SoldStockCheck] PROGRESS " + phase + " " + (n + 1)); yield return .01f; }
                }
                Check(PlayerStore.Instance.playerCash == expectedCash, phase + " preserves all sale proceeds");
                var sold = EmporiumEntry.Instance.soldElement.childItems;
                Check(sold.Count == count && sold.Select(i => i.uniqueId).SequenceEqual(ids), phase + " preserves item count, order and identity");
                Check(sold.Sum(i => i.unitCount) == count, phase + " sells individual goods rather than a single stacked item");
                Check(PlayerStore.Instance.soldItemsTracker.Count == count && PlayerStore.Instance.soldItemsTracker.Select(i => i.itemUniqueId).SequenceEqual(ids), phase + " records each sale exactly once");
                watch.Restart();
                foreach (var record in PlayerStore.Instance.soldItemsTracker) if (record.GetGameItem() == null) throw new Exception("Lost sold item");
                watch.Stop(); double lookupMs = watch.Elapsed.TotalMilliseconds;
                watch.Restart(); PlayerStore.Instance.SaveGame(); watch.Stop(); double saveMs = watch.Elapsed.TotalMilliseconds;
                watch.Restart(); SoldItem.HandleSoldItem(); watch.Stop(); double closeMs = watch.Elapsed.TotalMilliseconds;
                Check(EmporiumEntry.Instance.soldElement.childItems.Count == 0 && PlayerStore.Instance.soldItemsTracker.Count == 0 && sold.All(i => ((GameItemElement)i).IsDestroyed()), phase + " native closing destroys every sold item and clears records");
                measurements.Add(new { phase, repeat, count, optimized = enabled, saleMs, first100Ms = firstMs, last100Ms = lastMs, lookupMs, saveMs, closeMs, allocatedBytes = allocated });
                UnityEngine.Debug.Log("[SoldStockCheck] MEASUREMENT " + JsonConvert.SerializeObject(measurements.Last()));
                yield return .2f;
            }
            phase = "behavior-regression";
            var probe = new Harmony("soldstock.visualprobe");
            probe.Patch(AccessTools.Method(typeof(GameItemElement), "Validate"), prefix: new HarmonyMethod(typeof(Runner), nameof(CountVisual)));
            foreach (bool enabled in new[] { false, true })
            {
                Mode(enabled); Seed(); visualRefreshes = 0;
                for (int n = 0; n < 50; n++) PlayerStore.Instance.SellItem(Sample("wire"));
                measurements.Add(new { visualProbe = true, optimized = enabled, count = 50, visualRefreshes });
                ClearSold();
            }
            probe.UnpatchAll(probe.Id);
            Mode(true); Seed();
            var nested = PreBuiltItemHelper.LootCrateMedical();
            GeneralHelper.SetItemOwned(nested, true);
            EmporiumEntry.Instance.backInvinvElement.UncheckedAccept(nested);
            var tree = new[] { nested }.Concat(nested.FindAllChildItems()).Select(i => i.uniqueId).ToArray();
            PlayerStore.Instance.SellItem(nested);
            Check(new[] { nested }.Concat(nested.FindAllChildItems()).Select(i => i.uniqueId).SequenceEqual(tree), "Nested sold container preserves all child identities");
            Check(PlayerStore.Instance.soldItemsTracker.Single().GetGameItem() == nested, "Indexed lookup resolves a sold nested container");
            optimizer.OnDisable();
            Check(PlayerStore.Instance.soldItemsTracker.Single().GetGameItem() == nested, "Disabling restores native lookup with retained goods");
            optimizer.OnEnable();
            SoldItem.HandleSoldItem();
            Check(((GameItemElement)nested).IsDestroyed(), "Native closing destroys sold nested containers");
            var chemistry = ChemistryRegression(); while (chemistry.MoveNext()) yield return chemistry.Current;
            var reload = ReloadRegression(); while (reload.MoveNext()) yield return reload.Current;
            Check(errors.Count == 0, "Native scenarios captured no runtime errors");
        }
        IEnumerator ChemistryRegression()
        {
            // Run the same seeded chemical feedback against both paths and compare native state changes.
            var results = new List<string>();
            foreach (bool enabled in new[] { false, true })
            {
                Mode(enabled); Seed();
                PlayerStore.Instance.storeClientManager.chemicalFeedbackStack.Clear();
                var item = Sample("small_bottled_water");
                ChemicalFeedbackHelper.InitChemicalIntentItem(item, ChemicalFeedbackHelper.ChemicalIntent.NARCOTIC);
                var record = new SoldItem { itemId = item.identifier, itemName = item.name, itemUniqueId = item.uniqueId,
                    clientId = buyer.identifier, clientName = buyer.displayName, clientSprite = buyer.spriteName,
                    factionId = buyer.clientFaction, isPersonalUse = true, CanReturn = true, CanReturnAsBuyer = true, alwaysFeedback = true, soldPrice = 5 };
                item.parentInventory.Expel(item); EmporiumEntry.Instance.soldElement.UncheckedAccept(item);
                PlayerStore.Instance.soldItemsTracker.Add(record);
                var previousFront = new HashSet<int>(EmporiumEntry.Instance.frontInvinvElement.childItems.Select(i => i.uniqueId));
                Check(SoldItem.SelectEligibleChemicalItem().Item1.Count == 1, "Chemical fixture has a nonzero feedback level: " + enabled);
                string selected = JsonConvert.SerializeObject(SoldItem.SelectEligibleChemicalItem().Item1.Select(r => new { r.clientId, r.feedbackLevel }));
                Check(record.GetGameItem() == item && ChemicalFeedbackHelper.GetChemicalIntent(item) == ChemicalFeedbackHelper.ChemicalIntent.NARCOTIC, "Chemical item data remains available: " + enabled);
                SoldItem.HandleSoldItem();
                var feedback = PlayerStore.Instance.storeClientManager.chemicalFeedbackStack.Select(c => new {
                    c.identifier, c.displayName, c.clientIntent, c.serviceCost,
                    dialogue = JsonUtility.ToJson(c.mainDialogue)
                }).ToArray();
                var notes = EmporiumEntry.Instance.frontInvinvElement.childItems.Where(i => !previousFront.Contains(i.uniqueId)).ToArray();
                Check(feedback.Length + notes.Length > 0, "Chemical feedback actually creates a client or handnote: " + enabled);
                results.Add(JsonConvert.SerializeObject(new { selected, feedback, notes = notes.Select(i => new { i.identifier, i.name, i.customText }).ToArray(),
                    reputations = PlayerStore.Instance.storeReputations.Select(r => new { r.factionId, value = r.GetReputationExact() }).ToArray() }));
                Check(((GameItemElement)item).IsDestroyed() && PlayerStore.Instance.soldItemsTracker.Count == 0, "Chemical feedback closing completes: " + enabled);
                foreach (var note in notes) note.Destroy();
                yield return .1f;
            }
            Check(results[0] == results[1], "Seeded chemical feedback levels, queued clients, dialogue and reputation match baseline");
            PlayerStore.Instance.storeClientManager.chemicalFeedbackStack.Clear();
        }
        IEnumerator ReloadRegression()
        {
            Mode(true); Seed();
            var sold = Sample("wire"); PlayerStore.Instance.SellItem(sold);
            int unique = sold.uniqueId, slot = PlayerStore.Instance.saveSlotId;
            string run = PlayerStore.Instance.runID;
            PlayerStore.Instance.SaveGame();
            StoreUIManager.Instance.escapeUIManager.OpenEscapeMenu(); yield return .2f;
            StoreUIManager.Instance.escapeUIManager.OnMainMenu();
            while (MainMenuUIController.Instance == null || PlayerStore.HasInstance) yield return .25f;
            yield return .5f;
            NewGameData.Instance.saveSlot = slot; NewGameData.Instance.saveFile = SaveFiles.Slot(slot); NewGameData.Instance.isLoadFromMainMenu = true;
            MainMenuUIController.Instance.StartNewGameClick();
            while (!PlayerStore.HasInstance || EmporiumEntry.Instance == null) yield return .25f;
            yield return 2f;
            Check(PlayerStore.Instance.runID == run && EmporiumEntry.Instance.GetItemFromSoldElement(unique)?.identifier == "wire", "Native save reload preserves sold goods and resets lookup cache");
            SoldItem.HandleSoldItem();
            Check(EmporiumEntry.Instance.soldElement.childItems.Count == 0 && PlayerStore.Instance.soldItemsTracker.Count == 0, "Reloaded sold goods complete native closing");
            int day = StoreStation.Instance.dayCounter;
            buyer = StoreClientList.CreateUpperLowerVisitor();
            buyerInstance = StoreClientInstance.CreateClientInstance(buyer);
            Seed();
            var nextDaySale = Sample("wire");
            PlayerStore.Instance.SellItem(nextDaySale);
            EndOfDayUIManager.Instance.OpenUI(); yield return .25f;
            EndOfDayUIManager.Instance.OnEndDayButtonClick(); yield return 4f;
            Check(StoreStation.Instance.dayCounter == day + 1, "Native sleep flow advances the day with the optimizer enabled");
            PlayerStore.Instance.OnShutterOpened();
            Check(((GameItemElement)nextDaySale).IsDestroyed() && EmporiumEntry.Instance.soldElement.childItems.Count == 0 && PlayerStore.Instance.soldItemsTracker.Count == 0,
                "Next-day native shutter opening settles and destroys retained sold goods");
        }
    }
}
