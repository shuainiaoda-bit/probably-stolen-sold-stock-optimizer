using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.Loader;
using HarmonyLib;

string root = Path.GetFullPath(args[0]);
string managed = args.Length > 1 ? Path.GetFullPath(args[1]) : @"C:\Apps\steam\steamapps\common\Probably Stolen\Probably Stolen_Data\Managed";
AssemblyLoadContext.Default.Resolving += (_, name) => {
    string path = Path.Combine(managed, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(managed, "Assembly-CSharp.dll"));
var legacyContext = new AssemblyLoadContext("legacy-reproduction");
legacyContext.Resolving += (_, name) => name.Name == "Assembly-CSharp" ? game :
    name.Name == "0Harmony" ? typeof(Harmony).Assembly : AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
string legacyPath = args.Length > 2 ? Path.GetFullPath(args[2]) : Path.Combine(root, "release/0.1.1/content/SoldStockOptimizer.dll");
var legacy = legacyContext.LoadFromAssemblyPath(legacyPath);
var oldRewrite = legacy.GetType("SoldStockOptimizer.InventoryPerformance", true)!.GetMethod("CollectCalls", BindingFlags.NonPublic | BindingFlags.Static)!;
var fixture = AccessTools.Method(typeof(Fixture), nameof(Fixture.GetAllItems));
string oldError = "";
try { ((IEnumerable<CodeInstruction>)oldRewrite.Invoke(null, new object[] { Plain(), fixture })!).ToArray(); }
catch (TargetInvocationException error) { oldError = error.InnerException!.Message; }
Require(oldError == "GetAllItems traversal changed; expected 10 calls, found 0.", "Reproduced the screenshot's exact 0.1.1 exception");

var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "bin/Release/netstandard2.1/SoldStockOptimizer.dll"));
var performance = mod.GetType("SoldStockOptimizer.InventoryPerformance", true)!;
var collect = AccessTools.Method(performance, "CollectCalls");
var all = AccessTools.Method(performance, "AllItemsCalls");
var optional = AccessTools.Method(performance, "OptionalPatch");
var itemType = game.GetType("GameItem", true)!;
var graph = game.GetType("GraphUtils", true)!;
var checks = new List<string> { "Reproduced the screenshot's exact 0.1.1 exception" };
void Check(bool ok, string label) { Require(ok, label); checks.Add(label); }
CodeInstruction[] Rewrite(MethodInfo method, IEnumerable<CodeInstruction> instructions, MethodBase? original = null)
    => ((IEnumerable<CodeInstruction>)method.Invoke(null, new object[] { instructions, original ?? fixture })!).ToArray();
var nativeCollect = graph.GetMethods().Single(m => m.Name == "CollectAllChildrenType" && m.IsGenericMethodDefinition).MakeGenericMethod(itemType);
var nativeFind = graph.GetMethods().Single(m => m.Name == "FindAllChildrenType" && m.IsGenericMethodDefinition).MakeGenericMethod(itemType);

foreach (int count in new[] { 0, 1, 9, 10, 11, 23 })
{
    var input = Enumerable.Range(0, count).Select(_ => new CodeInstruction(OpCodes.Call, nativeCollect)).Concat(Plain()).ToArray();
    var output = Rewrite(collect, input);
    Check(output.Count(c => c.operand is MethodInfo m && m.Name == "CollectItems") == count, "Collect rewrite supports " + count + " matching calls");
    Check(input.Take(count).All(c => Equals(c.operand, nativeCollect)), "Input instructions remain unchanged for " + count + " calls");
}
foreach (int count in new[] { 0, 1, 11, 12 })
{
    var output = Rewrite(all, Enumerable.Range(0, count).Select(_ => new CodeInstruction(OpCodes.Call, nativeFind)).Concat(Plain()));
    Check(output.Count(c => c.operand is MethodInfo m && m.Name == "AllItems") == count, "Find rewrite supports " + count + " matching calls");
}
var foreign = graph.GetMethods().Single(m => m.Name == "CollectAllChildrenType" && m.IsGenericMethodDefinition).MakeGenericMethod(typeof(object));
var untouched = new[] { new CodeInstruction(OpCodes.Ldftn, nativeCollect), new CodeInstruction(OpCodes.Call, foreign) };
Check(Rewrite(collect, untouched).SequenceEqual(untouched), "Function pointers and other generic types are preserved");
var labeled = new CodeInstruction(OpCodes.Call, nativeCollect);
var generator = new DynamicMethod("label-holder", typeof(void), Type.EmptyTypes).GetILGenerator();
labeled.labels.Add(generator.DefineLabel());
labeled.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
var replacement = Rewrite(collect, new[] { labeled })[0];
Check(replacement.labels.SequenceEqual(labeled.labels) && replacement.blocks.SequenceEqual(labeled.blocks), "Branch labels and exception metadata are preserved");

