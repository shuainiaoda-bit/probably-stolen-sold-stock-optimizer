# 已售物品优化 0.1.0

减少连续大量售卖时隐藏已售仓库的重复 UI 刷新，优化编号查询和清理列表读取。保留原版收入、商品数据、嵌套内容和顾客反馈结算。

- 1000 件独立商品：累计交易处理耗时 2.416 秒 → 0.216 秒，约快 11.2 倍。
- 2000 件独立商品：12.501 秒 → 0.497 秒，约快 25.1 倍。
- 真实游戏每组测试三轮取中位数，79 项运行时检查通过。
- 支持游戏 1.0.0.15 测试版，无前置 Mod。

下载 `SoldStockOptimizer-0.1.0.zip`，将其中的 `SoldStockOptimizer` 文件夹复制到游戏的 `Mods` 文件夹，在主菜单启用并重启。`SHA256SUMS.txt` 可核验附件。

[Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3841557483) · [完整测试报告](https://github.com/shuainiaoda-bit/probably-stolen-sold-stock-optimizer/blob/main/docs/测试报告.md)

计时衡量原生交易处理速度。商品生成和鼠标操作不计入；结算销毁耗时本次未见稳定改善。
