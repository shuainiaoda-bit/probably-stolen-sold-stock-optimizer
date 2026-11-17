# 构建与原生验证

发布 Mod 的构建需要 .NET SDK，以及本机游戏的 `Probably Stolen_Data/Managed` 程序集。通过 `GameDir` 指定游戏目录，也可以直接传入 `GameManaged`。游戏程序集由本机安装提供。

`src/ModEntry.cs` 和 `src/InventoryPerformance.cs` 是 0.1.1 的运行时补丁。GitHub 安装包采用完成本轮真实运行时测试的同一份 DLL。重新构建所得文件的哈希可能随工具链和构建路径变化。

## 原生测试宿主

`Tests/Runner.cs` 保存实际游戏中的售卖对比和行为验证逻辑：每次调用原生 `PlayerStore.SellItem`，分别计时售卖、查询、保存和清理，并验证金额、物品编号、嵌套容器、化学品反馈、存读档和跨日结算。

`Tests/Harness.csproj` 是维护者工作区的诊断宿主，依赖本机已有的连锁店源码工程、游戏程序集和已通过游戏原生授权的测试环境。`ChainRoot` 和 `GameManaged` 可以通过 MSBuild 参数指定。该宿主不属于玩家安装包，独立 Mod 的构建与运行均无连锁店依赖。

原生测试会创建临时新存档。测试前备份真实用户目录和诊断宿主，测试结束后按文件哈希恢复；在隔离桌面以后台静音模式启动真实游戏。完整测试结果已作为 JSON 和报告公开，见 `verification` 与 `docs`。
