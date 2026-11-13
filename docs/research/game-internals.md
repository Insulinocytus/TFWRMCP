# 游戏内部机制:针对本地构建 26462009 的验证

来源:本地 ilspycmd 反编译结果 `C:/Users/NagaseYami/AppData/Local/Temp/tfwr-decomp/{Core,Utils,Assembly-CSharp,NewAssembly}` 以及本地游戏文件。未运行任何内容;未修改游戏安装目录或存档数据中的任何内容。

- Steam `appmanifest_2060160.acf`:`buildid` = `26462009`。Unity `6000.0.43f1`(`UnityPlayer.dll` ProductVersion `6000.0.43f1 (97272b72f107)`,`Player.log`)。Mono 后端(`MonoBleedingEdge/`)。
- 证据路径均相对于反编译根目录。**部分类型位于 `Utils/` 而非 `Core/`**:`ExecuteException`、`ItemBlock`、`Localizer`、`OptionHolder`、`KeyCombination`、`StringIds`、`Helper`、`LeaderboardEntryData`、`CodeInputField`。
- 各结论相对于 spec #1 / 任务书所假设的内容而言。`[INFERENCE]` = 在反编译代码中无法直接看到。
- 本机事实:`options.txt` 中 `language = ZH`(游戏语言为中文)、`file watcher = enabled`、`autosave = disabled`、`autosave progress = enabled`。

---

## 1. Run 的启动/停止

### 1.1 `CodeWindow.PressExecuteOrStop`:VERIFIED(切换)
`Core/CodeWindow.cs`:`public void PressExecuteOrStop()`
```
workspace.activeWindow = GetComponent<Window>();
if (!isExecuting || !MainSim.Inst.IsExecuting()) StartExecution(stepByStep: false);
else if (MainSim.Inst.StepByStepMode) MainSim.Inst.StepByStepMode = false;
else MainSim.Inst.StopMainExecution();
```
- `isExecuting` 是**每个窗口各自的**(`public bool isExecuting`)。当窗口 A 正在运行时在窗口 B 上按运行:B 的 `isExecuting` 为 false,因此调用 `StartExecution`,它会停止 A 并启动 B。在 A 运行时在 A 上按则停止它。在单步模式下在 A 上按则是继续运行而非停止。
- 热键路径(`Core/InputBoss.cs`,"start execution"):`component2.Parse()` 然后 `MainSim.Inst.StartMainExecution(component2, node)`(没有预先停止;若 `IsExecuting()` 则为空操作)。"stop execution":`if (MainSim.Inst.IsExecuting()) MainSim.Inst.StopMainExecution()`。

### 1.2 `CodeWindow.StartExecution`:VERIFIED,但为 private
`Core/CodeWindow.cs`:`private void StartExecution(bool stepByStep)`
```
MainSim.Inst.StopMainExecution();
Node node = Parse();
if (node != null) MainSim.Inst.StartMainExecution(this, node, stepByStep);
```
它总是先停止任何当前的 Run,因此想要"若正在运行则拒绝"的 Tool 必须自行检查 `MainSim.Inst.IsExecuting()`。等价的公开调用序列:`Parse()` + `StartMainExecution(cw, node)`。

### 1.3 `CodeWindow.Parse`:VERIFIED;错误不会抛出异常
`Core/CodeWindow.cs`:`public Node Parse()`
- 获取 `parseLock`,调用 `UpdateTokens()`(会访问 TMP `codeInput.text`;仅限主线程),按 `dirty` 缓存。
- 重新解析时设置 `MainSim.Inst.dirty = true`。
- `Parser.Parse(tokens, this)` + `syntaxTree.CheckForNonsensicalCode()` 位于 `try` 内;`catch (ParseException ex)` 存储 `errorString = ex.Message; errorStartIndex = ex.startIndex; errorEndIndex = ex.endIndex; parseException = ex; cachedProgram = null; return null;`
- 表现:**返回 `null`,没有异常传到调用方。** 错误数据位于 private volatile 字段 `errorString`、`errorStartIndex`、`errorEndIndex`、`parseException`(通过反射),或通过对 `Parse` 的 Harmony postfix 获取。窗口本身也会显示该错误。
- `Core/ParseException.cs`:`public ParseException(string message, int startIndex, int endIndex)`;公开字段 `startIndex`、`endIndex`。**位置 = 指向 `codeInput.text` 的字符偏移量,而非行/列。** 行号 = `startIndex` 之前 `\n` 的个数。
- **消息语言/格式(相对 spec 的"英文"为 CHANGED):** 17 处 throw 使用原始键(例如 `Parser.Parse`:`throw new ParseException("error_code_after_block", ...)`),12 处以上使用 `CodeUtilities.LocalizeAndFormat(...)`(已按当前游戏语言本地化,此处为 ZH)。UI 会执行 `Localizer.Localize(error)`(`Core/ErrorMessage.cs:91`)。因此 `ex.Message` 要么是键,要么是已本地化的文本;使用前应用 `Localizer.Localize(msg)`,并预期为中文,除非强制切换(见 7.4)。
- 缓存路径:若文本未变且上一次解析失败,`Parse()` 会从 `parseException` 重新填充 `errorString`,并返回 `cachedProgram?.syntaxTree`(null)。

### 1.4 `MainSim.StartMainExecution` / `StopMainExecution`:VERIFIED
`Core/MainSim.cs`:
- `public void StartMainExecution(CodeWindow cw, Node syntaxTree, bool stepByStep = false)`:在 `lock (lockSimulation)` 下;`if (IsExecuting()) return;`(静默空操作);停止所有窗口的执行模式;`activeCodeWindow = cw`;`cw.StartExecutionMode(closeErrors: false)`;`new Execution(sim, syntaxTree, executionId)`;**`Logger.Clear()`**(每次启动时重置输出,同时截断 `output.txt`);`sim.StartProgramExecution(execution)`。锁之后:`sim.UnlockAchievement("RUN_YOUR_FIRST_CODE")`。
- `public void StopMainExecution()`:在锁下;`if (sim.leaderboardType != none && != simulation) leaderboardCancelled = true;` `storedSim?.StopProgramExecution(); sim.StopProgramExecution();` 若之前在运行则播放停止音效。
- 会访问 `CodeWindow`/Unity 对象,因此须在主线程调用。

### 1.5 检测 Run 是否在进行:CHANGED 细微差别
- `Core/MainSim.cs`:`public bool IsExecuting()` => `lock { sim.IsExecuting() || storedSim != null }`。`storedSim != null` 表示有 Leaderboard/`simulate()` 子模拟处于活动状态(在排行榜结果界面上也保持非 null,直到 `OkPressed`/`RestoreMainSim`)。
- `Core/Simulation.cs`:`public bool IsExecuting() => Execution != null`。
- 哪个窗口:`CodeWindow.isExecuting` 每个窗口各自一个;`MainSim.activeCodeWindow` 是 private。
- `public bool IsPaused()` => `sim.Paused`。`public bool StepByStepMode { get; set; }`。

### 1.6 检测 Run 的结束及原因:CHANGED(无事件,错误不会结束 Run)
没有结束事件或结果对象。事实如下:
- **正常结束:** 所有无人机 `ProgramState` 终止 -> `Execution.ApplySideEffect` `case SideEffect.Terminated:` -> `StopExecution()` -> `Simulation.StopProgramExecution()`(`Core/Simulation.cs`:`public void StopProgramExecution()`;设置 `execution = null`,恢复速度,`farm.RemoveSpawnedDrones()`,`simRunner.Logger.Close()`)。在**模拟线程**上、`lock(lockSimulation)` 下运行。
- **被停止:** `MainSim.StopMainExecution()`(按钮、热键、通过 `CodeWindow.UpdateTokens` -> `StopMainExecution` 编辑正在运行的窗口的代码,或 `StartExecution` 的预先停止)。最终进入同一个 `Simulation.StopProgramExecution()`。
- **运行时错误:** `Execution.ApplySideEffect` `case SideEffect.Error:` 计算位置,调用 `codeWindow.SetErrorMessage(msg, start, end)`、`Logger.LogError(...)`,然后 `sim.Error()`(`Core/Simulation.cs`:`public void Error() { hasError = true; Paused = true; }`)。**Run 并不会被停止**:`Simulation.RunNextStep` 在 `Paused` 时提前返回,因此 `if (IsExecuting() && hasError) StopProgramExecution()` 分支只有在单步/恢复之后才会运行。结果:出错后 `MainSim.IsExecuting()` 保持 `true`,`IsPaused()` 保持 `true`,无人机冻结,直到调用 `StopMainExecution()`。Mod 必须自行检测错误并决定是否停止。
- 区分方式:必须使用 Harmony 钩子。建议集合:prefix/postfix `Simulation.StopProgramExecution()`(任何结束;prefix 中 `hasError` 仍为 true);`Simulation.Error()` 或 `CodeWindow.SetErrorMessage(string message, int startIndex, int endIndex)`(运行时错误 + 位置 + `__instance.fileName`;后者仅从 `Execution.cs:743` 调用);Mod 在自己调用 `StopMainExecution()` 前后设置的标志 = `stopped_by_tool`;否则停止时无错误且执行未终止 = `stopped_by_user`;终止且无错误 = `finished`。
- 轮询替代方案:`IsExecuting()` 在为 true 之后变为 false = 已结束;错误 = Harmony 标志。

