using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace SoldStockOptimizer
{
    public sealed class ModEntry : IMod
    {
        public const string Id = "sold_stock_optimizer";
        public const string PatchId = "mod." + Id;
        static Harmony harmony;
        internal static bool Enabled;
        static GameGridInventory indexedBag;
        static Dictionary<int, GameItem> index;
        static readonly FieldInfo ItemsField = AccessTools.Field(typeof(GameGridInventory), "items");

        public void Init(ModManifest manifest) { }

        public void OnEnable()
        {
            if (Enabled) return;
            if (ItemsField == null || ItemsField.FieldType != typeof(List<GameItem>))
                throw new NotSupportedException("The sold inventory layout is not supported by Sold Stock Optimizer.");
            harmony = new Harmony(PatchId);
            try
            {
                Patch(typeof(GameGridInventory), "Validate", prefix: nameof(ValidateHiddenBag));
                Patch(typeof(EmporiumEntry), "GetItemFromSoldElement", prefix: nameof(FindSoldItem));
                Patch(typeof(GameGridInventory), "UncheckedAccept", postfix: nameof(InventoryChanged));
                Patch(typeof(GameGridInventory), "Expel", postfix: nameof(InventoryChanged));
                Patch(typeof(SoldItem), "HandleSoldItem", transpiler: nameof(UseLiveCleanupList));
                InventoryPerformance.Install(harmony);
                ModHook.OnGameLoadedInit += Reset;
                Enabled = true;
            }
            catch
            {
                harmony.UnpatchAll(PatchId);
                harmony = null;
                Reset();
                throw;
            }
        }

        static void Patch(Type type, string name, string prefix = null, string postfix = null, string transpiler = null)
        {
            var target = ModReflect.Target(type, name);
            HarmonyMethod Method(string method) => method == null ? null : new HarmonyMethod(typeof(ModEntry), method);
            harmony.Patch(target, Method(prefix), Method(postfix), Method(transpiler));
        }

        public void OnDisable()
        {
            try { InventoryPerformance.Flush(); }
            finally
            {
                Enabled = false;
                ModHook.OnGameLoadedInit -= Reset;
                harmony?.UnpatchAll(PatchId);
                harmony = null;
                Reset();
            }
        }

        static void Reset() { indexedBag = null; index = null; InventoryPerformance.Reset(); }

        static bool IsSoldBag(GameGridInventory bag) => Enabled && bag != null &&
            EmporiumEntry.Instance != null && ReferenceEquals(bag, EmporiumEntry.Instance.soldElement);

        static bool ValidateHiddenBag(GameGridInventory __instance)
        {
            // Shape and state updates remain native; this method only refreshes the hidden inventory's visuals.
            return !IsSoldBag(__instance);
        }

        static void InventoryChanged(GameGridInventory __instance, bool __result)
        {
            if (__result && ReferenceEquals(__instance, indexedBag)) index = null;
        }

        static bool FindSoldItem(EmporiumEntry __instance, int itemUniqueId, ref GameItem __result)
        {
            if (!Enabled || __instance != EmporiumEntry.Instance || __instance.soldElement == null) return true;
            var bag = __instance.soldElement;
            if (!ReferenceEquals(indexedBag, bag) || index == null)
            {
                indexedBag = bag;
                index = new Dictionary<int, GameItem>();
                foreach (var item in bag.childItems)
                    if (item != null && !index.ContainsKey(item.uniqueId)) index.Add(item.uniqueId, item);
            }
            if (!index.TryGetValue(itemUniqueId, out var found)) return true;
            if (!ReferenceEquals(found.parentInventory, bag)) { index = null; return true; }
            __result = found;
            return false;
        }

        static List<GameItem> CleanupItems(GameGridInventory bag)
        {
            // The native reverse destruction loop can read its own backing list without making two copies per item.
            return IsSoldBag(bag) ? (List<GameItem>)ItemsField.GetValue(bag) : bag.childItems;
        }

        static IEnumerable<CodeInstruction> UseLiveCleanupList(IEnumerable<CodeInstruction> instructions)
        {
            var getter = AccessTools.PropertyGetter(typeof(GameGridInventory), "childItems");
            var baseGetter = AccessTools.PropertyGetter(typeof(GameInventory), "childItems");
            var replacement = AccessTools.Method(typeof(ModEntry), nameof(CleanupItems));
            int replaced = 0;
            var result = new List<CodeInstruction>();
            foreach (var code in instructions)
            {
                if (code.Calls(getter) || code.Calls(baseGetter))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = replacement;
                    replaced++;
                }
                result.Add(code);
            }
            if (replaced != 2) throw new NotSupportedException("The sold-item cleanup method has changed; expected two list reads.");
            return result;
        }
    }
}
