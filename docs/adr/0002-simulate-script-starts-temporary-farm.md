# simulate_script 由 Mod 直接启动临时农场

`simulate_script` 不生成包装 Script 去调用游戏的 `simulate()`，而是由 Mod 用游戏公开的 `MainSim.LeaderboardStartArgs` 与 `ScheduleLeaderboardStart` 直接启动 simulation 类型的临时农场，作为一次模拟 Run 跟踪。包装 Script 会在 Save 里留下一个 code window，还要把 Unlock、物品和 globals 拼成源码；直接启动则不需要。门槛不变：Save 必须已解锁 `simulation`，与 Script 调用 `simulate()` 相同。

## 考虑过的方案

- **生成包装 Script 调用 `simulate()`**：否决。会污染 Save，参数需拼成源码，`simulate()` 只返回耗时，产量仍要另取。

## 后果

- 游戏只在 Script 调用 `simulate()` 时做的事，Mod 必须自己补上：开始前清空 output；按 `game_seconds` 在模拟线程上精确截止（按帧轮询最多会超出 `0.2 × TimeFactor` 游戏秒）；Script 出错时停掉临时农场（游戏只会把它暂停）；结束流程在 Leaderboard 覆盖层未激活时会提前返回，需要兜底恢复真实农场。
- 有 Run 在进行时必须拒绝（或按 `stop_current` 先停掉）：此时启动临时农场会暂停该 Run，并在结束时覆盖其主无人机的返回值。
- 模拟期间真实农场完全暂停，与游戏内 `simulate()` 一致。
