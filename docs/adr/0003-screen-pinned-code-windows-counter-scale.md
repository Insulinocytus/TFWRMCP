# 自动整理时 code window 留在工作区容器里反向缩放，而非移到屏幕层

自动整理开启时 code window 要像 2D UI 一样固定在屏幕上、不随农场平移缩放。Mod 不把窗口挪出游戏的 `workspaceContainer`，而是每帧（`LateUpdate`）给每个 dock 组的首个窗口设 `localScale = 原始缩放 / zoom`，并按屏幕坐标摆放。游戏的新建、解除 dock、上下层顺序、Alt 穿透、隐藏时机和 Save 格式都假定窗口挂在该容器下，留在原处就都不用改。

## 考虑过的方案

- **把窗口重设父节点到不缩放的屏幕层**（仿 `TitleWorkspace`）：否决。Save 以容器坐标减 `farmPosition` 记录窗口位置，移出后存进去的是无意义坐标，不装 Mod 打开会错位；游戏新建和解除 dock 总会把窗口放回容器，需要每帧搬回；与留在容器里的文档窗口之间的层级判断、Alt 穿透都会失效。

## 后果

- `ContainerScaler.UpdateSize` 不考虑子节点的 `localScale`，拖动平移范围会算错，并随窗口漂移；开启相机自动居中时影响可以忽略。
- 游戏的高亮框（`BlinkManager`）按 `scaleFactor × zoom` 计算窗口尺寸，对反向缩放后的窗口不准；窗口都折叠后影响很小。
- 游戏的 `MoveCameraTo`（报错跳转、搜索跳转）对已固定在屏幕上的 code window 没有意义，自动整理期间由 Mod 跳过。
