# 第三方组件声明

Veloci Hub 自身代码以 [MIT](LICENSE) 协议发布。以下为随程序一起分发或链接的第三方组件及其许可，均与本项目许可兼容。

## 运行时（仅在自包含发布中包含）

| 组件 | 版本 | 许可 | 说明 |
| --- | --- | --- | --- |
| .NET Runtime（Microsoft.NETCore.App） | 6.0.x | MIT | 通用运行时 |
| .NET Desktop Runtime（Microsoft.WindowsDesktop.App） | 6.0.x | MIT | WPF / WinForms 桌面框架 |

版权归 .NET Foundation 与 Microsoft Corporation 所有。许可全文：<https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>

自包含发布时这些 DLL 会被打进 `VelociHub.exe`（单文件自解压），属于 MIT 允许的再分发行为；本声明与 MIT 许可文本随包提供即可满足署名要求。

## 系统接口

- `hid.dll`、`kernel32.dll`、`user32.dll`、`dwmapi.dll`、`shcore.dll` 等 Windows 系统库通过 P/Invoke 调用，**不随程序分发**，不受开源协议约束。

## 开发工具（不随程序分发）

- .NET SDK 6.0 —— MIT
- GitHub Actions 官方 Action：`actions/checkout`、`actions/setup-dotnet`、`actions/upload-artifact`、`softprops/action-gh-release`

## 本项目未使用

- 任何 GPL / AGPL / LGPL 代码或库
- 任何厂商 SDK、驱动程序或其反编译产物
- 任何需要署名的第三方 UI 控件库、图标字体或图片素材

如你认为此处遗漏了某项依赖，请提 issue 指出。
