# 已售物品优化：卖出东西不放在雷霆大包

《深空当铺》的独立性能 Mod，版本 **0.1.3**，适用于默认公开版 **Build26525552** 和 open_beta **Build26772488**。作者：是大帅鸟。

[Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3841557483) · [下载 0.1.3](https://github.com/shuainiaoda-bit/probably-stolen-sold-stock-optimizer/releases/tag/v0.1.3) · [0.1.1 完整测试报告](docs/性能检查与测试-0.1.1.md)

同一天连续出售大量商品时，隐藏已售仓库会反复刷新已有商品的 UI。本 Mod 跳过这些不可见的重复刷新，为已售物品建立编号索引，并减少清理时的列表复制。

售卖收入、商品数据、嵌套内容和顾客反馈继续按原版流程处理。需要后续结算的商品仍保留到原版结算完成。

## 0.1.3 兼容更新

改用官方 ModReflect，移除额外反射权限；同时匹配两个分支的仓库遍历调用。两份原版 SDK 的编译、扫描、接口绑定及实际补丁转换通过，beta 完成静音实机交易和库存检查。公开版的独立原生启用未做实机验证。

English: Uses official ModReflect without extra reflection permissions, supporting both traversal implementations. Compilation, native scanning, API binding and actual patch transformations passed for both branch SDKs. Silent native trading and inventory checks passed on beta. Independent native activation on public was not runtime-tested.

## 0.1.2 加载兼容修复

修复 `GetAllItems traversal changed; expected 10 calls, found 0.` 导致整个 Mod 加载失败的问题。按实际签名匹配查询调用，遇到不同代码结构或缺失可选方法时保留对应的现有实现，让其他可用优化继续安装。

通过 24 项离线兼容检查及原生权限扫描，并补充两轮全静音实机验证：与另外 10 个 Mod 共用，每轮售出 2000 件商品，36 项检查全部通过，运行错误为 0。系统音频会话均确认静音，输出峰值为 0。

[加载修复说明](docs/兼容性修复与验证-0.1.2.md) · [实机共用报告](docs/实机加载与共用验证-0.1.2.md) · [实机检查记录](verification/0.1.2/runtime-validation.json)

## 0.1.1 历史优化与性能数据

- 全店、自有及存档物品查询：减少遍历中的临时子列表，保留顺序和独立返回结果。
- 原版排列、撤销和批量交易：同步操作结束时统一刷新仓库 UI，保留原版算法和增删事件。
- 600 件排列：1514.68 → 460.48 毫秒；撤销：1576.53 → 432.36 毫秒。
- 2000 件全店查询连续 200 次：97.57 → 30.19 毫秒。

以上为真实游戏三轮中位数。库存压力测试扩大了临时仓库并使用简化背景，衡量方法处理耗时，不能直接换算为画面帧率。

## 0.1.1 历史大量售卖实测

真实游戏后台静音测试，相同电脑和 Mod 组合，每组交替测试三轮，以下为中位数。

| 独立商品数量 | 关闭优化 | 启用优化 | 处理速度 |
| --- | ---: | ---: | ---: |
| 1000 件 | 4.348 秒 | 0.305 秒 | 14.3 倍 |
| 2000 件 | 18.660 秒 | 0.678 秒 | 27.5 倍 |

计时为原生交易函数的累计耗时，不含商品生成、鼠标操作和帧间等待。收益主要集中在售卖和已售物品查询；结算销毁耗时本次未见稳定改善。

0.1.1 库存扩展场景通过 129 项检查，售卖场景通过 79 项检查。覆盖查询顺序、同帧移入移出、原生排列与撤销、固定物品、增删事件顺序、异常恢复、批量买卖、化学品反馈、存读档和次日结算。[完整数据](verification/0.1.1/summary.json)

## 安装

可以通过 Steam 创意工坊订阅，也可以在 [Release](https://github.com/shuainiaoda-bit/probably-stolen-sold-stock-optimizer/releases/tag/v0.1.3) 下载 `SoldStockOptimizer-0.1.3.zip`。解压后将 `SoldStockOptimizer` 文件夹放入游戏目录的 `Mods` 文件夹，在主菜单的 Mod 管理界面启用，按游戏原生提示启用后重启。

无前置 Mod。相同 Mod 选择一个来源启用即可。停用并重启游戏后恢复原版处理流程。

## 源码与构建

主要实现位于 [src/ModEntry.cs](src/ModEntry.cs)。使用 .NET SDK 和本机已安装的游戏程序集构建：

```powershell
dotnet build .\SoldStockOptimizer.csproj -c Release -p:GameDir="C:\Apps\steam\steamapps\common\Probably Stolen"
```

输出为 `bin/Release/netstandard2.1/SoldStockOptimizer.dll`。[开发与原生验证说明](docs/BUILDING.md)

## English

Sold Stock Optimizer reduces the growing transaction cost of selling many items in one day. It skips repeated UI refreshes of the hidden sold inventory, indexes sold-item lookups, and reduces list copying during native cleanup. Items needed for customer feedback remain available until native settlement completes.

Tested on Probably Stolen 1.0.0.15 open beta. Download the ZIP from Releases, copy its `SoldStockOptimizer` folder into the game's `Mods` folder, enable the mod in the main menu, and restart. No prerequisite mods are required.
