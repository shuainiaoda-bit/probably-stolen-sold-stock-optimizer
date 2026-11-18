# 0.1.2 实机共用检查

`Observer.cs` 通过临时合入连锁店程序集的 Harmony 观测入口运行。优化 Mod 使用独立的正式程序集，由游戏原生加载器启用；连锁店的原有逻辑全程运行。观测代码检查原生加载状态、2000 件售卖、物品查询、排列与撤销、智能整理、保存及已售物品结算。

本工程使用同机连锁店项目的 `Core`、`src` 和资源，默认路径为 `C:\game_develop\ProbablyStolenChainStoresMod`。运行脚本还使用该项目的 `tools/run_native_checks.py` 提供文件快照、还原和进程检查，以及 `tools/NativeAudit` 执行游戏原生权限扫描。构建前准备对应连锁店 0.1.3 源码与已安装游戏程序集，并按本机目录调整工程路径。

运行前，玩家需要在游戏原生菜单确认本次组合的权限并退出游戏。测试读取 `modTrustStore` 中的原生签名记录，不创建授权。`tools/run_runtime012.py` 要求事先准备并扫描 `.work/runtime012/stage`：复制原连锁店安装目录，再将本工程构建的 `ChainStores.dll` 放入该临时目录；将扫描结果保存到 `.work/runtime012/audit/verification/native-runtime-audit.json`，并在 `.work/runtime012/preparation.json` 记录 `observerSha256` 和 `auditSha256`。正式优化 DLL 必须保持发布的 0.1.2 哈希。

```powershell
dotnet build Tests/Runtime/Runtime.csproj -c Release
python tools/run_runtime012.py
```

脚本先备份全部用户数据和连锁店目录，再将游戏四项音量归零，并在进程恢复运行前接入系统音频防护。两轮分别将优化 Mod 放在加载顺序末位和首位。每次启动均使用 `-noaudio` 和独立后台桌面，系统音频会话持续静音并检查实际峰值。发现非零输出或失去静音验证时立即停止自有测试进程。其他应用音量保持原值。

测试结束后按哈希恢复存档、原生权限记录、音量和全部本地 Mod 文件，并检查工坊缓存。原始日志、音频端点和权限存储留在本机；公开报告只包含已清理的检查结果和汇总。
