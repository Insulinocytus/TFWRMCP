# MCP 服务运行在游戏进程内

由 Mod 自身在 localhost 上以 Streamable HTTP 提供 MCP，手写所需的最小协议子集；而不是采用"游戏内薄桥接 + 外部 stdio MCP 进程"的方案（Kalteew/the-farmer-was-replaced-mcp 的做法）。没有游戏运行时什么都玩不了，所以第二个进程和一层私有桥接协议毫无收益；不使用官方 C# MCP SDK，因为它依赖较新的 .NET，在游戏的 Unity 6 Mono 运行时下大概率无法加载。

## 考虑过的方案

- **Fork 或直接使用 Kalteew/the-farmer-was-replaced-mcp**：否决。它依赖按键模拟和截屏兜底，仓库里混有作者个人的存档进度，也没有 Input Lock。其调用游戏 API 的方式仍可作为参考。
- **游戏桥接 + 外部 Node/Python stdio MCP 服务**：否决。两个产物、两种语言，还多一层协议。