var nativeGetAll = game.GetType("EmporiumEntry", true)!.GetMethod("GetAllItems")!;
var originalNative = PatchProcessor.GetOriginalInstructions(nativeGetAll).ToArray();
var rewrittenNative = Rewrite(collect, originalNative, nativeGetAll);
int knownCalls = originalNative.Count(c => c.Calls(nativeCollect));
Check(knownCalls == 10 && rewrittenNative.Count(c => c.operand is MethodInfo m && m.Name == "CollectItems") == knownCalls,
    "Installed game retains all ten compatible GetAllItems rewrites");

foreach (int value in new[] { 37, 42 })
{
    var modified = new[] { new CodeInstruction(OpCodes.Ldc_I4, value), new CodeInstruction(OpCodes.Ret) };
    var output = Rewrite(collect, modified);
    var callable = new DynamicMethod("preserved-" + value, typeof(int), Type.EmptyTypes);
    var il = callable.GetILGenerator();
    foreach (var instruction in output)
        if (instruction.operand is int number) il.Emit(instruction.opcode, number); else il.Emit(instruction.opcode);
    Check(((Func<int>)callable.CreateDelegate(typeof(Func<int>)))() == value,
        "Zero-match native or previously modified IL retains result " + value);
}
var owner = new Harmony("compat.fixture.optimizer");
optional.Invoke(null, new object?[] { owner, typeof(Fixture), "MissingOptionalMethod", null, null, null });
Check(Harmony.GetAllPatchedMethods().All(m => Harmony.GetPatchInfo(m)?.Owners.Contains(owner.Id) != true),
    "Missing optional methods install no patch");
optional.Invoke(null, new object?[] { owner, null, "MissingOptionalType", null, null, null });
Check(Harmony.GetAllPatchedMethods().All(m => Harmony.GetPatchInfo(m)?.Owners.Contains(owner.Id) != true),
    "Missing optional types install no patch");
var report = new { status = "PASS", version = "0.1.2", legacyError = oldError, checks,
    nativeGetAllCompatibleCalls = knownCalls, shippingSha256 = Sha(Path.Combine(root, "bin/Release/netstandard2.1/SoldStockOptimizer.dll")),
    gameSha256 = Sha(Path.Combine(managed, "Assembly-CSharp.dll")), gameExecuted = false, audioPlayback = false };
Directory.CreateDirectory(Path.Combine(root, "verification/0.1.2"));
File.WriteAllText(Path.Combine(root, "verification/0.1.2/compatibility-results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine(JsonSerializer.Serialize(new { report.status, count = checks.Count, oldError, report.shippingSha256 }));

static CodeInstruction[] Plain() => new[] { new CodeInstruction(OpCodes.Ldc_I4, 37), new CodeInstruction(OpCodes.Ret) };
static string Sha(string file) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(file))).Replace("-", "").ToLowerInvariant(); }
static void Require(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); }
public static class Fixture
{
    [MethodImpl(MethodImplOptions.NoInlining)] public static int GetAllItems() => 37;
}
