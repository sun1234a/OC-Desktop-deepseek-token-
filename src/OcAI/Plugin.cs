using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using UnityEngine;

namespace OcAI
{
    /// <summary>
    /// OC DESKTOP AI companion plugin.
    ///
    /// Verified facts about the host game (see repo notes):
    ///   Unity 2022.3.62f3, IL2CPP, metadata v31, BepInEx 6.0.0-be.788
    ///   - UI.UiPreviewDialogue.PlayDialogue(string, Action)      -> character speech
    ///   - UI.UiPreviewDialogue.ShowText(OCData, string, Action)  -> resolved text
    ///   - OcTouchTrigger.TriggerOcTouching()                     -> head pat / touch
    ///
    /// Design constraints learned from the v31 risk analysis:
    ///   * no ClassInjector / RegisterTypeInIl2Cpp (the known-broken path)
    ///   * therefore no `new GameObject().AddComponent<MyBehaviour>()`
    ///   * the main-thread pump rides on an ALREADY EXISTING game MonoBehaviour
    ///     captured from a Harmony postfix, via BepInEx's MonoBehaviourExtensions
    /// </summary>
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BepInEx.Unity.IL2CPP.BasePlugin
    {
        internal static ManualLogSource Log2;
        internal static OcAIConfig Cfg;
        internal static DeepSeekClient Api;

        private static string _dbgFile;

        /// <summary>Live game MonoBehaviour used to start coroutines (no type injection).</summary>
        internal static UnityEngine.MonoBehaviour Pump;

        private static bool _pumpRunning;
        private static Harmony _harmony;

        // speech queue: filled from any thread, drained on the Unity main thread
        private static readonly Queue<string> _speak = new Queue<string>();
        private static readonly object _speakLock = new object();

        /// <summary>
        /// The character, kept as the actual MonoBehaviour.
        ///
        /// Storing the Transform and converting back later does NOT work: the
        /// round-trip produced null every time ("HeadUiPosition: anchor=&lt;null&gt;"
        /// even though the patch had just read that same component's transform), and
        /// UI.TopicImaginationInputWidget.GetHeadWorldPosition needs a real BongoOC.
        /// </summary>
        internal static UnityEngine.MonoBehaviour SpeakAnchorMb;

        /// <summary>Fallback bubble position, in the game's UI space.</summary>
        internal static UnityEngine.Vector2 _speakScreenPos;

        internal static void SetSpeakAnchor(UnityEngine.MonoBehaviour mb)
        {
            if (mb == null) return;
            if (SpeakAnchorMb == mb) return;      // hover fires every frame; do not re-log
            SpeakAnchorMb = mb;
            _bongoCache = null;      // new anchor: the old cached component may be gone
            Dbg("speak anchor set to " + mb.name + " (" + mb.GetType().Name + ")");
        }

        /// <summary>
        /// Resolves the character's BongoOC component.
        ///
        /// The head-pat hook gives us OcTouchTrigger, which merely SITS ON the
        /// character's GameObject (its name is "BongoOC(Clone)" but its type is
        /// OcTouchTrigger), so TryCast&lt;BongoOC&gt; fails. OcTouchTrigger holds a
        /// private _machine (OcMachine); the BongoOC is a sibling component on the
        /// same GameObject, so a component lookup is the reliable route.
        /// </summary>
        /// <summary>
        /// Cached BongoOC. Resolving is a component lookup plus, on the cold path, a
        /// scene scan -- far too much to repeat for every frame the bubble is visible
        /// (BubbleUi.Tick follows her continuously). Unity's overloaded == reports a
        /// destroyed object as null, so a stale cache refreshes itself.
        /// </summary>
        private static BongoOC _bongoCache;

        internal static BongoOC ResolveBongo()
        {
            if (_bongoCache != null) return _bongoCache;
            try
            {
                var anchor = SpeakAnchorMb;
                if (anchor == null) { Dbg("ResolveBongo: no anchor"); return null; }

                // 1) direct: the anchor itself might be the BongoOC
                var direct = anchor.TryCast<BongoOC>();
                if (direct != null) { _bongoCache = direct; return direct; }

                // 2) sibling component on the same GameObject
                var sib = anchor.GetComponent<BongoOC>();
                if (sib != null)
                {
                    Dbg("ResolveBongo: found via GetComponent on " + anchor.name);
                    _bongoCache = sib;
                    return sib;
                }

                // 3) follow OcTouchTrigger._machine -> its GameObject
                var f = anchor.GetType().GetField("_machine",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null)
                {
                    var machine = f.GetValue(anchor);
                    if (machine != null)
                    {
                        var mmb = machine as UnityEngine.MonoBehaviour;
                        if (mmb != null)
                        {
                            var viaMachine = mmb.GetComponent<BongoOC>();
                            if (viaMachine == null) viaMachine = mmb.TryCast<BongoOC>();
                            if (viaMachine != null)
                            {
                                Dbg("ResolveBongo: found via _machine on " + mmb.name);
                                _bongoCache = viaMachine;
                                return viaMachine;
                            }
                        }
                    }
                }
                else Dbg("ResolveBongo: no _machine field on " + anchor.GetType().Name);

                // 4) last resort: search the whole scene
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<BongoOC>();
                if (all != null && all.Length > 0)
                {
                    Dbg("ResolveBongo: scene scan found " + all.Length + " BongoOC");
                    _bongoCache = all[0];
                    return all[0];
                }
            }
            catch (Exception e) { Dbg("ResolveBongo: " + e.GetType().Name + ": " + e.Message); }
            return null;
        }

        /// <summary>Called by the head-pat patch with the cursor that produced the pat.</summary>
        internal static void SetSpeakScreenPos(float x, float y)
        {
            _speakScreenPos = new UnityEngine.Vector2(x, y);
        }

        // balance handling
        private static CancellationTokenSource _cts = new CancellationTokenSource();
        private static string _cachedBalanceLine;
        private static DateTime _cachedBalanceAt = DateTime.MinValue;
        private static bool _balanceInFlight;

        // Chat memory now lives entirely in ChatFlow. The old _history list is gone on
        // purpose: it mixed the GAME's own dialogue into the model's context as if the
        // model had said it, which made replies drift off topic.

        // ------------------------------------------------------- display guard (desktop)
        //
        // WHY THIS EXISTS
        // The game ships its own window manager -- WindowDisplaySync.ApplyResolution(int,int),
        // WindowDisplaySync.SyncToSize(), DisplayManager.MoveToDisplay / SwitchDisplayAndSync
        // / SwitchWindowedAndSync, plus TransparentWindowNew's click-through handling. It
        // rewrites the display mode (measured live: the desktop DPI value was found set to
        // 288 = 300% while the effective DPI was still 96, and the reported desktop bounds
        // had become 1536x864). When the mode changes, WINDOWS REARRANGES THE DESKTOP ICONS
        // -- that is the damage the user kept seeing.
        //
        // The plugin does not fight the game (blocking its own calls would break its
        // rendering). Instead it remembers the mode that was in use when the plugin loaded
        // and puts it back if the game changes it, so the damage is repaired within a
        // moment instead of persisting after the game exits.

        private static int _guardW, _guardH;
        private static int _guardLogPixels;
        private static bool _guardCaptured;
        private static DateTime _guardLastRestore = DateTime.MinValue;
        private static int _guardRestores;

        // ------------------------------------------------------- real system display mode
        //
        // Screen.width is NOT enough: the game's own canvas keeps rendering at 1920x1080
        // while the SYSTEM display mode is what changes (measured: EnumDisplaySettings
        // reported 1920x1080 with dmLogPixels=120 = 125%, and .NET then saw a 1536x864
        // desktop because 1920/1.25 = 1536). Windows rearranges the desktop icons when that
        // logical size changes. So the guard reads the real mode through Win32.

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
            public int dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        /// <summary>Reads the live system display mode: resolution + LogPixels (scaling).</summary>
        private static bool ReadSystemMode(out int w, out int h, out int logPixels)
        {
            w = h = logPixels = 0;
            try
            {
                var dm = new DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (!EnumDisplaySettings(null, -1, ref dm)) return false;
                w = dm.dmPelsWidth;
                h = dm.dmPelsHeight;
                logPixels = dm.dmLogPixels;
                return w > 0 && h > 0;
            }
            catch (Exception e)
            {
                Dbg("ReadSystemMode: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>Records the desktop mode to protect. Called once from Load().</summary>
        internal static void CaptureDisplayMode()
        {
            int w, h, lp;
            if (ReadSystemMode(out w, out h, out lp))
            {
                _guardW = w; _guardH = h; _guardLogPixels = lp;
                _guardCaptured = true;
                Info("[OcAI] display guard: baseline system mode " + w + "x" + h +
                     " logPixels=" + lp + " (" + (lp / 96.0 * 100.0).ToString("F0") + "% scaling)");
            }
            else
            {
                // Fall back to Unity's view rather than not guarding at all.
                try
                {
                    _guardW = UnityEngine.Screen.width;
                    _guardH = UnityEngine.Screen.height;
                    _guardLogPixels = 0;
                    _guardCaptured = _guardW > 0 && _guardH > 0;
                    Info("[OcAI] display guard: baseline (unity fallback) " + _guardW + "x" + _guardH);
                }
                catch (Exception e)
                {
                    _guardCaptured = false;
                    Dbg("CaptureDisplayMode: " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        /// <summary>
        /// Checks the REAL system mode and warns if the game changed it. Called from the
        /// pump every second; a 2 s cooldown stops a fight with the game if it insists.
        /// </summary>
        internal static void GuardDisplayMode()
        {
            if (!_guardCaptured) return;
            if (Cfg == null || !Cfg.DisplayGuard.Value) return;

            try
            {
                int w, h, lp;
                if (!ReadSystemMode(out w, out h, out lp)) return;

                bool modeChanged = (w != _guardW || h != _guardH);
                bool dpiChanged = (_guardLogPixels != 0 && lp != _guardLogPixels);
                if (!modeChanged && !dpiChanged) return;

                if ((DateTime.UtcNow - _guardLastRestore).TotalSeconds < 2.0) return;
                _guardLastRestore = DateTime.UtcNow;
                _guardRestores++;

                Warn("[OcAI] display guard: the game changed the system display mode -> " +
                     w + "x" + h + " logPixels=" + lp + " (baseline " + _guardW + "x" + _guardH +
                     " logPixels=" + _guardLogPixels + "). This is what rearranges the desktop " +
                     "icons; the baseline is re-applied in the registry and takes full effect " +
                     "after signing out.");

                if (dpiChanged)
                {
                    // The scaling lives in HKCU; writing it back is the supported way to undo
                    // the change. It is applied by Windows at the next sign-in (a logoff is
                    // required -- an explorer restart deliberately re-reads it as-is).
                    try
                    {
                        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                                   @"Control Panel\Desktop", true))
                        {
                            if (k != null)
                            {
                                k.SetValue("LogPixels", _guardLogPixels, Microsoft.Win32.RegistryValueKind.DWord);
                                Dbg("display guard: restored HKCU LogPixels=" + _guardLogPixels);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Dbg("display guard HKCU write: " + e.GetType().Name + ": " + e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                Dbg("GuardDisplayMode: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Diagnostic line for the log: what the guard is protecting.</summary>
        internal static string DisplayGuardState()
        {
            int w, h, lp;
            bool ok = ReadSystemMode(out w, out h, out lp);
            return "display guard: on=" + (Cfg != null && Cfg.DisplayGuard.Value) +
                   " baseline=" + _guardW + "x" + _guardH + " logPixels=" + _guardLogPixels +
                   " current=" + (ok ? w + "x" + h + " logPixels=" + lp : "<unreadable>") +
                   " restores=" + _guardRestores;
        }

        /// <summary>Master switch, exposed in the game's settings panel.</summary>
        internal static bool HeadPatEnabled
        {
            get { return Cfg != null && Cfg.EnableHeadPatBalance.Value; }
        }

        /// <summary>
        /// Master switch, exposed in the game's settings panel.
        /// When off the plugin does nothing at all: no bubble, no sound, no request.
        /// </summary>
        internal static bool PluginEnabled
        {
            get { return Cfg == null || Cfg.Enabled.Value; }
        }

        /// <summary>Hides any visible bubble immediately (used when switching off).</summary>
        internal static void HideBubbleNow()
        {
            try { BubbleUi.HideNow(); } catch { }
        }

        internal static void Dbg(string msg)
        {
            try
            {
                if (Cfg != null && !Cfg.VerboseLogging.Value) return;
                if (_dbgFile == null) return;
                File.AppendAllText(_dbgFile,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>Log helper usable from static patch code (BasePlugin.Log is an instance member).</summary>
        internal static void Info(string msg)
        {
            try { if (Log2 != null) Log2.LogInfo(msg); else Dbg("[info] " + msg); }
            catch { }
        }

        internal static void Warn(string msg)
        {
            try { if (Log2 != null) Log2.LogWarning(msg); else Dbg("[warn] " + msg); }
            catch { }
        }

        internal static void Err(string msg)
        {
            try { if (Log2 != null) Log2.LogError(msg); else Dbg("[error] " + msg); }
            catch { }
        }

        public override void Load()
        {
            Log2 = Log;

            string pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".";
            _dbgFile = Path.Combine(pluginDir, "ocai_debug.txt");
            try { File.Delete(_dbgFile); } catch { }

            Dbg("=== OcAI load, BepInEx " + typeof(Plugin).Assembly.GetName().Version + " ===");
            Dbg("runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

            Cfg = OcAIConfig.Bind(Config);
            Api = new DeepSeekClient(() => Cfg);

            // Let the sitting gate follow the config: with the guard off, an unreadable
            // pose counts as "not sitting".
            try { SitState.FailOpen = Cfg.SitFailOpen.Value; } catch { }

            Dbg("config: baseUrl=" + Cfg.BaseUrl.Value + " model=" + Cfg.Model.Value +
                " hasKey=" + Cfg.HasKey + " headPat=" + Cfg.EnableHeadPatBalance.Value);

            ApplyPatches();

            // Remember the desktop display mode BEFORE the game's own window manager gets a
            // chance to rewrite it; the guard restores it if that happens.
            CaptureDisplayMode();

            Info("[OcAI] loaded. key configured = " + Cfg.HasKey);
            if (!Cfg.HasKey)
                Warn("[OcAI] DeepSeek ApiKey is empty. Edit BepInEx\\config\\" +
                               MyPluginInfo.PLUGIN_GUID + ".cfg and restart the game.");
        }

        // ------------------------------------------------------------- patching

        private static System.Reflection.Assembly _gameAsm;

        /// <summary>Looks up a type in the game's own IL2CPP interop assembly.</summary>
        internal static Type GameType(string fullName)
        {
            try
            {
                if (_gameAsm == null)
                    _gameAsm = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
                return _gameAsm != null ? _gameAsm.GetType(fullName) : null;
            }
            catch { return null; }
        }

        private static void ApplyPatches()
        {
            try
            {
                _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);

                var asmCSharp = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

                if (asmCSharp == null)
                {
                    Dbg("FATAL: Assembly-CSharp (interop) not loaded; cannot patch");
                    Err("[OcAI] Assembly-CSharp interop not found. Is BepInEx\\interop generated?");
                    return;
                }
                _gameAsm = asmCSharp;

                var touchTrigger = asmCSharp.GetType("OcTouchTrigger");
                if (touchTrigger != null)
                {
                    var postfix = new HarmonyMethod(
                        typeof(TouchPatch).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic));

                    // OnMouseOver is the real trigger: it fires as soon as the pointer
                    // is over the character, so a plain click on the head counts.
                    var hover = touchTrigger.GetMethod("OnMouseOver",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (hover != null)
                    {
                        TouchPatch.Target = hover;
                        _harmony.Patch(hover, postfix: postfix);
                        Dbg("patched OcTouchTrigger.OnMouseOver (primary head-pat trigger)");
                    }
                    else Dbg("OnMouseOver not found");

                    // Keep the swipe/touch path too: it fires for drag-style pets.
                    var touch = touchTrigger.GetMethod("TriggerOcTouching",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (touch != null)
                    {
                        _harmony.Patch(touch, postfix: postfix);
                        Dbg("patched OcTouchTrigger.TriggerOcTouching (secondary)");
                    }
                    else Dbg("TriggerOcTouching not found");
                }
                else Dbg("type OcTouchTrigger not found");

                var dialog = asmCSharp.GetType("UI.UiPreviewDialogue");                if (dialog != null)
                {
                    var showText = dialog.GetMethod("ShowText",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (showText != null)
                    {
                        DialoguePatch.TargetShowText = showText;
                        _harmony.Patch(showText, postfix: new HarmonyMethod(
                            typeof(DialoguePatch).GetMethod("PostfixShowText",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Dbg("patched UiPreviewDialogue.ShowText");
                    }
                    else Dbg("ShowText not found");

                    var showDlg = dialog.GetMethod("ShowDialogue",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (showDlg != null)
                    {
                        DialoguePatch.TargetShowDialogue = showDlg;
                        _harmony.Patch(showDlg, postfix: new HarmonyMethod(
                            typeof(DialoguePatch).GetMethod("PostfixShowDialogue",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Dbg("patched UiPreviewDialogue.ShowDialogue");
                    }
                    else Dbg("ShowDialogue not found");
                }
                else Dbg("type UI.UiPreviewDialogue not found");

                // ---- settings UI: reuse the game's own text input modal --------
                SettingsUi.Locate(asmCSharp);

                // ---- sitting state hooks : the authoritative "is she sitting" -----
                // OcSittingState.OnEnter/OnExit are patched so the chat gate does not
                // have to infer the pose (the animation name does not change when she
                // sits, and the state machine's current state is not readable).
                SitState.Attach(asmCSharp);
                BubbleUi.Init(asmCSharp.GetType("UI.DialogueBubble"),
                              asmCSharp.GetType("UI.TopicImaginationInputWidget"));
                var inputWidget = asmCSharp.GetType("UI.TopicImaginationInputWidget");
                if (inputWidget != null)
                {
                    // remember the live instance whenever the game shows it
                    foreach (var mn in new[] { "Awake", "OnEnable", "Show" })
                    {
                        var m = inputWidget.GetMethod(mn,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (m == null) continue;
                        try
                        {
                            _harmony.Patch(m, postfix: new HarmonyMethod(
                                typeof(SettingsUi).GetMethod("NoteInputWidget",
                                    BindingFlags.Static | BindingFlags.NonPublic)));
                            Dbg("patched input modal " + mn + " (instance capture)");
                        }
                        catch (Exception e) { Dbg("patch " + mn + " failed: " + e.Message); }
                    }

                    // intercept its confirm button so we can save our value
                    var confirm = inputWidget.GetMethod("OnConfirmBtnClick",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (confirm != null)
                    {
                        _harmony.Patch(confirm, postfix: new HarmonyMethod(
                            typeof(SettingsUi).GetMethod("OnConfirm",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Dbg("patched TopicImaginationInputWidget.OnConfirmBtnClick");
                    }
                    else Dbg("OnConfirmBtnClick not found");
                }
                else Dbg("type UI.TopicImaginationInputWidget not found");

                // ---- inject an "AI 接入" row into the game's settings panel -------
                var sizeSetting = asmCSharp.GetType("SizeSetting");
                if (sizeSetting != null)
                {
                    var click = new HarmonyMethod(typeof(SettingsRowPatch).GetMethod("SizeClick",
                        BindingFlags.Static | BindingFlags.NonPublic));
                    foreach (var mn in new[] { "OnAddButtonClick", "OnSubButtonClick" })
                    {
                        var m = sizeSetting.GetMethod(mn,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (m == null) { Dbg("SizeSetting." + mn + " not found"); continue; }
                        try { _harmony.Patch(m, prefix: click); Dbg("patched SizeSetting." + mn); }
                        catch (Exception e) { Dbg("patch SizeSetting." + mn + " failed: " + e.Message); }
                    }
                }
                else Dbg("type SizeSetting not found");

                var shown = new HarmonyMethod(typeof(SettingsRowPatch).GetMethod("PanelShown",
                    BindingFlags.Static | BindingFlags.NonPublic));
                foreach (var pair in new[]
                {
                    new[] { "SettingPanel", "OnShow" },
                    new[] { "UI.SettingPanelWidget", "OnEnable" },
                })
                {
                    var t = asmCSharp.GetType(pair[0]);
                    if (t == null) { Dbg("type " + pair[0] + " not found"); continue; }

                    // GetMethod(name, flags) throws AmbiguousMatchException when the
                    // method is overloaded -- SettingPanel.OnShow has both
                    // (Il2CppReferenceArray<Object>) and (Object[]) forms. Pick one
                    // explicitly instead of letting the whole patch pass abort.
                    MethodInfo m = null;
                    try
                    {
                        m = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             .FirstOrDefault(x => x.Name == pair[1] && x.GetParameters().Length <= 1);
                    }
                    catch (Exception e) { Dbg("enumerate " + pair[0] + " failed: " + e.Message); }

                    if (m == null) { Dbg(pair[0] + "." + pair[1] + " not found"); continue; }
                    try
                    {
                        _harmony.Patch(m, postfix: shown);
                        Dbg("patched " + pair[0] + "." + pair[1] + " -> " + m + " (row injection hook)");
                    }
                    catch (Exception e) { Dbg("patch " + pair[0] + " failed: " + e.Message); }
                }

                Info("[OcAI] patches applied (touch=" + (TouchPatch.Target != null) +
                            ", dialog=" + (DialoguePatch.TargetShowText != null) +
                            ", settingsUi=" + (SettingsUi.TypeInputWidget != null) + ")");
            }
            catch (Exception e)
            {
                Dbg("ApplyPatches failed: " + e);
                Err("[OcAI] patching failed: " + e.Message);
            }
        }

        // ------------------------------------------------------- click routing
        //
        // The click stays exactly as it always was: one left click on her pops the
        // balance bubble, immediately, with no delay. Chat deliberately does NOT share
        // the mouse anymore -- the game owns the right button (it stands her up) and the
        // left button already means "balance", so chat is on a key instead (F6/F7, see
        // PollChatHotkey).

        private static DateTime _lastPatAt = DateTime.MinValue;

        /// <summary>Called by the touch patch on a real left-button press on her.</summary>
        internal static void PatRouter()
        {
            if (!PluginEnabled) return;

            if ((DateTime.UtcNow - _lastPatAt).TotalMilliseconds < 300.0) return;
            _lastPatAt = DateTime.UtcNow;

            if (Cfg == null || !Cfg.EnableHeadPatBalance.Value)
            {
                Dbg("left click ignored: balance on click is disabled");
                return;
            }

            Dbg("left click -> balance");
            OnHeadPat();
        }

        /// <summary>Kept for callers that only need the old deferred behaviour.</summary>
        internal static void MainThreadTick()
        {
        }

        /// <summary>
        /// Legacy entry point (was the double-click gesture). Now equivalent to pressing
        /// the chat key, and only used if something still calls it.
        /// </summary>
        internal static void OnChatClick()
        {
            OnChatKey();
        }

        /// <summary>Called when the player clicks the character.</summary>
        internal static void OnHeadPat()
        {
            if (!PluginEnabled) return;
            if (!HeadPatEnabled) return;

            // the pet answers a touch with a short call, like the reference pet
            if (Cfg == null || Cfg.EnableCallSound.Value) CallSound.PlayOk();

            // No key configured: say so instead of firing a request that is
            // guaranteed to come back 401.
            if (Cfg == null || !Cfg.HasKey)
            {
                EnqueueSpeak("还没设置 DeepSeek Key，按鼠标中键填一下");
                return;
            }

            // serve a fresh-enough cached line instantly, refresh in the background
            bool stale = (DateTime.UtcNow - _cachedBalanceAt).TotalSeconds > 30;
            if (_cachedBalanceLine != null && !stale)
            {
                EnqueueSpeak(_cachedBalanceLine);
                return;
            }

            if (_balanceInFlight) { Dbg("balance already in flight"); return; }
            _balanceInFlight = true;
            EnqueueSpeak("让我看看……");

            var token = _cts.Token;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var r = await Api.BalanceAsync(token).ConfigureAwait(false);
                    string line;
                    if (r.Ok)
                    {
                        string cur = string.IsNullOrEmpty(r.Currency) ? "" : " " + r.Currency;
                        string amount = r.TotalBalance ?? "?";
                        // keep the bubble short: it is a pet, not a dashboard
                        if (amount == "0.00" || amount == "0")
                            line = "额度用完了…";
                        else
                            line = "还剩 " + amount + cur;
                        _cachedBalanceLine = line;
                        _cachedBalanceAt = DateTime.UtcNow;
                        Dbg("balance ok: " + amount + cur + " | available=" + r.IsAvailable
                            + " | raw=" + Trunc(r.RawJson, 300));
                    }
                    else
                    {
                        line = ShortError(r.Error);
                        Dbg("balance failed: " + r.Error);
                    }
                    EnqueueSpeak(line);
                }
                catch (Exception e)
                {
                    Dbg("balance task error: " + e.Message);
                    EnqueueSpeak("查询失败，稍后再试");
                }
                finally { _balanceInFlight = false; }
            }, token);
        }

        /// <summary>Turns an API error into something short enough for a bubble.</summary>
        private static string ShortError(string err)
        {
            if (string.IsNullOrEmpty(err)) return "查询失败";
            if (err.IndexOf("401", StringComparison.Ordinal) >= 0 ||
                err.IndexOf("Authentication", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Key 无效，重新填一次";
            if (err.IndexOf("Timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                err.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                return "网络超时了";
            if (err.Length > 40) return err.Substring(0, 40) + "…";
            return err;
        }

        /// <summary>Accessible wrapper for the settings UI.</summary>
        internal static string ShortErrorPublic(string err) { return ShortError(err); }

        /// <summary>Fresh cancellation token, for callers outside this class.</summary>
        internal static CancellationToken ResetToken()
        {
            try { _cts.Cancel(); } catch { }
            _cts = new CancellationTokenSource();
            return _cts.Token;
        }

        /// <summary>Queue a line for the character to say on the main thread.</summary>
        internal static void EnqueueSpeak(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (_speakLock) { _speak.Enqueue(line); }

            // Mark it as ours: these lines reach the game's dialogue widget too, and
            // the widget patch would otherwise file the plugin's own words as things
            // the game character said.
            GameContext.NoteOwnLine(line);

            Dbg("queued speech: " + line);
        }

        /// <summary>
        /// The game made the character say something.
        ///
        /// Deliberately NOT recorded into the chat history any more. It used to be pushed
        /// as an "assistant" message, but the chat request only sends the user's own
        /// lines plus the model's replies -- so a game line (a topic reaction, a story
        /// beat, a greeting to nobody) made the model believe IT had said that, and
        /// answers drifted off topic. The conversation now contains only what the player
        /// actually typed and what the model actually answered.
        /// </summary>
        internal static void NoteGameUtterance(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Dbg("game utterance: " + text);
            GameContext.NoteUtterance(text);
        }

        internal static void RegisterPump(UnityEngine.MonoBehaviour mb)
        {
            if (mb == null || Pump != null) return;
            Pump = mb;
            Dbg("pump candidate registered: " + SafeName(mb));
            TryStartPump();
        }

        private static string SafeName(UnityEngine.Object o)
        {
            try { return o == null ? "<null>" : o.name; } catch { return "<err>"; }
        }

        private static void TryStartPump()
        {
            if (_pumpRunning || Pump == null) return;
            try
            {
                Pump.StartCoroutine(PumpLoop().WrapToIl2Cpp());
                _pumpRunning = true;
                Dbg("main-thread pump started on " + SafeName(Pump));
                Info("[OcAI] main thread pump running");
            }
            catch (Exception e)
            {
                Dbg("TryStartPump failed: " + e.GetType().Name + ": " + e.Message);
                _pumpRunning = false;
            }
        }

        private static IEnumerator PumpLoop()
        {
            var wait = new WaitForSeconds(0.02f);
            while (true)
            {
                string line = null;
                lock (_speakLock)
                {
                    if (_speak.Count > 0) line = _speak.Dequeue();
                }
                if (line != null) ShowBubble(line, BubbleSecondsFor(line));
                try { ChatFlow.MeasureInputBox(); } catch { }
                try { ChatFlow.ApplyInputPosition(); } catch { }
                try { MainThreadTick(); } catch (Exception e) { Dbg("MainThreadTick: " + e.Message); }
                try { BubbleUi.Tick(); } catch { }
                try { PollChatHotkey(); } catch (Exception e) { Dbg("PollChatHotkey: " + e.Message); }
                try { PollHotkeys(); } catch (Exception e) { Dbg("PollHotkeys: " + e.Message); }

                // once, a few seconds in (after the game has built its UI):
                _pumpTicks++;
                if (_pumpTicks == 250) { try { DumpLiveUiOnce(); } catch { } }

                // Harvest the lines she is showing in the game's own bubbles. Every 20
                // frames is plenty: these are lines that stay on screen for seconds.
                if (_pumpTicks % 20 == 0) { try { GameContext.PollBubbles(); } catch { } }

                // Protect the desktop display mode (see CaptureDisplayMode). Once a second
                // is enough: the game only changes modes on its own window events.
                if (_pumpTicks % 50 == 0)
                {
                    try { GuardDisplayMode(); } catch (Exception e) { Dbg("GuardDisplayMode: " + e.Message); }
                }

                // One-shot sitting-state dump, a few seconds in. This is the evidence
                // that tells us what the state machine really reports, without needing
                // anyone to press F7 first.
                if (_pumpTicks == 400 && Cfg != null && Cfg.VerboseLogging.Value)
                {
                    try { Dbg("=== startup " + SitState.Describe() + " ==="); }
                    catch (Exception e) { Dbg("startup sit probe failed: " + e.Message); }
                    try { Dbg("=== startup " + DisplayGuardState() + " ==="); }
                    catch (Exception e) { Dbg("startup display guard probe failed: " + e.Message); }
                }

                // Chat-memory lifecycle self-test, once, after the game UI is up. Verifies
                // remember / count / clear / rebuild without needing a keypress.
                if (_pumpTicks == 320)
                {
                    try { ChatFlow.SelfTest(); }
                    catch (Exception e) { Dbg("ChatFlow.SelfTest: " + e.Message); }
                }

                // One-shot web-search probe: proves the search path (DNS, TLS, HTML
                // parsing, provider fallback) works from inside the game process, with no
                // user interaction. Off by default; turn on while diagnosing 联网.
                if (_pumpTicks == 520 && Cfg != null && Cfg.WebSearchSelfTest.Value)
                {
                    try { ChatFlow.WebSelfTest(); }
                    catch (Exception e) { Dbg("ChatFlow.WebSelfTest: " + e.Message); }
                }

                // Bubble self-test: proves the display path works with no head-pat
                // and no valid API key needed.
                if (_pumpTicks == 150 && Cfg != null && Cfg.BubbleSelfTest.Value)
                {
                    Dbg("bubble self-test firing");
                    ShowBubble("测试：气泡显示正常 ✓");
                }

                yield return wait;
            }
        }

        private static int _pumpTicks;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // Virtual key codes. F6/F7 are deliberately NOT used any more: on many laptops
        // those are system keys (brightness / media) and never reach the game, which is
        // exactly what happened in testing. The chat trigger is the plain F key.
        private const int VK_MBUTTON = 0x04;
        private const int VK_SCROLL = 0x91;
        private const int VK_INSERT = 0x2D;
        private const int VK_PAUSE = 0x13;
        private const int VK_F = 0x46;
        private const int VK_F8 = 0x77;

        private static bool _hotkeyDown;
        private static bool _chatKeyDown;

        /// <summary>
        /// F: talk to the character the cursor is pointing at.
        ///
        /// Picking who to talk to is done with the cursor: OnMouseOver is the game's own
        /// answer to "which character", so with several characters on the desktop the one
        /// under the pointer is the one that answers.
        ///
        /// Why a key and not a mouse button: every mouse gesture on a character is taken.
        /// Left click reports the balance, and the game binds a right-click on a seated
        /// character to "stand up" -- that fires on the press itself, so a right-click
        /// trigger never sees her still sitting (measured). A key has no such conflict.
        /// </summary>
        private static void PollChatHotkey()
        {
            bool down = (GetAsyncKeyState(VK_F) & 0x8000) != 0;
            if (down && !_chatKeyDown)
            {
                Dbg("F key pressed -> chat request");
                OnChatKey();
            }
            _chatKeyDown = down;
        }

        /// <summary>The F action: chat with whoever the cursor is pointing at.</summary>
        internal static void OnChatKey()
        {
            if (!PluginEnabled) return;
            if (Cfg == null || !Cfg.EnableChat.Value) return;

            if (ChatFlow.IsInputOpen)
            {
                Dbg("chat key ignored: the input modal is already open");
                return;
            }

            // Always leave the state evidence in the log: if the gate says no, the reason
            // must be readable afterwards.
            try { Dbg("=== chat key: " + SitState.Describe() + " ==="); }
            catch (Exception e) { Dbg("chat key: sit probe failed: " + e.Message); }

            // Which character? The game's own hover first, geometry as second chance.
            bool onHer = TouchPatch.IsHoveringRecently;
            if (!onHer)
            {
                onHer = TouchPatch.IsCursorOverCharacterByGeometry();
                if (onHer) Dbg("chat key: hover recovered from cursor geometry");
            }

            if (!onHer)
            {
                Dbg("chat key ignored: cursor is not on any character");
                EnqueueSpeak("先把鼠标放到想聊的角色身上，再按 F 呀");
                return;
            }

            bool sitting = true;
            if (Cfg.RequireSitting.Value)
            {
                try { sitting = SitState.IsSitting(); }
                catch (Exception e)
                {
                    Dbg("OnChatKey: SitState failed: " + e.Message);
                    sitting = true;   // fail open, see the class comment on SitState
                }
            }
            else
            {
                Dbg("sit check skipped (RequireSitting = false)");
            }

            if (!sitting)
            {
                Dbg("chat key ignored: she is not sitting");
                EnqueueSpeak("先让我坐下再聊吧");
                return;
            }

            if (!Cfg.HasKey)
            {
                EnqueueSpeak("还没设置 DeepSeek Key，去设置面板点「AI 接入 Key」填一下");
                return;
            }

            Dbg("chat key while sitting -> chat input");
            ChatFlow.TryOpenInput();
        }

        private static int ResolveHotkey()
        {
            var s = (Cfg != null && Cfg.EditKeyHotkey != null)
                ? (Cfg.EditKeyHotkey.Value ?? "").Trim() : "MiddleMouse";
            switch (s.ToLowerInvariant())
            {
                case "middlemouse":
                case "middle":
                case "mmb": return VK_MBUTTON;
                case "scrolllock":
                case "scroll": return VK_SCROLL;
                case "insert":
                case "ins": return VK_INSERT;
                case "pause": return VK_PAUSE;
                case "f8": return VK_F8;
                case "none":
                case "off":
                case "": return 0;
                default:
                    Dbg("unknown EditKeyHotkey '" + s + "', falling back to MiddleMouse");
                    return VK_MBUTTON;
            }
        }

        private static void PollHotkeys()
        {
            PollBubbleNudge();

            int vk = ResolveHotkey();
            if (vk == 0) return;

            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (down && !_hotkeyDown)
            {
                Dbg("hotkey 0x" + vk.ToString("X2") + " -> open API key editor");
                SettingsUi.OpenApiKeyEditor();
                EnqueueSpeak("在这里输入 DeepSeek 的 API Key，然后点确定");
            }
            _hotkeyDown = down;
        }

        // ------------------------------------------------- bubble position nudge
        //
        // Ctrl+Up / Ctrl+Down move the speech bubble live and remember the result in
        // the config file, so the position can be dialled in by eye instead of
        // guessing numbers, rebuilding and restarting the game every time.

        private const int VK_CONTROL = 0x11;
        private const int VK_UP = 0x26;
        private const int VK_DOWN = 0x28;

        /// <summary>Pixels of vertical travel per Ctrl+Up / Ctrl+Down press.</summary>
        private const float NudgeStep = 20f;

        private static bool _nudgeUpDown;
        private static bool _nudgeDownDown;

        private static void PollBubbleNudge()
        {
            if (Cfg == null || Cfg.BubbleDropDown == null) return;

            bool held = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            bool up = held && (GetAsyncKeyState(VK_UP) & 0x8000) != 0;
            bool down = held && (GetAsyncKeyState(VK_DOWN) & 0x8000) != 0;

            float delta = 0f;
            if (up && !_nudgeUpDown) delta = -NudgeStep;      // less drop = higher up
            if (down && !_nudgeDownDown) delta = NudgeStep;   // more drop = lower down
            _nudgeUpDown = up;
            _nudgeDownDown = down;
            if (delta == 0f) return;

            float v = Cfg.BubbleDropDown.Value + delta;
            if (v < -400f) v = -400f;
            if (v > 900f) v = 900f;
            Cfg.BubbleDropDown.Value = v;
            try { Cfg.BubbleDropDown.ConfigFile.Save(); } catch { }
            Dbg("bubble nudge -> DropDown=" + v.ToString("F0"));

            // Re-show the last line at the new spot so the change is visible at once.
            RefreshBubble();
        }

        private static string _lastBubbleText;

        /// <summary>Re-displays the last spoken line (used after a live nudge).</summary>
        internal static void RefreshBubble()
        {
            if (string.IsNullOrEmpty(_lastBubbleText)) return;
            try
            {
                BubbleUi.Show(_lastBubbleText, ComputeSpeakPos());
            }
            catch (Exception e) { Dbg("RefreshBubble: " + e.Message); }
        }

        /// <summary>
        /// How long a line should stay on screen. A fixed 8 s is right for the pet's own
        /// one-liners, but an AI answer of two or three sentences needs reading time, so
        /// the duration grows with the text (roughly one extra second per nine chars).
        /// </summary>
        private static float BubbleSecondsFor(string text)
        {
            int len = string.IsNullOrEmpty(text) ? 0 : text.Length;
            float sec = 8f + len / 9f;
            if (sec < 8f) sec = 8f;
            if (sec > 40f) sec = 40f;
            return sec;
        }

        /// <summary>Speaks a line with an explicit on-screen duration.</summary>
        private static void ShowBubble(string text, float seconds)
        {
            try { BubbleUi.NextShowSeconds = seconds; } catch { }
            ShowBubble(text);
        }

        /// <summary>Make the character say something using the game's own UI.</summary>
        private static void ShowBubble(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            var pos = ComputeSpeakPos();

            // Remember it so Ctrl+Up / Ctrl+Down can re-show the same line while the
            // bubble's height is being tuned.
            _lastBubbleText = text;

            // Preferred: our own bubble, cloned from the game's DialogueBubble so it
            // uses the native panel art, font and pop animation. This is the big,
            // auto-wrapping, head-anchored bubble; UI.TipsWidget (below) is the
            // game's one-line strip and was reported as tiring to read.
            try
            {
                if (BubbleUi.Show(text, pos)) return;
            }
            catch (Exception e) { Dbg("ShowBubble custom path: " + e.Message); }

            // Fallback: the game's own floating-text widget.
            try
            {
                var tips = FindWidget<UI.TipsWidget>("UI.TipsWidget");
                if (tips != null)
                {
                    tips._tipsTxt.text = text;

                    var rt = tips._rectTf;
                    if (rt != null) rt.anchoredPosition = pos;

                    tips.gameObject.SetActive(true);

                    var cg = tips._canvasGroup;
                    if (cg != null)
                    {
                        cg.alpha = 1f;
                        cg.blocksRaycasts = false;
                        cg.interactable = false;
                    }

                    Dbg("ShowBubble OK via TipsWidget at " + pos + ": " + text);
                    return;
                }
                Dbg("ShowBubble: no UI.TipsWidget instance");
            }
            catch (Exception e)
            {
                Dbg("ShowBubble TipsWidget: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// Finds a live UI widget by interop type name and casts it to T.
        /// Delegates to the generic overload when the compile-time type is known.
        /// </summary>
        private static T FindWidget<T>(string typeFullName)
            where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
        {
            try
            {
                var t = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => { try { return a.GetType(typeFullName); } catch { return null; } })
                    .FirstOrDefault(x => x != null);
                if (t == null) { Dbg("FindWidget: type not found " + typeFullName); return null; }

                var il2 = Il2CppInterop.Runtime.Il2CppType.From(t);
                var arr = UnityEngine.Resources.FindObjectsOfTypeAll(il2);
                if (arr == null || arr.Length == 0) { Dbg("FindWidget: no instance of " + typeFullName); return null; }

                var first = arr[0];
                var objBase = first as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                if (objBase == null) { Dbg("FindWidget: wrapper is not Il2CppObjectBase"); return null; }

                var typed = objBase.TryCast<T>();
                if (typed == null) Dbg("FindWidget: TryCast<" + typeof(T).Name + "> returned null");
                return typed;
            }
            catch (Exception e)
            {
                Dbg("FindWidget(" + typeFullName + ") failed: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Where to put the speech bubble, in the game's UI space.
        ///
        /// Anchored to the CHARACTER's head, not the cursor: the reference pet always
        /// puts its bubble over the pet's head, which is what the user asked for.
        /// Delegates to the game's own UI.TopicImaginationInputWidget.GetHeadWorldPosition,
        /// because the character is a Spine skeleton and guessing from sprite bounds
        /// picked up a small accessory instead (world ended up at (-9,-7)).
        ///
        /// The game's UI reads a visible position as (sp.x, Screen.height - sp.y),
        /// where sp = Camera.WorldToScreenPoint.
        /// </summary>
        private static UnityEngine.Vector2 ComputeSpeakPos()
        {
            try
            {
                var cam = UnityEngine.Camera.main;
                if (cam != null && SpeakAnchorMb != null)
                    return BubbleUi.HeadUiPosition(cam);
            }
            catch (Exception e) { Dbg("ComputeSpeakPos: " + e.Message); }

            return FallbackSpeakPos();
        }

        /// <summary>Used when no character anchor is known yet.</summary>
        internal static UnityEngine.Vector2 FallbackSpeakPos()
        {
            return new UnityEngine.Vector2(UnityEngine.Screen.width * 0.5f,
                                           UnityEngine.Screen.height * 0.55f);
        }

        // ------------------------------------------------- generic widget finder

        /// <summary>
        /// One-shot discovery dump: lists the UI widgets that actually EXIST in the
        /// running scene. Added because static analysis of the interop assembly
        /// cannot tell which prefabs the game instantiates at runtime -- e.g.
        /// UI.RelationToastWidget is absent until something pops a toast.
        /// </summary>
        private static bool _uiDumped;

        internal static void DumpLiveUiOnce()
        {
            if (_uiDumped) return;
            _uiDumped = true;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
                if (asm == null) { Dbg("DumpLiveUi: no Assembly-CSharp"); return; }

                // ---- the game's own speech-bubble art, so a custom bubble can
                //      reuse the same sprites instead of a plain tips strip
                var bubbleType = asm.GetType("UI.DialogueBubble");
                if (bubbleType != null)
                {
                    var il2b = Il2CppInterop.Runtime.Il2CppType.From(bubbleType);
                    var barr = UnityEngine.Resources.FindObjectsOfTypeAll(il2b);
                    Dbg("=== DialogueBubble instances: " + (barr == null ? 0 : barr.Length) + " ===");
                    int shown = 0;
                    for (int i = 0; barr != null && i < barr.Length && shown < 5; i++)
                    {
                        var ob = barr[i] as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                        if (ob == null) continue;
                        var b = ob.TryCast<UI.DialogueBubble>();
                        if (b == null) continue;

                        string parent = "?", size = "?";
                        try
                        {
                            var rt = b.GetComponent<UnityEngine.RectTransform>();
                            if (rt != null)
                            {
                                parent = rt.parent != null ? rt.parent.name : "<root>";
                                size = rt.sizeDelta.x.ToString("F0") + "x" + rt.sizeDelta.y.ToString("F0");
                            }
                        }
                        catch { }

                        bool hasSprites = false;
                        try { hasSprites = b._smallBubble != null || b._middleBubble != null || b._bigBubble != null; }
                        catch { }

                        Dbg(string.Format("  bubble[{0}] inHierarchy={1} selfActive={2} parent={3} size={4} sprites={5}",
                            i, b.gameObject.activeInHierarchy, b.gameObject.activeSelf, parent, size, hasSprites));
                        shown++;
                    }
                }
                else Dbg("DumpLiveUi: UI.DialogueBubble type not found");

                string[] needles = { "Dialogue", "Bubble", "Toast", "Talk", "Tip", "Notify", "Chat", "Panel", "Widget" };
                var found = new List<string>();

                foreach (var t in asm.GetTypes())
                {
                    string n = t.Name;
                    if (n.Length < 3) continue;
                    if (!needles.Any(k => n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    if (!typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(t)) continue;

                    try
                    {
                        var il2 = Il2CppInterop.Runtime.Il2CppType.From(t);
                        var arr = UnityEngine.Resources.FindObjectsOfTypeAll(il2);
                        if (arr == null || arr.Length == 0) continue;

                        int active = 0;
                        foreach (var o in arr)
                        {
                            try
                            {
                                var mb = o.TryCast<UnityEngine.MonoBehaviour>();
                                if (mb != null && mb.gameObject.activeInHierarchy) active++;
                            }
                            catch { }
                        }
                        found.Add(string.Format("{0}  total={1} active={2}", t.FullName, arr.Length, active));
                    }
                    catch { }
                }

                found.Sort();
                Dbg("=== LIVE UI DUMP (" + found.Count + " types with instances) ===");
                foreach (var f in found) Dbg("  UI " + f);
                Dbg("=== END LIVE UI DUMP ===");
            }
            catch (Exception e)
            {
                Dbg("DumpLiveUiOnce failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static string Trunc(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }
    }
}