### 1.7 线程:VERIFIED
- `MainSim.Start()` 启动一个后台 `Thread`(`AboveNormal`),运行 `SimulationLoop` -> `sim.RunNextStep(goalTime, lockSimulation, ...)`,永不退出。`private object lockSimulation`。
- `MainSim.Update()`(主线程)在整个帧体内持有 `lockSimulation`,并以 `Monitor.Pulse(lockSimulation)` 结束。`Execution.Execute` 在应用副作用时获取 `lockSimulation`;纯解释器单步仅在 `lockExecution` 下运行。
- 因此:`print`/`quick_print` -> `Logger.Log`、`Simulation.Error/StopProgramExecution`、`Execution.ApplySideEffect` 全部运行在模拟线程上。该处的钩子回调禁止访问 Unity API。
- 必须在 Unity 主线程上:所有 `CodeWindow` 成员(TMP `CodeInput.text`、`Parse`、`Load`、`ApplyExternalCode`)、`Workspace.*`、`Window.Close`、`LeaderboardManager.*`(SetActive)、`Saver.Save/SaveCode/SaveProgress` 的调用(同步捕获 TMP 文本;IO 随后在 `Task.Run` 上运行)、`StartMainExecution`(访问 `CodeWindow`)、所有 UI/EventSystem 工作。
- 会锁定 `lockSimulation` 的 `MainSim` 访问器(`IsExecuting`、`GetInventory`、`GetUnlocks`、`NumUnlocked`、`IsUnlocked`、`GetWorldSize`、`GetCurrentTime`、`UnlockOrUpgrade`)是线程安全的,但在模拟线程或 `Update` 持有该锁时会阻塞。读取 `sim.farm.grid.cells`/`drones` 需要对 private `sim` 使用反射,并且最好对 private `lockSimulation` 使用 `Monitor.Enter`。

---

## 2. 输出与运行时错误

### 2.1 `Logger`:VERIFIED,附带 CHANGED 细节
`Core/Logger.cs`:
- `public const int NUM_OUTPUT_LIMIT = 100;` `public const int MESSAGE_LENGTH_LIMIT = 1000;`
- `public delegate void PrintAction();` `public event PrintAction OnOutputChanged;` **无参数**:它不携带条目;必须读取 `GetOutputString()`(`string.Join("\n", output)`)或在别处挂钩。
- 上限行为(CHANGED):`output.AddLast(msg); if (output.Count >= 100) output.RemoveFirst();` 因此**最多保留 99 条**(与常量相差 1)。
- 消息上限:若 `logMessage.Length > 1000`:先 `RemoveSyntaxTags`,再 `Substring(0, 1000)`。在写入文件之后应用。
- 每条消息还会追加到 `<Application.persistentDataPath>/output.txt`(`Path.Combine(persistentDataPath, "output.txt")`,已去除标签,**未截断**,包含分隔符)。`Clear()` 会截断该文件;`StartMainExecution` 会调用它。
- `Logger` 在 `MainSim.Awake` 中创建(`logger = new Logger();`,`public Logger Logger => logger;`)。`UISim`(文档代码示例)自行创建 `new Logger(writeToOutputFile: false)`;针对 `Logger` 的全局 Harmony 补丁必须按 `__instance == MainSim.Inst.Logger` 过滤。
- 唯一的订阅者:`Core/OutputText.cs`(输出窗口)。
- 条目形态:`Log(string)` 原样;`LogError(string errorMessage, ProgramState state)` => `"Error: " + msg + "\nIn: " + state.GetTrace()` + 68 个短横线的分隔符;`LogWarning(string, ProgramState)` => `"Warning: ..."` + 分隔符;当选项 `print warnings == disabled` 时被抑制,且每个节点仅一次。`Error:`/`Warning:` 前缀是硬编码的英文;消息部分是已本地化的。
- `ProgramState.GetTrace()` = 以 ` <- ` 连接的调用栈函数名(最多 5 个),换行,然后是**带有 `^` 指示行的源代码行文本**。没有行号。

### 2.2 `print` / `quick_print` 路径:VERIFIED
- `Core/BuiltinFunctions.cs`:`private static double Print(List<IPyObject> parameters, Simulation sim, Execution exec, int droneId)` 以 `' '` 连接 `CodeUtilities.ToNiceString(p)`,设置 `programState.currentSideEffect = SideEffect.Print; currentSideEffectArgument = new PyString(s)`。
- `Core/Execution.cs` `case SideEffect.Print:` `sim.farm.drones[droneId].PrintToAir(str); sim.simRunner.Logger.Log(str);` 花费 `floor(1/OpDuration.Seconds)` 个 op。
- `private static double QuickPrint(...)` 直接调用 `sim.simRunner.Logger.Log(logMessage)`(免费;无副作用,不在 `lockSimulation` 下)。
- 两者最终都进入 `Logger.Log(string)` -> private `WriteLog(string logMessage, bool addSeparator)`。

### 2.3 捕获每条条目的最佳钩子点:建议
对 private `Logger.WriteLog(string logMessage, bool addSeparator)` 使用 Harmony **Prefix**,可看到 print、quick_print、错误、警告的完整未截断文本(若 `addSeparator` 则自行追加分隔符)。将 `__instance` 过滤为 `MainSim.Inst.Logger`。替代方案:对 `Log`、`LogError(string, ProgramState)`、`LogWarning(string, ProgramState)` 使用 prefix,以便无需解析前缀即可对条目分类(错误检测需要)。线程:模拟线程;使用无锁/带锁队列。`output.txt` 文件是后备来源,而不是钩子。

### 2.4 运行时错误:VERIFIED 位置
- `Utils/ExecuteException.cs`:`public class ExecuteException : Exception { public int startIndex; public int endIndex; public ExecuteException(string message, int startIndex = -1, int endIndex = -1) }`。`-1` 表示"使用正在执行的节点的范围"。
- `Core/Execution.cs` `case SideEffect.Error:`
  ```
  ExecuteException e = States[droneId].currentExecuteException;
  Node n = States[droneId].CurrentExecutingNode;
  int startIndex = e.startIndex >= 0 ? e.startIndex : n.boxedParams.wordStart;
  int endIndex   = e.endIndex   >= 0 ? e.endIndex   : n.boxedParams.wordEnd;
  n.boxedParams.codeWindow?.SetErrorMessage(e.Message, startIndex, endIndex);
  sim.simRunner.Logger.LogError(Localizer.Localize(e.Message), States[droneId]);
  sim.Error();
  ```
- `Core/BoxedNodeParams.cs`:`wordStart`、`wordEnd`、`codeWindow`(即文件)。位置是**字符偏移量**,而非行。消息已本地化(游戏语言,此处为 ZH),并且在 `Localizer.Localize` 之前可能是键。
- `ProgramState` 具有公开的 `ExecuteException currentExecuteException` 和 `CurrentExecutingNode`。

---

## 3. Script

### 3.1 `Workspace.codeWindows`:VERIFIED
`Core/Workspace.cs`:`public volatile ConcurrentDictionary<string, CodeWindow> codeWindows = new ConcurrentDictionary<string, CodeWindow>();` 键 = 窗口/文件名(默认比较器:**区分大小写**)。`MainSim.workspace` 是公开的 `Workspace`。在 `RegisterWindow` 中注册,在 `RemoveWindow` 中移除,在 `RenameWindow` 中重新设键。
- 所有打开的窗口(代码和文档)位于 protected `WorkspaceBase.openWindows`;公开的 `OpenWindows`、`IsWindowOpen(string windowName, bool ignoreCase = false)`、`GetOpenWindow(string)`。

### 3.2 `OpenCodeWindow`:VERIFIED
`Core/Workspace.cs`:`public void OpenCodeWindow(string fileName, string code, Vector2 offset, Vector2 size = default(Vector2))`
```
if (!IsWindowOpen(fileName, ignoreCase: true)) { OpenWindow(codeWinPrefab, ...); codeWindow.workspace = this; codeWindow.Load(code or ""); codeWindow.Parse(); }
```
- **若存在任何同名窗口(代码或文档,不区分大小写),则静默空操作**;返回 void;通过 `codeWindows.TryGetValue` 验证。
- `offset` = 工作区容器中的锚点位置(游戏自己的 `AddNewWindow` 使用 `-workspaceContainer.anchoredPosition + spawnWindowOffset`,后者为 private)。`Workspace.AddNewWindow()` 通过 `GenerateFileName("f")` 创建空的 `f0`、`f1`……。

