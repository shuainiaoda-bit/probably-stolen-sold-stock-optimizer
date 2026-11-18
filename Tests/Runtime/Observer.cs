using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Localization.Settings;
using UnityEngine.SceneManagement;

namespace SoldStockRuntime012
{
    [HarmonyPatch(typeof(ChainStores.Native.ModEntry), "OnEnable")]
    static class Boot
    {
        static void Postfix()
        {
            if (!Environment.GetCommandLineArgs().Contains("-sold-runtime012")) return;
            var go = new GameObject("SoldStock.NativeRuntimeObserver");
            UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<Observer>();
        }
    }
    sealed class Observer : MonoBehaviour
    {
        const string Id = "sold_stock_optimizer";
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        readonly Stack<IEnumerator> routines = new Stack<IEnumerator>();
        readonly List<string> checks = new List<string>(), errors = new List<string>();
        readonly List<object> observations = new List<object>();
        bool finished; string phase = "startup"; float resume;
        void Start()
        {
            Application.runInBackground = true; Application.targetFrameRate = 30; QualitySettings.vSyncCount = 0;
            Application.logMessageReceived += Log; routines.Push(Main());
        }
        void Log(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) errors.Add(message + "\n" + stack);
        }
        void Update()
        {
            if (finished || Time.realtimeSinceStartup < resume) return;
            try
            {
                if (Time.realtimeSinceStartup > 420) throw new TimeoutException(phase);
                for (int i = 0; i < 32; i++)
                {
                    if (routines.Count == 0) { Check(errors.Count == 0, "No runtime Error, Exception or Assert messages"); Finish(true, "Native runtime validation completed"); return; }
                    var top = routines.Peek();
                    if (!top.MoveNext()) { routines.Pop(); continue; }
                    if (top.Current is IEnumerator nested) { routines.Push(nested); continue; }
                    resume = Time.realtimeSinceStartup + (top.Current is float delay ? delay : .03f); return;
                }
            }
            catch (Exception error) { Finish(false, error.ToString()); }
        }
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks.Add(label); UnityEngine.Debug.Log("[SoldRuntime012] PASS " + label);
        }
        IEnumerator Wait(Func<bool> predicate, string label, float timeout = 50)
        {
            float end = Time.realtimeSinceStartup + timeout;
            while (!predicate()) { if (Time.realtimeSinceStartup > end) throw new TimeoutException(label); yield return .1f; }
        }
        LoadedMod Target => ModLoader.Mods.Single(m => m.Manifest.ID == Id);
        IEnumerator Main()
        {
            yield return Wait(() => MainMenuUIController.Instance != null && ModUIManager.Instance != null && NewGameData.Instance != null && LocalizationSettings.InitializationOperation.IsDone && Time.realtimeSinceStartup > 4, "native main menu");
            Check(Application.isBatchMode && Environment.GetCommandLineArgs().Contains("-noaudio"), "Silent background launch flags are present");
            string[] expected = Environment.GetCommandLineArgs().SkipWhile(a => a != "-sold-runtime-mods").Skip(1).First().Split(',');
            observations.Add(new { kind = "native-catalogue-order", ids = ModLoader.Mods.Where(m => m.IsEnabled && m.LoadError == null).Select(m => m.Manifest.ID).ToArray() });
            foreach (string id in expected)
            {
                var mod = ModLoader.Mods.Single(m => m.Manifest.ID == id);
                Check(mod.IsEnabled && mod.Instance != null && mod.LoadError == null, "Native loader enabled " + id);
                observations.Add(new { kind = "loaded-mod", id, version = mod.Manifest.ModVersion, source = mod.Source.ToString(), error = mod.LoadError?.ToString() });
            }
            Check(Target.Manifest.ModVersion == "0.1.2" && Target.Instance.GetType().Assembly.GetName().Name == "SoldStockOptimizer", "Exact production 0.1.2 assembly owns its native entry");
            var enabled = Target.Instance.GetType().GetField("Enabled", All);
            Check((bool)enabled.GetValue(null), "Production optimizer Enabled flag is true");
            Check(ChainStores.Native.ModEntry.Enabled, "Chain Stores runtime stays enabled throughout compatibility validation");
            var patched = Harmony.GetAllPatchedMethods().Where(m => Harmony.GetPatchInfo(m).Owners.Contains("mod.sold_stock_optimizer")).Select(m => m.DeclaringType.Name + "." + m.Name).Distinct().OrderBy(x => x).ToArray();
            observations.Add(new { kind = "production-patches", methods = patched });
            Check(patched.Contains("EmporiumEntry.GetAllItems") && patched.Contains("PlayerStore.FindAllItem"), "Production traversal patches installed in the Unity runtime");
            phase = "new disposable game";
            NewGameData.Instance.startType = NewGameData.StartType.WATER_MERCHANT; NewGameData.Instance.skipIntro = true;
            NewGameData.Instance.disableTutorialHint = true; NewGameData.Instance.endlessMode = true; NewGameData.Instance.isInMainMenu = false;
            NewGameData.Instance.saveSlot = -1; NewGameData.Instance.isLoadFromMainMenu = false; SceneManager.LoadScene("InventoryScene");
            yield return Wait(() => PlayerStore.HasInstance && EmporiumEntry.Instance != null && PlayerStore.Instance.saveSlotId >= 0, "disposable inventory");
            yield return 4f; TutorialUIManager.Instance.CloseAllTutorial(); StoreUIManager.Instance.CloseAllUI();
            Check(ChainStores.Native.Runtime.Ready && ChainStores.Native.Runtime.Error == "", "Chain Stores binds the disposable campaign without error");
            var buyer = StoreClientList.CreateUpperLowerVisitor(); buyer.useClientBudget = false;
            PlayerStore.Instance.currentClientInstance = StoreClientInstance.CreateClientInstance(buyer); PlayerStore.Instance.playerCash = 1000000; PlayerStore.Instance.IsPropertyPaid = true;
            phase = "2000 sales with all selected mods enabled";
            var soldIds = new List<int>(); var watch = Stopwatch.StartNew(); int expectedCash = PlayerStore.Instance.playerCash;
            Check(EmporiumEntry.Instance.soldElement.childItems.Count == 0, "Disposable sold inventory starts empty");
            for (int n = 0; n < 2000; n++)
            {
                var item = DirectoryMaster.Item(n % 5 == 0 ? "wire" : "common_ore"); GeneralHelper.SetItemOwned(item, true);
                CheckAccept(EmporiumEntry.Instance.backInvinvElement.UncheckedAccept(item)); soldIds.Add(item.uniqueId);
                expectedCash += (int)item.GetNegociatedValue(); PlayerStore.Instance.SellItem(item);
                if (n % 200 == 199) { UnityEngine.Debug.Log("[SoldRuntime012] SALES " + (n + 1)); yield return .01f; }
            }
            watch.Stop(); observations.Add(new { kind = "sales", count = 2000, wallMilliseconds = watch.Elapsed.TotalMilliseconds, performanceComparison = false });
            Check(PlayerStore.Instance.playerCash == expectedCash, "2000 sales preserve all proceeds with other mods active");
            Check(EmporiumEntry.Instance.soldElement.childItems.Select(i => i.uniqueId).SequenceEqual(soldIds), "2000 sold items preserve order and identity");
            Check(PlayerStore.Instance.soldItemsTracker.Select(i => i.GetGameItem().uniqueId).SequenceEqual(soldIds), "Every sold record resolves the exact shipping indexed item");
            var owned = PlayerStore.Instance.FindAllItem(false);
            Check(owned.Select(i => i.uniqueId).Distinct().Count() == owned.Count, "Patched owned query returns no duplicate physical items");
            Check(owned.Select(i => i.uniqueId).SequenceEqual(PlayerStore.Instance.FindAllItem(false).Select(i => i.uniqueId)), "Repeated patched queries keep native order");
            phase = "native sorting and smart sort"; yield return Sort();
            PlayerStore.Instance.SaveGame(); Check(ChainStores.Native.Runtime.Ready && ChainStores.Native.Runtime.Error == "", "Native save preserves active Chain Stores state");
            SoldItem.HandleSoldItem();
            Check(EmporiumEntry.Instance.soldElement.childItems.Count == 0 && PlayerStore.Instance.soldItemsTracker.Count == 0, "Native closing clears all 2000 sold items and records");
            yield return 3f;
            Check((bool)enabled.GetValue(null) && ChainStores.Native.ModEntry.Enabled, "Both production mods remain enabled after trade, sort, save and closing");
        }
        void CheckAccept(bool accepted) { if (!accepted) throw new InvalidOperationException("Disposable sale fixture was refused"); }
        IEnumerator Sort()
        {
            var bag = DirectoryMaster.Item("backpack_medium"); GeneralHelper.SetItemOwned(bag, true); CheckAccept(EmporiumEntry.Instance.backInvinvElement.UncheckedAccept(bag));
            var inventory = GraphUtils.FindAllChildrenType<GameGridInventory>(bag).First();
            var samples = new List<GameItem>();
            foreach (string id in new[] { "energy_credit", "handmade_pistol", "bandage_item" })
            {
                var item = DirectoryMaster.Item(id); GeneralHelper.SetItemOwned(item, true); Check(GraphUtils.TryAcceptAll(inventory, item) == 0, "Native backpack accepts sort fixture " + id); samples.Add(item);
            }
            int[] ids = inventory.childItems.Select(i => i.uniqueId).OrderBy(i => i).ToArray();
            Check(InventorySortHelper.Sort(inventory, true, false), "Native sorting succeeds with production batch patch");
            Check(ids.SequenceEqual(inventory.childItems.Select(i => i.uniqueId).OrderBy(i => i)), "Native sorting preserves every physical item");
            Check(InventorySortHelper.Undo(inventory), "Native sorting undo succeeds");
            var context = ItemContextHandler.current; var field = typeof(ItemContextHandler).GetField("currentInventory", All);
            if (typeof(ItemContextHandler).GetField("sortWindow", All).GetValue(context) == null) typeof(ItemContextHandler).GetMethod("OnEventInit", All).Invoke(context, null);
            var window = (PixelWindow)typeof(ItemContextHandler).GetField("sortWindow", All).GetValue(context);
            var menu = window.childElement as GridPixelElement; Check(menu != null && menu.gridHeight >= 6, "Smart Inventory Arrange native menu extension is present");
            var button = menu.GetElement(0, 4) as RichTextElement; Check(button != null, "Actual smart-sort native action is available");
            object previous = field.GetValue(context); field.SetValue(context, inventory);
            typeof(ItemContextHandler).GetMethod("TriggerButton", All).Invoke(context, new object[] { button });
            yield return 8f; field.SetValue(context, previous); window.Hide();
            Check(ids.SequenceEqual(inventory.childItems.Select(i => i.uniqueId).OrderBy(i => i)) && samples.All(i => i.unitCount == 1), "Actual smart sort preserves item identities and quantities");
        }
        void Finish(bool pass, string detail)
        {
            finished = true;
            UnityEngine.Debug.Log("[SoldRuntime012] RESULT " + JsonConvert.SerializeObject(new { status = pass ? "PASS" : "FAIL", detail, phase, checks, errors, observations, gameVersion = Application.version, batch = Application.isBatchMode, audioDisabled = Environment.GetCommandLineArgs().Contains("-noaudio"), nativeLoader = true, productionEntryInvokedManually = false, chainStoresDisabled = false }));
            Application.Quit(pass ? 0 : 2);
        }
    }
}
