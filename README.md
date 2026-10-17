# HTC Vive 7invensun aGlass Module

VRCFaceTracking eye-tracking module for HTC Vive / 7invensun aGlass. Connects directly to an already-running aGlass Runtime 1.1 over local TCP/UDP; an optional OSC bridge input is available. Direct mode needs neither Python nor SteamVR.

面向 HTC Vive / 7invensun aGlass 的 VRCFaceTracking 眼动模块。**0.2.0 默认直连已运行的 aGlass Runtime 1.1**，提供左右眼视线与眨眼估算，只占用眼动槽位，可与嘴部追踪模块配合使用。

```text
aGlass → aGlass Runtime 1.1 → 本机 TCP/UDP → 本模块 → VRCFaceTracking → VRChat
```

项目主页：[HazukiKaguya/HTC-Vive-7invensun-aGlass-Module](https://github.com/HazukiKaguya/HTC-Vive-7invensun-aGlass-Module)

## 安装与启动

1. 按 [VRCFT 入门指南](https://docs.vrcft.io/docs/intro/getting-started) 配置 VRCFaceTracking、VRChat OSC 和支持相应眼动参数的模型。
2. 从本项目的 [Releases](https://github.com/HazukiKaguya/HTC-Vive-7invensun-aGlass-Module/releases) 下载 **`aGlassVRCFT-0.2.0.zip`**，在 VRCFT 的 Module Registry 中选择 **Install module from .zip**。若尚无 Release，可按下方步骤从源码构建。GitHub 自动生成的 `Source code (zip)` 是源码包，不能作为模块安装包导入。
3. 启动并校准 aGlass Runtime 1.1，确保设备及配套 USB Key 授权正常，再重新加载本模块或正常退出后重开 VRCFT。
4. 确认模块目录的 `aGlassVRCFT.json` 包含 `"Source": "runtime"`。日志应出现 `aGlass Runtime direct`。暂停其他占用眼动槽位的模块，嘴部模块可继续使用。
5. 直连时不要再启动 aGlass2OSC GUI 的“开始桥接”，避免两个客户端同时控制同一个 Runtime。若使用单独的 Key 服务，只需保持该服务运行。

安装包根目录包含以下五个文件，无额外目录层级：

```text
aGlassVRCFT.dll
aGlassVRCFT.json
module.json
README.md
LICENSE
```

本模块不启动或结束 Runtime，不加载 aGlass SDK DLL，也不安装设备驱动。**无需为使用模块安装 Python 或开发用 .NET SDK**；VRCFT 自身运行环境由其安装方式提供。`VRCFaceTracking.Core.dll` 由宿主提供，请勿另行复制到模块目录。

升级前备份自己修改过的 `aGlassVRCFT.json`，正常卸载／更新旧模块，避免两份 DLL 同时加载。ModuleId 固定为 `8ea02335-197a-4822-81ed-3e414bf4a1e3`。旧配置缺少 `Source` 时仍使用 `bridge`；新发布包明确设置 `runtime`。修改配置后需要重新加载模块。

## 兼容性与验证范围

| 项目 | 当前范围 |
| --- | --- |
| 使用平台 | Windows；Runtime 自动发现读取当前用户注册表 |
| 直连协议 | aGlass Runtime 1.1；支持协议中的左眼、右眼和双眼模式 |
| 旧 Runtime / DK II | 不保证其网络协议兼容；可通过独立桥接程序转为下述 OSC 输入 |
| VRCFT 接口 | 使用官方 5.2.3.0 源码的 Core API，程序集版本为 5.1.1.1，模块目标框架为 .NET 7 |
| 宿主实测 | VRCFT 5.4.5.0 已成功加载 0.2.0 并建立 Runtime 直连 |
| SteamVR | 直连使用可调 FOV，无需 SteamVR，可用于桌面模式；效果仍取决于模型及 VRCFT 输出配置 |

实机验证已确认 Runtime 会话和数据包接收，但当时采样中的视线均被设备标为无效。因此目前不能据此声称完成了“有效真机视线 → VRCFT → VRChat 模型”的完整验收。自动测试用有效模拟视线验证了模块写入真实 VRCFT Core 眼动字段的流程。

## 眼睑、单眼和断流行为

本版直连协议使用视线与眼睛有效性信息，**闭眼来自估算，不是原生连续眼睑开合度测量**。`EstimateBlink` 默认开启；只有曾成功追踪的眼睛，持续失效超过去抖时间后才估算闭合。若眼睛仍可见但视线无效，不将其当作眨眼。长时间丢失追踪或数据过期会恢复正视、睁眼。

双眼模式分别处理左右眼；单眼设备，或另一只眼尚未有效追踪时，可用眼驱动两只模型眼睛。已经追踪过的另一眼数据过期时，借用有效眼的视线并保持该眼睁开。模块不生成设备未提供的嘴部表情或瞳孔直径。

## 配置

编辑模块 DLL 旁的 `aGlassVRCFT.json`，然后重新加载模块。默认配置已包含所有字段。

| 字段 | 默认值 | 作用 |
| --- | --- | --- |
| `Source` | `runtime` | `runtime` 直连，或 `bridge` 接收本机 OSC |
| `RuntimePort` | `0` | 自动读取唯一 aGlassRuntime 进程对应的 `HKCU\SOFTWARE\aGlassRuntime\Service` 中 `ListenPort`；也可明确填写 TCP 端口 |
| `EventPort` / `DataPort` | `0` / `0` | 自动分配两个本机 UDP 端口；指定时须互不相同 |
| `FovX` / `FovY` | `100` / `100` | 对称视场角，单位度；近似映射，不读取 SteamVR 投影或镜片畸变 |
| `CenterX` / `CenterY` | `0.5` / `0.5` | 归一化注视点中心 |
| `GainX` / `GainY` | `1` / `1` | 水平、垂直增益 |
| `InvertX` / `InvertY` | `false` / `false` | 翻转对应方向 |
| `SmoothingMs` | `25` | 平滑时间常数，`0` 关闭 |
| `StaleMs` | `250` | 新时间戳数据的有效期；重复包不延长有效期 |
| `EstimateBlink` | `true` | 根据有效性估算眨眼 |
| `BlinkDelayMs` / `BlinkMaxMs` | `30` / `600` | 连续失效去抖和最长估算时间 |
| `CommandTimeoutMs` / `StopTimeoutMs` | `5000` / `1500` | 单条控制命令超时和退出清理总时限 |
| `Port` / `TimeoutMs` | `9015` / `500` | 仅桥接模式：本机 OSC 接收端口、数据有效期 |

## 可选桥接模式

将 `Source` 改为 `bridge` 并重新加载模块，再启动支持此协议的独立桥接程序。本仓库不包含 aGlass2OSC GUI、原生 SDK、Runtime 安装包或 Key 工具。

桥接程序向 `127.0.0.1:9015`（或配置的 `Port`）发送 OSC **单条消息**：

```text
地址：/aglass/v1/eyes
类型：,Tffffff 或 ,Fffffff
数据：有效标记、左 X、左 Y、左开合、右 X、右 Y、右开合
```

OSC 的 `T` / `F` 类型标签本身携带布尔值，没有额外布尔数据字节。六个数值为大端 32 位浮点数；X/Y 范围为 `[-1, 1]`，开合为 `[0, 1]`。`F`、数据超时均恢复中性眼动。非有限数值、错误长度、错误地址或类型标签会被拒绝。`tests/osc_simulator.py` 提供标准库编码示例。

如已另行安装 aGlass2OSC，请在其目录使用该项目的 `--target vrcft-module` 选项及对应设备配置；相关程序和依赖不属于本模块仓库。

## 从源码构建、测试和打包

开发环境：**Python 3.10+、.NET 8 SDK、首次构建可访问 GitHub 和 NuGet 的网络**。Python 仅使用标准库，不需要 `pip install`。`global.json` 选择已安装的 .NET 8 SDK；模块继续面向 .NET 7 API，测试程序运行于 .NET 8。

在仓库根目录执行：

```powershell
python tools/build.py
```

脚本依次执行元数据回归测试、获取并校验固定版本依赖、构建官方 Core 和模块、运行协议及生命周期测试，最后生成：

```text
release/aGlassVRCFT-0.2.0.zip
release/aGlassVRCFT-0.2.0.zip.sha256
```

依赖源码固定为官方 VRCFaceTracking 5.2.3.0 的提交 `ad06f2967a2243d85ad161a397eb911522182c15`。源码压缩包和 NuGet 包均检查 SHA-256，版本与校验值位于 `tools/build.py`。下载、还原及测试缓存保存在本仓库 `.build/`；构建输出在 `bin/`、`obj/` 和 `tests/bin/`。这些路径均被 Git 忽略，不依赖相邻的 aGlass2OSC 仓库或本机 VRCFT 安装目录。

自动测试使用本机随机端口的独立 Python TCP/UDP 模拟器，覆盖 OSC 拒绝规则、视线与眨眼、真实 Core 字段写入、断流、重复时间戳、分片 TCP 回复、CRC 错误、控制超时和失败后的清理顺序。它不会连接真实 Runtime 或开启 VRChat OSC。单独重跑已构建的测试：

```powershell
python tests/test_release.py
python tests/check_module.py
```

构建会检查 `module.json` 的版本、DLL 文件名、ModuleId 和项目页面网址，并再次检查打包结果。此前遗漏 `ModulePageUrl` 导致过 VRCFT 模块详情页崩溃；此字段固定为本项目 GitHub 地址。

仓库附带 GitHub Actions 工作流，在推送、PR 或手动触发时构建测试并保存安装包为工作流 artifact；**不会自动创建 GitHub Release**。发布时将上述 ZIP 与校验文件作为 Release 附件上传即可。版本更新须同步 `.csproj`、`module.json`、变更记录和文档中的版本号。

## 排错

- **无法发现 Runtime**：确认只运行一个 aGlassRuntime，或明确配置 `RuntimePort`。模块不会自动拉起 Runtime。
- **授权或初始化返回错误**：先确认 Runtime 本身可正常使用设备和配套授权，再重新加载模块。
- **已经连接但模型不动**：查看是否出现 `aGlass has no fresh tracking data`；检查校准、佩戴、有效视线、眼动槽位及模型参数。收到 UDP 包并不等于有有效视线。
- **控制连接断开**：恢复 Runtime 后手动重新加载模块；本版本不自动重连。
- **退出出现 7008 错误**：已观察到 Runtime 返回 `-1` 的情况；模块会记录警告并继续其余清理命令，不结束 Runtime。
- **构建提示找不到 SDK 或 Core**：安装 .NET 8 SDK 后重新打开终端，并使用 `python tools/build.py` 完成依赖准备。仅运行 `dotnet build` 不会自动下载官方 Core。

## 许可与关联

本模块使用 [MIT License](LICENSE)。VRCFaceTracking 及 aGlass 的名称、第三方软件和硬件归各自权利人所有。本项目是社区模块，并非 HTC、7invensun 或 VRCFaceTracking 官方产品。安装包不包含 VRCFT Core、aGlass SDK、Runtime 或授权数据。
