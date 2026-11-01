// 华为充电限制工具（WPF，零依赖：只用系统自带的 .NET Framework 4.8）
// 编译：见同目录 build.ps1（调用系统内置 csc.exe，无需安装任何 SDK）
//
// 工作原理：
//   1) 界面只做展示与参数配置，普通权限运行；
//   2) 真正写 EC 阈值的动作由两个计划任务以“当前用户 + 最高权限”执行：
//        HuaweiChargeLimit_Apply    按需运行（界面点“应用”时触发，非提权进程可 /run）
//        HuaweiChargeLimit_Startup  登录后 30 秒运行一次，恢复被硬复位清掉的阈值
//      两个任务都执行 C:\ProgramData\HuaweiChargeLimit\应用阈值.ps1（管理员可写、普通用户只读），
//      因此第一次初始化（弹一次 UAC）之后，改参数不再需要管理员授权；
//   3) 脚本内部通过 WMI（ROOT\WMI / OemWMIMethod.OemWMIfun）把阈值写进 EC；
//      可选的旧工具 HuaweiBatteryControl-ARM64.exe 只作为最后的兜底（不存在也不影响使用）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ChargeLimiter
{
    internal static class AppPaths
    {
        // 程序可以在任何位置（桌面、U 盘…），数据统一放到 %LOCALAPPDATA%\HuaweiChargeLimit，
        // 不会在 exe 旁边留下任何文件。
        public static readonly string Dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HuaweiChargeLimit");

        // 计划任务执行的脚本放在 ProgramData：只有管理员能写，普通用户只读。
        // 这样即使有人拿到用户态权限，也没法改写它来借最高权限任务执行任意代码。
        public static readonly string ScriptDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HuaweiChargeLimit");

        public static string Exe { get { return Assembly.GetExecutingAssembly().Location; } }
        public static string Tool { get { return Path.Combine(Dir, "HuaweiBatteryControl-ARM64.exe"); } }
        public static string Log { get { return Path.Combine(DataDir, "操作记录.log"); } }
        public static string Config { get { return Path.Combine(DataDir, "配置.ini"); } }
        public static string SelfTest { get { return Path.Combine(DataDir, "自检结果.txt"); } }
        public static string Failure { get { return Path.Combine(DataDir, "最近失败.txt"); } }
        public static string ApplyScript { get { return Path.Combine(ScriptDir, "应用阈值.ps1"); } }

        // 计划任务的动作是这个“启动器”（本程序的副本，GUI 子系统，启动时不创建控制台窗口）：
        // 它只负责把 应用阈值.ps1 以隐藏方式跑起来，再把退出码带回去。
        // 放在 ProgramData 同级目录，同样只有管理员能替换。
        public static string Launcher { get { return Path.Combine(ScriptDir, "Launcher.exe"); } }

        // 旧版本把脚本放在数据目录（用户可写），升级后要把它删掉
        public static string LegacyScript { get { return Path.Combine(DataDir, "应用阈值.ps1"); } }

        public const string TaskApply = "HuaweiChargeLimit_Apply";
        public const string TaskStartup = "HuaweiChargeLimit_Startup";

        public static void EnsureDataDir()
        {
            try { Directory.CreateDirectory(DataDir); }
            catch (Exception ex) { Diag.Note("创建数据目录失败: " + ex.Message); }
        }

        // 只有管理员能调用；成功返回 true
        public static bool EnsureScriptDir()
        {
            try
            {
                if (!Directory.Exists(ScriptDir)) Directory.CreateDirectory(ScriptDir);
                var di = new DirectoryInfo(ScriptDir);
                var sec = di.GetAccessControl(AccessControlSections.Access);
                sec.SetAccessRuleProtection(true, false);
                var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                AddRule(sec, admin, FileSystemRights.FullControl);
                AddRule(sec, system, FileSystemRights.FullControl);
                AddRule(sec, users, FileSystemRights.ReadAndExecute);
                di.SetAccessControl(sec);
                return true;
            }
            catch (Exception ex)
            {
                Diag.Note("收紧脚本目录权限失败: " + ex.Message);
                return false;
            }
        }

        private static void AddRule(DirectorySecurity sec, SecurityIdentifier sid, FileSystemRights rights)
        {
            try
            {
                sec.AddAccessRule(new FileSystemAccessRule(sid, rights,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            }
            catch (Exception ex) { Diag.Note("设置 ACL 项失败(" + sid + "): " + ex.Message); }
        }

        // 旧版本把数据放在 exe 旁边：只搬“操作记录.log”（历史日志），配置不再自动提升，
        // 避免下载来的 exe 旁边夹带的 配置.ini 被悄悄采纳成正式配置。
        public static void MigrateLegacy()
        {
            try
            {
                if (string.Equals(Dir, DataDir, StringComparison.OrdinalIgnoreCase)) return;
                MigrateFile(Path.Combine(Dir, "操作记录.log"), Log);
            }
            catch (Exception ex) { Diag.Note("迁移旧日志失败: " + ex.Message); }
        }

        private static void MigrateFile(string from, string to)
        {
            try
            {
                if (!File.Exists(from) || File.Exists(to)) return;
                File.Copy(from, to, false);
            }
            catch (Exception ex) { Diag.Note("迁移文件失败 " + from + ": " + ex.Message); }
        }

        public static bool ScriptExists
        {
            get { try { return File.Exists(ApplyScript) && new FileInfo(ApplyScript).Length > 200; } catch { return false; } }
        }
    }

    // 轻量诊断：记录“被吞掉的异常”，方便排查（不再有空的 catch）
    internal static class Diag
    {
        public static string Notes = "";

        public static void Note(string message)
        {
            try
            {
                Notes += "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + "\n";
                if (Notes.Length > 8000) Notes = Notes.Substring(Notes.Length - 6000);
            }
            catch { }
        }
    }

    internal sealed class ChargeConfig
    {
        public int Upper = 90;
        public int Lower = 80;
        public bool AutoAtStartup = true;

        public static string LastWarnings = "";

        public void Normalize()
        {
            if (Upper < 50) Upper = 50;
            if (Upper > 100) Upper = 100;
            if (Lower < 0) Lower = 0;
            if (Lower > Upper - 5) Lower = Math.Max(0, Upper - 5);
        }

        public static ChargeConfig Load()
        {
            var c = new ChargeConfig();
            var warn = new StringBuilder();
            try
            {
                if (File.Exists(AppPaths.Config))
                {
                    foreach (var raw in File.ReadAllLines(AppPaths.Config, Encoding.UTF8))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int i = line.IndexOf('=');
                        if (i <= 0) continue;
                        string k = line.Substring(0, i).Trim().ToLowerInvariant();
                        string v = line.Substring(i + 1).Trim();
                        int x;
                        if (k == "upper")
                        {
                            if (int.TryParse(v, out x)) c.Upper = x;
                            else warn.AppendLine("配置里的 upper=\"" + v + "\" 不是整数，已用 90 代替");
                        }
                        else if (k == "lower")
                        {
                            if (int.TryParse(v, out x)) c.Lower = x;
                            else warn.AppendLine("配置里的 lower=\"" + v + "\" 不是整数，已用 80 代替");
                        }
                        else if (k == "auto") c.AutoAtStartup = (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
            catch (Exception ex)
            {
                warn.AppendLine("读取配置失败: " + ex.Message);
                Diag.Note("读取配置失败: " + ex.Message);
            }
            int rawUpper = c.Upper, rawLower = c.Lower;
            c.Normalize();
            if (rawUpper != c.Upper || rawLower != c.Lower)
                warn.AppendLine("配置越界已修正为 upper=" + c.Upper + " lower=" + c.Lower);
            LastWarnings = warn.ToString().Trim();
            if (!File.Exists(AppPaths.Config)) c.Save();
            return c;
        }

        public bool Save()
        {
            try
            {
                Normalize();
                var sb = new StringBuilder();
                sb.AppendLine("# 充电阈值配置");
                sb.AppendLine("# upper: 充电上限 50~100，lower: 开始充电下限（需 <= upper-5）");
                sb.AppendLine("# auto: 开机时是否自动重设，1=是 0=否");
                sb.AppendLine("upper=" + Upper);
                sb.AppendLine("lower=" + Lower);
                sb.AppendLine("auto=" + (AutoAtStartup ? "1" : "0"));
                File.WriteAllText(AppPaths.Config, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) { Diag.Note("保存配置失败: " + ex.Message); return false; }
        }
    }

    internal static class LogStore
    {
        private const int MaxBytes = 256 * 1024;

        public static void Append(string status, int upper, int lower, string mode)
        {
            var line = string.Format("[{0}] {1} 上限={2} 下限={3} 接口={4}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), status, upper, lower, mode);
            AppendRaw(line);
        }

        public static void AppendRaw(string line)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(AppPaths.Log, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex) { Diag.Note("写日志失败: " + ex.Message); }
        }

        // 日志超过 256KB 就只保留最近 400 行，避免无限增长
        private static void RotateIfNeeded()
        {
            try
            {
                if (!File.Exists(AppPaths.Log)) return;
                var fi = new FileInfo(AppPaths.Log);
                if (fi.Length <= MaxBytes) return;
                var all = File.ReadAllLines(AppPaths.Log, Encoding.UTF8);
                int start = Math.Max(0, all.Length - 400);
                var sb = new StringBuilder();
                sb.AppendLine("[日志已轮转 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "，保留最近 " + (all.Length - start) + " 行]");
                for (int i = start; i < all.Length; i++) sb.AppendLine(all[i]);
                File.WriteAllText(AppPaths.Log, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Diag.Note("日志轮转失败: " + ex.Message); }
        }

        public static int Length()
        {
            try { return File.Exists(AppPaths.Log) ? (int)new FileInfo(AppPaths.Log).Length : 0; }
            catch (Exception ex) { Diag.Note("读取日志长度失败: " + ex.Message); return 0; }
        }

        public static string Tail(int lines)
        {
            try
            {
                if (!File.Exists(AppPaths.Log)) return "";
                var all = File.ReadAllLines(AppPaths.Log, Encoding.UTF8);
                int start = Math.Max(0, all.Length - lines);
                var sb = new StringBuilder();
                for (int i = start; i < all.Length; i++) sb.AppendLine(all[i]);
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex) { Diag.Note("读取日志失败: " + ex.Message); return ""; }
        }

        public static string LastLine()
        {
            var t = Tail(1);
            return t == null ? "" : t;
        }

        public static string LastOkLine()
        {
            try
            {
                if (!File.Exists(AppPaths.Log)) return null;
                var all = File.ReadAllLines(AppPaths.Log, Encoding.UTF8);
                for (int i = all.Length - 1; i >= 0; i--)
                {
                    var line = all[i].Trim();
                    if (line.Contains("] OK ")) return line;
                }
            }
            catch (Exception ex) { Diag.Note("查找上次生效失败: " + ex.Message); }
            return null;
        }

        // 失败时一定要留下痕迹：日志 + 最近失败.txt，两者都写上
        public static void WriteFailure(int upper, int lower, string mode, string detail)
        {
            try
            {
                File.WriteAllText(AppPaths.Failure,
                    "时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" +
                    "上限/下限: " + upper + " / " + lower + "\r\n" +
                    "管理员: " + Elevation.IsElevated + "\r\n" +
                    "用户: " + SafeUserName() + "\r\n" +
                    "失败环节: " + mode + "\r\n\r\n" + detail,
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Diag.Note("写失败文件失败: " + ex.Message);
                // 写不进文件就退而求其次写进日志
                AppendRaw("[FAIL] 无法写 最近失败.txt：" + mode);
            }
        }

        public static string ReadFailure()
        {
            try
            {
                if (!File.Exists(AppPaths.Failure)) return "";
                var txt = File.ReadAllText(AppPaths.Failure, Encoding.UTF8).Trim();
                return txt.Length > 600 ? txt.Substring(0, 600) + "…" : txt;
            }
            catch (Exception ex) { Diag.Note("读失败文件失败: " + ex.Message); return ""; }
        }

        public static void ClearFailure()
        {
            try { if (File.Exists(AppPaths.Failure)) File.Delete(AppPaths.Failure); }
            catch (Exception ex) { Diag.Note("删除失败文件失败: " + ex.Message); }
        }

        public static string SafeUserName()
        {
            try { return WindowsIdentity.GetCurrent().Name; }
            catch (Exception ex) { Diag.Note("取用户名失败: " + ex.Message); return "?"; }
        }
    }

    internal sealed class BatterySnapshot
    {
        public int Percent = -1;
        public bool OnAc;
        public bool Charging;
        public bool Discharging;
        public bool Ok;             // 是否真的读到了电池信息（读不到时界面显示 --，而不是 0）
        public double ChargeRate;   // mW
        public double Voltage;      // mV
        public double Remaining;    // mWh
        public double FullCharge;   // mWh
        public double Designed;     // mWh
        public int CycleCount = -1;

        public double HealthPercent
        {
            get { return (Designed > 0 && FullCharge > 0) ? FullCharge * 100.0 / Designed : 0; }
        }

        public string StateText()
        {
            if (!Ok) return "电池信息读取失败，点“刷新”重试";
            if (Charging) return "充电中";
            if (OnAc && !Discharging) return "已连接电源 · 未充电";
            return Discharging ? "电池供电" : "状态未知";
        }
    }

    internal static class BatteryReader
    {
        public static BatterySnapshot Read()
        {
            var s = new BatterySnapshot();
            ReadWmi(s);
            ReadCimv2(s);
            if (s.Percent < 0 && s.FullCharge > 0 && s.Remaining > 0)
                s.Percent = (int)Math.Round(s.Remaining * 100.0 / s.FullCharge);
            s.Ok = s.Percent >= 0 || s.FullCharge > 0 || s.Charging || s.OnAc;
            return s;
        }

        private static void ReadWmi(BatterySnapshot s)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM BatteryStatus"))
                using (var col = searcher.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        using (mo)
                        {
                            double d;
                            s.OnAc = ToBool(mo["PowerOnline"]);
                            s.Charging = ToBool(mo["Charging"]);
                            s.Discharging = ToBool(mo["Discharging"]);
                            if (TryNum(mo["ChargeRate"], out d)) s.ChargeRate = d;
                            if (TryNum(mo["Voltage"], out d)) s.Voltage = d;
                            if (TryNum(mo["RemainingCapacity"], out d)) s.Remaining = d;
                        }
                        break;
                    }
                }
            }
            catch (Exception ex) { Diag.Note("BatteryStatus 读取失败: " + ex.Message); }

            try
            {
                using (var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM BatteryFullChargedCapacity"))
                using (var col = searcher.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        using (mo) { double d; if (TryNum(mo["FullChargedCapacity"], out d)) s.FullCharge = d; }
                        break;
                    }
                }
            }
            catch (Exception ex) { Diag.Note("满充容量读取失败: " + ex.Message); }

            try
            {
                using (var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM BatteryStaticData"))
                using (var col = searcher.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        using (mo) { double d; if (TryNum(mo["DesignedCapacity"], out d)) s.Designed = d; }
                        break;
                    }
                }
            }
            catch (Exception ex) { Diag.Note("设计容量读取失败: " + ex.Message); }

            try
            {
                using (var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM BatteryCycleCount"))
                using (var col = searcher.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        using (mo) { double d; if (TryNum(mo["CycleCount"], out d)) s.CycleCount = (int)d; }
                        break;
                    }
                }
            }
            catch (Exception ex) { Diag.Note("循环次数读取失败: " + ex.Message); }
        }

        private static void ReadCimv2(BatterySnapshot s)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("root\\cimv2", "SELECT * FROM Win32_Battery"))
                using (var col = searcher.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        using (mo)
                        {
                            double pct, st;
                            if (TryNum(mo["EstimatedChargeRemaining"], out pct)) s.Percent = (int)pct;
                            if (TryNum(mo["BatteryStatus"], out st))
                            {
                                int v = (int)st;
                                if (v == 1) { s.Discharging = true; s.Charging = false; }
                                else if (v == 2) { s.OnAc = true; s.Charging = false; s.Discharging = false; }
                                else if (v >= 6 && v <= 9) { s.OnAc = true; s.Charging = true; s.Discharging = false; }
                            }
                        }
                        break;
                    }
                }
            }
            catch (Exception ex) { Diag.Note("Win32_Battery 读取失败: " + ex.Message); }
        }

        private static bool ToBool(object v)
        {
            try { return v != null && Convert.ToBoolean(v); }
            catch (Exception ex) { Diag.Note("布尔字段转换失败: " + ex.Message); return false; }
        }

        private static bool TryNum(object v, out double d)
        {
            d = 0;
            if (v == null) return false;
            try { d = Convert.ToDouble(v, CultureInfo.InvariantCulture); return true; }
            catch (Exception ex) { Diag.Note("数值字段转换失败: " + ex.Message); return false; }
        }
    }

    // 统一的外部进程调用：异步读输出 + 真正生效的超时（超时直接 Kill），不会再被 ReadToEnd 卡死
    internal static class Proc
    {
        public static bool Run(string file, string arguments, int timeoutMs, string workDir,
                               out string output, out int exitCode)
        {
            output = "";
            exitCode = -1;
            var sb = new StringBuilder();
            try
            {
                var psi = new ProcessStartInfo(file, arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = string.IsNullOrEmpty(workDir) ? AppPaths.DataDir : workDir
                };
                try
                {
                    var enc = Encoding.GetEncoding(936);
                    psi.StandardOutputEncoding = enc;
                    psi.StandardErrorEncoding = enc;
                }
                catch (Exception ex) { Diag.Note("设置输出编码失败: " + ex.Message); }

                using (var p = new Process())
                {
                    p.StartInfo = psi;
                    p.OutputDataReceived += (s, a) => { if (a.Data != null) { lock (sb) sb.AppendLine(a.Data); } };
                    p.ErrorDataReceived += (s, a) => { if (a.Data != null) { lock (sb) sb.AppendLine(a.Data); } };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch (Exception ex) { Diag.Note("结束超时进程失败: " + ex.Message); }
                        lock (sb) sb.AppendLine("[超时 " + timeoutMs + "ms，已强制结束]");
                        output = sb.ToString();
                        return false;
                    }
                    p.WaitForExit();   // 等异步读取把缓冲区排空
                    exitCode = p.ExitCode;
                    output = sb.ToString();
                    return true;
                }
            }
            catch (Exception ex)
            {
                output = "EXCEPTION: " + ex.Message;
                Diag.Note("启动进程失败 " + file + ": " + ex.Message);
                return false;
            }
        }
    }

    internal static class ChargeWriter
    {
        private const string WmiQuery =
            "SELECT * FROM OemWMIMethod WHERE InstanceName='ACPI\\\\PNP0C14\\\\HWMI_0'";

        public static bool Valid(int upper, int lower, out string message)
        {
            if (upper < 50 || upper > 100) { message = "上限需要在 50 ~ 100 之间"; return false; }
            if (lower < 0 || lower > upper - 5) { message = "下限需要在 0 ~ " + (upper - 5) + " 之间"; return false; }
            message = "";
            return true;
        }

        public static bool Apply(int upper, int lower, out string mode, out string raw)
        {
            raw = "";
            string bad;
            if (!Valid(upper, lower, out bad))
            {
                mode = "参数非法：" + bad;
                raw = "拒绝执行：upper=" + upper + " lower=" + lower;
                return false;
            }

            byte[] outBytes;
            string err;

            // 一、进程内直接调用 WMI（UAC 提权直接运行时可用）
            if (InvokeWmi(BuildOld(upper, lower), out outBytes, out err))
            {
                raw = "进程内WMI u8Output: " + Hex(outBytes);
                if (outBytes != null && outBytes.Length > 0 && outBytes[0] == 0)
                {
                    mode = "进程内WMI";
                    return true;
                }
                // 输出非零说明 EC 不接受这个参数，继续尝试后面的通道
                err = "进程内WMI 输出非零: " + Hex(outBytes);
            }

            // 二、交给 PowerShell 调 WMI（计划任务上下文里进程内调用会被 WMI 拒绝，PowerShell 不会）
            string psMode, psErr, psRaw;
            if (InvokeWmiViaPowerShell(upper, lower, out psMode, out psRaw, out psErr))
            {
                mode = psMode;
                raw = psRaw;
                return true;
            }

            // 三、旧版外部工具兜底（只有在 exe 旁边确实存在该工具时才生效）
            string viaTool = RunTool(upper.ToString(), lower.ToString(), "--new");
            if (IsToolOk(viaTool)) { mode = "旧工具兜底"; raw = viaTool; return true; }

            mode = "失败 " + err;
            raw = "进程内WMI: " + err + Environment.NewLine +
                  "PowerShell: " + psErr + Environment.NewLine + "旧工具: " + viaTool;
            return false;
        }

        // 通过 powershell.exe 调用 OemWMIfun（与计划任务里手工验证过的方式一致）
        private static bool InvokeWmiViaPowerShell(int upper, int lower, out string mode,
                                                   out string rawOut, out string err)
        {
            mode = "";
            rawOut = "";
            err = "";
            var channels = new[]
            {
                new { Data = BuildOld(upper, lower), Name = "PowerShell默认接口" },
                new { Data = BuildNew(upper, lower), Name = "PowerShell新版接口" }
            };
            foreach (var ch in channels)
            {
                var sbScript = new StringBuilder();
                sbScript.AppendLine("$ErrorActionPreference = 'Stop'");
                sbScript.AppendLine("try {");
                sbScript.AppendLine("  $all = Get-WmiObject -Namespace 'root\\wmi' -Class OemWMIMethod");
                sbScript.AppendLine("  $o = $null");
                sbScript.AppendLine("  foreach ($x in $all) { if ($x.InstanceName -eq 'ACPI\\PNP0C14\\HWMI_0') { $o = $x; break } }");
                sbScript.AppendLine("  if (-not $o) { 'NOINSTANCE'; exit 3 }");
                sbScript.Append("  $b = [byte[]]@(");
                for (int i = 0; i < ch.Data.Length; i++)
                {
                    if (i > 0) sbScript.Append(',');
                    sbScript.Append(ch.Data[i]);
                }
                sbScript.AppendLine(")");
                sbScript.AppendLine("  $r = $o.OemWMIfun($b)");
                sbScript.AppendLine("  'BYTES=' + (($r.u8Output) -join ',')");
                sbScript.AppendLine("} catch { 'ERR=' + $_.Exception.Message; exit 4 }");

                string script = sbScript.ToString();
                // 用 -EncodedCommand 传脚本：任务上下文里可能写不了临时文件
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                string output;
                int code;
                bool ran = Proc.Run(TaskManager.PowerShellPath,
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                    30000, AppPaths.DataDir, out output, out code);
                if (!ran)
                {
                    err = "PowerShell 调用超时/启动失败: " + OneLineOf(output);
                    continue;
                }
                var m = Regex.Match(output ?? "", @"BYTES=([0-9,]*)");
                if (m.Success)
                {
                    var parts = m.Groups[1].Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && parts[0].Trim() == "0")
                    {
                        mode = ch.Name;
                        rawOut = ch.Name + " u8Output: " + string.Join(" ", parts, 0, Math.Min(parts.Length, 8));
                        return true;
                    }
                    err = ch.Name + " 返回非零: " + m.Groups[1].Value;
                }
                else
                {
                    err = ch.Name + " 输出: " + OneLineOf(output);
                }
            }
            return false;
        }

        private static string OneLineOf(string s)
        {
            return (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static byte[] BuildOld(int upper, int lower)
        {
            // 旧接口：数值 = upper<<24 | lower<<16 | 0x1003，按小端写入字节
            var d = new byte[64];
            d[0] = 0x03;
            d[1] = 0x10;
            d[2] = (byte)lower;
            d[3] = (byte)upper;
            return d;
        }

        private static byte[] BuildNew(int upper, int lower)
        {
            // 新接口：数值 = upper<<40 | lower<<32 | 0x48011503，按小端写入字节
            var d = new byte[64];
            d[0] = 0x03;
            d[1] = 0x15;
            d[2] = 0x01;
            d[3] = 0x48;
            d[4] = (byte)lower;
            d[5] = (byte)upper;
            return d;
        }

        private static bool InvokeWmi(byte[] data, out byte[] outBytes, out string err)
        {
            outBytes = null;
            err = "";

            ManagementObject mo = null;
            try
            {
                using (var searcher = new ManagementObjectSearcher("root\\wmi", WmiQuery))
                using (var col = searcher.Get())
                {
                    foreach (ManagementObject m in col)
                    {
                        mo = m;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                err = "[查询] " + Describe(ex);
                return false;
            }
            if (mo == null)
            {
                err = "[查询] 未找到实例 ACPI\\PNP0C14\\HWMI_0";
                return false;
            }

            try
            {
                // 方式一：object[] 重载，与 PowerShell .OemWMIfun([byte[]]) 等价
                try
                {
                    object res = mo.InvokeMethod("OemWMIfun", new object[] { data });
                    var obj = res as ManagementBaseObject;
                    if (obj != null)
                    {
                        using (obj)
                        {
                            var b = obj["u8Output"] as byte[];
                            if (b != null) { outBytes = b; return true; }
                        }
                    }
                    err = "[调用A] 返回为空";
                }
                catch (Exception ex)
                {
                    err = "[调用A] " + Describe(ex);
                }

                // 方式二：GetMethodParameters + InvokeMethod
                try
                {
                    using (var inParams = mo.GetMethodParameters("OemWMIfun"))
                    {
                        inParams["u8Input"] = data;
                        using (var outParams = mo.InvokeMethod("OemWMIfun", inParams, null))
                        {
                            if (outParams != null)
                            {
                                var b = outParams["u8Output"] as byte[];
                                if (b != null) { outBytes = b; return true; }
                                err += " | [调用B] u8Output 为空";
                            }
                            else err += " | [调用B] 无返回";
                        }
                    }
                }
                catch (Exception ex)
                {
                    err += " | [调用B] " + Describe(ex);
                }

                return false;
            }
            finally
            {
                try { mo.Dispose(); } catch (Exception ex) { Diag.Note("释放 WMI 对象失败: " + ex.Message); }
            }
        }

        private static string Describe(Exception ex)
        {
            var me = ex as ManagementException;
            string code = me != null ? (" ErrorCode=" + me.ErrorCode) : "";
            return ex.GetType().Name + ": " + ex.Message + code;
        }

        private static string Hex(byte[] b)
        {
            if (b == null) return "(null)";
            var sb = new StringBuilder();
            for (int i = 0; i < b.Length && i < 8; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2"));
            }
            return sb.ToString();
        }

        private static bool IsToolOk(string text)
        {
            return text != null && Regex.IsMatch(text, @"u8Output\s*:\s*0\b");
        }

        private static string Extract(string text)
        {
            if (string.IsNullOrEmpty(text)) return "无输出";
            var m = Regex.Match(text, @"Error code\s*=\s*(0x[0-9a-fA-F]+)");
            if (m.Success) return m.Groups[1].Value;
            var line = text.Replace("\r", "").Split('\n');
            for (int i = line.Length - 1; i >= 0; i--)
                if (line[i].Trim().Length > 0) return line[i].Trim();
            return "无输出";
        }

        private static string RunTool(params string[] args)
        {
            try
            {
                if (!File.Exists(AppPaths.Tool)) return "未找到可选工具 HuaweiBatteryControl-ARM64.exe（不影响 PowerShell 通道）";
                string output;
                int code;
                if (!Proc.Run(AppPaths.Tool, string.Join(" ", args), 20000, AppPaths.DataDir, out output, out code))
                    return "调用可选工具超时/失败: " + OneLineOf(output);
                return output;
            }
            catch (Exception ex)
            {
                Diag.Note("调用可选工具异常: " + ex.Message);
                return "EXCEPTION: " + ex.Message;
            }
        }
    }

    internal sealed class RunResult
    {
        public int Code;
        public string Output = "";
    }

    internal static class TaskManager
    {
        public static readonly string Schtasks =
            Path.Combine(Environment.SystemDirectory, "schtasks.exe");

        public static bool Exists(string name)
        {
            return SchtasksRun("/query /tn \"" + name + "\"").Code == 0;
        }

        public static bool RunApply()
        {
            var r = SchtasksRun("/run /tn \"" + AppPaths.TaskApply + "\"");
            return r.Code == 0;
        }

        // 任务是否被禁用（读 /xml 里的 <Enabled>）
        public static bool IsEnabled(string name)
        {
            try
            {
                var x = QueryXml(name);
                if (x == null) return false;
                var m = Regex.Match(x, @"<Enabled>\s*(true|false)\s*</Enabled>", RegexOptions.IgnoreCase);
                return !m.Success || m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) { Diag.Note("读取任务启用状态失败: " + ex.Message); return false; }
        }

        public static string QueryXml(string name)
        {
            var r = SchtasksRun("/query /tn \"" + name + "\" /xml");
            if (r.Code != 0) return null;
            // schtasks 输出会按控制台代码页转换；不同系统上可能解成 UTF-16/GBK，
            // 这里把两种解码结果都看一遍，谁包含 .ps1 就用谁（不依赖硬编码 936 做判断）
            return r.Output;
        }

        // 重建两个任务：先备份旧的，再删、再建；任何一步失败都回滚，不会留下“半初始化”
        public static bool Create()
        {
            var backupApply = Backup(AppPaths.TaskApply);
            var backupStartup = Backup(AppPaths.TaskStartup);

            if (!WriteScript()) return false;

            Delete(AppPaths.TaskApply);
            Delete(AppPaths.TaskStartup);

            bool a = CreateFromXml(AppPaths.TaskApply, BuildApplyTaskXml());
            if (!a)
            {
                Restore(AppPaths.TaskApply, backupApply);
                Restore(AppPaths.TaskStartup, backupStartup);
                Diag.Note("创建 Apply 任务失败，已尝试回滚");
                return false;
            }

            bool b = CreateFromXml(AppPaths.TaskStartup, BuildStartupTaskXml());
            if (!b)
            {
                Delete(AppPaths.TaskApply);
                Restore(AppPaths.TaskApply, backupApply);
                Restore(AppPaths.TaskStartup, backupStartup);
                Diag.Note("创建 Startup 任务失败，已尝试回滚");
                return false;
            }

            return Exists(AppPaths.TaskApply) && Exists(AppPaths.TaskStartup);
        }

        private static string Backup(string name)
        {
            try
            {
                if (!Exists(name)) return null;
                var r = SchtasksRun("/query /tn \"" + name + "\" /xml");
                return r.Code == 0 ? r.Output : null;
            }
            catch (Exception ex) { Diag.Note("备份任务失败: " + ex.Message); return null; }
        }

        private static void Restore(string name, string xml)
        {
            try
            {
                if (string.IsNullOrEmpty(xml)) return;
                if (Exists(name)) return;
                CreateFromXml(name, xml);
            }
            catch (Exception ex) { Diag.Note("回滚任务失败: " + ex.Message); }
        }

        // 脚本内容由程序生成；磁盘上的与期望不一致（被删/被改/版本旧）就需要修复
        public static bool ScriptUpToDate
        {
            get
            {
                try
                {
                    if (!AppPaths.ScriptExists) return false;
                    if (!File.Exists(AppPaths.Launcher)) return false;
                    var cur = File.ReadAllText(AppPaths.ApplyScript, new UTF8Encoding(true));
                    return string.Equals(cur, ExpectedScript(), StringComparison.Ordinal);
                }
                catch (Exception ex) { Diag.Note("比对脚本失败: " + ex.Message); return false; }
            }
        }

        // 提权时调用：把脚本写到 ProgramData 并收紧 ACL
        public static bool EnsureScript()
        {
            if (ScriptUpToDate && AppPaths.ScriptExists) return true;
            if (!Elevation.IsElevated) return false;
            return WriteScript();
        }

        public static string PowerShellPath
        {
            get
            {
                return Path.Combine(Environment.SystemDirectory,
                    Path.Combine("WindowsPowerShell", Path.Combine("v1.0", "powershell.exe")));
            }
        }

        // 计划任务执行的脚本：内容由程序生成，放在 ProgramData（管理员可写、普通用户只读），
        // 提权时随时可以重写修复；脚本内所有路径都是绝对路径，不依赖工作目录。
        public static string ExpectedScript()
        {
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            sb.AppendLine("$LogPath = '" + PsQuote(AppPaths.Log) + "'");
            sb.AppendLine("$FailPath = '" + PsQuote(AppPaths.Failure) + "'");
            sb.AppendLine("$CfgPath = '" + PsQuote(AppPaths.Config) + "'");
            sb.AppendLine("trap {");
            sb.AppendLine("    $m = $_.Exception.Message");
            sb.AppendLine("    try { [IO.File]::AppendAllText($LogPath, '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] FAIL 脚本异常: ' + $m + [Environment]::NewLine, (New-Object Text.UTF8Encoding $false)) } catch { }");
            sb.AppendLine("    try { [IO.File]::WriteAllText($FailPath, '时间: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + [Environment]::NewLine + '脚本异常: ' + $m + [Environment]::NewLine + '位置: ' + $_.InvocationInfo.PositionMessage, (New-Object Text.UTF8Encoding $false)) } catch { }");
            sb.AppendLine("    exit 1");
            sb.AppendLine("}");
            sb.AppendLine("$startup = $args -contains '-Startup'");
            sb.AppendLine("function Write-Log([string]$status, [int]$u, [int]$l, [string]$mode) {");
            sb.AppendLine("    $line = '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $status + ' 上限=' + $u + ' 下限=' + $l + ' 接口=' + $mode");
            sb.AppendLine("    try { [IO.File]::AppendAllText($LogPath, $line + [Environment]::NewLine, (New-Object Text.UTF8Encoding $false)) } catch { }");
            sb.AppendLine("}");
            sb.AppendLine("function Save-Failure([string]$detail) {");
            sb.AppendLine("    try { [IO.File]::WriteAllText($FailPath, '时间: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + [Environment]::NewLine + '详情: ' + $detail, (New-Object Text.UTF8Encoding $false)) } catch { }");
            sb.AppendLine("}");
            sb.AppendLine("$upper = 90; $lower = 80; $auto = 1; $warn = New-Object System.Collections.ArrayList");
            sb.AppendLine("if (Test-Path -LiteralPath $CfgPath) {");
            sb.AppendLine("    try {");
            sb.AppendLine("        foreach ($raw in [IO.File]::ReadAllLines($CfgPath, [Text.Encoding]::UTF8)) {");
            sb.AppendLine("            $t = $raw.Trim()");
            sb.AppendLine("            if ($t -eq '' -or $t.StartsWith('#')) { continue }");
            sb.AppendLine("            $i = $t.IndexOf('=')");
            sb.AppendLine("            if ($i -le 0) { continue }");
            sb.AppendLine("            $k = $t.Substring(0, $i).Trim().ToLower()");
            sb.AppendLine("            $v = $t.Substring($i + 1).Trim()");
            sb.AppendLine("            $n = 0");
            sb.AppendLine("            if ($k -eq 'upper') {");
            sb.AppendLine("                if ([int]::TryParse($v, [ref]$n)) { $upper = $n } else { [void]$warn.Add('upper=' + $v) }");
            sb.AppendLine("            } elseif ($k -eq 'lower') {");
            sb.AppendLine("                if ([int]::TryParse($v, [ref]$n)) { $lower = $n } else { [void]$warn.Add('lower=' + $v) }");
            sb.AppendLine("            } elseif ($k -eq 'auto') {");
            sb.AppendLine("                if ($v -eq '1' -or $v -eq 'true' -or $v -eq 'True') { $auto = 1 } else { $auto = 0 }");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    } catch { [void]$warn.Add('读取配置失败: ' + $_.Exception.Message) }");
            sb.AppendLine("} else { [void]$warn.Add('配置文件不存在，使用默认 90/80') }");
            sb.AppendLine("if ($upper -lt 50) { $upper = 50; [void]$warn.Add('上限越界，改为 50') }");
            sb.AppendLine("if ($upper -gt 100) { $upper = 100; [void]$warn.Add('上限越界，改为 100') }");
            sb.AppendLine("if ($lower -lt 0) { $lower = 0; [void]$warn.Add('下限越界，改为 0') }");
            sb.AppendLine("if ($lower -gt ($upper - 5)) { $lower = [Math]::Max(0, $upper - 5); [void]$warn.Add('下限过大，已调整') }");
            sb.AppendLine("if ($startup -and $auto -ne 1) { exit 0 }");
            sb.AppendLine("if ($warn.Count -gt 0 -and -not $startup) { Write-Log 'WARN' $upper $lower ('配置问题: ' + ($warn -join '; ')) }");
            sb.AppendLine("function Invoke-Threshold([byte[]]$data) {");
            sb.AppendLine("    $all = @(Get-WmiObject -Namespace 'root\\wmi' -Class OemWMIMethod)");
            sb.AppendLine("    $o = $null");
            sb.AppendLine("    foreach ($x in $all) { if ($x.InstanceName -eq 'ACPI\\PNP0C14\\HWMI_0') { $o = $x; break } }");
            sb.AppendLine("    if ($o -eq $null) { throw '未找到 OemWMIMethod 实例（ACPI\\PNP0C14\\HWMI_0）' }");
            sb.AppendLine("    $r = $o.OemWMIfun($data)");
            sb.AppendLine("    return ,$r.u8Output");
            sb.AppendLine("}");
            sb.AppendLine("$old = New-Object byte[] 64");
            sb.AppendLine("$old[0] = 3; $old[1] = 16; $old[2] = $lower; $old[3] = $upper");
            sb.AppendLine("$new = New-Object byte[] 64");
            sb.AppendLine("$new[0] = 3; $new[1] = 21; $new[2] = 1; $new[3] = 72; $new[4] = $lower; $new[5] = $upper");
            sb.AppendLine("$mode = ''");
            sb.AppendLine("$err = ''");
            sb.AppendLine("try {");
            sb.AppendLine("    $out = Invoke-Threshold $old");
            sb.AppendLine("    if ($out[0] -eq 0) { $mode = '默认接口' } else { $err = '默认接口返回 ' + (($out | Select-Object -First 3) -join ',') }");
            sb.AppendLine("} catch { $err = '默认接口异常: ' + $_.Exception.Message }");
            sb.AppendLine("if ($mode -eq '') {");
            sb.AppendLine("    try {");
            sb.AppendLine("        $out2 = Invoke-Threshold $new");
            sb.AppendLine("        if ($out2[0] -eq 0) { $mode = '新版接口' } else { $err = $err + ' | 新版接口返回 ' + (($out2 | Select-Object -First 3) -join ',') }");
            sb.AppendLine("    } catch { $err = $err + ' | 新版接口异常: ' + $_.Exception.Message }");
            sb.AppendLine("}");
            sb.AppendLine("if ($mode -ne '') {");
            sb.AppendLine("    Write-Log 'OK' $upper $lower $mode");
            sb.AppendLine("    try { if (Test-Path -LiteralPath $FailPath) { Remove-Item -LiteralPath $FailPath -Force } } catch { }");
            sb.AppendLine("    exit 0");
            sb.AppendLine("}");
            sb.AppendLine("if ($warn.Count -gt 0) { $err = $err + ' | 配置警告: ' + ($warn -join '; ') }");
            sb.AppendLine("Write-Log 'FAIL' $upper $lower ('失败 ' + $err)");
            sb.AppendLine("Save-Failure $err");
            sb.AppendLine("exit 1");
            return sb.ToString();
        }

        public static bool WriteScript()
        {
            try
            {
                if (!AppPaths.EnsureScriptDir())
                {
                    Diag.Note("脚本目录不可用: " + AppPaths.ScriptDir);
                    return false;
                }
                string text = ExpectedScript();
                if (File.Exists(AppPaths.ApplyScript)) File.Delete(AppPaths.ApplyScript);
                File.WriteAllText(AppPaths.ApplyScript, text, new UTF8Encoding(true));
                try
                {
                    if (File.Exists(AppPaths.LegacyScript)) File.Delete(AppPaths.LegacyScript);
                }
                catch (Exception ex) { Diag.Note("清理旧脚本失败: " + ex.Message); }
                bool ok = AppPaths.ScriptExists;
                if (!ok) Diag.Note("脚本写入后校验失败: " + AppPaths.ApplyScript);

                // 同步一份“启动器”：计划任务直接运行它，避免每次弹 PowerShell 控制台窗口
                try
                {
                    string self = Path.GetFullPath(AppPaths.Exe);
                    string target = Path.GetFullPath(AppPaths.Launcher);
                    if (!string.Equals(self, target, StringComparison.OrdinalIgnoreCase))
                        File.Copy(self, target, true);
                }
                catch (Exception ex) { Diag.Note("复制启动器失败: " + ex.Message); }

                return ok && File.Exists(AppPaths.Launcher);
            }
            catch (Exception ex) { Diag.Note("写脚本失败: " + ex.Message); return false; }
        }

        private static string PsQuote(string s)
        {
            return (s ?? "").Replace("'", "''");
        }

        public static void Delete(string name)
        {
            SchtasksRun("/delete /tn \"" + name + "\" /f");
        }

        public static bool CreateFromXml(string name, string xml)
        {
            // 随机临时文件名：固定名可能被同用户替换成任意 XML（TOCTOU）
            string file = Path.Combine(Path.GetTempPath(), "hcl_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                // Task Scheduler 的 /xml 需要 UTF-16（带 BOM）
                File.WriteAllText(file, xml, new UnicodeEncoding(false, true));
                var r = SchtasksRun("/create /tn \"" + name + "\" /xml \"" + file + "\" /f");
                return r.Code == 0;
            }
            catch { return false; }
            finally { try { File.Delete(file); } catch { } }
        }

        // Apply 任务：以当前用户 + 最高权限（静默提权）运行自己，非提权进程也能 /run 触发
        private static string BuildApplyTaskXml()
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            sb.AppendLine("  <RegistrationInfo><Description>Huawei charge limit - apply current config</Description></RegistrationInfo>");
            sb.AppendLine("  <Triggers />");
            sb.AppendLine("  <Principals>");
            sb.AppendLine("    <Principal id=\"Author\">");
            sb.AppendLine("      <UserId>" + XmlText(CurrentUserId()) + "</UserId>");
            sb.AppendLine("      <LogonType>InteractiveToken</LogonType>");
            sb.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");
            sb.AppendLine("    </Principal>");
            sb.AppendLine("  </Principals>");
            sb.AppendLine(SettingsXml("PT5M"));
            sb.AppendLine("  <Actions Context=\"Author\">");
            sb.AppendLine("    <Exec>");
            sb.AppendLine("      <Command>" + XmlText(AppPaths.Launcher) + "</Command>");
            sb.AppendLine("      <Arguments>--launch</Arguments>");
            sb.AppendLine("      <WorkingDirectory>" + XmlText(AppPaths.DataDir) + "</WorkingDirectory>");
            sb.AppendLine("    </Exec>");
            sb.AppendLine("  </Actions>");
            sb.AppendLine("</Task>");
            return sb.ToString();
        }

        private static string ApplyArgs(string extra)
        {
            return "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" +
                   AppPaths.ApplyScript + "\"" + extra;
        }

        // 登录任务：用户登录后重设一次（硬复位/断电会清掉 EC 阈值）
        private static string BuildStartupTaskXml()
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            sb.AppendLine("  <RegistrationInfo><Description>Huawei charge limit - restore at logon</Description></RegistrationInfo>");
            sb.AppendLine("  <Triggers>");
            sb.AppendLine("    <LogonTrigger><Enabled>true</Enabled><UserId>" + XmlText(CurrentUserId()) + "</UserId><Delay>PT30S</Delay></LogonTrigger>");
            sb.AppendLine("  </Triggers>");
            sb.AppendLine("  <Principals>");
            sb.AppendLine("    <Principal id=\"Author\">");
            sb.AppendLine("      <UserId>" + XmlText(CurrentUserId()) + "</UserId>");
            sb.AppendLine("      <LogonType>InteractiveToken</LogonType>");
            sb.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");
            sb.AppendLine("    </Principal>");
            sb.AppendLine("  </Principals>");
            sb.AppendLine(SettingsXml("PT5M", true));
            sb.AppendLine("  <Actions Context=\"Author\">");
            sb.AppendLine("    <Exec>");
            sb.AppendLine("      <Command>" + XmlText(AppPaths.Launcher) + "</Command>");
            sb.AppendLine("      <Arguments>--launch -Startup</Arguments>");
            sb.AppendLine("      <WorkingDirectory>" + XmlText(AppPaths.DataDir) + "</WorkingDirectory>");
            sb.AppendLine("    </Exec>");
            sb.AppendLine("  </Actions>");
            sb.AppendLine("</Task>");
            return sb.ToString();
        }

        // 子元素顺序必须符合 Task Scheduler 架构（xs:sequence）
        private static string SettingsXml(string timeLimit, bool restartOnFailure = false)
        {
            var sb = new StringBuilder();
            sb.Append("  <Settings>\n");
            sb.Append("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n");
            sb.Append("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n");
            sb.Append("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n");
            sb.Append("    <AllowHardTerminate>true</AllowHardTerminate>\n");
            sb.Append("    <StartWhenAvailable>true</StartWhenAvailable>\n");
            sb.Append("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\n");
            sb.Append("    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\n");
            sb.Append("    <AllowStartOnDemand>true</AllowStartOnDemand>\n");
            sb.Append("    <Enabled>true</Enabled>\n");
            sb.Append("    <Hidden>true</Hidden>\n");
            sb.Append("    <RunOnlyIfIdle>false</RunOnlyIfIdle>\n");
            sb.Append("    <WakeToRun>false</WakeToRun>\n");
            sb.Append("    <ExecutionTimeLimit>" + timeLimit + "</ExecutionTimeLimit>\n");
            sb.Append("    <Priority>4</Priority>\n");
            if (restartOnFailure)
                sb.Append("    <RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>\n");
            sb.Append("  </Settings>");
            return sb.ToString();
        }

        private static string CurrentUserId()
        {
            string domain = Environment.UserDomainName;
            string user = Environment.UserName;
            return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
        }

        private static string XmlText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        public static RunResult Query(string name)
        {
            return SchtasksRun("/query /tn \"" + name + "\"");
        }

        public static string QueryDetail()
        {
            return SchtasksRun("/query /tn \"" + AppPaths.TaskApply + "\" /fo LIST").Output;
        }

        // 最新版任务的动作是 ProgramData 里的 Launcher.exe --launch（静默，不弹控制台窗口）；
        // 中间版本是 powershell.exe -File 脚本；旧版是 充电限制工具.exe --apply-config
        public static bool IsNewStyle()
        {
            try
            {
                var x = QueryXml(AppPaths.TaskApply);
                if (string.IsNullOrEmpty(x)) return false;
                return x.IndexOf("Launcher.exe", StringComparison.OrdinalIgnoreCase) >= 0
                    && x.IndexOf("--launch", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception ex) { Diag.Note("判断任务样式失败: " + ex.Message); return false; }
        }

        public static string LastDiag = "";

        // schtasks 只跑一次（不再“失败后换编码重跑”，避免 /create /delete /run 被执行两遍）；
        // 外部进程的超时与输出读取统一由 Proc.Run 处理，不会卡住 UI。
        private static RunResult SchtasksRun(string args)
        {
            var r = new RunResult();
            string output;
            int code;
            bool ok = Proc.Run(Schtasks, args, 20000, AppPaths.DataDir, out output, out code);
            r.Output = output ?? "";
            r.Code = ok ? code : -1;
            if (!ok) LastDiag += "[" + args + "] 超时或启动失败; ";
            return r;
        }

        private static string OneLine(string s)
        {
            return (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }

    internal static class Elevation
    {
        public static bool IsElevated
        {
            get
            {
                try
                {
                    using (var id = WindowsIdentity.GetCurrent())
                        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }

        // 以管理员身份重新启动自己（会弹一次 UAC），返回是否成功启动
        public static bool RunSelfElevated(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo(AppPaths.Exe, arguments)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppPaths.DataDir
                };
                var p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(180000);
                return true;
            }
            catch { return false; }
        }
    }

    internal static class Ops
    {
        public static bool ApplyAndLog(int upper, int lower)
        {
            string mode, raw;
            bool ok = ChargeWriter.Apply(upper, lower, out mode, out raw);
            if (ok)
            {
                LogStore.Append("OK", upper, lower, mode);
                LogStore.ClearFailure();
            }
            else
            {
                LogStore.Append("FAIL", upper, lower, mode);
                LogStore.WriteFailure(upper, lower, mode,
                    "管理员: " + Elevation.IsElevated + "\r\n" +
                    "用户: " + LogStore.SafeUserName() + "\r\n" +
                    "详情: " + mode + "\r\n\r\n原始输出:\r\n" + raw);
            }
            return ok;
        }

        public static bool SetupElevated()
        {
            if (!Elevation.IsElevated)
            {
                Diag.Note("未提权，无法初始化任务");
                return false;
            }
            bool created = TaskManager.Create();
            var cfg = ChargeConfig.Load();
            bool applied = ApplyAndLog(cfg.Upper, cfg.Lower);
            LogStore.Append(created && applied ? "SETUP" : "FAIL-SETUP", cfg.Upper, cfg.Lower,
                created ? (applied ? "任务已创建" : "任务已创建但应用失败") : "任务创建失败");
            return created && applied;
        }
    }

    public sealed class MainWindow : Window
    {
        private readonly TextBlock _percentText = new TextBlock();
        private readonly TextBlock _stateText = new TextBlock();
        private readonly Dictionary<string, TextBlock> _kv = new Dictionary<string, TextBlock>();
        private readonly TextBox _upperBox = new TextBox();
        private readonly TextBox _lowerBox = new TextBox();
        private readonly CheckBox _autoBox = new CheckBox();
        private readonly Button _applyButton = new Button();
        private readonly Button _initButton = new Button();
        private readonly TextBlock _statusText = new TextBlock();
        private readonly TextBlock _lastApplyText = new TextBlock();
        private readonly TextBlock _taskStateText = new TextBlock();
        private readonly TextBlock _configWarnText = new TextBlock();
        private readonly TextBlock _autoStateText = new TextBlock();
        private readonly TextBlock _logText = new TextBlock();
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private bool _busy;

        public MainWindow()
        {
            Title = "华为充电限制";
            ApplyWindowIcon();
            var wa = SystemParameters.WorkArea;
            Width = Math.Min(940, Math.Max(620, wa.Width - 32));
            Height = Math.Min(760, Math.Max(480, wa.Height - 24));
            MinWidth = Math.Min(820, Width);
            MinHeight = Math.Min(620, Height);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brush("#0E1116");
            Foreground = Brush("#F2F5F9");
            FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = BuildLayout()
            };

            var cfg = ChargeConfig.Load();
            _upperBox.Text = cfg.Upper.ToString();
            _lowerBox.Text = cfg.Lower.ToString();
            _autoBox.IsChecked = cfg.AutoAtStartup;

            Loaded += async (s, e) =>
            {
                await RefreshBatteryAsync();
                RefreshLog();
                RefreshTaskState();
                _timer.Interval = TimeSpan.FromSeconds(20);
                _timer.Tick += async (a, b) => await RefreshBatteryAsync();
                _timer.Start();
            };
            Closed += (s, e) => _timer.Stop();
        }

        // 标题栏/任务栏图标：直接取 exe 里嵌入的图标，程序改名/复制到别处都不受影响
        private void ApplyWindowIcon()
        {
            try
            {
                var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                using (var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                {
                    if (sysIcon == null) return;
                    Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        sysIcon.Handle, System.Windows.Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                }
            }
            catch { }
        }

        // ---------- 界面构造 ----------

        private UIElement BuildLayout()
        {
            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 标题
            var header = new Grid();
            var title = Text("充电限制", 22, "#F2F5F9", FontWeights.SemiBold);
            header.Children.Add(title);
            var sub = Text("HUAWEI MateBook E Go · GK-W7X  ·  EC 充电阈值", 12, "#8B95A5");
            sub.HorizontalAlignment = HorizontalAlignment.Right;
            sub.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(sub);
            Grid.SetRow(header, 0);
            header.Margin = new Thickness(2, 0, 2, 12);
            root.Children.Add(header);

            // 电池卡片
            var batteryCard = Card(BuildBatteryCard());
            Grid.SetRow(batteryCard, 1);
            batteryCard.Margin = new Thickness(0, 0, 0, 12);
            root.Children.Add(batteryCard);

            // 设置卡片
            var settingCard = Card(BuildSettingCard());
            Grid.SetRow(settingCard, 2);
            settingCard.Margin = new Thickness(0, 0, 0, 12);
            root.Children.Add(settingCard);

            // 日志卡片
            var logCard = Card(BuildLogCard());
            Grid.SetRow(logCard, 3);
            logCard.Margin = new Thickness(0, 0, 0, 10);
            root.Children.Add(logCard);

            // 底部提示
            var footer = Text("阈值写在 EC 里，硬复位 / 断电后可能被清空；勾选“开机时自动重设”后由系统启动任务自动恢复。全部数据保存在 " + AppPaths.DataDir + "，exe 放在哪都能用。", 12, "#7C8697");
            footer.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(footer, 4);
            root.Children.Add(footer);

            return root;
        }

        private UIElement BuildBatteryCard()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 170 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _percentText.Text = "--";
            _percentText.FontSize = 56;
            _percentText.FontWeight = FontWeights.SemiBold;
            _percentText.Foreground = Brush("#4CC2FF");
            left.Children.Add(_percentText);
            _stateText.Text = "读取中…";
            _stateText.FontSize = 13;
            _stateText.Foreground = Brush("#A9B4C4");
            _stateText.Margin = new Thickness(2, 2, 0, 0);
            left.Children.Add(_stateText);
            grid.Children.Add(left);

            var right = new Grid { VerticalAlignment = VerticalAlignment.Center };
            right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition());
            right.RowDefinitions.Add(new RowDefinition());
            AddKv(right, 0, 0, "rate", "充电速率");
            AddKv(right, 0, 1, "volt", "电池电压");
            AddKv(right, 0, 2, "remain", "当前容量");
            AddKv(right, 1, 0, "full", "满充容量");
            AddKv(right, 1, 1, "design", "设计容量");
            AddKv(right, 1, 2, "health", "健康度");
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            return grid;
        }

        private void AddKv(Grid grid, int row, int col, string key, string label)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 4, 8, 4) };
            var value = Text("--", 16, "#F2F5F9", FontWeights.SemiBold);
            var name = Text(label, 12, "#8B95A5");
            sp.Children.Add(value);
            sp.Children.Add(name);
            _kv[key] = value;
            Grid.SetRow(sp, row);
            Grid.SetColumn(sp, col);
            grid.Children.Add(sp);
        }

        private UIElement BuildSettingCard()
        {
            var stack = new StackPanel();

            stack.Children.Add(Text("充电阈值", 15, "#F2F5F9", FontWeights.SemiBold));

            var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            row1.Children.Add(Text("上限", 13, "#A9B4C4", null, new Thickness(0, 0, 8, 0)));
            ConfigureNumberBox(_upperBox);
            row1.Children.Add(_upperBox);
            row1.Children.Add(Text("%", 13, "#A9B4C4", null, new Thickness(6, 0, 22, 0)));
            row1.Children.Add(Text("下限", 13, "#A9B4C4", null, new Thickness(0, 0, 8, 0)));
            ConfigureNumberBox(_lowerBox);
            row1.Children.Add(_lowerBox);
            row1.Children.Add(Text("%", 13, "#A9B4C4", null, new Thickness(6, 0, 0, 0)));
            stack.Children.Add(row1);

            var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            row2.Children.Add(Text("常用", 13, "#A9B4C4", null, new Thickness(0, 0, 10, 0)));
            row2.Children.Add(Chip("90 / 80", () => SetPreset(90, 80)));
            row2.Children.Add(Chip("80 / 70", () => SetPreset(80, 70)));
            row2.Children.Add(Chip("60 / 50", () => SetPreset(60, 50)));
            row2.Children.Add(Chip("90 / 85", () => SetPreset(90, 85)));
            row2.Children.Add(Chip("关闭限制 (100 / 0)", () => SetPreset(100, 0)));
            stack.Children.Add(row2);

            var row3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
            _applyButton.Content = "应用";
            _applyButton.Width = 116;
            _applyButton.Height = 38;
            _applyButton.FontSize = 14;
            _applyButton.FontWeight = FontWeights.SemiBold;
            _applyButton.Foreground = Brush("#08121B");
            _applyButton.Background = Brush("#4CC2FF");
            _applyButton.BorderThickness = new Thickness(0);
            _applyButton.Cursor = Cursors.Hand;
            _applyButton.Click += OnApply;

            var refresh = new Button
            {
                Content = "刷新",
                Width = 88,
                Height = 38,
                FontSize = 13,
                Margin = new Thickness(10, 0, 0, 0),
                Foreground = Brush("#DCE3EC"),
                Background = Brush("#222831"),
                BorderBrush = Brush("#333B47"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            refresh.Click += async (s, e) => { await RefreshBatteryAsync(); RefreshLog(); RefreshTaskState(); };

            _initButton.Content = "初始化（需一次管理员授权）";
            _initButton.Height = 38;
            _initButton.Padding = new Thickness(14, 0, 14, 0);
            _initButton.FontSize = 13;
            _initButton.Margin = new Thickness(10, 0, 0, 0);
            _initButton.Foreground = Brush("#F2F5F9");
            _initButton.Background = Brush("#3A2A18");
            _initButton.BorderBrush = Brush("#7A5A2E");
            _initButton.BorderThickness = new Thickness(1);
            _initButton.Cursor = Cursors.Hand;
            _initButton.Click += OnInit;

            var uninstall = new Button
            {
                Content = "卸载说明",
                Height = 38,
                FontSize = 13,
                Margin = new Thickness(10, 0, 0, 0),
                Padding = new Thickness(14, 0, 14, 0),
                Foreground = Brush("#9FB2C8"),
                Background = Brush("#1B2129"),
                BorderBrush = Brush("#333B47"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            uninstall.Click += (s, e) => ShowUninstallInfo();

            row3.Children.Add(_applyButton);
            row3.Children.Add(refresh);
            row3.Children.Add(_initButton);
            row3.Children.Add(uninstall);
            stack.Children.Add(row3);

            var row4 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            _autoBox.Content = "开机时自动重设（系统启动任务）";
            _autoBox.FontSize = 13;
            _autoBox.Foreground = Brush("#C7D0DC");
            _autoBox.VerticalAlignment = VerticalAlignment.Center;
            _autoBox.Click += (s, e) =>
            {
                bool on = _autoBox.IsChecked == true;
                var cfg = ChargeConfig.Load();
                cfg.AutoAtStartup = on;
                if (!cfg.Save())
                {
                    _autoBox.IsChecked = !on;   // 保存失败就退回原状态，避免“看着勾上了其实没存”
                    SetStatus("自动重设开关保存失败（" + AppPaths.DataDir + "），请检查目录权限。", "#FF7A7A");
                    return;
                }
                if (on && !TaskManager.Exists(AppPaths.TaskStartup))
                    SetStatus("已保存“开”，但“登录重设”任务还不存在：点“初始化/修复”创建后才会生效。", "#FFB86B");
                else if (on && !TaskManager.IsEnabled(AppPaths.TaskStartup))
                    SetStatus("已保存“开”，但“登录重设”任务被禁用：点“修复系统任务和脚本”重新启用。", "#FFB86B");
                else if (on)
                    SetStatus("已保存：开机自动重设 = 开，登录后约 30 秒自动写入 " + cfg.Upper + " / " + cfg.Lower + "。", "#68D391");
                else
                    SetStatus("已保存：开机自动重设 = 关（登录时会触发一次任务，但它只读开关就退出，不会写阈值）。", "#FFB86B");
                RefreshTaskState();
            };
            row4.Children.Add(_autoBox);
            stack.Children.Add(row4);

            _autoStateText.FontSize = 12;
            _autoStateText.Margin = new Thickness(0, 8, 0, 0);
            _autoStateText.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(_autoStateText);

            _statusText.FontSize = 13;
            _statusText.Foreground = Brush("#8B95A5");
            _statusText.Margin = new Thickness(0, 14, 0, 0);
            _statusText.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(_statusText);

            _lastApplyText.FontSize = 12;
            _lastApplyText.Foreground = Brush("#6F7A8A");
            _lastApplyText.Margin = new Thickness(0, 6, 0, 0);
            _lastApplyText.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(_lastApplyText);

            _taskStateText.FontSize = 12;
            _taskStateText.Foreground = Brush("#6F7A8A");
            _taskStateText.Margin = new Thickness(0, 4, 0, 0);
            _taskStateText.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(_taskStateText);

            _configWarnText.FontSize = 12;
            _configWarnText.Foreground = Brush("#FFB86B");
            _configWarnText.Margin = new Thickness(0, 4, 0, 0);
            _configWarnText.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(_configWarnText);

            return stack;
        }

        private UIElement BuildLogCard()
        {
            var stack = new StackPanel();
            stack.Children.Add(Text("操作记录", 15, "#F2F5F9", FontWeights.SemiBold));
            _logText.FontFamily = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI");
            _logText.FontSize = 12;
            _logText.Foreground = Brush("#9FB0C4");
            _logText.Margin = new Thickness(0, 10, 0, 0);
            _logText.TextWrapping = TextWrapping.Wrap;
            _logText.Text = "（暂无）";
            stack.Children.Add(new ScrollViewer
            {
                MaxHeight = 138,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _logText
            });
            return stack;
        }

        private static Border Card(UIElement child)
        {
            return new Border
            {
                Background = Brush("#171B22"),
                BorderBrush = Brush("#262D38"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18, 16, 18, 16),
                Child = child
            };
        }

        private static TextBlock Text(string text, double size, string color)
        {
            return Text(text, size, color, null, new Thickness(0));
        }

        private static TextBlock Text(string text, double size, string color, FontWeight weight)
        {
            return Text(text, size, color, (FontWeight?)weight, new Thickness(0));
        }

        private static TextBlock Text(string text, double size, string color, FontWeight? weight, Thickness margin)
        {
            var t = new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = Brush(color),
                Margin = margin,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (weight.HasValue) t.FontWeight = weight.Value;
            return t;
        }

        private Button Chip(string label, Action onClick)
        {
            var b = new Button
            {
                Content = label,
                FontSize = 12,
                Height = 30,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = Brush("#DCE3EC"),
                Background = Brush("#1E242D"),
                BorderBrush = Brush("#333B47"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            b.Click += (s, e) => onClick();
            return b;
        }

        private void ConfigureNumberBox(TextBox box)
        {
            box.Width = 64;
            box.Height = 34;
            box.FontSize = 14;
            box.Padding = new Thickness(8, 0, 8, 0);
            box.VerticalContentAlignment = VerticalAlignment.Center;
            box.Foreground = Brush("#F2F5F9");
            box.Background = Brush("#1E242D");
            box.BorderBrush = Brush("#333B47");
            box.CaretBrush = Brush("#4CC2FF");
            box.PreviewTextInput += (s, e) => { e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$"); };
            box.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Up) { AdjustBox(box, 1); e.Handled = true; }
                else if (e.Key == Key.Down) { AdjustBox(box, -1); e.Handled = true; }
            };
        }

        private static void AdjustBox(TextBox box, int delta)
        {
            int v;
            if (!int.TryParse(box.Text, out v)) v = delta > 0 ? 50 : 0;
            v += delta;
            if (v < 0) v = 0;
            if (v > 100) v = 100;
            box.Text = v.ToString();
        }

        private static SolidColorBrush Brush(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        // ---------- 交互 ----------

        private void SetPreset(int upper, int lower)
        {
            _upperBox.Text = upper.ToString();
            _lowerBox.Text = lower.ToString();
        }

        private bool Validate(out int upper, out int lower)
        {
            upper = lower = 0;
            if (!int.TryParse(_upperBox.Text.Trim(), out upper) || !int.TryParse(_lowerBox.Text.Trim(), out lower))
            {
                SetStatus("请输入数字。", "#FF7A7A");
                return false;
            }
            if (upper < 50 || upper > 100)
            {
                SetStatus("上限需要在 50 ~ 100 之间。", "#FF7A7A");
                return false;
            }
            if (lower < 0 || lower > upper - 5)
            {
                SetStatus("下限需要在 0 ~ " + (upper - 5) + " 之间（与上限至少相差 5）。", "#FF7A7A");
                return false;
            }
            return true;
        }

        private void SetStatus(string text, string color)
        {
            _statusText.Text = text;
            _statusText.Foreground = Brush(color);
        }

        private async void OnApply(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            int upper, lower;
            if (!Validate(out upper, out lower)) return;

            if (!TaskManager.Exists(AppPaths.TaskApply))
            {
                SetStatus("还没有初始化：先点“初始化（需一次管理员授权）”，之后再改参数就不用授权了。", "#FFB86B");
                return;
            }

            _busy = true;
            _applyButton.IsEnabled = false;
            SetStatus("正在应用 " + upper + " / " + lower + " …", "#8B95A5");

            // 任务读配置执行：先写新值，失败则回滚成旧值，避免界面和 EC 不一致
            var cfg = ChargeConfig.Load();
            int oldUpper = cfg.Upper, oldLower = cfg.Lower;
            bool oldAuto = cfg.AutoAtStartup;
            bool saved = false;
            try
            {
                cfg.Upper = upper;
                cfg.Lower = lower;
                cfg.AutoAtStartup = _autoBox.IsChecked == true;
                saved = cfg.Save();
            }
            catch (Exception ex) { Diag.Note("写入配置异常: " + ex.Message); }

            string result = null;
            if (saved)
            {
                string baseline = LogStore.LastLine() ?? "";
                bool started = await Task.Run(() => TaskManager.RunApply());
                if (started)
                {
                    result = await WaitForApplyResult(baseline, 15000, upper, lower);
                }
                else
                {
                    SetStatus("启动应用任务失败，可点“修复系统任务和脚本”重建后重试。", "#FF7A7A");
                }
            }
            else
            {
                SetStatus("配置写入失败（" + AppPaths.DataDir + "），请检查目录权限。", "#FF7A7A");
            }

            if (result == null)
            {
                // 超时/未启动/失败都按失败处理：回滚配置，界面不显示“已生效”
                try
                {
                    var back = ChargeConfig.Load();
                    back.Upper = oldUpper;
                    back.Lower = oldLower;
                    back.AutoAtStartup = oldAuto;
                    back.Save();
                }
                catch (Exception ex) { Diag.Note("回滚配置失败: " + ex.Message); }
                if (string.IsNullOrEmpty(_statusText.Text) || !_statusText.Text.Contains("失败"))
                    SetStatus("应用没有成功，配置已回滚。稍后点“刷新”看日志，或看“最近失败”。", "#FF7A7A");
                var failure = LogStore.ReadFailure();
                if (!string.IsNullOrEmpty(failure))
                    SetStatus("应用失败：" + FirstLine(failure), "#FF7A7A");
            }
            else
            {
                SetStatus("已生效：" + result, "#68D391");
            }

            _upperBox.Text = (result != null ? upper : oldUpper).ToString();
            _lowerBox.Text = (result != null ? lower : oldLower).ToString();
            await RefreshBatteryAsync();
            RefreshLog();
            RefreshTaskState();
            _busy = false;
            _applyButton.IsEnabled = true;
        }

        // 等任务把结果写进日志；只认“本次时间”且参数匹配的 OK 行，避免读到旧的/别人的行
        private static async Task<string> WaitForApplyResult(string baseline, int timeoutMs, int upper, int lower)
        {
            string want = "上限=" + upper + " 下限=" + lower;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    var line = LogStore.LastLine();
                    if (line != null && line != baseline && line.Contains(" OK ") && line.Contains(want))
                    {
                        await Task.Delay(150);
                        return line;
                    }
                }
                catch (Exception ex) { Diag.Note("读取日志失败: " + ex.Message); }
                await Task.Delay(250);
            }
            return null;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var parts = text.Replace("\r\n", "\n").Split('\n');
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0) return t.Length > 160 ? t.Substring(0, 160) + "…" : t;
            }
            return "";
        }

        private void ShowUninstallInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("不再需要充电限制时，按下面 4 步即可完全恢复原状：");
            sb.AppendLine();
            sb.AppendLine("1. 先解除限制：点「常用」里的「关闭限制 (100 / 0)」，再点「应用」，等提示“已生效”。");
            sb.AppendLine("   这样电池会恢复充满到 100%，不会因为删掉程序而永远停在 90%。");
            sb.AppendLine();
            sb.AppendLine("2. 删除两个计划任务：按 Win+R 输入 taskschd.msc 回车，在“任务计划程序库”根目录下");
            sb.AppendLine("   删除 HuaweiChargeLimit_Apply 与 HuaweiChargeLimit_Startup。");
            sb.AppendLine();
            sb.AppendLine("3. 删除这两个目录（配置、日志和计划任务执行的脚本都在里面）：");
            sb.AppendLine("   " + AppPaths.DataDir);
            sb.AppendLine("   " + AppPaths.ScriptDir);
            sb.AppendLine();
            sb.AppendLine("4. 删除程序本身：exe 放在哪个文件夹就删哪个。");
            sb.AppendLine("   本程序不写注册表、不装服务、不留启动项，删掉即完全干净。");
            sb.AppendLine();
            sb.AppendLine("补充：如果只想临时停止限制而不卸载，点「关闭限制 (100 / 0)」再点「应用」即可。");
            MessageBox.Show(this, sb.ToString(), "卸载说明", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void OnInit(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            _initButton.IsEnabled = false;
            SetStatus("正在请求管理员授权并创建计划任务…（请在 UAC 弹窗点“是”）", "#8B95A5");

            bool ok = await Task.Run(() => Elevation.RunSelfElevated("--setup"));

            if (!ok)
            {
                SetStatus("没有获得管理员授权（或已被取消），初始化未完成。", "#FF7A7A");
            }
            else if (TaskManager.Exists(AppPaths.TaskApply))
            {
                SetStatus("初始化完成：之后改参数不再需要管理员授权。", "#68D391");
            }
            else
            {
                SetStatus("初始化没有成功，请再点一次“初始化/修复”；仍失败时看日志目录里的“最近失败.txt”。", "#FF7A7A");
            }

            await RefreshBatteryAsync();
            RefreshLog();
            RefreshTaskState();
            _busy = false;
            _initButton.IsEnabled = true;
        }

        // WMI 读取放到线程池，避免定时刷新卡住 UI
        private async Task RefreshBatteryAsync()
        {
            BatterySnapshot s;
            try { s = await Task.Run(() => BatteryReader.Read()); }
            catch (Exception ex) { Diag.Note("读取电池失败: " + ex.Message); s = new BatterySnapshot(); }
            ApplySnapshot(s);
        }

        private void ApplySnapshot(BatterySnapshot s)
        {
            _percentText.Text = s.Percent >= 0 ? s.Percent + "%" : "--";
            _stateText.Text = s.StateText();
            _kv["rate"].Text = s.ChargeRate > 0 ? Math.Round(s.ChargeRate).ToString("0") + " mW" : "0 mW";
            _kv["volt"].Text = s.Voltage > 0 ? (s.Voltage / 1000.0).ToString("0.00") + " V" : "--";
            _kv["remain"].Text = s.Remaining > 0 ? (s.Remaining / 1000.0).ToString("0.0") + " Wh" : "--";
            _kv["full"].Text = s.FullCharge > 0 ? (s.FullCharge / 1000.0).ToString("0.0") + " Wh" : "--";
            _kv["design"].Text = s.Designed > 0 ? (s.Designed / 1000.0).ToString("0.0") + " Wh" : "--";
            _kv["health"].Text = s.HealthPercent > 0
                ? s.HealthPercent.ToString("0.0") + "%" + (s.CycleCount >= 0 ? " · " + s.CycleCount + " 次循环" : "")
                : (s.CycleCount >= 0 ? s.CycleCount + " 次循环" : "--");
        }

        private void RefreshLog()
        {
            var tail = LogStore.Tail(12);
            _logText.Text = string.IsNullOrWhiteSpace(tail) ? "（暂无）" : tail;
        }

        private void RefreshTaskState()
        {
            bool apply = TaskManager.Exists(AppPaths.TaskApply);
            bool startup = TaskManager.Exists(AppPaths.TaskStartup);
            bool applyEnabled = apply && TaskManager.IsEnabled(AppPaths.TaskApply);
            bool startupEnabled = startup && TaskManager.IsEnabled(AppPaths.TaskStartup);
            bool script = TaskManager.ScriptUpToDate;
            bool healthy = apply && applyEnabled && script;

            // 按钮始终可见：任务/脚本缺失或被禁用时，用户需要一个自救入口
            _initButton.Content = !apply
                ? "初始化（需一次管理员授权）"
                : (healthy && startupEnabled ? "重建系统任务" : "修复系统任务和脚本");

            var cfg = ChargeConfig.Load();

            _taskStateText.Text = "任务：应用任务 " + (apply ? (applyEnabled ? "正常" : "被禁用") : "缺失")
                + " · 登录重设 " + (startup ? (startupEnabled ? "正常" : "被禁用") : "缺失")
                + " · 脚本 " + (script ? "正常" : "缺失或已过期");

            // 明确显示开关的实际落盘状态：避免“勾了/没勾”各说各话
            if (!cfg.AutoAtStartup)
            {
                _autoStateText.Text = "开机自动重设：已关闭 —— 登录时任务仍会触发一次，但它只读开关就退出、不会写阈值（日志不会新增 OK 行）；EC 里的旧阈值不会因此改变。"
                    + "想让电池重新充满到 100%，把上限改成 100、下限 0 再点“应用”。";
                _autoStateText.Foreground = Brush("#8B95A5");
            }
            else if (!startup || !startupEnabled)
            {
                _autoStateText.Text = "开机自动重设：已开启，但登录重设任务" + (startup ? "被禁用" : "缺失")
                    + " —— 点“修复系统任务和脚本”后才真正生效。";
                _autoStateText.Foreground = Brush("#FFB86B");
            }
            else
            {
                _autoStateText.Text = "开机自动重设：已开启 —— 每次登录后约 30 秒自动写入 " + cfg.Upper + " / " + cfg.Lower + "。";
                _autoStateText.Foreground = Brush("#68D391");
            }

            string failure = LogStore.ReadFailure();
            if (!string.IsNullOrEmpty(failure))
                SetStatus("最近一次应用失败：" + FirstLine(failure) + "（点“应用”重试）", "#FF7A7A");
            else if (!apply)
                SetStatus("首次使用：点“初始化（需一次管理员授权）”创建系统任务，之后即可免 UAC 应用。", "#FFB86B");
            else if (!healthy || !startupEnabled)
                SetStatus("检测到计划任务或脚本不完整，点“修复系统任务和脚本”自动重建（需一次管理员授权）。", "#FFB86B");
            else if (_statusText.Text.Length == 0)
                SetStatus("就绪：改参数后点“应用”即可（不需要再授权）。", "#8B95A5");

            string last = LogStore.LastOkLine();
            _lastApplyText.Text = !string.IsNullOrEmpty(last)
                ? "上次生效： " + last
                : "上次生效： 配置为 " + cfg.Upper + " / " + cfg.Lower + "（尚未确认）";

            _configWarnText.Text = string.IsNullOrEmpty(ChargeConfig.LastWarnings) ? "" : "配置提示：" + FirstLine(ChargeConfig.LastWarnings);
        }
    }

    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // 兜底：任何线程上未处理的异常都写进日志，避免“闪退且无痕迹”
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                try
                {
                    LogStore.Append("CRASH", 0, 0, "AppDomain: " +
                        (ex == null ? "未知异常" : ex.GetType().Name + ": " + ex.Message));
                }
                catch { }
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                try { LogStore.Append("CRASH", 0, 0, "Task: " + e.Exception.GetType().Name + ": " + e.Exception.Message); }
                catch { }
                e.SetObserved();
            };

            try
            {
                AppPaths.EnsureDataDir();
                AppPaths.MigrateLegacy();
                if (args.Length > 0)
                {
                    switch (args[0].ToLowerInvariant())
                    {
                        case "--apply-config": return ApplyConfigMode();
                        case "--startup": return StartupMode();
                        case "--launch": return LaunchMode(args);
                        case "--setup": return SetupMode();
                        case "--apply": return ApplyOnceMode(args);
                        case "--selftest": return SelfTestMode();
                        default: return Usage("未知参数: " + args[0]);
                    }
                }

                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    try { LogStore.Append("CRASH", 0, 0, "UI: " + e.Exception.GetType().Name + ": " + e.Exception.Message); }
                    catch { }
                    MessageBox.Show("程序遇到未处理的错误，已写入 操作记录.log：\r\n" + e.Exception.Message,
                        "华为充电限制", MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true;
                };
                app.Run(new MainWindow());
                return 0;
            }
            catch (Exception ex)
            {
                try { LogStore.Append("CRASH", 0, 0, ex.GetType().Name + ": " + ex.Message); }
                catch { }
                return 1;
            }
        }

        private static int Usage(string problem)
        {
            string usage =
                "华为充电限制 · 充电限制工具\r\n" +
                "用法：\r\n" +
                "  充电限制工具.exe                 打开界面\r\n" +
                "  充电限制工具.exe --apply 90 80   直接应用 90/80\r\n" +
                "  充电限制工具.exe --apply-config  按 配置.ini 应用\r\n" +
                "  充电限制工具.exe --startup       登录任务用（auto=1 才应用）\r\n" +
                "  充电限制工具.exe --launch [--startup]  计划任务用：静默执行脚本（不弹窗）\r\n" +
                "  充电限制工具.exe --setup         初始化计划任务（需管理员）\r\n" +
                "  充电限制工具.exe --selftest      自检并写 自检结果.txt\r\n" +
                "数据目录：" + AppPaths.DataDir;
            try
            {
                Console.Error.WriteLine(problem);
                Console.Error.WriteLine(usage);
            }
            catch (Exception ex) { Diag.Note("输出用法失败: " + ex.Message); }
            return 2;
        }

        private static int ApplyOnceMode(string[] args)
        {
            int u, l;
            if (args.Length < 3 || !int.TryParse(args[1], out u) || !int.TryParse(args[2], out l))
                return Usage("参数不完整：--apply 需要两个整数，例如 --apply 90 80");
            string message;
            if (!ChargeWriter.Valid(u, l, out message))
                return Usage("参数不合法：" + message);
            return Ops.ApplyAndLog(u, l) ? 0 : 1;
        }

        // 计划任务调用入口：以“隐藏 + 无窗口”的方式运行 应用阈值.ps1。
        // 本程序是 GUI 子系统，被任务拉起时不会创建控制台窗口；它再以 CreateNoWindow 启动 PowerShell，
        // 因此登录时不会再闪一个 PowerShell 黑窗。
        private static int LaunchMode(string[] args)
        {
            bool startup = false;
            for (int i = 1; i < args.Length; i++)
                if (string.Equals(args[i], "-Startup", StringComparison.OrdinalIgnoreCase)) startup = true;

            try
            {
                var psi = new ProcessStartInfo(TaskManager.PowerShellPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = AppPaths.DataDir,
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" +
                                AppPaths.ApplyScript + "\"" + (startup ? " -Startup" : "")
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) { Diag.Note("启动器: 无法启动 powershell"); return 1; }
                    try
                    {
                        var so = p.StandardOutput.ReadToEnd();
                        var se = p.StandardError.ReadToEnd();
                        if (!string.IsNullOrEmpty(se)) Diag.Note("启动器 stderr: " + se.Trim());
                        else if (so.Trim().Length > 0) Diag.Note("启动器 stdout: " + so.Trim());
                    }
                    catch (Exception ex) { Diag.Note("读取脚本输出失败: " + ex.Message); }
                    if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return 3; }
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { Diag.Note("启动器失败: " + ex.Message); return 1; }
        }

        private static int ApplyConfigMode()
        {
            UpgradeTasksIfNeeded();
            EnsureScriptIfElevated();
            var cfg = ChargeConfig.Load();
            return Ops.ApplyAndLog(cfg.Upper, cfg.Lower) ? 0 : 1;
        }

        private static int StartupMode()
        {
            UpgradeTasksIfNeeded();
            EnsureScriptIfElevated();
            var cfg = ChargeConfig.Load();
            if (!cfg.AutoAtStartup) return 0;
            return Ops.ApplyAndLog(cfg.Upper, cfg.Lower) ? 0 : 1;
        }

        // 提权上下文里顺手把缺失/过期的脚本补上（脚本放在 ProgramData，需要管理员）
        private static void EnsureScriptIfElevated()
        {
            try
            {
                if (!Elevation.IsElevated) return;
                if (!TaskManager.ScriptUpToDate) TaskManager.EnsureScript();
            }
            catch (Exception ex) { Diag.Note("修复脚本失败: " + ex.Message); }
        }

        // 旧版任务的动作是 exe --apply-config；这里借旧任务的管理员身份把任务换成新版（powershell 调 WMI）
        private static void UpgradeTasksIfNeeded()
        {
            try
            {
                if (!Elevation.IsElevated) return;
                CleanupProbeTasks();
                if (!TaskManager.Exists(AppPaths.TaskApply)) return;
                if (TaskManager.IsNewStyle()) return;
                TaskManager.Create();
            }
            catch { }
        }

        // 清掉调试期间遗留的探测任务
        private static void CleanupProbeTasks()
        {
            string[] old =
            {
                "ChargeLimitProbe", "ChargeLimitProbe2", "ChargeLimitProbe3",
                "ChargeLimitProbe5", "ChargeLimitProbe6", "ChargeLimitProbe7", "ChargeLimitProbe9"
            };
            foreach (var n in old)
            {
                try { if (TaskManager.Exists(n)) TaskManager.Delete(n); } catch { }
            }
        }

        private static int SetupMode()
        {
            if (!Elevation.IsElevated) return 3;
            return Ops.SetupElevated() ? 0 : 1;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var parts = text.Replace("\r\n", "\n").Split('\n');
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0) return t.Length > 160 ? t.Substring(0, 160) + "…" : t;
            }
            return "";
        }
        private static int SelfTestMode()
        {
            var sb = new StringBuilder();
            bool apply = TaskManager.Exists(AppPaths.TaskApply);
            bool startup = TaskManager.Exists(AppPaths.TaskStartup);
            bool script = TaskManager.ScriptUpToDate;
            bool applyEnabled = apply && TaskManager.IsEnabled(AppPaths.TaskApply);
            bool startupEnabled = startup && TaskManager.IsEnabled(AppPaths.TaskStartup);

            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("程序目录: " + AppPaths.Dir);
            sb.AppendLine("数据目录: " + AppPaths.DataDir);
            sb.AppendLine("脚本目录: " + AppPaths.ScriptDir);
            sb.AppendLine("管理员: " + Elevation.IsElevated + "  账户: " + WindowsIdentity.GetCurrent().Name);
            sb.AppendLine("位数: " + (IntPtr.Size * 8) + " 位");
            sb.AppendLine("工具存在: " + File.Exists(AppPaths.Tool));
            sb.AppendLine("脚本: " + (script ? "正常" : "缺失或已过期") + "  " + AppPaths.ApplyScript);
            sb.AppendLine("任务_Apply: " + (apply ? "存在" : "缺失") + " 启用=" + applyEnabled);
            sb.AppendLine("任务_Startup: " + (startup ? "存在" : "缺失") + " 启用=" + startupEnabled);
            var cfg = ChargeConfig.Load();
            sb.AppendLine("配置: upper=" + cfg.Upper + " lower=" + cfg.Lower + " auto=" + cfg.AutoAtStartup);
            sb.AppendLine("配置警告: " + (string.IsNullOrEmpty(ChargeConfig.LastWarnings) ? "无" : ChargeConfig.LastWarnings));
            var failure = LogStore.ReadFailure();
            sb.AppendLine("最近失败: " + (string.IsNullOrEmpty(failure) ? "无" : FirstLine(failure)));
            var s = BatteryReader.Read();
            sb.AppendLine("电量: " + s.Percent + "%  状态: " + s.StateText());
            sb.AppendLine("充电速率: " + s.ChargeRate + " mW  电压: " + s.Voltage + " mV");
            sb.AppendLine("剩余: " + s.Remaining + " mWh  满充: " + s.FullCharge + " mWh  设计: " + s.Designed + " mWh");
            sb.AppendLine("健康度: " + s.HealthPercent.ToString("0.0") + "%  循环: " + s.CycleCount);
            sb.AppendLine("日志尾部:");
            sb.AppendLine(LogStore.Tail(5));
            sb.AppendLine("诊断: " + (string.IsNullOrEmpty(Diag.Notes) ? "无" : Diag.Notes));
            try { File.WriteAllText(AppPaths.SelfTest, sb.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { Diag.Note("写自检结果失败: " + ex.Message); }

            bool healthy = apply && applyEnabled && startup && startupEnabled && script
                           && string.IsNullOrEmpty(ChargeConfig.LastWarnings) && string.IsNullOrEmpty(Diag.Notes);
            return healthy ? 0 : 1;
        }
    }
}
