# OcAI — 角色屋 桌面伙伴 · DeepSeek 余额桌宠插件

给 Steam 游戏 **《角色屋 桌面伙伴 Demo》**（AppID 5166840）写的 BepInEx 插件：

**点一下桌宠 → 她叫一声 → 头顶冒气泡显示你 DeepSeek 账户的剩余余额。**

> 基于 BepInEx 6 (IL2CPP) 开发。游戏是 Unity 2022.3.62f3 + IL2CPP（metadata v31）。

---

## 功能

| 功能 | 说明 |
|---|---|
| 💰 **点她查余额** | 调 DeepSeek 官方 `GET /user/balance`，气泡显示真实剩余额度 |
| 🐋 **叫声** | 正弦波合成音，成功走上行 C 大调琶音（G5→C6→E6→G6），失败走下行 A 小调 |
| 💬 **头顶气泡** | 克隆游戏自带的 `UI.DialogueBubble`，用原生美术；自动适应文字长度换行 |
| 🎛️ **游戏内设置** | 在游戏设置面板注入 `AI 接入：开/已关闭` 开关行，一键停用整个插件 |
| ⚙️ **Key 输入** | 复用游戏自己的输入弹窗，支持中文输入法 |
| 📐 **体型自适应** | 1x / 1.5x / 2x 体型下气泡间距自动缩放 |
| 🧍 **姿态区分** | 站立时气泡偏到她头右侧不挡脸；趴屏时回到正上方 |

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
| **点她身上任意位置** | 叫一声 + 头顶冒余额气泡 |
| **鼠标中键** | 打开 Key 输入框 |
| **`Ctrl+←` / `Ctrl+→`** | 站立时气泡左右微调（自动保存） |
| **`Ctrl+↑` / `Ctrl+↓`** | 气泡高度微调（自动保存） |

### 关闭插件

**游戏设置面板 → `AI 接入：开（点此关闭）`** → 点一下即停用，**点她完全没反应**（不冒泡、不响、不联网）。
再点一下恢复。

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
  所以提供了 `SizeScale` 手动指定，比自动更稳。
- **设置行的文字**可能显示成模板行原有的文字（取决于游戏是否在只读时缓存了 `settingName`）。

---

## 鸣谢

- 叫声的音色设计参考自 [dsh-whale-girl-pet](https://github.com/yanzwzz/dsh-whale-girl-pet)（MIT）——
  它用 WebAudio 现场合成提示音，本项目把同样的正弦琶音方案移植成了 WAV 生成。
- 鲸鱼娘形象来自 [dafeiyu-pet](https://github.com/1190fasheqi/dafeiyu-pet)（MIT）。

## License

MIT
