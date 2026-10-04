# SoD2SE Loader

[English README](README.md)

SoD2SE Loader 负责启动《腐烂国度 2》、等待实际游戏进程，并加载兼容的 SoD2SE DLL 插件。本仓库收录加载器及其编译所需的 Core、Game API 源码；不包含游戏文件、玩法插件 DLL、MCM 原生渲染组件或 MO2 游戏支持插件。

当前 Game API 的目标游戏构建号是 **16535856**。加载器可以启动其他构建的游戏，但内存补丁和原生 Hook 只有在目标版本严格匹配时才能使用。项目仍为预览版，与 Undead Labs、Microsoft 无官方关联。

## 编译与离线检查

需要 Windows x64、PowerShell 和 .NET Framework 4.x 的 C# 编译器 `csc.exe`。在 PowerShell 中运行：

```powershell
& .\build.ps1
& .\test.ps1
```

输出位于 `artifacts\bin`，包含 `SoD2SE.Loader.exe`、`SoD2SE.Core.dll`、`SoD2SE.GameApi.dll`。`test.ps1` 会重新编译，运行加载器自检，以及在临时目录中测试 MCM 配置和中英文自动检测；不会启动或附加游戏。若已安装 MO2，还可以运行[可选的 USVFS 测试](Tests/README.md)，它使用模拟子进程。

## 安装与使用

将上述三个文件放在含有 `StateOfDecay2.exe` 的游戏根目录。兼容的玩法插件另行安装到 `Plugins\`。单独安装加载器不会改变游戏玩法。

双击 `SoD2SE.Loader.exe` 时，它默认直接创建游戏启动程序，随后等待真正的游戏进程。正常启动不会弹出命令行窗口；日志写在加载器旁的 `SoD2SE-launch.log`，若目录不可写则写到系统临时目录。加载器不会调用 Steam 的 `-applaunch`，但 Steam 版游戏自身仍可能要求客户端已就绪。

下面两项不会启动游戏，可用于先检查构建和路径：

```powershell
& .\artifacts\bin\SoD2SE.Loader.exe --self-test
& .\artifacts\bin\SoD2SE.Loader.exe --diagnose-launch
```

常用参数：

| 参数 | 作用 |
| --- | --- |
| `--game-exe <路径或目录>` | 指定游戏主程序或游戏根目录。 |
| `--game-args=<参数>` | 原样传递游戏启动参数；可传空值。 |
| `--direct-main` | 直接创建 Win64 主程序。 |
| `--attach` | 附加已运行的游戏并加载插件。 |
| `--console` | 打开诊断控制台。 |
| `--mo2` | 保持 MO2 启动进程和 USVFS 环境，并检查游戏进程是否继承 USVFS。 |

通过 MO2 启动时，还需要兼容的《腐烂国度 2》MO2 支持插件，向加载器提供 `--mo2 --direct-main` 和经 MO2 管理的游戏主程序路径。MO2 支持插件不在本仓库内。

## 源码结构

| 目录 | 内容 |
| --- | --- |
| `Loader/` | 启动、进程跟踪、插件发现、日志和 MO2 环境检查。 |
| `Core/` | 插件 ABI、配置、运行时服务和补丁事务。 |
| `GameApi/` | 固定游戏版本的能力声明与补丁信息。 |
| `Tests/` | 离线自检和可选 MO2 USVFS 测试。 |

`Core/SoD2SE.Core.cs` 中的 `FrameworkInfo.Version` 是三个程序集共用的唯一版本来源。旧的加载器自绘 MCM 覆盖层已经停用，因此未收入仓库；MCM 现为独立插件。`.gitignore` 排除了编译产物、日志、用户配置、本机游戏路径提示和压缩包。

这份源码已通过离线检查，但尚未作为新的公开二进制版本完成实机验收。独立 MCM 插件的原版设置页也仍需游戏内验收。若要发布可执行文件，应将三个匹配版本的运行文件放在同一发行包，并确认目标游戏和 MO2 配置下的实际运行结果。

目前没有替本仓库指定开源许可证。确定允许他人如何使用、修改和再发布代码后，再添加 `LICENSE` 文件；公开仓库本身不等于授予复用权限。


## 开发、离线验证与打包

Windows x64、PowerShell 7 和 .NET Framework 4.x C# 编译器（`csc.exe`）。

独立 checkout 使用本仓库维护的入口：

```powershell
.\build.ps1
.\test.ps1
.\package.ps1
```


在 workspace 根目录，`Automation/dev.ps1` 会调用同一套产品入口：

```powershell
.\Automation\dev.ps1 build SoD2SE-Loader
.\Automation\dev.ps1 check SoD2SE-Loader
.\Automation\dev.ps1 test SoD2SE-Loader
.\Automation\dev.ps1 package SoD2SE-Loader
```

构建、测试和打包入口不会启动或附加到游戏；可复现产物写入忽略的 `.work`。 版本来源、tag 约定、验收材料和发布输入见 [RELEASE.md](RELEASE.md)。
