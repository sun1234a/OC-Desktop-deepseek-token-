# ocdesktop-mod 构建与发布说明

这是 OC DESKTOP（角色屋 桌面伙伴 Demo）的 BepInEx 插件 `OcAI` 的源码工程。

## 编译

SDK 用的是仓库自带的离线副本 `tools\dotnet\dotnet.exe`，不需要联网：

```powershell
$root = "C:\Users\Lenovo\Desktop\ocdesktop-mod"
$env:DOTNET_CLI_HOME   = "$root\.dotnet-home"      # 避免写用户目录（沙箱会拒绝）
$env:APPDATA           = "$root\.dotnet-home\AppData\Roaming"
$env:LOCALAPPDATA      = "$root\.dotnet-home\AppData\Local"
$env:NUGET_PACKAGES    = "$root\tools\nuget"

& "$root\tools\dotnet\dotnet.exe" build "$root\src\OcAI\OcAI.csproj" -c Release
```

产物：`src\OcAI\bin\Release\OcAI.dll`

## 发布前必须做的一步：RefCheck

```powershell
& "$root\tools\dotnet\dotnet.exe" "$root\tools\RefCheck\bin\Release\net8.0\RefCheck.dll" `
    "$root\src\OcAI\bin\Release\OcAI.dll"
```

期望输出结尾是 `OK: no suspicious type bindings`，并且引用表里 `System.*` 全部是 **6.0.0.0**。

**为什么必须有这一步**：这个插件被"编译能过、进游戏加载不了"坑过一次。用 .NET 8 的引用程序集编译时，编译器把
`System.Runtime.InteropServices.RuntimeInformation` 绑定到了 `System.Runtime`；而 .NET 6 运行时里这个类型住在
`System.Runtime.InteropServices.RuntimeInformation.dll`，于是 BepInEx 直接放弃加载整个插件：

```
[Error : BepInEx] Error loading [OC AI Companion 0.2.0]:
System.TypeLoadException: Could not load type
'System.Runtime.InteropServices.RuntimeInformation'
from assembly 'System.Runtime, Version=6.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a'.
   at OcAI.Plugin.Load()
```

表现是"插件整个消失、气泡完全没有了"，而不是某段逻辑出错。

## 编译引用程序集是从哪来的

本机 SDK 只带了 8.0 的 targeting pack，而且硬要 `Microsoft.NETCore.App.Ref 6.0.36`；
`src\OcAI\nuget.config` 故意没有配置任何远程源，离线 feed 里又只有 6.0.0，所以标准 net6.0
还原必然 NU1100 / NU1102。

解决方式（见 `src\Directory.Build.props`）：

1. 从离线 feed 的 nupkg 里解出真正的 net6.0 引用程序集到 `refpack\`
   （`microsoft.netcore.app.ref` / `windowsdesktop` / `aspnetcore`，各取 `ref/net6.0/*.dll`）。
2. `DisableImplicitFrameworkReferences=true` 关掉 SDK 的框架引用（顺带绕开那次不可能的下载）。
3. 把 `refpack\*.dll` 作为 `Reference` 显式加进来，语言版本钉在 C# 9（.NET 6 的 C# 版本）。

在 6.0 targeting pack 齐全（或有可用 NuGet 源）的机器上，直接 `-p:TargetFramework=net6.0`
按常规方式编译即可。

## 部署

插件的加载位置：

```
D:\SteamLibrary\steamapps\common\角色屋 桌面伙伴 Demo\BepInEx\plugins\OcAI.dll
```

游戏运行时该文件被占用，覆盖会失败。做法是先复制成 `OcAI.new.dll`，再改名替换：

```powershell
$plug = "D:\SteamLibrary\steamapps\common\角色屋 桌面伙伴 Demo\BepInEx\plugins"
Copy-Item "$root\src\OcAI\bin\Release\OcAI.dll" "$plug\OcAI.new.dll" -Force
Rename-Item "$plug\OcAI.dll" "OcAI.prev.dll"
Rename-Item "$plug\OcAI.new.dll" "OcAI.dll"
Remove-Item "$plug\OcAI.prev.dll" -Force
```

改名只是让**下次启动**加载新版本；插件 DLL 只在游戏启动时读取，改完必须重启游戏。

同一目录里**不要留任何其它 `*.dll`**，否则 BepInEx 会把它们也当插件加载。

## 运行时日志

| 文件 | 内容 |
| --- | --- |
| `BepInEx\LogOutput.log` | BepInEx 自己的加载日志，`Error loading [...]` 出现在这里 |
| `BepInEx\plugins\ocai_debug.txt` | 插件诊断输出（气泡定位、头摸、说话等），由 `[Debug] VerboseLogging` 控制 |

排查"气泡不见了"时先看 `LogOutput.log` 有没有 `Error loading`：有的话是加载失败，不是显示逻辑问题。
