# 已售物品优化 0.1.2

修复 `GetAllItems traversal changed; expected 10 calls, found 0.` 引起的 Mod 加载失败。

- 按实际参数和返回类型匹配查询调用，适应调用数量变化。
- 对零匹配、可选类型或方法缺失保留现有实现，其他可用优化继续安装。
- 可选补丁安装失败时只回滚自身对应方法。
- 保留已售仓库刷新、物品索引和结算清理优化。

通过 24 项离线兼容检查，准确复现旧异常并验证修复后的零匹配行为。本轮未启动游戏；完整实机性能记录见 0.1.1 历史报告。原生权限扫描与发布包哈希另附。

下载 `SoldStockOptimizer-0.1.2.zip`，关闭游戏，将其中 `SoldStockOptimizer` 文件夹替换到游戏 `Mods` 目录，启用后重启。相同 Mod 选择一个来源启用即可。已安装游戏验证基线为 1.0.0.15 测试版，无前置 Mod。

[Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3841557483) · [源码](https://github.com/shuainiaoda-bit/probably-stolen-sold-stock-optimizer)
