using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace SoldStockOptimizer
{
    internal static class InventoryPerformance
    {
        static readonly FieldInfo Items = AccessTools.Field(typeof(GameGridInventory), "items");
        [ThreadStatic] static Queue<GraphNodeStorage> queue;
        [ThreadStatic] static HashSet<GameItem> seen;
        [ThreadStatic] static bool collecting;
        [ThreadStatic] static int batchDepth;
        [ThreadStatic] static bool flushing;
        [ThreadStatic] static List<GameGridInventory> pending;
        [ThreadStatic] static HashSet<GameGridInventory> pendingSet;
        static readonly List<string> compatibilityNotes = new List<string>();

        internal static void Install(Harmony harmony)
        {
            compatibilityNotes.Clear();
            void Traverse(Type type, string method, bool all)
            {
                var replacement = new HarmonyMethod(typeof(InventoryPerformance), all ? nameof(AllItemsCalls) : nameof(CollectCalls));
                OptionalPatch(harmony, type, method, transpiler: replacement);
            }
            Traverse(typeof(EmporiumEntry), "GetAllItems", false);
            Traverse(typeof(EmporiumEntry), "CollectOwnedItems", false);
            Traverse(typeof(PlayerStore), "FindAllItem", true);
            foreach (var target in new[] {
                (AccessTools.TypeByName("InventorySortHelper"), "Commit"),
                (typeof(NegociationUIManager), "BuyMultipleItem"),
                (typeof(NegociationUIManager), "SellMultiple") })
                OptionalPatch(harmony, target.Item1, target.Item2,
                    prefix: new HarmonyMethod(typeof(InventoryPerformance), nameof(BeginBatch)),
                    finalizer: new HarmonyMethod(typeof(InventoryPerformance), nameof(EndBatch)));
            OptionalPatch(harmony, typeof(GameGridInventory), "Validate",
                prefix: new HarmonyMethod(typeof(InventoryPerformance), nameof(DeferVisualRefresh)));
            foreach (var note in compatibilityNotes)
                UnityEngine.Debug.LogWarning("[SoldStockOptimizer] " + note);
        }

        static void OptionalPatch(Harmony harmony, Type type, string name, HarmonyMethod prefix = null,
            HarmonyMethod transpiler = null, HarmonyMethod finalizer = null)
        {
            if (type == null) { Note(name + " optional type is unavailable; keeping the existing implementation."); return; }
            MethodInfo target = null;
            try
            {
                target = AccessTools.Method(type, name);
                if (target == null)
                {
                    Note(type.Name + "." + name + " is unavailable; keeping the existing implementation.");
                    return;
                }
                harmony.Patch(target, prefix: prefix, transpiler: transpiler, finalizer: finalizer);
            }
            catch (Exception error)
            {
                // Remove only this feature's methods, preserving core and other mods' patches.
                foreach (var patch in new[] { transpiler, prefix, finalizer })
                    if (target != null && patch != null) harmony.Unpatch(target, patch.method);
                Note(type.Name + "." + name + " optional optimization was skipped: " + error.GetType().Name + ".");
            }
        }

        static void Note(string message)
        {
            if (!compatibilityNotes.Contains(message)) compatibilityNotes.Add(message);
        }

        internal static void Reset()
        {
            queue?.Clear(); seen?.Clear(); collecting = false;
            batchDepth = 0; pending?.Clear(); pendingSet?.Clear(); flushing = false;
        }

        internal static void Flush()
        {
            if (flushing || pending == null || pending.Count == 0) return;
            var inventories = pending.ToArray(); pending.Clear(); pendingSet.Clear();
            flushing = true;
            try
            {
                foreach (var inventory in inventories)
                    if (inventory.handler != null) inventory.Validate();
            }
            finally { flushing = false; }
        }

        static void BeginBatch(out bool __state)
        {
            __state = ModEntry.Enabled;
            if (__state) batchDepth++;
        }

        static Exception EndBatch(Exception __exception, bool __state)
        {
            if (!__state || batchDepth <= 0) return __exception;
            batchDepth--;
            if (batchDepth == 0)
            {
                try { Flush(); }
                catch (Exception error) { if (__exception == null) return error; }
            }
            return __exception;
        }

        static bool DeferVisualRefresh(GameGridInventory __instance)
        {
            if (!ModEntry.Enabled || batchDepth == 0 || flushing || __instance.GetType() != typeof(GameGridInventory)) return true;
            // Constructors and shape changes update layout dimensions immediately.
            int grid = UISettings.current.gridSize;
            if (__instance.inventoryShape == null || __instance.widthPixels <= 0 || __instance.heightPixels <= 0 ||
                __instance.widthPixels != __instance.inventoryShape.width * grid ||
                __instance.heightPixels != __instance.inventoryShape.height * grid) return true;
            if (pendingSet == null) { pendingSet = new HashSet<GameGridInventory>(); pending = new List<GameGridInventory>(); }
            if (pendingSet.Add(__instance)) pending.Add(__instance);
            return false;
        }

        static void EnqueueChildren(GraphNodeStorage node)
        {
            if (node.GetType() == typeof(GameGridInventory))
            {
                var items = (List<GameItem>)Items.GetValue(node);
                for (int i = 0; i < items.Count; i++) queue.Enqueue(items[i]);
            }
            else if (node.GetType() == typeof(GameItemElement))
            {
                var item = (GameItemElement)node;
                if (item.child != null) queue.Enqueue(item.child);
                if (item.examineWindow != null) queue.Enqueue(item.examineWindow);
            }
            else
            {
                // Other node types keep their own child enumeration, including mod extensions.
                foreach (var child in node.children) queue.Enqueue(child);
            }
        }

        static void CollectItems(GraphNodeStorage root, List<GameItem> results)
        {
            if (!ModEntry.Enabled || collecting)
            {
                if (root != null && results != null) results.AddRange(GraphUtils.FindAllChildrenType<GameItem>(root));
                return;
            }
            if (root == null || results == null) return;
            if (queue == null) { queue = new Queue<GraphNodeStorage>(); seen = new HashSet<GameItem>(); }
            collecting = true;
            try
            {
                queue.Enqueue(root);
                while (queue.Count > 0)
                {
                    var node = queue.Dequeue();
                    if (node != root && node is GameItem item && seen.Add(item)) results.Add(item);
                    EnqueueChildren(node);
                }
            }
            finally { queue.Clear(); seen.Clear(); collecting = false; }
        }

        static List<GameItem> AllItems(GraphNodeStorage root, Func<GameItem, bool> filter, Func<GameItem, bool> passthrough)
        {
            if (filter != null || passthrough != null || !ModEntry.Enabled || collecting)
                return GraphUtils.FindAllChildrenType(root, filter, passthrough);
            var result = new List<GameItem>();
            CollectItems(root, result);
            return result;
        }

        static IEnumerable<CodeInstruction> CollectCalls(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
            => Replace(instructions, __originalMethod, "CollectAllChildrenType", nameof(CollectItems));
        static IEnumerable<CodeInstruction> AllItemsCalls(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
            => Replace(instructions, __originalMethod, "FindAllChildrenType", nameof(AllItems));

        static bool SameSignature(MethodInfo target, MethodInfo replacement)
        {
            if (!target.IsStatic || target.ReturnType != replacement.ReturnType) return false;
            var left = target.GetParameters(); var right = replacement.GetParameters();
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i].ParameterType != right[i].ParameterType) return false;
            return true;
        }

        static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, MethodBase original, string name, string replacement)
        {
            var implementation = AccessTools.Method(typeof(InventoryPerformance), replacement);
            var result = new List<CodeInstruction>(); int count = 0;
            foreach (var code in instructions)
            {
                if (code.opcode == OpCodes.Call && code.operand is MethodInfo target && target.DeclaringType == typeof(GraphUtils) && target.Name == name &&
                    target.IsGenericMethod && target.GetGenericArguments().Length == 1 && target.GetGenericArguments()[0] == typeof(GameItem) &&
                    SameSignature(target, implementation))
                {
                    result.Add(new CodeInstruction(code) { operand = implementation }); count++;
                }
                else result.Add(code);
            }
            if (count == 0) Note(original.DeclaringType.Name + "." + original.Name + " has no compatible traversal calls; keeping the existing implementation.");
            return result;
        }
    }
}
