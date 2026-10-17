# OcAI — 角色屋 桌面伙伴 · DeepSeek 余额桌宠插件

给 Steam 游戏 **《角色屋 桌面伙伴 Demo》**（AppID 5166840）写的 BepInEx 插件：

**点一下桌宠 → 她叫一声 → 头顶冒气泡显示你 DeepSeek 账户的剩余余额。**

> 基于 BepInEx 6 (IL2CPP) 开发。游戏是 Unity 2022.3.62f3 + IL2CPP（metadata v31）。

---

## 功能

| 功能 | 说明 |
|---|---|
| 💰 **点她查余额** | 调 DeepSeek 官方 `GET /user/balance`，气泡显示真实剩余额度 |
| 💬 **坐下按 F 聊天** | **她坐下时，鼠标指着她按 `F`** → 右下角弹游戏原生输入框 → 说的话发给 DeepSeek → 回答在头顶气泡里，多轮上下文记忆，可一键清空。多只角色时，光标指着谁就是跟谁聊 |
| 🌐 **联网搜索** | 问题里带「最新 / 今天 / 新闻 / 搜一下 …」时，插件先去搜索引擎取资料，再让 DeepSeek 据此回答（可在设置面板一键关闭） |

> 聊天走的是**普通 DeepSeek 助手**（提示词里说明了它是杭州深度求索公司的 AI），
> 不做角色扮演（不加人设、不装作是她本人在说话）。
> 当前的系统提示词就是这四句：
> ```
> 你是由杭州深度求索（DeepSeek）公司开发的 AI 助手 DeepSeek。请用中文回答。
> 需要回答用户，不要绕开问题。
> 如果问题不清楚，就直说不确定并问一句与澄清。
> 不要用 Markdown 标记、不要列条目、不要写代码块。
> ```
> 想加自己的要求，填 `[Persona] SystemPromptExtra`（会以「补充要求：…」追加）。
> 每次请求实际发送的系统提示词原文也会写进 `ocai_debug.txt`（`messages system text >>>`），
> 可以随时核对，不用看源码。| 🐋 **叫声** | 正弦波合成音，成功走上行 C 大调琶音（G5→C6→E6→G6），失败走下行 A 小调 |
| 🫧 **头顶气泡** | 克隆游戏自带的 `UI.DialogueBubble`，用原生美术；自动适应文字长度换行 |
| 🎛️ **游戏内设置** | 在游戏设置面板注入 `AI 接入` / `坐下按 F 聊天` / `清空对话记忆` / `聊天要求先坐下` 等开关行 |
| ⚙️ **Key 输入** | 复用游戏自己的输入弹窗，支持中文输入法 |
| 📐 **体型自适应** | 1x / 1.5x / 2x 体型下气泡间距自动缩放 |
| 🧍 **姿态区分** | 站立时气泡偏到她头右侧不挡脸；趴屏时回到正上方 |

> **为什么不是右键？** 游戏自己把**右键点角色**绑成了「让她站起来」，
> 所以右键在按下的一瞬间就把她从 `OcSittingState` 里踢出去了，插件再快也抢不到。
> 左键单击是查余额，所以聊天改到 `F` 键上，谁的时序都不抢。
>
> **为什么不是 F6/F7？** 很多笔记本上 F6/F7 是亮度/媒体系统键，按键根本不会送到游戏（实测按了没反应）。

---

## 安装

### 1. 装 BepInEx 6（IL2CPP 版，必须 BE 构建）

游戏是 IL2CPP，**BepInEx 5 完全不支持**，必须用 BepInEx 6 的 bleeding edge 版本：

<https://builds.bepinex.dev/projects/bepinex_be>

下载 **`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip`** 或更新的 BE 构建，
解压到游戏根目录（`OC DESKTOP.exe` 同级），解压后应该多出：

```
winhttp.dll
doorstop_config.ini
.doorstop_version
BepInEx\
dotnet\
```

### 2. 装插件

把 `dist\OcAI.dll` 放到：

```
<游戏目录>\BepInEx\plugins\OcAI.dll
```