### 3.3 `CodeWindow.Load` 与读取文本:VERIFIED,附带一个陷阱
- `Core/CodeWindow.cs`:`public void Load(string code, bool readOnly = false)` 在 `!readOnly` 时每次调用都会添加 8 个 UI 监听器(`onTextInserted`、`onTextMeshUpdated` ……),然后设置 `codeInput.text = code`。**不要对已存在的窗口调用 `Load`**(监听器会重复)。使用 `public void ApplyExternalCode(string code)` 覆盖已存在的窗口:`if (codeInput.text != code) { codeInput.text = code; Parse(); CloseError()/ShowError(); }`。
- 读取文本:`codeWindow.CodeInput.text`(`public CodeInputField CodeInput`)。游戏自身在 `Saver.CaptureCodeFiles` 中读取它。
- 副作用:更改 `isExecuting` 为 true 的窗口的文本会调用 `MainSim.Inst.StopMainExecution()`(`UpdateTokens`)。选项 `tabs to spaces = enabled` 会把制表符改写为 4 个空格,因此读回的内容可能与写入的不同。

### 3.4 创建 / 覆盖 / 删除(内存 + 磁盘)
- 创建:先校验,然后 `workspace.OpenCodeWindow(name, code, offset)`。覆盖:`codeWindows[name].ApplyExternalCode(code)`。删除:`codeWindows[name].GetComponent<Window>().Close()`(`Core/Window.cs`:`public void Close()` -> `workspace.RemoveWindow(this)` + `Destroy`)。游戏自己的 `CodeWindow.PromptDelete()` 在 `isExecuting` 时拒绝;`Close()` 本身不检查,因此 Tool 必须先拒绝/停止。
- 磁盘:**窗口创建/编辑时不会向磁盘写入任何内容。** 持久化 = `Saver`:
  - `Core/Saver.cs`:`public static Task<bool> Save(MainSim mainSim)`、`public static Task<bool> SaveProgress(MainSim mainSim)`、`public static Task<bool> SaveCode(MainSim mainSim)`。
  - `SaveCode` -> `CaptureCodeFiles(workspace)`(主线程,使用 `item.CodeInput.text`)-> 排队的 `WriteCodeFiles(Dictionary<string,string> code, string builtins, string savePath)`,它以原子方式写入 `<savePath>/<name>.py`(`WriteTextAtomically`,UTF-8 无 BOM,临时文件 + `File.Replace`),若缺失则写入 `__builtins__.py`,并**删除目录中每一个没有对应代码窗口的 `.py`**(已删除的 Script 就是这样从磁盘上消失的)。
  - 文件:`<Application.persistentDataPath>/Saves/<saveName>/<script>.py` + `save.json`(`Saver.GetPathOfSaveDirectory(string saveName)`)。
  - 自动保存:选项 `autosave`(代码,`Menu.RestartAutosave` 中 30 秒计时器)、`autosave progress`;**当 `file watcher` 启用时 `autosave` 会被自动禁用**(`Saver.EnableDisableFileWatcher`),反之亦然。本机上代码自动保存是关闭的,因此 Agent 写入的 Script 只有在 Mod 调用 `Saver.SaveCode(MainSim.Inst)`(或人类保存)时才会持久化。`Parse()` 会设置 `MainSim.dirty = true`,因此游戏的退出提示会出现。
  - `Saver.Load` 会把所有 `*.py`(`__builtins__.py` 除外)加载到窗口中,**仅在场景加载时**进行;之后放到磁盘上的新 `.py` 不会被打开。

### 3.5 文件名校验:VERIFIED
- `Core/Workspace.cs`:`public override bool IsValidFileName(string fileName)` = `Saver.IsValidCodeFileName(fileName) && base.IsValidFileName(fileName)`。
- `Core/WorkspaceBase.cs`:`public virtual bool IsValidFileName(string fileName)` = `Saver.IsValidPortableFileName(fileName) && !IsWindowOpen(fileName, ignoreCase: true)`(与 Script 同名的文档窗口同样会冲突)。
- `Core/Saver.cs`:`public static bool IsValidPortableFileName(string fileName)`:非空,不以 `.` 开头,不以 `.` 或空格结尾,不含 `<>:"/\|?*` 中的任何字符,不含 `< ' '` 的字符,第一个以点分隔的段不是 Windows 保留设备名(CON、PRN、AUX、NUL、COM1-9/¹²³、LPT1-9/¹²³,不区分大小写)。`public static bool IsValidCodeFileName(string fileName)` 额外拒绝 `__builtins__`(不区分大小写)。
- `public static bool SanitizeCodeFileName(string fileName, out string newFileName)`:去掉一个开头/结尾的 `.`、一个结尾的空格、无效字符和控制字符;返回 `newFileName.Length > 0`。被 `CaptureCodeFiles` 用来重命名无效窗口(`item.Rename(newFileName)`,回退到 `GenerateFileName("f")`)。
- 游戏代码中没有长度限制。磁盘上会追加 `.py`;显示名称不带扩展名。
- 陷阱:`codeWindows` 的查找区分大小写,但名称冲突不区分大小写。

### 3.6 File Watcher 行为:VERIFIED
`Core/Saver.cs`:`StartFileWatcher()`(在 `Load` 末尾调用)、`StopFileWatcher()`、`OnFileChanged`、`ApplyCodeChanges(Workspace)`(每帧由 `MainSim.Update` 调用)。
- 仅当选项 `file watcher == enabled` 时才活动(本机:enabled)。监视 Save 目录中 `*.py` 的 `Changed`/`Created`/`Renamed`(不含 `Deleted`),250 毫秒稳定时间,并带有稳定读取检查。
- 当某个**已有代码窗口**的名称发生变化,且内容与 `lastWrittenCode`(游戏自己上一次写入的内容)以及窗口文本都不同时:`ApplyExternalCode(code)`。未知名称会被忽略。游戏发起的写入会记录在 `lastWrittenCode` 中,因此不会循环回来。
- 在 `Saver.IsSaving` 期间跳过。

---

## 4. 已加载 Save 与主菜单:CHANGED

- **不存在独立的主菜单场景。** `Core/Menu.cs` `Start()`:`LoadSave(OptionHolder.GetString("activeSave","Save0"))`、`Saver.Load(MainSim.Inst)`,然后 `Open()`。`Open()` 仅停用 `MainSim.Inst.workspace.gameObject` 和各 UI 画布,并激活标题 `menu` 对象。因此在"主菜单"时 Save **已经加载**,且 `MainSim.Inst` 非 null。
- 菜单是否打开的判断:`MainSim.Inst.menu.gameObject.activeInHierarchy`(`public Menu menu`;游戏在 `MainSim.Update`/`InputBoss` 中正是这样用的)。Esc(`menu` 键)切换它。`Menu.Play()` 将其隐藏。
- "尚未加载"的判断:`MainSim.sim`(private)在 `SetupSim`(由 `Saver.Load` 调用)之前为 null。`MainSim.Update`/大多数成员在解引用 `sim` 时不做 null 检查。`Core/MainSim.cs`:`public static MainSim Inst => _instance;` 在 `Awake` 中设置。
- 切换 Save:`SaveChooser.LoadSave` -> `menu.LoadSave(name)` 然后 `SceneManager.LoadScene(0)`(整个场景重新加载;新的 `MainSim`)。已销毁对象的 `_instance` 是 Unity 伪 null:请使用 `MainSim.Inst == null` / `!MainSim.Inst`;**`?.`/`is null` 检测不到它**。
- 当前 Save 名称:`OptionHolder.GetString("activeSave", "Save0")`(`Utils/OptionHolder.cs`,内存中的选项,同时持久化到 `options.txt`)。路径:`Saver.GetPathOfSaveDirectory(name)`。名称有效性:`Saver.IsValidSaveName`。在 `LoadSave` 与重新加载之间有一个短暂的间隙,此时选项与已加载的 Save 不一致。
- 对 spec 的影响:"主菜单 = 未加载 Save"必须重新定义(见影响部分)。

---

## 5. 状态

### 5.1 `Farm`:VERIFIED
`Core/Farm.cs`:
- `public Simulation sim; public GridManager grid; public List<Drone> drones; public int mainDroneId; public ItemBlock Items => _items;`
- 物品:`Utils/ItemBlock.cs`:`public double[] items`,以物品 id 为索引(`StringIds.NumItems`);`GetNumber(int)`、`Contains(ItemBlock)`、`Remove(ItemBlock)`(原子性:先检查 `Contains`)。名称:`ResourceManager.GetItem(int itemId)` -> `ItemSO.itemName`;`StringIds.GetItemName(int)`、`StringIds.GetItemId(string)`(未知则为 -1)。安全副本:`MainSim.GetInventory()` 在锁下返回 `new ItemBlock(sim.farm.Items)`。
- Unlock:`private Dictionary<string,int> unlocks`(键为小写)。安全副本:`public Dictionary<string,int> GetUnlocks()`(按已安装的 DLC 过滤)和 `MainSim.GetUnlocks()`。`public int NumUnlocked(UnlockSO)`、`public int NumUnlocked(string unlock)`(**不转小写**;`IsUnlocked(string)` 会转小写)、`public bool IsUnlocked(string s)`。`MainSim.NumUnlocked(string)`、`MainSim.IsUnlocked(string)`。
- 初始 Unlock:`Farm.startUnlocks` = grass, soil, harvest, pass, do_a_flip, pet_the_piggy, grassland, hay, straw_hat, tap, stats, comments。

