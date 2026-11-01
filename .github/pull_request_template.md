## 改了什么

<!-- 一句话说明这个 PR 解决的问题；关联 Issue 用 Fixes #123 -->

## 改了哪条写入通道 / 任务 / 提权逻辑

<!-- 若涉及 WMI 通道、计划任务、UAC、%ProgramData% 脚本权限，请说明改动点与失败时的回滚方式；不涉及可写"无" -->

## 怎么验证的

- [ ] `powershell -NoProfile -ExecutionPolicy Bypass -File .\源码\build.ps1` 编译通过
- [ ] `充电限制工具.exe --selftest` 结果为健康
- [ ] 实测机型：<!-- 机型 + BIOS + Windows 版本 -->

## 文档

- [ ] 已同步更新 `README.md`（若有行为变化）
- [ ] 已同步更新 `CHANGELOG.md`

## 检查项

- [ ] 没有提交 exe、zip、`配置.ini`、日志、自检结果等运行时文件
- [ ] 没有混入本机用户名或个人信息
- [ ] 没有引入第三方依赖（本项目坚持零依赖、单文件、双击即用）