> ⚠️ `plugins\` 目录里**不要放其它 dll**，BepInEx 会把它们全当插件加载。

### 3. 第一次启动

启动游戏，**第一次会跑 Cpp2IL 生成 interop 程序集，需要 1–3 分钟**，期间看起来像是卡住，属正常。

跑完后 `BepInEx\interop\` 会多出 130 多个 dll。

### 4. 填 API Key

启动游戏后，把鼠标移到桌宠身上，**按鼠标中键** → 弹出游戏原生输入框 → 粘贴你的 DeepSeek API Key
→ **点「确定」按钮**（⚠️ 按回车无效，游戏只接了按钮事件）。

Key 会立刻被打一次接口验证，她会告诉你结果。

> 也可以用 `Ctrl+V` 直接编辑配置文件：
> `<游戏目录>\BepInEx\config\com.lenovo.ocdesktop.ocai.cfg`

---

## 使用

| 操作 | 效果 |
|---|---|
| **点她身上任意位置（单击）** | 叫一声 + 头顶冒余额气泡 |
| **她坐下后，鼠标指着她按 `F`** | 弹输入框 → 打字 → 她调 DeepSeek 回答，气泡里显示 |
| **输入框里打 `/clear`** | 清空对话记忆（`/reset` 也行，`/help` 看提示） |
| **鼠标中键** | 打开 Key 输入框（也可用设置面板的 `AI 接入 Key` 行） |
| **`Ctrl+←` / `Ctrl+→`** | 站立时气泡左右微调（自动保存） |
| **`Ctrl+↑` / `Ctrl+↓`** | 气泡高度微调（自动保存） |

### 按 F 聊天怎么用

1. 先让她**坐下**，然后把**鼠标移到她身上**，按键盘 **`F`**。
   - **默认要求先坐下**（`[Chat] RequireSitting = true`）。站着按 F 会提示「先让我坐下再聊吧」。
     想随时聊就把 `RequireSitting` 改成 `false`，或在设置面板切「聊天要求先坐下」。
   - 坐姿判定走的是 Harmony 钩住 `OcSittingState.OnEnter/OnExit`（游戏自己的坐下状态），
     不是猜动画。
   - 鼠标不在任何角色身上时按 F，会提示「先把鼠标放到想聊的角色身上，再按 F 呀」。
   - **多只角色**：光标停在哪只身上，就是跟哪只聊（走游戏自己的 `OnMouseOver` 判定）。
2. 弹出游戏自带的输入框（就是填 Key 用的那个，中文输入法正常）。
3. 打完字点**「确定」按钮**（回车不生效，游戏只接了按钮事件）。
4. 她的回答会逐句出现在头顶气泡里；回答越长气泡停留越久（8–40 秒）。实测：
   > 你说「你好」→ 她答「嗨，你终于来啦，我正坐这儿发呆等你呢。」「今天想干点啥？」
   > （按句号拆成两个气泡，分别停留 10.1s / 8.8s）
5. 想重新开始聊：输入 `/clear`，或者在**游戏设置面板**点「清空对话记忆（已记 N 轮）」。

记忆条数由 `[DeepSeek] HistoryTurns` 控制（默认 6 轮），每次请求只带最近这么多轮，
再往前的不会发出去，所以聊久了也不会越聊越贵。实测第二次按 F 时日志会写
`chat -> '你好' (1 turns of memory)`，她的回答也会引用上一次的内容。

### 联网搜索怎么用

DeepSeek 的 chat 接口本身没有联网能力，所以「联网」是插件这边做的：
把问题丢给搜索引擎取回前几条结果，塞进本次请求的上下文，再让模型据此作答。

- **自动触发**：问题里出现 `最新/今天/现在/新闻/股价/天气/汇率/搜一下/百度一下/latest/news/price` 等词时才搜，
  普通闲聊不联网（省时间也省 token）。
- **想每句都搜**：把 `[Search] SearchAlways` 改成 `true`。
- **完全离线**：设置面板点「联网搜索：开」关闭，或 `EnableWebSearch = false`。
- **搜索源**：`cn.bing.com` → `bing.com` → `so.com` 依次尝试，第一个能出结果的就用
  （实测 Baidu 会给这种客户端返回 1.5 KB 的反爬空页，所以排在后面）。
- 搜不到也不会中断对话，只是这一句没有新资料而已。

### 关闭部分功能

- **设置面板 → `AI 接入：开（点此关闭）`**：整个插件停用，点她完全没反应（不冒泡、不响、不联网）。
- **设置面板 → `坐下按 F 聊天：开（点此关闭）`**：只关掉聊天，单击查余额照常。
- **设置面板 → `聊天要求先坐下：否/是`**：切换是否强制要求她坐着。
- **设置面板 → `联网搜索：开（点此关闭）`**：只关掉联网，其余照常。

---

## 配置

`<游戏目录>\BepInEx\config\com.lenovo.ocdesktop.ocai.cfg`

```ini
[General]
## 总开关（游戏设置面板里也能切）
Enabled = true

