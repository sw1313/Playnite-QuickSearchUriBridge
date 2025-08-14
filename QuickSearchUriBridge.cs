using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace QuickSearchUriBridge
{
    public class QuickSearchUriBridgeSettings
    {
        public bool AutoDetectHotkey { get; set; } = true;
        public bool PreferGlobalHotkey { get; set; } = true;   // 优先全局快捷键
        public string FallbackInAppGesture { get; set; } = "Ctrl+F"; // 兜底（应用内），仅 SendInput
        public int DelayBeforeTypeMs { get; set; } = 240;      // 热键后等待 QuickSearch 出现
        public bool PressEnter { get; set; } = false;
    }

    public class QuickSearchUriBridge : GenericPlugin
    {
        private static readonly ILogger log = LogManager.GetLogger();
        private QuickSearchUriBridgeSettings settings;

        private string detectedGlobalGesture;
        private string detectedInAppGesture;
        private Hotkey parsedGlobal, parsedInApp, parsedFallbackInApp;

        public override Guid Id => Guid.Parse("b8cf6ca3-0f1b-4b70-9f5f-f2b8f3fbb8a2");

        public QuickSearchUriBridge(IPlayniteAPI api) : base(api)
        {
            settings = LoadPluginSettings<QuickSearchUriBridgeSettings>() ?? new QuickSearchUriBridgeSettings();
            parsedFallbackInApp = ToHotkey(settings.FallbackInAppGesture);
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            PlayniteApi.UriHandler.RegisterSource("quicksearch", async (PlayniteUriEventArgs uriArgs) =>
            {
                try
                {
                    if (settings.AutoDetectHotkey && parsedGlobal.IsEmpty && parsedInApp.IsEmpty)
                    {
                        DetectHotkeys();
                    }

                    var query = ParseQuery(uriArgs);
                    await OpenQuickSearchWithQuery(query);
                }
                catch (Exception ex)
                {
                    log.Error(ex, "QuickSearchUriBridge: URI handling failed.");
                }
            });
        }

        // 支持：playnite://quicksearch?q=... | /q/... | /qb64/<base64-utf8> | 其它段拼接
        private static string ParseQuery(PlayniteUriEventArgs args)
        {
            if (args == null) return string.Empty;
            var parts = args.Arguments ?? new string[0];
            var raw = string.Join("/", parts);

            var mQ = Regex.Match(raw, @"(?:^|[?&])q=([^&]+)", RegexOptions.IgnoreCase);
            if (mQ.Success)
            {
                var v = mQ.Groups[1].Value;
                try { return Uri.UnescapeDataString(v); } catch { return v; }
            }

            if (parts.Length >= 2 && string.Equals(parts[0], "qb64", StringComparison.OrdinalIgnoreCase))
            {
                try { return Encoding.UTF8.GetString(Convert.FromBase64String(parts[1] ?? "")); }
                catch { return ""; }
            }

            if (parts.Length >= 2 && string.Equals(parts[0], "q", StringComparison.OrdinalIgnoreCase))
            {
                var v = parts[1] ?? "";
                try { return Uri.UnescapeDataString(v); } catch { return v; }
            }

            if (parts.Length > 0)
            {
                var joined = string.Join(" ", parts);
                try { return Uri.UnescapeDataString(joined); } catch { return joined; }
            }
            return string.Empty;
        }

        private async Task OpenQuickSearchWithQuery(string query)
        {
            bool usedGlobal = false;

            // 1) 先试全局快捷键（不切前台）
            if (settings.PreferGlobalHotkey && !parsedGlobal.IsEmpty)
            {
                usedGlobal = TrySendHotkeyBySendInput(parsedGlobal);
                await Task.Delay(settings.DelayBeforeTypeMs);
            }

            // 2) 无全局或未触发 → 用应用内（先把 Playnite 前置）
            if (!usedGlobal)
            {
                BringPlayniteToFront();
                var hk = !parsedInApp.IsEmpty ? parsedInApp :
                         (!parsedFallbackInApp.IsEmpty ? parsedFallbackInApp : default(Hotkey));
                if (!hk.IsEmpty) TrySendHotkeyBySendInput(hk);
                await Task.Delay(settings.DelayBeforeTypeMs);
            }

            // 3) Ctrl+A 清空（用 SendInput，而非 SendKeys）
            SendCtrlA();

            // 4) 逐字发送（UNICODE），绝不使用剪贴板
            if (!string.IsNullOrEmpty(query))
            {
                TypeUnicodeText(query);
            }

            if (settings.PressEnter)
            {
                PressEnter();
            }
        }

        private void DetectHotkeys()
        {
            try
            {
                string inAppText, globalText;
                if (TryScanQuickSearchConfig(out inAppText, out globalText))
                {
                    detectedInAppGesture = inAppText;
                    detectedGlobalGesture = globalText;

                    parsedInApp = ToHotkey(detectedInAppGesture);
                    parsedGlobal = ToHotkey(detectedGlobalGesture);

                    if (parsedGlobal.HasWin)
                    {
                        log.Warn("QuickSearchUriBridge: Global hotkey contains Win key; OS may swallow it.");
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, "QuickSearchUriBridge: detect hotkeys failed.");
                parsedInApp = default(Hotkey);
                parsedGlobal = default(Hotkey);
            }
        }

        private bool TryScanQuickSearchConfig(out string inApp, out string global)
        {
            inApp = null; global = null;
            var dataRoot = PlayniteApi.Paths.ExtensionsDataPath;
            if (!Directory.Exists(dataRoot)) return false;

            var dirs = Directory.GetDirectories(dataRoot);
            Array.Sort(dirs, (a, b) =>
            {
                var an = Path.GetFileName(a) ?? "";
                var bn = Path.GetFileName(b) ?? "";
                int ascore = an.IndexOf("QuickSearch", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0;
                int bscore = bn.IndexOf("QuickSearch", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0;
                return bscore.CompareTo(ascore);
            });

            foreach (var dir in dirs)
            {
                int count = 0;
                foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    if (!(file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                          file.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) ||
                          file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    string text;
                    try { text = File.ReadAllText(file); } catch { continue; }
                    if (!Regex.IsMatch(text, "(hotkey|shortcut)", RegexOptions.IgnoreCase))
                        continue;

                    string i1, g1;
                    if (TryExtractHotkeysFromJson(text, out i1, out g1) ||
                        TryExtractHotkeysFromText(text, out i1, out g1))
                    {
                        if (string.IsNullOrEmpty(inApp) && !string.IsNullOrEmpty(i1)) inApp = i1;
                        if (string.IsNullOrEmpty(global) && !string.IsNullOrEmpty(g1)) global = g1;
                    }

                    count++; if (count >= 30) break;
                }

                if (!string.IsNullOrEmpty(global) || (!string.IsNullOrEmpty(inApp) && !string.IsNullOrEmpty(global)))
                    break;
            }

            return !string.IsNullOrEmpty(inApp) || !string.IsNullOrEmpty(global);
        }

        // —— JSON 抽取 —— //
        private static bool TryExtractHotkeysFromJson(string text, out string inApp, out string global)
        {
            inApp = null; global = null;
            try
            {
                var root = JToken.Parse(text);
                foreach (var prop in EnumerateAllProperties(root))
                {
                    var name = prop.Name;
                    if (name == null || !Regex.IsMatch(name, "(hotkey|shortcut)", RegexOptions.IgnoreCase))
                        continue;

                    var val = prop.Value.Type == JTokenType.String ? (string)prop.Value : null;
                    if (string.IsNullOrWhiteSpace(val)) continue;

                    var lname = name.ToLowerInvariant();
                    if (lname.IndexOf("global", StringComparison.Ordinal) >= 0)
                    {
                        if (string.IsNullOrEmpty(global)) global = val;
                    }
                    else if (lname.IndexOf("inapp", StringComparison.Ordinal) >= 0 ||
                             lname.IndexOf("in_app", StringComparison.Ordinal) >= 0 ||
                             lname.IndexOf("playnite", StringComparison.Ordinal) >= 0 ||
                             lname.IndexOf("local", StringComparison.Ordinal) >= 0 ||
                             lname.IndexOf("open", StringComparison.Ordinal) >= 0 ||
                             lname.IndexOf("activation", StringComparison.Ordinal) >= 0)
                    {
                        if (string.IsNullOrEmpty(inApp)) inApp = val;
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(inApp)) inApp = val;
                    }
                }
                return !string.IsNullOrEmpty(inApp) || !string.IsNullOrEmpty(global);
            }
            catch { return false; }
        }

        private static IEnumerable<JProperty> EnumerateAllProperties(JToken root)
        {
            if (root == null) yield break;
            var asProp = root as JProperty;
            if (asProp != null) yield return asProp;
            foreach (var child in root.Children())
                foreach (var p in EnumerateAllProperties(child))
                    yield return p;
        }

        // —— 文本/CFG 抽取 —— //
        private static bool TryExtractHotkeysFromText(string text, out string inApp, out string global)
        {
            inApp = null; global = null;
            var rxLine = new Regex(@"(?<key>(InApp|Local|Playnite|Open|Activation|Global).*?(Hotkey|Shortcut))\s*[:=]\s*[""']?(?<val>[^""'\r\n]+)", RegexOptions.IgnoreCase);
            foreach (Match m in rxLine.Matches(text))
            {
                var key = m.Groups["key"].Value.ToLowerInvariant();
                var val = m.Groups["val"].Value.Trim();
                if (key.IndexOf("global", StringComparison.Ordinal) >= 0)
                {
                    if (string.IsNullOrEmpty(global)) global = val;
                }
                else
                {
                    if (string.IsNullOrEmpty(inApp)) inApp = val;
                }
            }
            if (inApp == null && global == null)
            {
                var rxAny = new Regex(@"(?<![A-Za-z0-9])((Ctrl|Shift|Alt|Win|Windows|None)\s*\+\s*)*(F\d{1,2}|[A-Za-z0-9]|Space)", RegexOptions.IgnoreCase);
                var m = rxAny.Match(text);
                if (m.Success) inApp = m.Value;
            }
            return inApp != null || global != null;
        }

        // =========================
        //     SendInput 工具
        // =========================
        private struct Hotkey
        {
            public bool Ctrl, Alt, Shift, Win;
            public ushort Vk; // 虚拟键
            public bool IsEmpty { get { return !Ctrl && !Alt && !Shift && !Win && Vk == 0; } }
            public bool HasWin { get { return Win; } }
        }

        private static Hotkey ToHotkey(string gesture)
        {
            var hk = new Hotkey();
            if (string.IsNullOrWhiteSpace(gesture)) return hk;

            var raw = gesture.Split(new[] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string key = null;
            foreach (var r in raw)
            {
                var t = r.Trim().TrimEnd(',').ToLowerInvariant();
                if (t == "ctrl" || t == "control") hk.Ctrl = true;
                else if (t == "alt") hk.Alt = true;
                else if (t == "shift") hk.Shift = true;
                else if (t == "win" || t == "windows" || t == "meta") hk.Win = true;
                else if (key == null) key = r.Trim();
            }
            hk.Vk = MapToVk(key);
            return hk;
        }

        private static ushort MapToVk(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            var t = key.ToLowerInvariant();

            if (Regex.IsMatch(t, @"^f\d{1,2}$"))
            {
                int n;
                if (int.TryParse(t.Substring(1), out n) && n >= 1 && n <= 24) return (ushort)(0x70 + (n - 1)); // VK_F1=0x70
            }
            if (t.Length == 1)
            {
                char c = t[0];
                if (c >= 'a' && c <= 'z') return (ushort)('A' + (c - 'a'));
                if (c >= '0' && c <= '9') return (ushort)(0x30 + (c - '0'));
            }
            switch (t)
            {
                case "space": return 0x20;
                case "enter": return 0x0D;
                case "tab": return 0x09;
                case "esc":
                case "escape": return 0x1B;
                case "backspace": return 0x08;
                case "delete":
                case "del": return 0x2E;
                case "insert": return 0x2D;
                case "home": return 0x24;
                case "end": return 0x23;
                case "pageup": return 0x21;
                case "pagedown": return 0x22;
                case "up": return 0x26;
                case "down": return 0x28;
                case "left": return 0x25;
                case "right": return 0x27;
            }
            return 0;
        }

        private bool TrySendHotkeyBySendInput(Hotkey hk)
        {
            if (hk.IsEmpty || hk.Vk == 0) return false;

            var inputs = new List<INPUT>();
            if (hk.Win) inputs.Add(KeyDown(0x5B)); // VK_LWIN
            if (hk.Ctrl) inputs.Add(KeyDown(0x11)); // VK_CONTROL
            if (hk.Shift) inputs.Add(KeyDown(0x10)); // VK_SHIFT
            if (hk.Alt) inputs.Add(KeyDown(0x12)); // VK_MENU

            inputs.Add(KeyDown(hk.Vk));
            inputs.Add(KeyUp(hk.Vk));

            if (hk.Alt) inputs.Add(KeyUp(0x12));
            if (hk.Shift) inputs.Add(KeyUp(0x10));
            if (hk.Ctrl) inputs.Add(KeyUp(0x11));
            if (hk.Win) inputs.Add(KeyUp(0x5B));

            return SendInputBatch(inputs);
        }

        private static void SendCtrlA()
        {
            var inputs = new List<INPUT>();
            inputs.Add(KeyDown(0x11));         // Ctrl
            inputs.Add(KeyDown((ushort)'A'));  // A
            inputs.Add(KeyUp((ushort)'A'));
            inputs.Add(KeyUp(0x11));
            SendInputBatch(inputs);
        }

        private static void PressEnter()
        {
            var inputs = new List<INPUT>();
            inputs.Add(KeyDown(0x0D)); // VK_RETURN
            inputs.Add(KeyUp(0x0D));
            SendInputBatch(inputs);
        }

        // 用 KEYEVENTF_UNICODE 逐字发送 Unicode 字符（不会触碰剪贴板/键盘布局/IME）
        private static void TypeUnicodeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            var inputs = new List<INPUT>(text.Length * 2);
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                // 直接按 UTF-16 单元发送（包含代理对将按两个单元发）
                inputs.Add(new INPUT
                {
                    type = 1,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = ch,
                            dwFlags = 0x0004 // KEYEVENTF_UNICODE
                        }
                    }
                });
                inputs.Add(new INPUT
                {
                    type = 1,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = ch,
                            dwFlags = 0x0004 | 0x0002 // UNICODE + KEYUP
                        }
                    }
                });
            }
            SendInputBatch(inputs);
        }

        private static INPUT KeyDown(ushort vk) => new INPUT
        {
            type = 1,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = 0 } }
        };
        private static INPUT KeyUp(ushort vk) => new INPUT
        {
            type = 1,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = 2 } } // KEYUP
        };

        private static bool SendInputBatch(List<INPUT> inputs)
        {
            if (inputs == null || inputs.Count == 0) return false;
            uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
            return sent == inputs.Count;
        }

        private void BringPlayniteToFront()
        {
            try
            {
                var p = Process.GetCurrentProcess();
                var h = p.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    ShowWindow(h, 9); // SW_RESTORE
                    SetForegroundWindow(h);
                }
            }
            catch { }
        }

        // Win32
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion U; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

        #region Settings
        public override ISettings GetSettings(bool firstRunSettings)
            => new SimpleSettingsView<QuickSearchUriBridgeSettings>(settings, this);

        private class SimpleSettingsView<T> : ISettings where T : class, new()
        {
            private readonly T data; private readonly GenericPlugin plugin;
            public SimpleSettingsView(T data, GenericPlugin plugin) { this.data = data; this.plugin = plugin; }
            public void BeginEdit() { }
            public void CancelEdit() { }
            public void EndEdit() => plugin.SavePluginSettings(data);
            public bool VerifySettings(out List<string> errors) { errors = null; return true; }
        }
        #endregion
    }
}