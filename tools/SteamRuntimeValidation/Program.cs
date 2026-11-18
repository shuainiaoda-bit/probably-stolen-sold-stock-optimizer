using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Steamworks;

static class Program
{
    const uint App = 4348910;
    const ulong Id = 3841557483;
    const string Root = @"C:\game_develop\ProbablyStolenSoldStockOptimizer";
    const string Title = "已售物品优化：卖出东西不放在雷霆大包";
    const string Shipping = "c1fd2350fd6a2e27002ff8885e57eb912dc0a6711789cfeded681078987158cd";
    const string Payload = "c8b627dbb81699876fd19718d5a8e39c2642f8f11cb1b9f8e7cbb240b173d416";
    static readonly string Release = Path.Combine(Root, "release/0.1.2/runtime-validation");
    static readonly string Work = Path.Combine(Release, "steam");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    record Item(string Title, string Description, ulong Owner, uint AppId, string Visibility, bool Banned,
        int FileSize, string ContentHandle, string Cover, string Metadata, string Tags, ulong[] Children);
    record Plan(string BaselineSha256, string DescriptionSha256, string RuntimeSha256, string GithubReceiptSha256);
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    static void Save(string name, object data) => File.WriteAllText(Path.Combine(Work, name), JsonSerializer.Serialize(data, Json)+"\n");
    static T Load<T>(string name) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(Work, name)))!;
    static async Task<T> Wait<T>(SteamAPICall_t call, string label) where T : struct
    {
        var task = new TaskCompletionSource<T>();
        using var callback = CallResult<T>.Create((value, failed) => { if (failed) task.TrySetException(new IOException(label)); else task.TrySetResult(value); });
        callback.Set(call); var end = DateTime.UtcNow.AddMinutes(3);
        while (!task.Task.IsCompleted) { SteamAPI.RunCallbacks(); Require(DateTime.UtcNow < end, "Timed out: "+label); await Task.Delay(50); }
        return await task.Task;
    }
    static async Task<Item> Read()
    {
        var q = SteamUGC.CreateQueryUGCDetailsRequest([new(Id)], 1);
        Require(SteamUGC.SetReturnLongDescription(q, true) && SteamUGC.SetReturnMetadata(q, true) && SteamUGC.SetReturnChildren(q, true) && SteamUGC.SetAllowCachedResponse(q, 0), "Query options rejected");
        try
        {
            var r = await Wait<SteamUGCQueryCompleted_t>(SteamUGC.SendQueryUGCRequest(q), "Workshop readback");
            Require(r.m_eResult == EResult.k_EResultOK && r.m_unNumResultsReturned == 1, "Workshop query failed");
            Require(SteamUGC.GetQueryUGCResult(q, 0, out var d) && d.m_eResult == EResult.k_EResultOK, "Workshop details unavailable");
            Require(d.m_nConsumerAppID.m_AppId == App && d.m_ulSteamIDOwner == SteamUser.GetSteamID().m_SteamID && d.m_rgchTitle == Title && !d.m_bBanned && d.m_eVisibility == ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic, "Workshop identity or owner differs");
            Require(SteamUGC.GetQueryUGCMetadata(q, 0, out var metadata, 16384), "Workshop metadata unavailable");
            Require(SteamUGC.GetQueryUGCPreviewURL(q, 0, out var cover, 8192), "Workshop preview unavailable");
            var parsed = JsonDocument.Parse(metadata).RootElement;
            Require(parsed.GetProperty("ModId").GetString() == "sold_stock_optimizer" && parsed.GetProperty("Version").GetString() == "0.1.2" && parsed.GetProperty("PayloadHash").GetString() == Payload, "Published payload differs from tested 0.1.2");
            var children = new PublishedFileId_t[d.m_unNumChildren];
            if (children.Length > 0) Require(SteamUGC.GetQueryUGCChildren(q, 0, children, (uint)children.Length), "Dependency query failed");
            return new(d.m_rgchTitle, d.m_rgchDescription, d.m_ulSteamIDOwner, App, d.m_eVisibility.ToString(), d.m_bBanned, d.m_nFileSize, d.m_hFile.ToString(), cover, metadata, d.m_rgchTags, children.Select(x => x.m_PublishedFileId).ToArray());
        }
        finally { SteamUGC.ReleaseQueryUGCRequest(q); }
    }
    static void SamePayload(Item before, Item after)
    {
        Require(before.Title == after.Title && before.Owner == after.Owner && before.AppId == after.AppId && before.Visibility == after.Visibility && before.Banned == after.Banned &&
            before.FileSize == after.FileSize && before.ContentHandle == after.ContentHandle && before.Cover == after.Cover && before.Metadata == after.Metadata && before.Tags == after.Tags && before.Children.SequenceEqual(after.Children), "A surface outside the description changed");
    }
    static void RuntimeGate()
    {
        var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "verification/0.1.2/runtime-validation.json"))).RootElement;
        Require(runtime.GetProperty("status").GetString() == "PASS" && runtime.GetProperty("shippingSha256").GetString() == Shipping && runtime.GetProperty("totalChecks").GetInt32() == 72 && runtime.GetProperty("audio").GetProperty("maximumTargetPeak").GetDouble() == 0, "Runtime validation did not pass");
        var github = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "verification/0.1.2/github-runtime-publication.json"))).RootElement;
        Require(github.GetProperty("status").GetString() == "PASS" && github.GetProperty("downloadedAssetsMatch").GetBoolean() && github.GetProperty("shippingSha256").GetString() == Shipping &&
            github.GetProperty("runtimeEvidenceSha256").GetString() == Hash(Path.Combine(Root, "verification/0.1.2/runtime-validation.json")), "Verify matching GitHub runtime evidence before Steam publication");
    }
    static void DownloadedPayload()
    {
        Require(SteamUGC.GetItemInstallInfo(new(Id), out _, out var folder, 8192, out _) && Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) == Id.ToString(), "Workshop installation unavailable");
        var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "release/0.1.2/steam/plan.json"))).RootElement.GetProperty("Files");
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
        Require(files.Length == 3 && expected.EnumerateObject().Count() == 3, "Workshop runtime file count differs");
        foreach (var entry in expected.EnumerateObject()) Require(Hash(Path.Combine(folder, entry.Name)) == entry.Value.GetString(), "Downloaded file differs: "+entry.Name);
        Require(Hash(Path.Combine(folder, "SoldStockOptimizer.dll")) == Shipping && Hash(Path.Combine(Root, "dist/SoldStockOptimizer-0.1.2.zip")) == "13ad05b53372a0405eb905e75604ca3b897303cd71611e762ad475ac9cadba81", "Shipping package differs");
    }
    static async Task<int> Main(string[] args)
    {
        bool initialized = false;
        try
        {
            Require(args.Length == 1 && new[]{"freeze","publish","verify"}.Contains(args[0]), "Use freeze|publish|verify");
            Directory.CreateDirectory(Work); RuntimeGate();
            Environment.SetEnvironmentVariable("SteamAppId", App.ToString()); Environment.SetEnvironmentVariable("SteamGameId", App.ToString());
            Require(SteamAPI.Init(), "Steam unavailable"); initialized = true;
            Require(SteamUser.BLoggedOn() && SteamUtils.GetAppID().m_AppId == App && SteamApps.BIsSubscribedApp(new(App)), "Steam context differs");
            string descriptionPath = Path.Combine(Release, "description.bbcode.txt"), runtimePath = Path.Combine(Root, "verification/0.1.2/runtime-validation.json"), githubPath = Path.Combine(Root, "verification/0.1.2/github-runtime-publication.json");
            string description = File.ReadAllText(descriptionPath);
            Require(description == description.Trim() && Encoding.UTF8.GetByteCount(description) < 7900, "Description format or size differs");
            var current = await Read();
            if (args[0] == "freeze")
            {
                Require(!File.Exists(Path.Combine(Work, "plan.json")), "A frozen plan already exists"); DownloadedPayload(); Save("before.json", current);
                Save("plan.json", new Plan(Hash(Path.Combine(Work, "before.json")), Hash(descriptionPath), Hash(runtimePath), Hash(githubPath)));
                Console.WriteLine("FROZEN original Workshop item and validated runtime evidence"); return 0;
            }
            var plan = Load<Plan>("plan.json");
            Require(plan.BaselineSha256 == Hash(Path.Combine(Work, "before.json")) && plan.DescriptionSha256 == Hash(descriptionPath) && plan.RuntimeSha256 == Hash(runtimePath) && plan.GithubReceiptSha256 == Hash(githubPath), "Frozen publication inputs changed");
            var baseline = Load<Item>("before.json"); SamePayload(baseline, current);
            uint subscribed = SteamUGC.GetItemState(new(Id)) & (uint)EItemState.k_EItemStateSubscribed;
            if (args[0] == "publish" && current.Description != description)
            {
                Require(current.Description == baseline.Description, "The public description changed since freeze");
                var update = SteamUGC.StartItemUpdate(new(App), new(Id)); Require(SteamUGC.SetItemDescription(update, description), "Description rejected");
                var response = await Wait<SubmitItemUpdateResult_t>(SteamUGC.SubmitItemUpdate(update, "0.1.2 实机共用验证通过：11 个 Mod，两轮各售出 2000 件商品，72 项检查通过，全程系统音频输出峰值为 0。安装包保持一致。"), "Workshop description update");
                Save("submit.json", new { Result=response.m_eResult.ToString(), NeedsAgreement=response.m_bUserNeedsToAcceptWorkshopLegalAgreement, AtUtc=DateTime.UtcNow });
                Require(response.m_eResult == EResult.k_EResultOK && !response.m_bUserNeedsToAcceptWorkshopLegalAgreement, "Workshop submission incomplete");
            }
            Item after = current;
            for (int i=0; i<8; i++) { after=await Read(); SamePayload(baseline, after); if(after.Description==description) break; await Task.Delay(1500); }
            Require(after.Description == description, "Workshop description readback differs"); DownloadedPayload();
            Require((SteamUGC.GetItemState(new(Id)) & (uint)EItemState.k_EItemStateSubscribed) == subscribed, "Subscription changed");
            var receipt = new { status="PASS", version="0.1.2", workshopId=Id.ToString(), url="https://steamcommunity.com/sharedfiles/filedetails/?id="+Id,
                githubVerifiedBeforeSteamUpdate=true, descriptionReadbackMatches=true, contentAndMetadataUnchanged=true, downloadedFilesMatch=true, subscriptionUnchanged=true,
                shippingSha256=Shipping, runtimeEvidenceSha256=Hash(runtimePath), descriptionSha256=Hash(descriptionPath), verifiedAtUtc=DateTime.UtcNow };
            Save("verification.json", receipt); File.WriteAllText(Path.Combine(Root,"verification/0.1.2/steam-runtime-publication.json"), JsonSerializer.Serialize(receipt,Json)+"\n");
            Console.WriteLine("VERIFIED original Workshop item, description and matching three-file payload"); return 0;
        }
        catch(Exception error) { if(Directory.Exists(Work)) Save("last-error.json",new{error=error.ToString(),atUtc=DateTime.UtcNow}); Console.Error.WriteLine(error.Message); return 2; }
        finally { if(initialized) SteamAPI.Shutdown(); }
    }
}