[DeepSeek]
ApiKey = sk-...
BaseUrl = https://api.deepseek.com
Model = deepseek-chat

[HeadPat]
## 点她身上多大的范围算数，1.0 = 全身
HeadRegionPercent = 1

[Chat]
## 鼠标指着角色按 F → 和 DeepSeek 聊天
EnableChat = true
## 输入框里的提示语
InputTips = 想和我说什么？
## 单次回答最多多少 token（桌宠建议短一点）
MaxTokens = 400
## 长回答按句子拆开逐句显示（而不是挤成一个大气泡）
SplitLongReplies = true
## 输入框离屏幕底边的距离（像素）。越大越靠上
InputOffsetY = 110
## 输入框离屏幕右边的距离（像素）。越大越靠左
InputOffsetX = 420
## 是否要求「必须先坐下」才响应 F 键。
## 默认 true：靠 Harmony 钩住 OcSittingState.OnEnter/OnExit 来判断坐姿
## （动画名和状态机当前状态都不可用，原因见下方「已知问题」）。
## 改成 false 则无论站着坐着，F 键都能聊。
RequireSitting = true
## 坐姿判定彻底失败时是否放宽（仅在 RequireSitting = true 时有意义）
TreatUnknownPoseAsSitting = true

[Search]
## 允许聊天联网查资料
EnableWebSearch = true
## false = 只在问题看起来需要实时信息时才搜；true = 每句都先搜一遍
SearchAlways = false

[Sound]
## 点她时的提示音
EnableCallSound = true

[Bubble]
## 0 = 自动按体型缩放；填 1.5 可强制按 1.5 倍算
SizeScale = 0
## 气泡离头顶多远
Lift = 55
## 站立时往右偏多少（避免挡脸）
StandOffsetX = 130
## 水平微调总偏移
OffsetX = 0
## 额外下移（一般 0，气泡飘太高时才加）
DropDown = 0