### 5.2 费用:VERIFIED
`Core/UnlockSO.cs` 字段:`unlockName`、`ItemBlock unlockCost`、`List<ItemBlock> multiUnlockCost`、`float multiUnlockFactor = 2f`、`int maxUnlockLevel = 1`、`bool IsMultiUnlock => maxUnlockLevel > 1`、`string parentUnlock`、`AdditionalUnlockRequirement additionalUnlockRequirement {unlockName, level}`、`string docs`、`string unlockedHat`、`EDlcType dlc`、`bool enabled`。

`Core/Farm.cs`:`public ItemBlock GetUnlockCost(UnlockSO unlockSO, int numUnlocked = -1)`:
```
num = numUnlocked >= 0 ? numUnlocked : NumUnlocked(unlockSO.unlockName);
if (num >= unlockSO.maxUnlockLevel) return ItemBlock.CreateEmpty();     // max level: empty cost
if (IsMultiUnlock) { count = multiUnlockCost.Count;
  if (num > 0) { if (count < num) { cost = multiUnlockCost[count-1] * Pow(multiUnlockFactor, num-count); round each to 3 decimals; TruncateNumbers(); return cost; }
                 return multiUnlockCost[num-1]; } }
return unlockSO.unlockCost;   // first purchase (num == 0) and single unlocks
```
`MainSim.GetUnlockCost(UnlockSO)` 是公开的带锁包装。`get_cost(Unlocks.X, n)`(`SideEffect.GetCost`)= `GetUnlockCost(unlock, n)`,其中 `n` 是**当前已拥有的等级**,而非目标等级。对于实体:当设置了 `yieldUpgradeName` 时,为 `entitySO.cost * (1 << max(0, NumUnlocked(yieldUpgradeName) - 1))`。

### 5.3 `GridManager`:VERIFIED
`Core/GridManager.cs`:
- `public struct Cell { public Entity entity; public double waterVolume; public int cactusNumber; public Vector2Int pos; public int depth; }`
- `public Cell[] cells;` 行主序:`public ref Cell CellAt(int x, int y)` => `cells[y * WorldSize.x + x]`。
- `public Vector2Int WorldSize { get; private set; }`(Script 的 `get_world_size()` 返回 `WorldSize.y`)。安全读取:`MainSim.GetWorldSize()`。
- 实体类型名:`cell.entity?.objectSO.objectName`(`Core/EntitySO.cs`:`public string objectName`;`ToString()` = `"Entities." + ToUpperSnake(objectName)`)。`null` 实体 = 空(Script 得到 `None`)。
- 地面:`public ref GroundData.Ground GroundAt(int x, int y)`;`Core/GroundData.cs`:`public struct Ground { public GroundSO groundSo; public int hardness; public int stability; }`;名称 `groundSo.objectName`(`Core/GroundSO.cs`)。`cell.depth` = 挖掘深度(Script 的 `get_pos_z` = `-depth`)。
- 名称由资源定义(`ResourceManager.GetAllEntitites()`、`GetAllGrounds()`、`GetAllUnlocks()`、`GetAllItems()`;`ResourceManager.LoadAll` 中的 `Resources.LoadAll`,由 `Menu.Start` 调用)。`[INFERENCE]` 来自 `Languages/EN/Strings/object_tooltips.txt` 的键:实体 apple、grass、bush、tree、carrot、rice、bamboo、pumpkin、dead_pumpkin、sunflower、cactus、hedge、treasure、dinosaur;地面 grassland、soil、dirt、rock、clay、block_blue/red/green、coal、iron、quartz、limestone、sand、bedrock、petrified_pumpkin、loam、perlite。应在运行时枚举。
- 网格无法通过 `MainSim` 的公开成员访问:需要对 private `sim`(`Simulation.farm` 是公开的)和 private `lockSimulation` 使用反射。

### 5.4 无人机:VERIFIED
`Core/Drone.cs`:`public Vector2Int pos; public int depth; public int DroneId { get; set; }; public Hat hat`。`Farm.drones` 可能包含 `null` 空洞(已移除的无人机)。数量 = 非 null 条目数(`SideEffect.NumDrones` 统计非 null 的 `ProgramState`)。上限 = `Helper.NumDrones(farm.NumUnlocked("megafarm"))` = `1 << n`(`Utils/Helper.cs`)。生成的无人机在 Run 结束时被移除(`RemoveSpawnedDrones`),因此在 Run 之外恰好只有 `drones[0]`。主无人机 = `farm.mainDroneId`。

---

## 6. Unlock

### 6.1 签名与返回值:VERIFIED,附 CHANGED 细节
- `Core/Farm.cs`:`public bool UnlockOrUpgrade(UnlockSO unlockSO, bool requireParent = true)`
- `Core/MainSim.cs`:`public bool UnlockOrUpgrade(UnlockSO unlockSO)` => `lock { if (IsSimulating()) return false; return sim.farm.UnlockOrUpgrade(unlockSO); }`
- 返回值 = 单纯的 `bool`;**没有原因**。

### 6.2 校验顺序(位于 `Farm.UnlockOrUpgrade` 内部),任一条件成立即返回 false:
1. `requireParent && !IsNullOrEmpty(parentUnlock) && !IsUnlocked(parentUnlock)`(缺少父项)
2. `NumUnlocked(unlockSO) >= maxUnlockLevel`(已达最高等级)
3. 设置了 `additionalUnlockRequirement` 且 `unlockName` 非空,并且 `NumUnlocked(req.unlockName) < req.level`(额外要求)
4. `sim.singleDrone && (megafarm || expand)`(仅限排行榜模拟)
5. `GetUnlockCost(unlockSO)` 为 null,或 `!Items.Remove(cost)`(物品不足;失败时不会扣除任何物品)
之后 `Unlock(name, NumUnlocked+1)` 和 `UnlockAllIn(unlockSO)`。
另外 `MainSim.UnlockOrUpgrade` 在 `IsSimulating()`(排行榜 / `simulate`)期间返回 false。
- **游戏在此处不检查:** `UnlockSO.enabled`、DLC 是否已安装(对于未安装的 DLC,`NumUnlocked(UnlockSO)` 返回 0,因此会"购买"1 级)。

### 6.3 区分失败原因:可通过预检查实现
未知名称:`ResourceManager.GetUnlock(string name)` 返回 `null`(`unlocks.GetValueOrDefault`);名称为小写的 `unlockName`。然后按 6.2 的相同顺序复现(父项通过 `MainSim.IsUnlocked(parentUnlock)`,等级通过 `NumUnlocked`,额外要求,费用通过 `MainSim.GetUnlockCost` 对比 `GetInventory().Contains(cost)`),另外加上 Mod 层面的 `enabled`/DLC(`Core.Platform.PlatformManager.IsDlcInstalled(unlockSO.dlc)`,与 `ResearchMenu.Setup` 的做法相同)以及 `IsSimulating()`。检查和调用应在同一个主线程 tick 内完成(状态可能在模拟线程下发生变化;以返回值作为最终裁决)。

### 6.4 研究菜单调用的内容:VERIFIED
`Core/UnlockBox.cs` `public void ButtonClicked()`:
```
if (NumUnlocked() > unlockSO.maxUnlockLevel) { Achievements.enabled = true; MainSim.Inst.ResetUnlocksToMax(); }
else if (MainSim.Inst.UnlockOrUpgrade(unlockSO)) {
  FMODSoundManager.PlaySound(SoundEffectType.Unlock, Vector3.zero);
  if (!string.IsNullOrEmpty(unlockSO.unlockedHat)) MainSim.Inst.UnlockHat(ResourceManager.GetHat(unlockSO.unlockedHat));
}
```
- 按钮仅在 `Unlockable`/`Upgradable` 状态下启用:根 Unlock 始终为"可解锁";子项在父项等级 > 0 时可解锁(`SetupChildren(unlockable && valueOrDefault > 0, ...)`)。买不起的仍可点击(只是着色不同)。
- Tool 调用否则会错过的 UI 路径副作用:帽子解锁(`MainSim.UnlockHat(HatSO)`,公开,入队)、音效。文档自动打开由 `ResearchMenu.Update` -> `UnlockBox.SetupRec` 完成(当等级变为 1 且文档不在 `researchMenu.openedUnlockDocs` 中时,`workspace.OpenAndGoTo(unlockSO.docs)`;另外还有 `docs/unlocks/expand_2.md`),每帧执行,无论菜单是否打开,因此在 Tool 执行 Unlock 之后同样会触发(并且如果研究菜单是打开的,`OpenAndGoTo` 会将其关闭)。
- Script 的 `unlock()`(`SideEffect.Unlock`)调用 `Farm.UnlockOrUpgrade(u, requireParent: false)`:**忽略父项**,与研究菜单不同。

