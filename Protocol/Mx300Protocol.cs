using System;
using System.Collections.Generic;
using System.Linq;

namespace Mx300Hub.Protocol
{
    public enum ReportKind
    {
        Unknown,
        Battery,
        Config,
        DualDpi,
        DpiColors,
        KeyMap,
        Firmware,
        Status,
        KeyEvent,
        Macro,
        Wakeup,
        MouseStatus
    }

    public sealed class DeviceReport
    {
        public ReportKind Kind;
        public int HeaderOffset;
        public byte Command;
        public byte[] Raw = Array.Empty<byte>();

        public int Level;
        public bool Charging;
        public DeviceConfig? Config;
        public DpiSnapshot? Dpi;
        public string? FirmwareVersion;
        public List<string>? Colors;
        public string? Text;
        public int PollingRate;
        public int ActiveStage;
    }

    public sealed class DeviceConfig
    {
        public byte Reserved;
        public byte LightEffect;
        public int PollingCode = 4;
        public int DpiCount = 6;
        public int DpiIndex;
        public int[] DpiStages = new int[6];
        public bool ScrollReverse;
        public int Lod = 1;
        public byte SensorFlags;
        public int Debounce = 4;
        public int SleepTime = 5;
        public bool HighSpeed;
        public int WakeMode;

        public bool AngleSnapping => (SensorFlags & 0x01) != 0;
        public bool RippleControl => (SensorFlags & 0x10) != 0;
        public bool MotionSync => (SensorFlags & 0x20) != 0;

        public void SetSensorFlag(byte mask, bool on)
        {
            if (on) SensorFlags |= mask; else SensorFlags = (byte)(SensorFlags & ~mask);
        }

        public int PollingRate => Mx300Protocol.PollingFromCode(PollingCode);

        public DeviceConfig Clone()
        {
            var c = (DeviceConfig)MemberwiseClone();
            c.DpiStages = (int[])DpiStages.Clone();
            return c;
        }
    }

    public sealed class DpiSnapshot
    {
        public int ProtocolMode = 1;
        public int UiMode = 1;              // 1 = 射击（普通），2 = 高速
        public bool XyDifferent;
        public int CurrentLevel;
        public int[] Mode1 = new int[6];
        public int[] Mode2 = new int[6];
    }

    /// <summary>
    /// MX300（与 MX301 共用固件家族）的二进制协议。
    /// 帧格式：64 字节，起始 0x55，第二字节为命令字。
    /// 电量：55 30 A5 0B 2E 01 01 01 → 应答 [8]=电量百分比，[9]=充电中。
    /// </summary>
    public static class Mx300Protocol
    {
        public const int ReportLength = 64;
        public const byte Magic = 0x55;

        public const byte CmdFirmware = 0x03;
        public const byte CmdGetKeyMap = 0x08;
        public const byte CmdSetKeyMap = 0x09;
        public const byte CmdGetConfig = 0x0E;
        public const byte CmdSetConfig = 0x0F;
        public const byte CmdBattery = 0x30;
        public const byte CmdSetDualDpi = 0x60;
        public const byte CmdGetDualDpi = 0x61;
        public const byte CmdSetDpiColor = 0x62;
        public const byte CmdGetDpiColor = 0x63;
        public const byte CmdMouseStatus = 0xED;

        private static readonly HashSet<byte> KnownCommands = new()
        {
            0x03, 0x08, 0x09, 0x0E, 0x0F, 0x30, 0x60, 0x61, 0x62, 0x63,
            0xE0, 0xE6, 0xED, 0xFA, 0x02
        };

        public static int PollingFromCode(int code) => code switch
        {
            1 => 125,
            2 => 250,
            3 => 500,
            4 => 1000,
            _ => 1000
        };

        public static int CodeFromPolling(int rate) => rate switch
        {
            125 => 1,
            250 => 2,
            500 => 3,
            1000 => 4,
            _ => 4
        };

        public static int EncodeDpiRaw(int dpi) => (int)Math.Round(dpi * 1.173);
        public static int DecodeDpiRaw(int raw) => (int)Math.Round(raw / 1.173);

        public static byte[] Frame(params byte[] head)
        {
            var b = new byte[ReportLength];
            Array.Copy(head, b, Math.Min(head.Length, ReportLength));
            return b;
        }

        // ---------------- 请求帧 ----------------
        public static byte[] GetBattery() => Frame(0x55, CmdBattery, 0xA5, 0x0B, 0x2E, 0x01, 0x01, 0x01);
        public static byte[] GetFirmware() => Frame(0x55, CmdFirmware);
        public static byte[] GetMouseStatus() => Frame(0x55, CmdMouseStatus);
        public static byte[] GetConfig() => Frame(0x55, CmdGetConfig, 0xA5, 0x0B, 0x2F, 0x01, 0x01, 0x01);
        public static byte[] GetDualDpi() => Frame(0x55, CmdGetDualDpi, 0xA5, 0x0B, 0x1B, 0x01, 0x01, 0x01);
        public static byte[] GetDpiColors() => Frame(0x55, CmdGetDpiColor, 0xA5, 0x0B, 0x24, 0x01, 0x01, 0x01);
        public static byte[] GetKeyMap() => Frame(0x55, CmdGetKeyMap, 0xA5, 0x0B, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00);

