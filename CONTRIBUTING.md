# 参与贡献

感谢愿意帮忙。本项目是一个单文件 WPF 小工具，改动成本低，但也直接写硬件参数，所以对"可复现、可回滚、有日志"要求比较严。

## 提 Issue 之前

1. 先跑一次自检，把结果贴上来：

   ```cmd
   充电限制工具.exe --selftest
   ```

   它会在 `%LOCALAPPDATA%\HuaweiChargeLimit\自检结果.txt` 写一份汇总（任务、脚本、配置、电池、最近失败），健康返回 0，不健康返回 1。

2. 如果是"某机型不生效"，请用 [机型适配报告](.github/ISSUE_TEMPLATE/device_report.yml) 模板，至少包含：
   - 机型全称、BIOS 版本、Windows 版本与架构（ARM64 / x64）
   - `Get-PnpDevice -InstanceId 'ACPI\PNP0C14\HWMI*'` 的输出
   - `操作记录.log` 里最近几行（含"接口="字段，能看出走了哪条写入通道）

3. 日志和自检结果里可能含用户名路径，贴之前请自行打码。

## 开发环境

不需要 Visual Studio、.NET SDK 或 NuGet：

| 依赖 | 说明 |
|---|---|
| Windows 10/11 | 自带 .NET Framework 4.x 的 `csc.exe` |
| Windows PowerShell 5.1 | 运行编译脚本（不需要 PowerShell 7） |

编译：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\源码\build.ps1
```

产物是仓库根目录下的 `充电限制工具.exe`（AnyCPU 的 WPF exe，已被 `.gitignore` 忽略）。

注意事项：

- `源码\Program.cs` 与 `源码\build.ps1` 必须保存为 **UTF-8 with BOM**，否则 PowerShell 5.1 按 GBK 解析会导致中文乱码甚至语法错误。
- 编译前先退出正在运行的 `充电限制工具.exe`，否则会报 `CS0016`（文件被占用）。

## 代码结构

`源码\Program.cs` 是刻意保持的单文件实现，按区域注释排列：UI 构建、配置读写、日志、WMI 通道、计划任务管理、自检。修改时请保持这个分区，不要引入新的项目分层或第三方包 —— "零依赖、单文件、双击即用"是本项目的前提。

## 提交规范

- 一个提交只做一件事；提交信息用中文或英文均可，格式 `类型: 说明`（如 `修复: 应用失败时误判成功`）。
- 涉及写入通道、计划任务、提权逻辑的改动，请在 PR 描述里写明：改了哪条通道、失败时如何回滚、如何验证。
- 不要提交：`充电限制工具.exe`、`*.zip`、`配置.ini`、`操作记录.log`、`自检结果.txt`、`最近失败.txt`，以及任何含本机用户名/路径的内容。

## PR 检查清单

- [ ] 本地 `build.ps1` 编译通过，CI 编译通过
- [ ] 在本机跑过 `--selftest`，结果为健康（或说明不健康的原因）
- [ ] 新增/修改的行为已写进 `README.md` 或 `CHANGELOG.md`
- [ ] 没有把个人路径、日志、二进制混进提交

## 许可证

提交即表示同意以本仓库的 [MIT 许可证](LICENSE) 授权你的贡献。第三方组件的许可证约束见 [NOTICE.md](NOTICE.md)。
