# 已售物品优化 0.1.2

修复 `GetAllItems traversal changed; expected 10 calls, found 0.` 引起的 Mod 加载失败。

- 按实际参数和返回类型匹配查询调用，适应调用数量变化。
- 对零匹配、可选类型或方法缺失保留现有实现，其他可用优化继续安装。
- 可选补丁安装失败时只回滚自身对应方法。
- 保留已售仓库刷新、物品索引和结算清理优化。

首次发布完成 24 项离线兼容检查与原生权限扫描，准确复现旧异常并验证零匹配行为。后续补充实机加载与共用验证，结果如下。完整性能比较保留在 0.1.1 历史报告中。

下载 `SoldStockOptimizer-0.1.2.zip`，关闭游戏，将其中 `SoldStockOptimizer` 文件夹替换到游戏 `Mods` 目录，启用后重启。相同 Mod 选择一个来源启用即可。已安装游戏验证基线为 1.0.0.15 测试版，无前置 Mod。

[Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3841557483) · [源码](https://github.com/shuainiaoda-bit/probably-stolen-sold-stock-optimizer)

## 实机加载与共用验证

0.1.2 与另外 10 个 Mod 完成两轮冷启动验证，分别将优化 Mod 放在加载顺序首位和末位。每轮售出 2000 件商品，36 项检查全部通过，加载与运行错误为 0。覆盖全店查询、原版排列与撤销、智能整理的实际菜单动作、保存和已售物品结算。

全程将游戏音量归零，使用 `-noaudio`，并验证系统音频会话静音，输出峰值为 0。原存档、原生授权记录、音量和 Mod 文件均已恢复。安装包与原 0.1.2 保持一致。

附加文件 `RuntimeValidation-0.1.2.md`、`RuntimeValidation-0.1.2.json` 和 `RuntimeValidation-SHA256SUMS.txt` 提供报告、检查记录和校验值。
