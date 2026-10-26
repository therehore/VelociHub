# GitHub 描述文案（中英对照）

直接复制对应段落即可。

---

## 1. 仓库 About 一栏（短描述，限 350 字符）

**中文（推荐，约 55 字）**

```
名龙星球 MX300 无线鼠标的轻量本地控制台：托盘常驻，原生 HID 直读电量与 DPI，切档弹 OSD，支持充电上限保护。免安装单文件，非官方。
```

**English（about 105 chars）**

```
A lightweight tray console for the MLOONG MX300 wireless mouse — native HID battery/DPI, on-screen OSD, charge-limit guard. Portable exe. Unofficial.
```

**中英混排（GitHub 单行最省事，约 160 字符）**

```
MX300 无线鼠标的轻量本地控制台 · Lightweight tray console for the MLOONG MX300 mouse — native HID battery & DPI, OSD, charge-limit guard. Portable, unofficial.
```

---

## 2. 一句话简介（用于 awesome 列表、论坛签名、分享卡片）

**中文**

> 让你的 MX300 在托盘里显示电量、一键切 DPI、切档时弹 OSD —— 一个 25 MB 内存的免安装小工具。

**English**

> Battery in the tray, one-click DPI, an OSD when you switch gears — a portable 25 MB tool for the MX300.

---

## 3. 完整描述（用于 Release 说明、产品页、投稿长文）

**中文**

> **Veloci Hub** 是给名龙星球 MX300 无线鼠标写的轻量本地控制台。它常驻系统托盘，直接通过原生 HID 报文与鼠标通信，不经过厂商驱动，也不需要后台服务。
>
> 核心能力：托盘图标实时反映电量与充电状态；面板内可一键切换 DPI 档位，用鼠标实体 DPI 键切换后 1 秒内自动同步；切档、低电量、充电到上限时在屏幕角落弹出带开场动画的 OSD；充电上限保护会在电量达到设定阈值时提醒你拔线，避免长期满电损耗电池。
>
> 外观支持深浅两套主题、0–100 背景不透明度、以及把软件图标换成自己的图片。后台默认每 2 分钟轮询一次电量，面板打开或鼠标悬停时自动提速到 5 秒，兼顾实时性和功耗。
>
> 单文件自包含，不需要安装 .NET 运行时，解压即用。纯本地运行，不联网、无遥测。**本项目为非官方作品，与鼠标厂商无任何关联。**

**English**

> **Veloci Hub** is a lightweight local console for the MLOONG MX300 wireless mouse. It lives in the system tray and talks to the mouse directly over native HID reports — no vendor driver, no background service.
>
> What it does: a tray icon that reflects battery level and charging state in real time; one-click DPI switching from the panel, with changes made via the mouse's physical DPI button synced back within a second; an on-screen OSD with an intro animation for gear changes, low battery, and charge-limit reminders; and a charge-limit guard that nudges you to unplug at a configurable threshold (55%–95%) so the battery isn't parked at 100% all day.
>
> Appearance is configurable: dark and light themes, background opacity from 0 to 100, and a custom image in place of the built-in logo. Battery polling runs every 2 minutes in the background and speeds up to 5 seconds while the panel is open or the tray icon is hovered, balancing freshness against power draw.
>
> Shipped as a single self-contained executable — no .NET runtime to install, just unzip and run. Fully offline: no network calls, no telemetry. **This is an unofficial project and is not affiliated with the mouse manufacturer.**

---

## 4. Topics（仓库标签，GitHub 上限 20 个）

```
windows  wpf  csharp  dotnet  hid  mouse  dpi  battery  osd  tray-icon
desktop-app  portable  hardware  mx300  unofficial
```

---

## 5. 免责声明（英文版，可与中文并列放 README 底部）

```
This project is unofficial and is not affiliated with, endorsed by, or sponsored by
MLOONG or any hardware manufacturer. Product names and trademarks belong to their
respective owners and are used here only for descriptive, interoperability purposes.

The software is provided "as is", without warranty of any kind. Use it at your own
risk; the authors are not liable for any damage to hardware, data, or warranty status.
No vendor firmware or driver is redistributed.
```
