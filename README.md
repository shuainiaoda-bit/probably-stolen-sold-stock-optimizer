# 已售物品优化：卖出东西不放在雷霆大包

《深空当铺》的独立性能 Mod，版本 **0.1.0**，适用于 **1.0.0.15 测试版**。作者：是大帅鸟。

[Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3841557483) · [下载 0.1.0](https://github.com/shuainiaoda-bit/probably-stolen-sold-stock-optimizer/releases/tag/v0.1.0) · [完整测试报告](docs/测试报告.md)

同一天连续出售大量商品时，隐藏已售仓库会反复刷新已有商品的 UI。本 Mod 跳过这些不可见的重复刷新，为已售物品建立编号索引，并减少清理时的列表复制。

售卖收入、商品数据、嵌套内容和顾客反馈继续按原版流程处理。需要后续结算的商品仍保留到原版结算完成。

## 实测速度

真实游戏后台静音测试，相同电脑和 Mod 组合，每组交替测试三轮，以下为中位数。

| 独立商品数量 | 关闭优化 | 启用优化 | 处理速度 |
| --- | ---: | ---: | ---: |
| 1000 件 | 2.416 秒 | 0.216 秒 | 11.2 倍 |
| 2000 件 | 12.501 秒 | 0.497 秒 | 25.1 倍 |

计时为原生交易函数的累计耗时，不含商品生成、鼠标操作和帧间等待。收益主要集中在售卖和已售物品查询；结算销毁耗时本次未见稳定改善。

79 项运行时检查通过，覆盖金额、数量、物品编号、嵌套容器、实际化学品反馈、存读档、停用恢复和次日结算。[逐轮计时与检查记录](verification/benchmark-results.json)

## 安装

可以通过 Steam 创意工坊订阅，也可以在 [Release](https://github.com/shuainiaoda-bit/probably-stolen-sold-stock-optimizer/releases/tag/v0.1.0) 下载 `SoldStockOptimizer-0.1.0.zip`。解压后将 `SoldStockOptimizer` 文件夹放入游戏目录的 `Mods` 文件夹，在主菜单的 Mod 管理界面启用，并按游戏提示确认 `reflection-external` 权限后重启。

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
