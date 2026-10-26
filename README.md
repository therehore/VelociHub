# Waaagh Hub

名龙星球 MX300 无线鼠标的轻量本地控制台 —— 一个托盘小工具，用原生 HID 报文读取电量、切换 DPI 档位，并在屏幕上弹出 OSD 提示。

> 非官方项目，与鼠标厂商及任何游戏厂商均无关联。详见下方[免责声明](#免责声明)。

## 功能

- **电量与充电状态**：托盘图标实时反映电量，充电中/已充满单独标注
- **DPI 档位切换**：面板内一键切档，鼠标实体 DPI 键切换后 1 秒内同步到面板
- **OSD 提示**：切档、低电量预警、充电上限提醒时在屏幕角落淡入淡出，带开场动画
- **充电上限保护**：达到设定阈值（55%–95%）时提醒取下充电线，保护电池
- **低电量预警**：两级阈值，可配置是否在充电时也提醒
- **外观**：深色/浅色主题、背景不透明度 0–100、软件图标可换成自定义图片
- **两级轮询**：后台默认 2 分钟一次，面板打开或鼠标悬停时提到 5 秒，兼顾实时性与功耗

## 环境要求

| 项 | 要求 |
| --- | --- |
| 系统 | Windows 10 1809+ / Windows 11，x64 |
| 运行时 | [.NET 6 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/6.0)（用自包含发布方式则可免装） |
| 构建 | .NET 6 SDK |
| 设备 | 名龙星球 MX300（VID `0xA8A4`–`0xA8A7`），2.4G 接收器或直连均可 |

除 MX300 外的设备不会被识别，程序会一直显示"未连接"。

## 构建

```bash
git clone https://github.com/<your-name>/WaaaghHub.git
cd WaaaghHub
dotnet build -c Release
dotnet run -c Release
```

打包成免安装的单文件 exe（约 62 MB，含运行时）：

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none \
  -o dist
```

## 使用

程序启动后只驻留托盘，双击托盘图标打开面板。

**命令行参数**

| 参数 | 说明 |
| --- | --- |
| `--show` | 启动后立即展开面板 |
| `--osd` | 立刻弹一次 OSD（调试外观用） |
| `--logo <路径>` | 设置自定义软件图标 |
| `--logo default` | 恢复默认 W 标 |
| `--climit` | 弹一次充电上限提醒 |
| `--flip` | 弹一次翻页/模式切换提示 |

**本地文件位置**：`%APPDATA%\MX300Hub\`

- `settings.json` — 全部设置项
- `brand.png` — 自定义图标（≤256px，选图时自动缩放转码）
- `app.log` — 运行日志（超过 512 KB 自动重建）

这些文件不会入库，删除目录即恢复出厂设置。

## 项目结构

```
├─ App.xaml(.cs)            应用入口、托盘、轮询节奏、命令行参数
├─ Hid/
│  ├─ HidDeviceFinder.cs    按 VID + 厂商控制集合定位设备
│  └─ HidChannel.cs         Overlapped 异步读写通道
├─ Protocol/Mx300Protocol.cs 报文编解码（电量、DPI、心跳、状态）
├─ Services/
│  ├─ DeviceService.cs      轮询调度、事件分发、状态缓存
│  ├─ AppSettings.cs        设置持久化与版本迁移
│  ├─ BrandIcon.cs          自定义图标载入/缩放/落盘
│  ├─ AppPaths.cs           本地目录约定
│  └─ TrayService.cs        托盘图标与右键菜单
├─ UI/
│  ├─ FlyoutWindow.xaml     主面板
│  ├─ OsdWindow.xaml        OSD 弹窗
│  ├─ Theme.xaml            控件样式与调色板
│  ├─ Backdrop.cs           亚克力背景
│  └─ Controls/             电量环、品牌标（自绘控件）
└─ Native/NativeMethods.cs  Win32 / HID P-Invoke
```

## 实现说明

设备没有公开的 SDK，报文格式通过抓包与逆向得到：

- 走 Windows 原生 HID（`hid.dll` + `kernel32`），不依赖任何厂商驱动或后台服务
- 厂商控制集合为 `UsagePage 0xFF01 / Usage 0x10`，输出报文长度 ≥ 8
- 异步读写全程使用非托管缓冲区（`Marshal.AllocHGlobal`），避免重叠 I/O 期间托管数组被移动导致的堆损坏
- 鼠标实体 DPI 键**不主动上报报文**，因此 DPI 档位靠心跳报文中的活跃档位字段 + 2500 ms 兜底回读来同步

协议细节可能随固件版本变化。若新版固件不兼容，欢迎提 issue 并附上 `app.log`。

## 免责声明

- 本项目为个人非官方工具，**与名龙星球（及任何鼠标厂商）无任何隶属、授权或背书关系**。
- 程序仅通过本机 HID 接口读写鼠标状态，**不联网、不上传任何数据、不含遥测**。
- 报文格式来自公开可观察的 USB 流量逆向，可能触发厂商不认可的用法；由此产生的设备异常、数据丢失或保修问题，由使用者自行承担。
- "Waaagh" 一词及 Warhammer 相关名称、标识的商标权归 Games Workshop 所有；"MX300" 及相关商标归其各自权利人。本项目仅作技术演示，**不得用于商业用途**，如权利人提出异议将立即更名。
- 代码按 MIT 协议提供，不附带任何担保。

## 许可证

[MIT](LICENSE)