---

## 7. 文档

### 7.1 来源:VERIFIED
- 文件位于 `TheFarmerWasReplaced_Data/StreamingAssets/Languages/<LANG>/` 之下(`EN`、`ZH`、`DE` 等 14 种语言):`docs/*.md`(EN:65 个文件:`home.md`、`getting_started.md`、`first_program.md`、`output.md`、`stats.md`、`backup.md`、`external_editor.md`、`credits.md`、`scripting/*.md`(break、comments、continue、debug、dicts、for、functions、if、import、lists、operators、scopes、sets、tuples、variables、while)、`unlocks/*.md`(约 45 个文件,包括 `expand_1.md`、`expand_2.md`))以及 `Strings/*.txt`(`@key = text`)。`Languages/builtins.py` 是共享的。
- 加载:`Utils/Localizer.cs`:`public static string LoadDoc(string doc)` 通过 `PlatformManager.PlatformHandler.ReadStreamingFileText` 读取 `Path.Combine(Application.streamingAssetsPath, "Languages", Lang, doc)`(PC 上为普通 `File.ReadAllText`)。若缺失则返回 `""`(文档不会回退到 EN)。
- **并非所有页面都是文件。** `Core/DocsWindow.cs` `DisplayDoc(string doc)`:前缀 `functions/<n>` -> `Localizer.Localize("code_tooltip_" + n)`(+ "unlocked in" 链接);`unlocks/<n>` -> `TooltipUtils.UnlockTooltip(n).text`;`items/`、`objects/`、`grounds/` -> `TooltipUtils.ItemTooltip/FarmObjectTooltip/GroundTooltip`;其他任何内容 -> `Localizer.LoadDoc(doc)`。某个 Unlock 的 Unlock 文档来自 `UnlockSO.docs`(例如 `docs/unlocks/plant.md`)。

### 7.2 按 Unlock 的可见性:CHANGED(并非隐藏;而是被占位页替代)
`Core/DocsWindow.cs`:`private UnlockSO GetMissingUnlock(string doc)`:
```
if (doc == "docs/output.md") return null;
name = Path.GetFileNameWithoutExtension(doc);
unlockSO = ResourceManager.GetUnlockOf(name) ?? ResourceManager.GetUnlockOf(name.ToLowerInvariant());
if (unlockSO == null || MainSim.Inst.IsUnlocked(name)) return null; return unlockSO;
```
若非 null,窗口显示 `string.Format(Localizer.Localize("missing_unlock_page"), Localizer.Localize(missingUnlock.unlockName), TooltipUtils.UnlockTooltip(missingUnlock.unlockName).text)`(EN 文本:"You need to unlock {0} in the [tech tree](tech_tree) before you can see this page.")。`UnlockTooltip` 还会暴露费用/描述。因此游戏从不列出/隐藏页面;对应未解锁名称的页面会显示占位页。基名不对应任何 Unlock 的页面(home、getting_started 等)始终可见。`Core/ResourceManager.cs`:`public static UnlockSO GetUnlockOf(string name)` 把已解锁事物的名称(Unlock 名称、`UnlockSO.unlocks` 的条目、去除数字后的名称)映射到授予它的 Unlock。Mod 的 `list_docs` 必须重新实现 `GetMissingUnlock`(private)并自行枚举文件。
- `ResearchMenu` 还会把已打开的文档记录在 `researchMenu.openedUnlockDocs` 中;`TooltipUtils.GetKeywordDocsPage(string keyword)` 优先使用 `docs/scripting/<keyword>`,否则使用 `UnlockSO.docs`。

### 7.3 无论语言都为英文:部分可行
- Markdown 页面:直接读取 `Application.streamingAssetsPath/Languages/EN/<doc>`(`Application.streamingAssetsPath` + File IO)。`Localizer.Lang` 是 `OptionHolder` 的 `language`(此处为 `ZH`)。
- 字符串表页面(`functions/`、`unlocks/`、`items/` 等)使用 `Localizer.Localize` = 当前语言,仅对缺失的键回退到 EN(`Localizer.Localize(string key)`:`language` 字典,否则 private `en` 字典,否则键本身)。没有仅限英文的公开 API。选项:对 private `Localizer.LoadLangShortcodeFromAssets(string shortcode)` -> `Dictionary<string,string>` 使用反射获取 `"EN"`,然后自行格式化模板;或者在设置了 Mod 标志/thread-static 期间,使用 Harmony prefix 让 `Localizer.Localize` 返回 EN。`[INFERENCE]` 后者是安全的;未运行。

### 7.4 错误消息为英文:CHANGED
解析/运行时/警告消息是在抛出时本地化的(`Localizer.Localize`/`CodeUtilities.LocalizeAndFormat`),因此在 `language = ZH` 时是中文。相同的 Harmony 思路:在**模拟线程**上强制使用 EN(在 `Simulation.RunNextStep` 的 prefix 中设置 thread-static),以及在 Mod 发起、位于主线程上的 `CodeWindow.Parse()` 期间强制使用 EN,同时不改变人类用户的 UI 语言。`[INFERENCE]` 未运行。

---

## 8. Leaderboard Run

### 8.1 `leaderboard_run` 内置函数路径:VERIFIED
`Core/BuiltinFunctions.cs`:`new PyFunction("leaderboard_run", LeaderboardRun)`;`private static double LeaderboardRun(List<IPyObject> parameters, Simulation sim, Execution exec, int droneId)` 接受 **(LeaderboardSO, PyString fileName, PyNumber speedFactor)**。构造 `MainSim.LeaderboardStartArgs(fileName, unlockList, startItems, globals, leaderboardName, steamLeaderboardName, leaderboardType, fullyGenerateUnderground, dlc, -1, ...)`,设置 `SideEffect.RunLeaderboard`。
`Execution.ApplySideEffect` `case SideEffect.RunLeaderboard:` 若 `sim.leaderboardType == none`:`AssertUnlocked("leaderboard")`(Mining DLC 则为 `"underground_leaderboard"`),`sim.simRunner.ScheduleLeaderboardStart(args)`(若没有该名称的窗口则抛出 `ExecuteException("error_file_not_found")`),`sim.simRunner.TimeFactor = speedFactor`,`sim.Paused = true`;嵌套调用会记录 `warning_recursive_simulation`。
`MainSim.Update` -> `StartLeaderboard(args)`:解析该窗口;对于非 `simulation` 类型 **`StopMainExecution()`(调用方的 Run 被停止)**;`storedSim = sim`;`sim = new Simulation(...)`;新建 `Execution`;`leaderboardManager.StartLeaderboardRun(args.leaderboardType != LeaderboardType.simulation)`。`Core/LeaderboardType.cs`:`none, simulation, reset, farm_resources`。`simulate()` 使用相同的覆盖层,类型为 `simulation`,且没有结果界面。

### 8.2 `LeaderboardManager`:VERIFIED
`Core/LeaderboardManager.cs`:
- `public void StartLeaderboardRun(bool showAverageText)`(开启覆盖层;`showAverageText == true` <=> 真正的 Leaderboard Run,`false` <=> `simulate()`)
- `public void RemoveOverlay()`
- `public void StopLeaderboardRun(bool finished, string leaderboardName, string steamLeaderboardName, TimeSpan timeSpan)`:关闭覆盖层,`workspace` 设为不活动,打开结果界面,`leaderboard.FillLeaderboard(steamLeaderboardName, score)`。
- `public void SpeedUp()`:`if (MainSim.Inst.TimeFactor <= 128.0) TimeFactor *= 2.0;`
- `public void SlowDown()`:`if (TimeFactor >= 2.0) TimeFactor /= 2.0;` 同时切换 `leftButton`/`rightButton` 的可见性(SpeedUp 不会)。
- `public void OkPressed()`:`workspace.SetActive(true); leaderboardScreen.SetActive(false); MainSim.Inst.RestoreMainSim();`
- `public bool IsRunning => overlay.activeInHierarchy;` `public bool IsLeaderBoardScreenOpen => leaderboardScreen.activeInHierarchy;`
- 结果界面上的 Esc(`menu` 键)会调用 `OkPressed()`(`InputBoss`)。

