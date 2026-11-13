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
游戏对一个 Script 的一次执行，从开始到正常结束、出错或被停止。
_避免_: Execution、任务

**Leaderboard Run**:
由 `leaderboard_run()` 在临时农场上发起的计时 Run，其结果会提交到 Steam 排行榜。
_避免_: Benchmark、排位赛

**Input Lock**:
因 Tool 正在执行而使游戏拒绝人类输入的状态。
_避免_: 冻结、暂停

**Save**:
保存物品、Unlock 和 Script 的游戏存档；同一时间只有一个处于激活状态。
_避免_: 槽位、Profile、世界

**Unlock**:
研究树中用物品购买的条目，决定哪些游戏功能、内置函数和文档可用。
_避免_: Upgrade、研究、科技