        /// <summary>整包写入配置（读-改-写，保留未修改字段）。</summary>
        public static byte[] BuildConfig(DeviceConfig cfg)
        {
            var b = new byte[ReportLength];
            b[0] = 0x55;
            b[1] = CmdSetConfig;
            b[2] = 0xAE;
            b[3] = 0x0A;
            b[4] = 0x2F;
            b[5] = 0x01;
            b[6] = 0x01;
            b[7] = 0x01;
            b[8] = cfg.Reserved;
            b[9] = cfg.LightEffect;
            b[10] = (byte)Math.Clamp(cfg.PollingCode, 1, 4);

            int count = Math.Clamp(cfg.DpiCount <= 0 ? 6 : cfg.DpiCount, 1, 6);
            int index = Math.Clamp(cfg.DpiIndex, 0, count - 1);
            b[11] = (byte)count;
            b[12] = (byte)(index + 1);

            for (int i = 0; i < 6; i++)
            {
                int raw = EncodeDpiRaw(Math.Max(0, cfg.DpiStages[i]));
                b[13 + i * 2] = (byte)(raw & 0xFF);
                b[14 + i * 2] = (byte)((raw >> 8) & 0xFF);
            }

            b[48] = (byte)(cfg.ScrollReverse ? 1 : 0);
            b[49] = (byte)(cfg.Lod == 2 ? 2 : 1);
            b[50] = cfg.SensorFlags;
            b[51] = (byte)Math.Clamp(cfg.Debounce, 0, 30);
            b[52] = (byte)Math.Clamp(cfg.SleepTime, 0, 60);
            b[53] = (byte)(cfg.HighSpeed ? 1 : 0);
            b[54] = (byte)(cfg.WakeMode == 1 ? 1 : 0);
            return b;
        }

        /// <summary>写入 DPI 档位与当前档（0x60）。</summary>
        public static byte[] BuildDualDpi(DpiSnapshot s)
        {
            var b = new byte[ReportLength];
            b[0] = 0x55;
            b[1] = CmdSetDualDpi;
            b[2] = 0xA5;
            b[3] = 0x0B;
            b[4] = 0x1B;
            b[5] = 0x01;
            b[6] = 0x01;
            b[7] = 0x01;
            b[8] = (byte)(s.UiMode == 2 ? 0 : 1);
            b[9] = (byte)(s.XyDifferent ? 1 : 0);
            b[10] = (byte)(Math.Clamp(s.CurrentLevel, 0, 5) + 1);

            for (int i = 0; i < 6; i++)
            {
                int raw = EncodeDpiRaw(Math.Max(0, s.Mode1[i]));
                b[11 + i * 2] = (byte)(raw & 0xFF);
                b[12 + i * 2] = (byte)((raw >> 8) & 0xFF);

                int raw2 = EncodeDpiRaw(Math.Max(0, s.Mode2[i]));
                b[23 + i * 2] = (byte)(raw2 & 0xFF);
                b[24 + i * 2] = (byte)((raw2 >> 8) & 0xFF);
            }
            return b;
        }

        // ---------------- 应答解析 ----------------
        public static int HeaderOffset(byte[] d)
        {
            if (d.Length > 0 && (d[0] == 0x55 || d[0] == 0xAA)) return 0;
            if (d.Length > 1 && (d[1] == 0x55 || d[1] == 0xAA)) return 1;
            return -1;
        }

        private static int B(byte[] d, int i) => (i >= 0 && i < d.Length) ? d[i] : 0;

        public static DeviceReport? Parse(byte[] raw)
        {
            if (raw == null || raw.Length < 4) return null;

            // 按键事件报告（0x02 打头，5 字节）
            if (raw[0] == 0x02) return new DeviceReport { Kind = ReportKind.KeyEvent, Command = 0x02, Raw = raw };

            int t = HeaderOffset(raw);
            if (t < 0 || t + 1 >= raw.Length) return null;

            byte cmd = raw[t + 1];
            if (!KnownCommands.Contains(cmd)) return null;

            var rep = new DeviceReport { Command = cmd, HeaderOffset = t, Raw = raw };

            switch (cmd)
            {
                case CmdBattery:
                    rep.Kind = ReportKind.Battery;
                    rep.Level = Math.Clamp(B(raw, t + 8), 0, 100);
                    rep.Charging = B(raw, t + 9) != 0;
                    break;

                case CmdGetConfig:
                    rep.Kind = ReportKind.Config;
                    rep.Config = ParseConfig(raw, t);
                    break;

                case CmdGetDualDpi:
                    rep.Kind = ReportKind.DualDpi;
                    rep.Dpi = ParseDualDpi(raw, t);
                    break;

                case CmdGetDpiColor:
                    rep.Kind = ReportKind.DpiColors;
                    rep.Colors = ParseColors(raw, t);
                    break;

                case CmdGetKeyMap:
                    rep.Kind = ReportKind.KeyMap;
                    rep.Text = ParseKeyMap(raw, t);
                    break;

                case CmdFirmware:
                    rep.Kind = ReportKind.Firmware;
                    rep.FirmwareVersion = ParseFirmware(raw, t);
                    break;

                case 0xFA:
                    rep.Kind = ReportKind.Status;
                    rep.ActiveStage = Math.Max(0, B(raw, t + 9) - 1);
                    rep.PollingRate = PollingFromCode(B(raw, t + 10));
                    break;

                case CmdMouseStatus:
                    rep.Kind = ReportKind.MouseStatus;
                    rep.Level = B(raw, t + 8);
                    break;

                case 0xE6:
                    rep.Kind = ReportKind.Wakeup;
                    break;

                case 0xE0:
                    rep.Kind = ReportKind.Macro;
                    break;

                default:
                    rep.Kind = ReportKind.Unknown;
                    break;
            }
            return rep;
        }