### 8.3 `TimeFactor` 的边界:CHANGED
`Core/MainSim.cs`:`public double TimeFactor { get; set; }` setter:`value < 0 || > 1_000_000 || NaN || Infinity` -> `1_000_000`;`< 0.01` -> `0.01`;否则为 value。因此该属性的真实边界是 **[0.01, 1e6]**;**256 的上限仅来自 UI 按钮**(`LeaderboardManager.MAX_SPEEDUP = 256.0` 是未使用的常量;`SpeedUp` 在 `<= 128` 时允许翻倍)。初始倍率是 Script 的第 3 个参数(取 [0.01, 1e6] 内的任意值)。如果 Script 以例如 100 开始,`SpeedUp` 会得到 200 然后停止。在结束时(`LeaderboardExecutionFinished`)以及启动失败时重置为 `1.0`。每帧模拟预算:`MAX_SIM_TIME_PER_FRAME = 0.2` 秒的真实 delta 时间乘以 `TimeFactor`。忠实模拟人类的方式:调用 `SpeedUp()`/`SlowDown()`(主线程),而不是设置 `TimeFactor`。

### 8.4 结果:时间、已完成/已取消、排名
- 结束检测:`MainSim.Update`(主线程)`if (sim.leaderboardType != none && !sim.IsExecuting()) LeaderboardExecutionFinished();` -> 计算 `finished`(reset:`Unlocks leaderboard/underground_leaderboard` 已解锁;farm_resources:`Items.Contains(goalItems)`;`simulation` 类型:无),`leaderboardCancelled` 会强制 `finished=false`;`totalLeaderboardTime += sim.CurrentTime; numLeaderboardRuns++`;若 `total >= 2h` **或 `!finished`** -> `leaderboardManager.StopLeaderboardRun(finished, sim.leaderboardName, sim.steamLeaderboardName, totalLeaderboardTime.ToTimeSpan() / numLeaderboardRuns)`;**否则它会自动重新启动另一个子 Run** 并取平均值(`MainSim.numLeaderboardRuns`、`totalLeaderboardTime` 为公开)。
- **最佳钩子:** 对 `LeaderboardManager.StopLeaderboardRun(bool finished, string leaderboardName, string steamLeaderboardName, TimeSpan timeSpan)` 使用 Harmony postfix,可得到 finished 标志 + 时间(每个子 Run 的平均值)。`finished == false` 同时涵盖用户取消和"Script 结束但未达成目标"两种情况(UI 文本 `run_cancelled`)。显示的时间使用 `LeaderboardManager.StringFromTimeSpan(TimeSpan)`。
- **排名:** 异步的 Steam 回调。`Core/Leaderboard.cs`:`private void OnPlayerLoaded(LeaderboardEntryData[] data)` 设置 `rankText.text = "#" + data[0].rank`(`Utils/LeaderboardEntryData.cs`:`playerName`、`score` 毫秒、`rank`)。对该 private 方法挂钩(postfix)以捕获排名/分数。`Utils/Platforms.Handler/SteamHandler.cs` 的 `LoadLeaderboard` 在 `!SteamManager.Initialized` 时什么都不做,因此排名可能永远不会到达;应视为可选/null 并设置超时。分数为 `(int)timeSpan.TotalMilliseconds`,若未完成 / 超过 `int.MaxValue` 毫秒则为 0。
- **活动检测:** `MainSim.Inst.leaderboardManager.IsRunning`(覆盖层)或 `IsLeaderBoardScreenOpen`;`MainSim.Inst.IsSimulating()`(`sim.leaderboardType != none`,包含 `simulate()`);`MainSim.Inst.MightBeSimulating()`(`storedSim != null`)。区分 Leaderboard Run 与 `simulate()`:通过 `StartLeaderboardRun` 的 `showAverageText` 参数(钩子)或 private 的 `sim.leaderboardType`。
- 关闭:`LeaderboardManager.OkPressed()`(主线程),仅在 `IsLeaderBoardScreenOpen` 时有意义。钩子时机:结果界面在 `StopLeaderboardRun` 中被同步设置好。

---

## 9. 输入

- 直接轮询旧版 `UnityEngine.Input`。UI 事件通过 `StandaloneInputModule` 子类:`Core/CustomStandaloneInputModule.cs`(`public PointerEventData GetPointerData()`;被 `Core/Tooltip.cs:44` 以空安全方式使用)。
- `Core/InputBoss.cs`(`MonoBehaviour`,`Update`):滚动(缩放/滚动),热键通过 `OptionHolder.GetKeyCombination(name).IsKeyPressed(true)` 处理 `menu`、`search`、`redo 1/2`、`undo`、`save`、WASD 平移(`move up/left/down/right`)、`start execution`、`stop execution`、`next step`、`pause execution`、`hide UI`。**如果鼠标在窗口之外则提前返回**(`Update` 的最前几行),因此游戏热键只有在光标位于窗口内时才有效。
- 其他直接使用原始 `Input.*` 的地方:`MainSim.Update`(`Input.mousePosition` 射线/悬停提示)、`Workspace`/`WorkspaceBase`(光标、Alt)、`CodeCompleter`(方向键)、`MarkdownText`(Mouse0)、`Tooltip`(Mouse1、位置)、`PiggyBank`、`ResearchMenu.Scroll`、`TitleWorkspace`、`SteamScript`(Space)、`CheatConsole`(死代码)、`KeyBindOptionUI`、`CodeInputField`(通过 `EventSystem.current.currentInputModule.input` 输入文本,`IUpdateSelectedHandler`)。
- 相机:`Core/CameraController.cs` 不读取输入;它跟随 `workspaceContainer.anchoredPosition`,该值由 `InputBoss`(WASD、滚动)和 uGUI 拖拽改变。
- 按键绑定可由用户重新绑定(`KeyBindOptionUI`,任意键;默认值位于序列化资源中)。本机 `options.txt`:menu=Escape、save=Ctrl S、start=F5、stop=Shift F5、pause=Ctrl F5、next step=F11、search=Ctrl F、undo=Ctrl Z、redo=Ctrl Shift Z / Ctrl Y、hide UI=Ctrl Alt H、WASD、autocomplete=Ctrl Space、indent=Tab。
- **F10:在代码中任何地方都 NOT FOUND**(对反编译结果 `grep F10|KeyCode.F`:无命中),在本机 `options.txt` 中也没有。默认情况下它是空闲的,但用户可以把某个动作重新绑定到它;启动时读取 `OptionHolder` 的组合键,并在冲突时发出警告。F5 / F11 被游戏使用。
- 最干净的 Input Lock `[VERIFIED 运行时,ticket #6]`:仅靠拦截射线的覆盖层是**不够的**(热键、滚动缩放,以及在已获得焦点的 `CodeInputField` 中打字都不使用射线)。组合使用:(a) `InputBoss.enabled = false`(`FindFirstObjectByType<InputBoss>()`;一次性关掉所有热键、滚动、WASD),(b) `EventSystem.enabled = false`(关掉点击、拖拽、悬停、焦点输入框打字),并在释放时重新启用两者。在运行中的游戏里用 SendInput 验证:锁定期间点击、字母键、F5/Shift F5/Ctrl Z、WASD、滚轮都无效,已获得焦点的代码输入框也不接收字符,解锁后输入立即恢复,焦点仍在原输入框。**推断中有一处错误**:禁用 EventSystem 会让 `EventSystem.current` 变为 null,而游戏代码会无保护地解引用它(`CodeWindow.ShowError`,在 `CodeWindow.Update` 与 `ApplyExternalCode` 中;带语法错误的窗口在锁定期间每帧抛 `NullReferenceException`,`write_script` 写入带语法错误的代码时失败)。修复:锁定期间同时启用一个 Mod 自己的空 `EventSystem`(无输入模块)作为 `current`,释放时再关掉(修复后同样场景无异常)。场景重新加载会重建 `InputBoss` 和 EventSystem(新的、已启用的),所以 Mod 在锁定期间每帧重新查找并禁用;`load_save` 在锁定下重新加载后解锁输入正常(已验证),重新加载期间的拦截本身未单独验证。锁图标使用 Mod 自己的 `ScreenSpaceOverlay` Canvas(`sortingOrder` 最大),用 `RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition, null)` 自行做悬停检测;在 `hide UI`(Ctrl Alt H)开启时图标仍然显示(已验证)。Mod 自己的 F10 轮询是 `Plugin` 同对象上的 `MonoBehaviour`(`Input.GetKeyDown(KeyCode.F10)`),F10 之后输入恢复、Run 状态为 `stopped_by_user`(已验证)。启动时的 F10 绑定检查:`Resources.LoadAll<KeyBindOptionSO>("Options/")` + `OptionHolder.GetKeyCombination(name).key == KeyCode.F10`(把 `stop execution` 改绑到 F10 后启动,BepInEx 日志出现警告,已验证)。
- `hide UI` 热键会切换 `InputBoss.UIsToHide` 对象;Mod 的图标应放在它自己的画布上。

---

## 10. 执行速度

