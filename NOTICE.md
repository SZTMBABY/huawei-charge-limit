# NOTICE — 第三方组件与参考成果

本项目（华为充电限制 / Huawei Charge Limit）代码以 MIT 许可证发布，但以下内容来自第三方，
各自遵循其原始许可证。分发本仓库或二进制时请保留本文件与相关许可证原文。

---

## 1. HuaweiBatteryControl（二进制兜底工具）

| 项目 | 说明 |
|---|---|
| 文件 | `第三方\HuaweiBatteryControl\HuaweiBatteryControl-ARM64.exe`（20,992 字节，未做任何修改） |
| 来源 | https://github.com/AceDroidX/HuaweiBatteryControl （v1.1 Release，ARM64 资产） |
| 作者 | AceDroidX |
| 许可证 | **Mozilla Public License 2.0 (MPL-2.0)**，全文见 `第三方\HuaweiBatteryControl\LICENSE` |
| 修改 | 无。二进制按原样分发 |
| 源码获取 | 见上方仓库地址（MPL-2.0 要求可获取对应源码） |
| 在本项目中的作用 | 可选的第 4 条写入通道。缺少该文件时程序其余三条通道照常工作，仅失去兜底 |

MPL-2.0 摘要（非法律文本，以许可证原文为准）：允许商用、修改、再分发，须保留版权与许可证声明，
并公开被 MPL 覆盖文件（此处为上述二进制对应的源码）的修改版本。

---

## 2. 接口知识与技术参考（非代码，仅为致谢）

- **AceDroidX/HuaweiBatteryControl 及其博客**
  - https://github.com/AceDroidX/HuaweiBatteryControl
  - https://blog.acedroidx.top/HuaweiBatteryControl/
  - 提供了 `root\wmi` → `OemWMIMethod.OemWMIfun` 的调用格式（64 字节 `u8Input`、`u8Output` 判定、`--new` 格式）。

- **qu1x/huawei-wmi**
  - https://github.com/qu1x/huawei-wmi
  - 说明充电阈值写在 EC、且需要在每次开机后重新写入（systemd 服务实现）。

- **Linux 内核 `gaokun` EC 驱动（Qualcomm sc8280xp 平台）**
  - 含 `gaokun_ec_psy_set_threshold`，用于确认同类机型由 EC 提供充电阈值能力。

- **KDE bug 450551** — "Battery charge limit is not preserved after reboot on many laptops"
  - https://bugs.kde.org/show_bug.cgi?id=450551
  - 解释"登录自动重设"的必要性。

- **华为官网「智能充电模式」支持机型列表**
  - https://consumer.huawei.com/cn/support/content/zh-cn15783714/
  - 用于确认目标机型具备该硬件能力。

以上项目与资料均未被复制进本项目代码，仅作为技术依据引用。

---

## 3. 本项目原创部分

`源码\Program.cs`、`源码\build.ps1`、`源码\make-icon.ps1`、`源码\icon*.png`、`源码\icon.ico`、
`源码\生成产物示例-*`、`源码\旧版脚本\`、`README.md`、`使用说明.txt`、`界面预览.png` 等
由本项目作者编写，以 MIT 许可证发布（见根目录 `LICENSE`）。

图标为 `make-icon.ps1` 用 `System.Drawing` 程序化生成，未使用任何第三方素材。
