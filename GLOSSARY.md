# TFWRMCP

一个 The Farmer Was Replaced 的 Mod，通过 MCP 把游戏暴露给 AI Agent，让 Agent 像人类玩家一样玩：编写并运行 Script。

## 术语

**Agent**:
通过 Tool 玩游戏的 AI。
_避免_: Bot、AI 玩家

**Mod**:
本项目构建的、加载进游戏进程的 BepInEx 扩展。
_避免_: Plugin、插件

**Tool**:
Agent 通过 MCP 调用的一项能力。
_避免_: Endpoint、命令、API

**Script**:
用游戏内置类 Python 语言写的代码文件；在游戏中显示为一个 code window。
_避免_: Program、代码、宏

**Run**:
游戏对一个 Script 的一次执行，从开始到正常结束、出错或被停止。一次 Run 可以涉及多个 Script。
_避免_: Execution、任务

**参与 Run 的 Script**:
发起 Run 的 Script，以及本次 Run 中执行过其代码（如被 import）的 Script；它们的 code window 显示为运行中，直到 Run 结束。
_避免_: 运行中的文件、活动窗口

**Leaderboard Run**:
由 `leaderboard_run()` 在临时农场上发起的计时 Run，其结果会提交到 Steam 排行榜。
_避免_: Benchmark、排位赛

**Input Lock**:
因 Tool 正在执行而使游戏拒绝人类输入的状态。
_避免_: 冻结、暂停

**Save**:
保存物品、Unlock 和 Script 的游戏存档；同一时间只有一个处于激活状态。
_避免_: 槽位、Profile、世界

**入门指南**:
游戏在加载一个从未保存过的 Save 时自动打开的 Getting Started 文档窗口。
_避免_: 教程、新手引导

**Unlock**:
研究树中用物品购买的条目，决定哪些游戏功能、内置函数和文档可用。
_避免_: Upgrade、研究、科技

**Unlock 等级**:
一个 Unlock 已购买的次数；0 表示未解锁，多数 Unlock 最高 1 级，部分可多次购买。
_避免_: Upgrade 等级、科技等级

**购买 Unlock**:
花费物品使某个 Unlock 的 Unlock 等级 +1，包括首次解锁（0→1）和再次购买（N→N+1）。
_避免_: 升级、Upgrade、研究

**资源栏**:
游戏界面上显示当前持有物品数量的面板。
_避免_: 物品面板、Inventory、资源 UI

**dock 组**:
通过 dock 连在一起的一串 code window，以最上面的窗口为首；自动整理时作为一个整体移动。含文档窗口的 dock 组不参与自动整理。
_避免_: 窗口组、停靠组、窗口栈