        public static DeviceConfig ParseConfig(byte[] d, int t)
        {
            var cfg = new DeviceConfig
            {
                Reserved = (byte)B(d, t + 8),
                LightEffect = (byte)B(d, t + 9),
                PollingCode = Math.Clamp(B(d, t + 10), 1, 4),
                DpiCount = Math.Clamp(B(d, t + 11), 1, 6),
                DpiIndex = Math.Max(0, B(d, t + 12) - 1),
                ScrollReverse = B(d, t + 48) == 1,
                Lod = B(d, t + 49) == 2 ? 2 : 1,
                SensorFlags = (byte)B(d, t + 50),
                Debounce = B(d, t + 51),
                SleepTime = B(d, t + 52),
                HighSpeed = (B(d, t + 53) & 1) != 0,
                WakeMode = B(d, t + 54) == 1 ? 1 : 0
            };

            for (int i = 0; i < 6; i++)
            {
                int raw = B(d, t + 13 + i * 2) | (B(d, t + 14 + i * 2) << 8);
                cfg.DpiStages[i] = raw > 0 ? DecodeDpiRaw(raw) : 0;
            }
            if (cfg.DpiIndex >= cfg.DpiCount) cfg.DpiIndex = cfg.DpiCount - 1;
            return cfg;
        }

        public static DpiSnapshot ParseDualDpi(byte[] d, int t)
        {
            var s = new DpiSnapshot
            {
                ProtocolMode = B(d, t + 8) == 0 ? 0 : 1,
                XyDifferent = B(d, t + 9) != 0,
                CurrentLevel = Math.Max(0, B(d, t + 10) - 1)
            };
            s.UiMode = s.ProtocolMode == 0 ? 2 : 1;

            var list1 = new List<int>();
            for (int i = 0; i < 6; i++)
            {
                int raw = B(d, t + 11 + i * 2) | (B(d, t + 12 + i * 2) << 8);
                int v = DecodeDpiRaw(raw);
                if (v >= 100) list1.Add(v);
            }
            var list2 = new List<int>();
            for (int i = 0; i < 6; i++)
            {
                int raw = B(d, t + 23 + i * 2) | (B(d, t + 24 + i * 2) << 8);
                int v = DecodeDpiRaw(raw);
                if (s.ProtocolMode == 0) v = (int)(Math.Round(v / 100.0) * 100);
                if (v >= 100) list2.Add(v);
            }

            for (int i = 0; i < 6; i++)
            {
                s.Mode1[i] = i < list1.Count ? list1[i] : 0;
                s.Mode2[i] = i < list2.Count ? list2[i] : 0;
            }
            return s;
        }

        public static List<string> ParseColors(byte[] d, int t)
        {
            var list = new List<string>();
            int o = t + 8;
            for (int i = 0; i < 12; i++)
            {
                int a = B(d, o++), b = B(d, o++), c = B(d, o++);
                // 设备侧字节序为 BGR，协议层按 (G,R,B) 组包
                list.Add($"#{c:X2}{b:X2}{a:X2}");
            }
            return list;
        }

        public static string ParseKeyMap(byte[] d, int t)
        {
            if (B(d, t) != 0xAA || B(d, t + 1) != 0x08) return "";
            var names = new[] { "左键", "右键", "中键", "后退", "前进" };
            var parts = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                int o = t + 8 + i * 4;
                parts.Add($"{names[i]}:{B(d, o):X2}{B(d, o + 1):X2}{B(d, o + 2):X2}{B(d, o + 3):X2}");
            }
            return string.Join("  ", parts);
        }

        public static string? ParseFirmware(byte[] d, int t)
        {
            if (B(d, t + 1) != CmdFirmware) return null;
            int a = B(d, t + 23), b = B(d, t + 24), c = B(d, t + 25);
            if (a == 0 && b == 0 && c == 0) return null;
            static string S(int v) => v is >= 48 and <= 57 ? ((char)v).ToString() : v.ToString();
            return $"{S(a)}.{S(b)}.{S(c)}";
        }

        public static string Hex(byte[] data, int max = 24)
        {
            int n = Math.Min(data.Length, max);
            var s = string.Join(" ", data.Take(n).Select(x => x.ToString("X2")));
            return data.Length > max ? s + " …" : s;
        }
    }
}