- `set_execution_speed`(`BuiltinFunctions.SetExecutionSpeed(List<IPyObject> parameters, Simulation sim, Execution exec, int droneId)`;`SideEffect.SetExecutionSpeed`):`Execution.ApplySideEffect`:`if (NaN || n > farm.MaxSpeedFactor() || n < 0.1) sim.ChangeExecutionSpeed(sim.farm.MaxSpeedFactor()); else sim.ChangeExecutionSpeed(n);`(无效值会重置为最大值,不报错)。
- `Core/Simulation.cs`:`public void ChangeExecutionSpeed(double speedFactor)` { `OpDuration = new Duration((long)Math.Ceiling(2500000.0 / speedFactor)); SpeedFactor = speedFactor;` }(基础 op = 2.5 毫秒)。在 `StopProgramExecution` 时重置为 `farm.MaxSpeedFactor()`。
- `Core/Farm.cs`:`public double MaxSpeedFactor()` = `Math.Pow(1.5, NumSpeedUpgrades)`(`NumSpeedUpgrades` = Unlock `speed` 的等级),当 `power` 物品数量 > 0 时 x2。电力通过 `UsePower` 计时器消耗。
- 正常游玩中的人类加速:除了 `speed` Unlock 和 Script 调用之外**没有**。`MainSim.TimeFactor` 在正常游玩中为 1.0(序列化默认值 `timeFactor = 1.0`);只有 `LeaderboardManager.SpeedUp/SlowDown`(覆盖层按钮,仅限 Leaderboard/`simulate()`)和 Script 的速度参数会改变它。人类可以暂停/单步(`StepByStepMode`、`pause execution`、`next step`)。结论:VERIFIED(与 spec "Leaderboard Run 之外没有时间加速"相符)。

---

## 11. 版本

- `Application.version` 在反编译结果中**任何地方都没有使用**(grep `Application.version|bundleVersion|gameVersion`:无命中);游戏内没有版本常量。只有 `Saver.version = 3` / `SaveGame.version`(存档格式)和 `Achievements.STATS_VERSION = 3`。
- 来自文件的构建号:Steam `buildid = 26462009`(`steamapps/appmanifest_2060160.acf`;`TargetBuildID` 相同;`LastUpdated` 1794484480)。Unity `6000.0.43f1`。
- `Application.version` 的值:`[INFERENCE]` PlayerSettings 的 `bundleVersion` 以二进制形式存储在 `globalgamemanagers` 中;其中仅有的类似版本号的字符串是 `1.0`、`1.0`、`0.1`;最可能是 `"1.0"`(Unity 默认值)。**NOT CONFIRMED**:在运行时记录一次 `Application.version` 和 `Application.unityVersion`。Steam 构建号无法通过 `Application.version` 读取;仅当已知 Steam 库路径时才从 `appmanifest` 读取,否则改为比较反射目标(程序集 MVID / 成员是否存在)。程序集版本为 `0.0.0.0`(`Properties/AssemblyInfo.cs`),对于门控毫无用处。

---

## 12. `CheatConsole`:VERIFIED(死代码)
`Core/CheatConsole.cs`:`MonoBehaviour`,带有 `private bool showConsole`,**从未被赋值为 true**(没有 setter,不是 `[SerializeField]`,反编译结果中任何地方都没有对该类的引用)。`Update` 仅在 `showConsole` 时才起作用;`InvokeCommand` 处理唯一的字符串 `"aint nobody got time for this"` -> `ToggleFastMode()`,在 1<->10 之间切换 `MainSim.Inst.harvestFactor`。`GetRichQuickly()` 会对每个 Unlock 调用 `MainSim.Inst.UnlockOrUpgrade`,但从未被调用。在正常游玩中不可达 `[INFERENCE]`(场景挂载情况无从得知)。仅作记录;不要使用。

---

## 13. 内置函数与 `SideEffect`

### 13.1 函数:VERIFIED,无差异
`Core/BuiltinFunctions.cs` 的 `functionList` 有 **60 个条目,名称和顺序与**任务书中给出的旧列表**完全相同**(已通过程序比较):harvest, can_harvest, swap, range, plant, move, can_move, till, get_pos_x, get_pos_y, get_pos_z, get_world_size, get_entity_type, get_ground_type, get_time (free), get_tick_count (free), use_item, place, get_water, do_a_flip, change_hat, print, quick_print (free), len, num_items, get_cost, clear, get_companion, unlock, num_unlocked, leaderboard_run, measure, min, max, abs, str, random, simulate, list, set, dict, set_execution_speed, set_world_size, spawn_drone, num_drones, max_drones, wait_for, has_finished, pet_the_piggy, tap, dig, get_hardness, get_stability, prospect_iron, prospect_quartz, push, can_push, jump, hover, get_hover。

补充事实:
- `methodList`(独立字典 `BuiltinFunctions.Methods`):`append`、`add`、`remove`、`pop`、`insert`。
- **已实现但未在 `functionList`/`methodList` 中注册**(因此 Script 无法调用):`GetDroneId`(`get_drone_id`,有 `SideEffect.GetDroneId` 和一个 `Execution` case)、`Send`(`send`)、`Receive`(`receive`)、`SimulateAsync`、`MinOrMax`(辅助函数)。`Core/BuiltinFunctions.cs` 的 `private static List<string> resetUnlocks` 仍然列出名称 `get_drone_id`、`send`、`receive`。VERIFIED:对 Core/Utils/Assembly-CSharp/NewAssembly 执行 `get_drone_id|"send"|"receive"` 的 grep:仅有的命中位于 `Core/BuiltinFunctions.cs`(resetUnlocks 列表位于第 92-93 行,实现体位于 788/813/831);没有其他注册位置。
- `Core/Execution.cs` `private double ApplySideEffect(int droneId)`。

### 13.2 `SideEffect` 枚举(`Core/SideEffect.cs`),当前完整列表(50 个值):
None, Harvest, CanHarvest, Swap, Plant, Move, CanMove, Till, GetPosX, GetPosY, GetPosZ, GetWorldSize, GetEntityType, GetGroundType, UseItem, Place, GetWater, ChangeHat, NumItems, GetCost, Clear, GetCompanion, Unlock, NumUnlocked, Measure, SetExecutionSpeed, SetWorldSize, GetTime, SpawnDrone, GetDroneId, NumDrones, MaxDrones, Await, HasFinished, Terminated, Error, DoAFlip, PetThePiggy, Print, Simulate, RunLeaderboard, Dig, GetHardness, GetStability, ProspectIron, ProspectQuartz, Push, CanPush, Jump, Hover, GetHover。
(未提供旧的枚举列表,因此未计算差异;不存在 `QuickPrint`、`Send`、`Receive`、`Min/Max/Abs/Random/Len/Str/Range/...` 的 side effect:它们是纯内置函数。`GetTickCount` 没有 SideEffect 值。)

---

## 结论汇总

| # | 项目 | 结论 |
|---|------|---------|
| 1.1 | `PressExecuteOrStop` 切换 | VERIFIED |
| 1.2 | `StartExecution` | VERIFIED(private,先停止当前 Run) |
| 1.3 | `Parse` 错误表现 | CHANGED(返回 null;错误在 private 字段中;字符偏移量;本地化文本或原始键) |
| 1.4 | Start/StopMainExecution 签名 | VERIFIED |
| 1.5-1.6 | Run 进行中 / 结束原因 | CHANGED(无事件;运行时错误会使 Run 保持执行中+暂停) |
| 1.7 | 线程 | VERIFIED |
| 2 | Logger / print 路径 / 错误 | VERIFIED;CHANGED(实际 99 条、无参数事件、消息已本地化) |
| 3 | Script | VERIFIED;陷阱:`Load` 会重复监听器、没有 `Saver.SaveCode` 就不写磁盘、大小写敏感性不一致 |
| 4 | 已加载 Save 与菜单 | CHANGED(菜单是覆盖层;Save 始终已加载;Unity 伪 null) |
| 5 | 状态 | VERIFIED |
| 6 | Unlock | VERIFIED;仅 bool;原因通过预检查获得;UI 副作用(帽子、文档) |
| 7 | 文档 | CHANGED(占位页而非隐藏;生成的页面;EN 需要 EN 文件/字典) |
| 8 | Leaderboard | VERIFIED;CHANGED(TimeFactor 边界 0.01..1e6,256 仅来自按钮;自动重复子 Run;排名异步/可选) |
| 9 | 输入 | VERIFIED;F10 NOT FOUND(默认空闲,可重新绑定) |
| 10 | 执行速度 | VERIFIED |
| 11 | 版本 | `Application.version` 在代码中 NOT FOUND;值 NOT CONFIRMED(很可能是 `1.0`);构建号 26462009 来自 Steam manifest |
| 12 | CheatConsole | VERIFIED 死代码 |
| 13 | 内置函数 | VERIFIED 相同(60 个);额外未注册的实现:get_drone_id、send、receive |

### NOT YET CHECKED
- (已解决)`get_drone_id`/`send`/`receive` 没有在其他任何地方注册(见 13.1)。
- 场景/预制体结构(`InputBoss.UIsToHide`):不在反编译结果中。EventSystem、`InputBoss` 的存在与行为已在运行中验证(第 9 节)。
- 实际的 `Application.version` 字符串和默认按键绑定(二进制资源)。
- 错误路径之外的 `Execution.PerformExecutionStep`/`ProgramState` 内部实现;`ErrorMessage` 的视觉表现。