[UI]
## 打开 Key 输入框的按键：MiddleMouse / ScrollLock / Insert / Pause / F8 / None
EditKeyHotkey = MiddleMouse
```

> **`F9` 和 `F8` 不建议用**：很多笔记本上这两个是锁屏 / 飞行模式功能键，按键根本不会送到游戏。

---

## 兼容性

| 项目 | 版本 |
|---|---|
| 游戏 | 角色屋 桌面伙伴 Demo（AppID 5166840） |
| 引擎 | Unity 2022.3.62f3，IL2CPP，metadata v31 |
| BepInEx | 6.0.0-be.788（IL2CPP win-x64） |
| 运行时 | .NET 6.0.7（BepInEx 内嵌） |

---

## 从源码编译

见 [docs/BUILD.md](docs/BUILD.md)。

简言之：需要 .NET SDK 与 BepInEx 的运行时 + interop 程序集作为编译引用。

---

## 已知问题

- **`Class::Init signatures have been exhausted`**：Unity 2022.3 的 metadata v31 是
  Il2CppInterop 的已知脆弱区，本插件实测会出现这条警告。目前功能正常，但它是一条降级路径，
  游戏更新后可能失效。插件已通过 `BepInEx.cfg` 的 `[Logging] UnityLogListening = false` 规避了
  最主要的触发点。
- **气泡位置依赖体型**：自动缩放测的是骨骼世界高度，会随待机动画轻微抖动。
- **气泡可能被顶部裁掉（已修）**：气泡的尾巴（`Point1_Img` / `Point2_Img`）挂在面板**下方**，
  早先顶部只预留了 8px 净空，且没算尾巴高度，角色站得靠上时气泡顶部会被窗口边缘切掉
  （用户反馈「气泡的头已经到外面去了」）。现在顶部预留 = 面板半高 + 45px（尾巴高度），
  该常量在 `BubbleUi.KeepOnScreen` 里，注释标了原因。
  所以提供了 `SizeScale` 手动指定，比自动更稳。
- **设置行的文字**可能显示成模板行原有的文字（取决于游戏是否在只读时缓存了 `settingName`）。
- **聊天输入框的位置：两条互相矛盾的坐标事实（踩坑记录）**，来回错了好几次，记录在此：
  - `TipsManager.ShowInputTxt(pos, ...)` 的 **pos 参数根本不移动面板**（只影响指针轨迹之类），
    实测传什么都一样。所以面板位置改由插件自己接管：持续写 `RectTransform.position`。
  - 面板所在层与**窗口呈现的垂直方向是反的**：把面板放到层坐标 y=272
    （`RectTransform` 报 "BOTTOM-RIGHT"）时，屏幕上看到的是**右上角**。
    因此 `[Chat] InputOffsetY`（距屏幕底边）在写入前会被镜像一次，代码里标注了 `VERTICAL FLIP`。
  - 早先还有个"拖动前先判断鼠标是否落在面板上"的检查，因为两套坐标原点不一致而**永远不成立**，
    表现就是"拖不动"，已删除；现在靠 10px 位移阈值区分点击与拖动。
  - 微调方式：改 `[Chat] InputOffsetY`（值大=更高）/ `InputOffsetX`（值大=更左），
    或者打开输入框后用鼠标直接拖。
- **「必须先坐下」的判定**：这一条踩了几个坑，记录在这里省得再试一遍。
  直接读坐姿的两条常规路都走不通：
  - **Spine 动画名**：坐下时动画名**依然是 `new/standing-e`**，没换 clip，
    所以「当前动画是不是 sit」永远得到 false。
  - **状态机当前状态**：`GetCurrentStateType()` 返回的 `Il2CppSystem.Type` 的 `ToString()`
    是字面量 `"Il2CppSystem.Type"`；`GetCurrentState()` 返回的 wrapper 是按**声明返回类型**
    `IState` 生成的，`.GetType().Name` 恒为 `IState`，拿不到 `OcSittingState`。
  - **现在的做法**：用 Harmony 给 `OcSittingState.OnEnter()` / `OnExit()` 挂 postfix，
    维护一个深度计数，「坐着」= 深度 > 0。这是唯一不依赖推断的信号。
    日志里会写 `sitting hooks: enter=True exit=True` 以及每次进出事件。
    如果哪天钩子失效，`RequireSitting` 会自动退回到 clip / 姿态判定，
    再由 `TreatUnknownPoseAsSitting` 决定读不出来时是否放行；
    实在判定不了就把 `RequireSitting` 关掉，F 键仍能正常聊天。
- **游戏会改写系统显示模式（已加守护）**：这是踩过最大的一个坑，会导致**桌面图标被重排**。
  游戏自带一整套窗口管理（`WindowDisplaySync.ApplyResolution(int,int)` / `SyncToSize()`、
  `DisplayManager.MoveToDisplay` / `SwitchDisplayAndSync` / `SwitchWindowedAndSync`、
  `TransparentWindowNew` 的 click-through），它会改分辨率并把
  `HKCU\Control Panel\Desktop\LogPixels` 写脏。实测抓到的现场：
  ```
  EnumDisplaySettings → 1920x1080, dmLogPixels=120   (125% 缩放)
  .NET 逻辑桌面尺寸  → 1536x864                       (1920/1.25)
  HKCU LogPixels     → 288                            (300%，异常脏值)
  ```
  逻辑尺寸一变，Windows 立刻重排桌面图标。**注意不能用 `Screen.width` 监测**：
  游戏的画布始终渲染在 1920x1080，`Screen.width` 一直报 1920，所以早先那版守护
  永远不触发（日志里 `restores=0`）。现在的做法是插件用 Win32 `EnumDisplaySettings`
  读**真实系统模式**作为基线，每秒比对，一旦被改就把基线写回注册表并告警，
  设置面板里也有 `保护桌面显示：开/关` 开关（`[Display] ProtectDesktopDisplay`）。
  缩放的注册表修改**需要注销/重启才完全生效**，这也是"图标还有一些没恢复"的原因。
- **不要重复启动游戏**：这个游戏自己接管透明窗口 + 分辨率同步
  （`TransparentWindowNew` / `WindowDisplaySync` / `DisplayManager`）。
  同时跑两个实例可能让那块不透明窗口层盖住整个桌面，表现为**全屏黑屏**。
  实测踩过一次，结束进程即恢复；必要时重启 `explorer.exe`。

---

## 鸣谢

- 叫声的音色设计参考自 [dsh-whale-girl-pet](https://github.com/yanzwzz/dsh-whale-girl-pet)（MIT）——
  它用 WebAudio 现场合成提示音，本项目把同样的正弦琶音方案移植成了 WAV 生成。
- 鲸鱼娘形象来自 [dafeiyu-pet](https://github.com/1190fasheqi/dafeiyu-pet)（MIT）。

## License

MIT