---

## 对 spec #1 的影响

- **Story 30 / "未加载 Save(主菜单)"**:模型有误。不存在独立的主菜单场景;Save 在标题菜单显示之前就已加载(`Menu.Start`:`Saver.Load(MainSim.Inst)` 然后 `Open()`),并且在菜单处 `MainSim.Inst` 非 null。"未加载 Save"只存在于 `MainSim.sim` 创建之前 / 场景重新加载期间。通过 `MainSim.Inst.menu.gameObject.activeInHierarchy` 检测标题菜单,并明确决定菜单覆盖层是否算作"未加载 Save"(建议:算,返回该错误,因为工作区处于不活动状态)。对 `MainSim.Inst` 使用 Unity 的 null 语义。
- **Story 12(解析错误的"消息和位置")**:`CodeWindow.Parse()` 返回 null 且不抛出异常;读取 private 的 `parseException`/`errorString`/`errorStartIndex`/`errorEndIndex`。位置是**字符偏移量**,而非行/列;Mod 必须进行转换。消息可能是本地化键或本地化文本。
- **Story 16-17 / 运行按钮切换**:已确认,另外:`StartExecution` 会先停止任何当前的 Run;`StartMainExecution` 在执行中时静默忽略;在 Tool 中通过 `MainSim.Inst.IsExecuting()` 拒绝(在 Leaderboard/`simulate` 子模拟期间及其结果界面上同样为 true)。
- **Story 20-21(Run 状态)**:运行时错误**不会**结束 Run:它保持 `IsExecuting()==true`、`IsPaused()==true`,直到被停止。Mod 必须对错误挂钩(`Simulation.Error()` / `CodeWindow.SetErrorMessage(string,int,int)` / `Logger.LogError`)并自行停止 Run,或者在仍然停止的同时报告 `error`。没有结束事件;挂钩 `Simulation.StopProgramExecution()`。`stopped_by_user` 与 `stopped_by_tool` 的区分需要在 Mod 自己调用 `StopMainExecution()` 前后设置一个 Mod 标志。
- **Story 18-19 / 输出**:游戏内的上限实际上是 **99** 条(`Count >= 100` -> `RemoveFirst`),而非 100。`OnOutputChanged` 没有载荷,因此要挂钩 private 的 `Logger.WriteLog(string, bool)`(过滤到 `MainSim.Inst.Logger`;`UISim` 有自己的 Logger)。输出同时会写入 `persistentDataPath/output.txt`。错误条目为 `"Error: <msg>\nIn: <trace>"` + 分隔符;trace 包含调用栈和带 `^` 标记的源代码行,没有行号。每条消息 1000 字符的上限是在写入文件之后才应用的(钩子看到的是未截断的文本,因此如果 Mod 想要游戏的行为,必须重新应用该上限)。钩子运行在模拟线程上。
- **语言规则("所有面向 Agent 的文本为英文")**:游戏产生的文本(解析错误、运行时错误、警告、生成的文档页面)遵循游戏语言(此处为 `ZH`);只有 markdown 文档可以通过文件以英文读取。Spec 需要要么采用 Harmony 的 `Localizer.Localize` 英文覆盖(模拟线程 + Mod 发起的解析),要么对游戏原生消息明确设置例外。
- **Story 7-10 / Script 的增删改查**:写入窗口不会持久化到磁盘;要写磁盘需调用 `Saver.SaveCode(MainSim.Inst)`(主线程),它同时会删除已移除窗口的 `.py`。使用 `CodeWindow.ApplyExternalCode` 覆盖,而不是 `Load`(监听器重复);如果该窗口正在运行,这会停止 Run。`OpenCodeWindow` 对已存在的名称静默空操作(不区分大小写,包括文档窗口)。`codeWindows` 的查找区分大小写。`Window.Close()` 不检查 `isExecuting`(只有游戏的 `PromptDelete` 检查)。校验 = `Workspace.IsValidFileName`(在 `Saver.IsValidCodeFileName` 之上增加窗口名称冲突)。File Watcher(此处已启用,这会禁用代码自动保存)只会从磁盘重新同步已有的窗口,并忽略新文件。
- **Story 22-23(Unlock)**:`UnlockOrUpgrade` 仅返回 `bool`;游戏不提供失败原因,需按游戏的顺序预检查来推导(父项、最高等级、additionalUnlockRequirement、费用、模拟中),另加未知名称(`ResourceManager.GetUnlock` 为 null)以及 Mod 自行添加的 `enabled`/DLC 检查(游戏在此处不检查它们)。研究菜单路径还会解锁 `unlockedHat`,并且文档窗口会在一帧之后自动打开;Script 的 `unlock()` 则不同(`requireParent:false`)。
- **Story 24-25(文档)**:游戏**不会**为被锁定的内容隐藏文档;它渲染一个"你需要解锁 X"的占位页(`DocsWindow.GetMissingUnlock`,private)。`list_docs`/`read_doc` 必须重新实现该规则。文档位于 `StreamingAssets/Languages/<LANG>/docs/*.md`;`functions/`、`unlocks/`、`items/`、`objects/`、`grounds/` 页面由字符串表生成,而不是文件。
- **Story 26(Leaderboard 结果)**:结果通过对 `LeaderboardManager.StopLeaderboardRun(bool finished, string, string, TimeSpan)` 的 postfix 获得;`finished=false` 也意味着"结束但未达成目标",而不仅仅是已取消;时间是自动重复的子 Run 的**平均值**(reset/farm_resources 类型会重复,直到总计 >= 2 小时或失败)。排名通过 private 的 `Leaderboard.OnPlayerLoaded` 异步获得,并且可能永远不会到达(无 Steam);应使其可选。Leaderboard Run 开始时调用方的 Run 会被停止(`StartLeaderboard` -> `StopMainExecution()`),并且 `IsExecuting()` 在整个结果界面期间保持 true。
- **Story 27(自动关闭)**:在结果界面打开(`IsLeaderBoardScreenOpen`)之后,在主线程上调用 `LeaderboardManager.OkPressed()`;已验证它会调用 `workspace.SetActive(true)` 和 `MainSim.RestoreMainSim()`。
- **Story 28-29(速度倍率"最高 256x")**:256 的上限仅存在于 UI 按钮中(`SpeedUp` 在倍率 <= 128 时允许)。`MainSim.TimeFactor` 接受 0.01..1e6,起始倍率来自 Script 参数。要模拟人类:调用 `SpeedUp()`/`SlowDown()`;可达到的最大值取决于起始倍率。活动检测:`leaderboardManager.IsRunning`,而不是单独的 `IsSimulating()`(对 `simulate()` 也为 true)。
- **Story 33-37(Input Lock / F10)**:F10 默认不被游戏绑定(已在代码和本机选项上验证),但游戏的所有按键都可重新绑定;启动时检查冲突。仅靠拦截射线的层是不够的:热键(`InputBoss`)、滚动缩放和在已聚焦文本中打字都会绕过射线。锁定期间禁用 `InputBoss` 与 EventSystem(已在运行时验证,但需要额外的空 EventSystem 作为 `current`,见第 9 节),并为锁图标的提示自行做命中检测。当光标在窗口之外时 `InputBoss` 会忽略按键。
- **版本检查(story 42 / 平台)**:游戏内没有版本常量,`Application.version` 游戏并未使用且未得到确认(很可能是 `1.0`)。构建号 26462009 仅存在于 Steam manifest 中;Unity 为 `6000.0.43f1`(与 spec 相符)。降级门控应基于反射目标的解析结果,而不是版本字符串。
- **Spec "Further Notes" 入口点**:全部已验证存在且签名如下:`MainSim.Inst`、`StartMainExecution(CodeWindow, Node, bool)`、`StopMainExecution()`、`CodeWindow.PressExecuteOrStop()`(切换)、`Workspace.codeWindows`、`OpenCodeWindow(string, string, Vector2, Vector2)`、`Logger`(`OnOutputChanged`)、`Execution.ApplySideEffect(int)`(private)、`Farm`、`GridManager`、`UnlockOrUpgrade`(`Farm`:`(UnlockSO, bool requireParent = true)`;`MainSim`:`(UnlockSO)`)、`LeaderboardManager.StopLeaderboardRun/OkPressed/SpeedUp/SlowDown`、`ResourceManager`。`ItemBlock`、`ExecuteException`、`Localizer`、`OptionHolder` 位于 `Utils.dll`,而不是 `Core.dll`(反射必须针对正确的程序集)。
- **内置函数列表**:已注册的 60 个函数与旧列表相比没有差异;spec 中没有任何内容依赖于它的变化。
- **spec 的线程部分**:确认需要切回主线程;补充:位于模拟线程上的 Harmony 钩子(`Logger.WriteLog`、`Simulation.Error/StopProgramExecution`)必须只做数据入队,而对网格/无人机的快照读取需要对 private `sim` 和 `lockSimulation` 使用反射。
