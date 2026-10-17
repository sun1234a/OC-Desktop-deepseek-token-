using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using BepInEx.Unity.IL2CPP.Utils;
using UnityEngine;
using UnityEngine.Events;

namespace OcAI
{
    /// <summary>
    /// AI chat, triggered by right-clicking the character while she is SITTING.
    ///
    /// Flow:
    ///   right-click (TouchPatch picks up the button edge)
    ///     -> plugin checks SitState.IsSitting()
    ///     -> the game's own text-input modal pops up (TipsManager.ShowInputTxt)
    ///     -> the typed line goes to DeepSeek as a chat message with history
    ///     -> her answer comes back on a worker thread, is queued, and the bubble
    ///        shows it on the Unity main thread (Plugin.EnqueueSpeak)
    ///
    /// Threading: every Unity call (the modal, the bubble) runs on the main thread;
    /// only the HTTP round-trip happens on a task thread. The modal callback already
    /// arrives on the main thread, so it may touch UI directly.
    ///
    /// Memory: history is a ConcurrentQueue because the worker thread appends the
    /// reply while the main thread may read the list for the next request.
    /// Commands typed into the same modal: /clear (or /reset), /help.
    /// </summary>
    internal static class ChatFlow
    {
        /// <summary>Recent exchanges, oldest first. Each entry is a role/content pair.</summary>
        private static readonly ConcurrentQueue<Dictionary<string, string>> History =
            new ConcurrentQueue<Dictionary<string, string>>();

        /// <summary>Guards against a second request while one is in flight.</summary>
        private static int _inFlight;

        /// <summary>Set while our modal is the one on screen, so the key editor stays apart.</summary>
        internal static bool InputOpen;

        private static DateTime _inputOpenedAt = DateTime.MinValue;

        /// <summary>
        /// True while our input modal should be up. Self-healing: if the game closes the
        /// modal without calling either callback (ESC, a scene change), a stale flag
        /// would block every later click, so it expires on its own after a while.
        /// </summary>
        internal static bool IsInputOpen
        {
            get
            {
                if (!InputOpen) return false;
                if ((DateTime.UtcNow - _inputOpenedAt).TotalSeconds > InputTimeoutSeconds)
                {
                    Plugin.Dbg("ChatFlow: input flag expired after timeout");
                    ReleaseInput();
                    return false;
                }
                return true;
            }
        }

        /// <summary>How long the modal may stay "open" without a callback.</summary>
        private const float InputTimeoutSeconds = 180f;

        private static void ReleaseInput()
        {
            InputOpen = false;
            _inputOpenedAt = DateTime.MinValue;
            Interlocked.Exchange(ref _inFlight, 0);
        }

        internal static int HistoryCount { get { return History.Count; } }

        /// <summary>Clears the remembered dialogue. Returns false when it was already empty.</summary>
        internal static bool ClearHistory()
        {
            bool had = !History.IsEmpty;
            Dictionary<string, string> drop;
            while (History.TryDequeue(out drop)) { }

            // Log to BOTH sinks: the BepInEx console log carries Info, and the plugin's
            // own debug file carries Dbg. Verification reads either one, and a missing
            // line in one file must not look like "the feature did not run".
            Plugin.Dbg("=== chat memory cleared, had=" + had + " ===");
            Plugin.Info("[OcAI] chat memory cleared (had" + (had ? "" : " nothing") + ")");
            return had;
        }

        // ------------------------------------------------------------ entry point

        /// <summary>
        /// Right-click while sitting. Returns true when the request was accepted (a
        /// modal was opened, or a clear command ran), false when nothing happened
        /// because the plugin is off or a request is already running.
        /// </summary>
        internal static bool TryOpenInput()
        {
            if (Plugin.Cfg == null || !Plugin.PluginEnabled) return false;

            if (IsInputOpen)
            {
                Plugin.Dbg("ChatFlow: input modal already open");
                return false;
            }

            if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            {
                Plugin.EnqueueSpeak("我还在想上一个问题……");
                return false;
            }

            bool opened = false;
            try
            {
                string tips = BuildTips();
                opened = ShowInput(tips);

                if (opened)
                {
                    InputOpen = true;
                    _inputOpenedAt = DateTime.UtcNow;
                    if (Plugin.Cfg.EnableCallSound.Value) CallSound.PlayOk();
                    Plugin.Info("[OcAI] chat input opened (sitting=" + SitState.LastVerdict + ")");
                }
            }
            catch (Exception e)
            {
                Plugin.Dbg("ChatFlow.TryOpenInput: " + e.GetType().Name + ": " + e.Message);
                opened = false;
            }
            finally
            {
                if (!opened)
                {
                    Interlocked.Exchange(ref _inFlight, 0);
                    Plugin.EnqueueSpeak("打不开输入框，稍后再试");
                }
            }
            return opened;
        }

        private static string BuildTips()
        {
            string head = Plugin.Cfg != null ? Plugin.Cfg.ChatInputTips.Value : "";
            if (string.IsNullOrEmpty(head)) head = "想和我说什么？";
            if (HistoryCount == 0) return head;
            return head + "（已记 " + (HistoryCount / 2) + " 轮，输入 /clear 清空）";
        }

        // ------------------------------------------------------------- the modal

        /// <summary>
        /// Pops the game's own input modal, the same widget the API-key editor uses.
        /// TipsManager.ShowInputTxt is the game's generic entry point:
        ///   ShowInputTxt(Vector2 pos, string tips, string default,
        ///                UnityAction&lt;string&gt; confirm, UnityAction cancel)
        /// Note the callbacks must be IL2CPP delegates, so the implicit conversions
        /// from the plain System.Action / System.Action&lt;string&gt; are used.
        /// </summary>
        private static bool ShowInput(string tips)
        {
            TipsManager mgr = FindTipsManager();
            if (mgr == null)
            {
                Plugin.Dbg("ChatFlow: TipsManager instance not found");
                return false;
            }

            Vector2 pos = InputAnchor();

            UnityAction<string> onConfirm = new Action<string>(OnTextEntered);
            UnityAction onCancel = new Action(OnCancelled);

            try
            {
                mgr.ShowInputTxt(pos, tips, "", onConfirm, onCancel);
                Plugin.Dbg("ChatFlow: ShowInputTxt called at " + pos + " tips='" + tips + "'");

                // Measure where the box ACTUALLY ended up. Guessing the coordinate space
                // cost several rounds, so the plugin now reports the widget's real screen
                // rectangle and lets the log settle the question.
                _measured = false;
                _measureAt = DateTime.UtcNow;

                // ShowInputTxt's position argument turned out to be ignored for the panel
                // itself (measured: the panel sits at a fixed spot under Canvas/ToastLayer
                // no matter what is passed). So the panel is positioned by hand instead:
                // ApplyInputPosition runs every pump tick while our modal is open, which
                // also beats the game's own layout pass if it re-anchors.
                _panelRt = null;
                _positionApplied = false;
                InputOpen = true;
                _inputOpenedAt = DateTime.UtcNow;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Dbg("ChatFlow.ShowInputTxt: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        private static DateTime _measureAt = DateTime.MinValue;
        private static bool _measured;
        private static RectTransform _panelRt;
        private static bool _positionApplied;

        /// <summary>
        /// Keeps the input panel in the bottom-right corner while it is open.
        ///
        /// Why this is done by hand: ShowInputTxt's Vector2 argument does not move the
        /// panel -- measured live, the panel ('PresetConfirm_Widget/Bg', 949x543) stays
        /// anchored under Canvas/ToastLayer at a fixed spot whatever is passed in. Its
        /// local axes in that layer line up with pixels, so anchoredPosition is a
        /// pixel-precise handle: the panel centre goes to the middle of the screen, then
        /// the configured margins are subtracted.
        /// </summary>
        internal static void ApplyInputPosition()
        {
            try
            {
                if (!IsInputOpen) return;

                if (_panelRt == null)
                {
                    _panelRt = FindInputPanel();
                    if (_panelRt == null) return;
                    Plugin.Dbg("ApplyInputPosition: panel = '" + _panelRt.name + "' size=" +
                               _panelRt.rect.size.x.ToString("F0") + "x" +
                               _panelRt.rect.size.y.ToString("F0"));
                }

                // The panel may be re-anchored by the game's own layout; re-assert while
                // it is open, but only when it has actually drifted (cheap in steady state).
                float marginX = Plugin.Cfg != null ? Plugin.Cfg.InputOffsetX.Value : 420f;
                float marginY = Plugin.Cfg != null ? Plugin.Cfg.InputOffsetY.Value : 90f;

                var size = _panelRt.rect.size;
                float sw = UnityEngine.Screen.width;
                float sh = UnityEngine.Screen.height;

                // Centre of the wanted position, in screen pixels (origin bottom-left,
                // which is also this layer's local axis direction).
                //
                // VERTICAL FLIP -- the bug that made every previous attempt land in the wrong
                // place. Measured live: with the panel at position.y=272 (Unity calls that
                // "BOTTOM-RIGHT") the user saw it in the TOP-right corner. So this window's
                // present coordinates are inverted relative to the panel layer's position
                // axis: a larger position.y draws LOWER on screen. The wanted screen height
                // is therefore mirrored before being applied.
                float cx = sw - marginX - size.x * 0.5f;
                float cy = sh - (marginY + size.y * 0.5f);

                var want = new Vector2(cx, cy);

                var cur = _panelRt.position;
                float dx = cur.x - cx;
                float dy = cur.y - cy;

                // Anti-regression: while a drag is in progress this method must NOT write the
                // position, or the two would fight and the panel would appear stuck.
                //
                // DragToPlace is also what STARTS a drag, so it has to be called while the
                // flag is still false; it returns immediately when the button is not held.
                if (!_dragging) DragToPlace(marginX, marginY, cx, cy);

                if (_dragging) return;

                if (!_positionApplied || Mathf.Abs(dx) > 1.5f || Mathf.Abs(dy) > 1.5f)
                {
                    _panelRt.position = new UnityEngine.Vector3(cx, cy, cur.z);
                    _positionApplied = true;
                    Plugin.Dbg(string.Format(
                        "ApplyInputPosition: want=({0:F0},{1:F0}) was=({2:F0},{3:F0}) delta=({4:F0},{5:F0}) margin=({6:F0},{7:F0})",
                        cx, cy, cur.x, cur.y, dx, dy, marginX, marginY));
                }
            }
            catch (Exception e)
            {
                Plugin.Dbg("ApplyInputPosition: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool _dragging;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private const int VK_LBUTTON = 0x01;

        /// <summary>
        /// Lets the panel be dragged with the mouse and remembers the result.
        ///
        /// The first version computed a delta and asked whether it exceeded a threshold,
        /// which did not make the panel follow the cursor (reported: "x 拖不动"). This one
        /// drives the panel straight from the OS cursor position while the left button is
        /// held, which is what "drag" means to a user. The cursor is read through Win32
        /// rather than UnityEngine.Input so it cannot be swallowed by the input field's
        /// focus.
        ///
        /// Axis mapping: the cursor's y grows downwards, the panel layer's grows upwards,
        /// hence the flip.
        /// </summary>
        private static void DragToPlace(float marginX, float marginY, float wantX, float wantY)
        {
            try
            {
                bool down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

                if (!down)
                {
                    if (_dragging)
                    {
                        _dragging = false;
                        _positionApplied = false;   // re-assert from the saved margins
                        Plugin.Dbg("drag ended: margins right=" +
                                   (Plugin.Cfg != null ? Plugin.Cfg.InputOffsetX.Value.ToString("F0") : "?") +
                                   " bottom=" +
                                   (Plugin.Cfg != null ? Plugin.Cfg.InputOffsetY.Value.ToString("F0") : "?") +
                                   " (saved)");
                    }
                    _pressPending = false;
                    return;
                }

                POINT p;
                if (!GetCursorPos(out p)) return;

                float sw = UnityEngine.Screen.width;
                float sh = UnityEngine.Screen.height;
                float px = p.X;
                float py = p.Y;                  // y axis MATCHES the panel layer (the flip
                                                 // is in how the window is presented, so both
                                                 // axes grow the same way here); see the
                                                 // VERTICAL FLIP note in ApplyInputPosition.

                if (!_dragging)
                {
                    if (!_pressPending)
                    {
                        // A button press only ARMS a drag. Deliberately no "over the panel"
                        // test: the panel's reported position and the rendered position do
                        // not share an origin in this window, so that test silently rejected
                        // every real drag (reported: "拖不动"). The movement threshold below
                        // is what keeps a normal click from becoming a drag.
                        _pressPending = true;
                        _pressCursorX = px;
                        _pressCursorY = py;
                        _lastCursorX = px;
                        _lastCursorY = py;
                        return;
                    }

                    // Anti-regression: require real movement before stealing the gesture.
                    // Without this, a plain click into the text field counted as a drag and
                    // the panel slid out from under the user.
                    float movedFromPress = Mathf.Abs(px - _pressCursorX) + Mathf.Abs(py - _pressCursorY);
                    if (movedFromPress < 10f) return;

                    _dragging = true;
                    Plugin.Dbg("drag started (cursor " + px.ToString("F0") + "," + py.ToString("F0") + ")");
                }

                // Drag by the cursor's MOVEMENT, keeping the grab offset: setting the panel
                // centre straight to the cursor would jump it under the pointer.
                var now = _panelRt.position;
                float stepX = px - _lastCursorX;
                float stepY = py - _lastCursorY;
                _lastCursorX = px;
                _lastCursorY = py;

                float nx = now.x + stepX;
                float ny = now.y + stepY;

                // Keep the panel inside the screen while dragging.
                float hw = _panelRt.rect.size.x * 0.5f;
                float hh = _panelRt.rect.size.y * 0.5f;
                if (nx < hw) nx = hw;
                if (nx > sw - hw) nx = sw - hw;
                if (ny < hh) ny = hh;
                if (ny > sh - hh) ny = sh - hh;

                _panelRt.position = new UnityEngine.Vector3(nx, ny, now.z);

                // Convert the live position back into margins so the choice survives a
                // restart. Both formulas are the inverse of the mapping in
                // ApplyInputPosition, INCLUDING the vertical flip: marginY is a distance from
                // the screen bottom, so it is measured from the top of the panel layer's axis.
                float newMarginX = sw - (nx + hw);
                float newMarginY = sh - (ny + hh);
                if (newMarginX < 0f) newMarginX = 0f;
                if (newMarginY < 0f) newMarginY = 0f;

                if (Plugin.Cfg != null)
                {
                    Plugin.Cfg.InputOffsetX.Value = newMarginX;
                    Plugin.Cfg.InputOffsetY.Value = newMarginY;
                    try { Plugin.Cfg.InputOffsetX.ConfigFile.Save(); } catch { }
                }

                // Throttled feedback so the drag can be confirmed from the log while it is
                // happening (the previous run only had "drag started" and no end marker, so
                // it was impossible to tell whether the drag ever took effect).
                double sinceLog = (DateTime.UtcNow - _lastDragLog).TotalSeconds;
                if (sinceLog > 0.4)
                {
                    _lastDragLog = DateTime.UtcNow;
                    Plugin.Dbg(string.Format(
                        "dragging: panel=({0:F0},{1:F0}) i.e. '{2}' screen={3:F0}x{4:F0} -> margins right={5:F0} bottom={6:F0}",
                        nx, ny,
                        (ny > sh * 0.66f ? "TOP" : (ny < sh * 0.33f ? "BOTTOM" : "MIDDLE")) + "-" +
                        (nx < sw * 0.33f ? "LEFT" : (nx > sw * 0.66f ? "RIGHT" : "CENTER")),
                        sw, sh, newMarginX, newMarginY));
                }
            }
            catch (Exception e)
            {
                Plugin.Dbg("DragToPlace: " + e.Message);
            }
        }

        private static bool _pressPending;
        private static float _pressCursorX;
        private static float _pressCursorY;
        private static float _lastCursorX;
        private static float _lastCursorY;
        private static DateTime _lastDragLog = DateTime.MinValue;

        /// <summary>
        /// The container to move.
        ///
        /// First attempt moved 'Bg' (measured 949x543 inside PresetConfirm_Widget) and the
        /// user reported no visible change at all -- so the visible dialog is an ancestor,
        /// not that image. The container chain measured live was:
        ///
        ///   Canvas/ToastLayer/Tips/PresetConfirm_Widget/Bg/InputTxt_Widget
        ///
        /// So the panel is resolved by walking up from the live input widget and taking
        /// the first ancestor whose rect is big enough to be the dialog, which yields
        /// PresetConfirm_Widget itself (and falls back to a named lookup if the walk
        /// fails).
        /// </summary>
        private static RectTransform FindInputPanel()
        {
            try
            {
                var t = FindType("UI.InputTxtWidget");
                if (t == null) { Plugin.Dbg("FindInputPanel: InputTxtWidget type not found"); return null; }

                var arr = UnityEngine.Resources.FindObjectsOfTypeAll(
                    Il2CppInterop.Runtime.Il2CppType.From(t));
                if (arr == null) { Plugin.Dbg("FindInputPanel: no instances"); return null; }

                for (int i = 0; i < arr.Length; i++)
                {
                    var ob = arr[i] as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                    if (ob == null) continue;
                    var mb = ob.TryCast<UnityEngine.MonoBehaviour>();
                    if (mb == null) continue;
                    if (!mb.gameObject.activeInHierarchy) continue;

                    // Walk up and prefer the PARENT OF Bg (the dialog), but never go past
                    // a full-screen layer: Tips (100x100) and ToastLayer (whole screen) are
                    // containers we must not drag along.
                    var tr = mb.transform;
                    RectTransform best = null;
                    for (int lvl = 0; lvl < 6 && tr != null; lvl++)
                    {
                        var rt = tr.TryCast<RectTransform>();
                        if (rt != null)
                        {
                            var size = rt.rect.size;
                            bool fullScreen = size.x >= UnityEngine.Screen.width * 0.98f ||
                                              size.y >= UnityEngine.Screen.height * 0.98f;
                            if (!fullScreen && size.x >= 120f && size.y >= 80f) best = rt;
                        }
                        tr = tr.parent;
                    }

                    if (best != null)
                    {
                        // Report the visible size (Bg) as well: the container picked here may
                        // be a small wrapper around a much larger Bg, and it is the Bg that
                        // decides how much room the dialog actually needs.
                        string vis = "-";
                        try
                        {
                            var p = best.parent;
                            if (p != null)
                            {
                                var prt = p.TryCast<RectTransform>();
                                if (prt != null) vis = prt.rect.size.x.ToString("F0") + "x" +
                                                       prt.rect.size.y.ToString("F0");
                            }
                        }
                        catch { }

                        Plugin.Dbg("FindInputPanel: chosen '" + best.name + "' size=" +
                                   best.rect.size.x.ToString("F0") + "x" +
                                   best.rect.size.y.ToString("F0") + " parentSize=" + vis);
                        return best;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Dbg("FindInputPanel: " + e.GetType().Name + ": " + e.Message);
            }
            return null;
        }

        /// <summary>
        /// Called from the pump a few frames after the modal opened: locates the live
        /// input widget and logs its screen rectangle, converted to a corner-relative
        /// description so the position can be judged without a screenshot.
        /// </summary>
        internal static void MeasureInputBox()
        {
            if (_measured || _measureAt == DateTime.MinValue) return;
            if ((DateTime.UtcNow - _measureAt).TotalSeconds < 0.35) return;
            _measured = true;

            try
            {
                var t = FindType("UI.InputTxtWidget") ?? FindType("UI.SelectTopicWidget");
                if (t == null) { Plugin.Dbg("MeasureInputBox: widget type not found"); return; }

                var arr = UnityEngine.Resources.FindObjectsOfTypeAll(
                    Il2CppInterop.Runtime.Il2CppType.From(t));
                if (arr == null || arr.Length == 0)
                {
                    Plugin.Dbg("MeasureInputBox: no live " + t.Name + " instance");
                    return;
                }

                for (int i = 0; i < arr.Length && i < 4; i++)
                {
                    var ob = arr[i] as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                    if (ob == null) continue;
                    var mb = ob.TryCast<UnityEngine.MonoBehaviour>();
                    if (mb == null) continue;
                    if (!mb.gameObject.activeInHierarchy) continue;

                    float sw = UnityEngine.Screen.width;
                    float sh = UnityEngine.Screen.height;

                    // Walk UP from the widget: the widget itself is often a zero-sized
                    // anchor and the visible panel is an ancestor.
                    //
                    // MEASUREMENT FIX: this canvas is Screen Space - Overlay, where
                    // camera.WorldToScreenPoint() collapses every corner onto the screen
                    // centre (measured: all six levels reported (960,540)). BubbleUi hit
                    // exactly the same trap and solved it by reading RectTransform
                    // .position directly, because for an Overlay canvas that value is
                    // already in screen pixels. Same approach here.
                    var tr = mb.transform;
                    int level = 0;
                    while (tr != null && level < 8)
                    {
                        string path = tr.name;
                        try
                        {
                            var parent = tr.parent;
                            if (parent != null) path = parent.name + "/" + path;
                        }
                        catch { }

                        var rt = tr.TryCast<RectTransform>();
                        if (rt != null)
                        {
                            var size = rt.rect.size;
                            var world = rt.position;
                            float ux = world.x;
                            float uy = sh - world.y;      // world(y-up) -> UI(y-down)

                            string hCorner = ux < sw * 0.33f ? "LEFT" : (ux > sw * 0.66f ? "RIGHT" : "CENTER");
                            string vCorner = uy < sh * 0.33f ? "TOP" : (uy > sh * 0.66f ? "BOTTOM" : "MIDDLE");

                            Plugin.Dbg(string.Format(
                                "MeasureInputBox: L{0} '{1}' size={2:F0}x{3:F0} uiPos=({4:F0},{5:F0}) -> {6}-{7}",
                                level, path, size.x, size.y, ux, uy, vCorner, hCorner));
                        }
                        else
                        {
                            Plugin.Dbg("MeasureInputBox: L" + level + " '" + path + "' (no RectTransform)");
                        }

                        tr = tr.parent;
                        level++;
                    }
                    return;
                }

                Plugin.Dbg("MeasureInputBox: no active widget instance to measure");
            }
            catch (Exception e)
            {
                Plugin.Dbg("MeasureInputBox: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// Where the modal should hang: the BOTTOM-RIGHT corner of the screen.
        ///
        /// Coordinate facts, measured against a live game rather than assumed (this took
        /// two wrong attempts, so it is written down):
        ///   * TipsManager.ShowInputTxt takes a SCREEN point with the origin at the
        ///     TOP-LEFT and y growing DOWNWARDS -- the same direction as UI space.
        ///   * Evidence: asking for y=200 put the box in the upper part of the screen,
        ///     y=624 and y=954 both moved it DOWN, i.e. smaller y = higher up.
        ///   * So no flip is needed: y_screen = Screen.height - marginFromBottom.
        ///
        /// The box is placed a fixed margin from the corner instead of relative to the
        /// character, because she can be anywhere on the desktop while the typing spot
        /// should be predictable.
        /// </summary>
        private static Vector2 InputAnchor()
        {
            try
            {
                float w = UnityEngine.Screen.width;
                float h = UnityEngine.Screen.height;

                // Sensible fallbacks when the screen size is not readable yet.
                if (w <= 0f) w = 1920f;
                if (h <= 0f) h = 1080f;

                float marginX = Plugin.Cfg != null ? Plugin.Cfg.InputOffsetX.Value : 420f;
                float marginY = Plugin.Cfg != null ? Plugin.Cfg.InputOffsetY.Value : 90f;

                // y grows downwards, so subtract the margin from the bottom edge.
                float x = w - marginX;
                float y = h - marginY;

                // Clamp so a wild config value cannot push the box off screen.
                if (x < 200f) x = 200f;
                if (x > w - 120f) x = w - 120f;
                if (y < 140f) y = 140f;
                if (y > h - 60f) y = h - 60f;

                Plugin.Dbg("ChatFlow input anchor: screen=" + w + "x" + h +
                           " margin=(right " + marginX.ToString("F0") + ", bottom " + marginY.ToString("F0") +
                           ") -> (" + x.ToString("F0") + "," + y.ToString("F0") + ") [screen coords, y from TOP]");
                return new Vector2(x, y);
            }
            catch (Exception e)
            {
                Plugin.Dbg("ChatFlow.InputAnchor: " + e.Message);
                return Plugin.FallbackSpeakPos();
            }
        }

        private static TipsManager FindTipsManager()
        {
            try
            {
                var m = TipsManager.Instance;
                if (m != null) return m;
            }
            catch (Exception e) { Plugin.Dbg("ChatFlow TipsManager.Instance: " + e.Message); }

            // Fallback: locate the singleton in the scene (it may not be created yet).
            try
            {
                var t = FindType("TipsManager");
                if (t != null)
                {
                    var arr = UnityEngine.Resources.FindObjectsOfTypeAll(
                        Il2CppInterop.Runtime.Il2CppType.From(t));
                    if (arr != null && arr.Length > 0)
                    {
                        var ob = arr[0] as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                        if (ob != null) return ob.TryCast<TipsManager>();
                    }
                }
            }
            catch (Exception e) { Plugin.Dbg("ChatFlow FindTipsManager scan: " + e.Message); }
            return null;
        }

        private static Type FindType(string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = a.GetType(name);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        // --------------------------------------------------------- modal results

        private static void OnCancelled()
        {
            Plugin.Dbg("ChatFlow: input cancelled");
            ReleaseInput();
        }

        /// <summary>Modal confirm. Runs on the Unity main thread.</summary>
        private static void OnTextEntered(string text)
        {
            try
            {
                text = (text ?? "").Trim();

                if (text.Length == 0)
                {
                    ReleaseInput();
                    return;
                }

                // The modal itself is done with, but the request lock stays held until the
                // answer arrives: right-clicking during the wait says so instead of
                // starting a second request.
                InputOpen = false;
                _inputOpenedAt = DateTime.MinValue;

                if (text[0] == '/')
                {
                    HandleCommand(text.ToLowerInvariant());
                    return;   // HandleCommand releases the in-flight flag
                }

                Interlocked.Exchange(ref _inFlight, 0);
                Send(text);
            }
            catch (Exception e)
            {
                Plugin.Dbg("ChatFlow.OnTextEntered: " + e.GetType().Name + ": " + e.Message);
                ReleaseInput();
                Plugin.EnqueueSpeak("出了点问题，再说一次？");
            }
        }

        private static void HandleCommand(string cmd)
        {
            try
            {
                switch (cmd)
                {
                    case "/clear":
                    case "/reset":
                    case "/清除":
                        bool had = ClearHistory();
                        Plugin.EnqueueSpeak(had ? "好，之前聊的都忘掉啦" : "我们还没聊过呢");
                        break;

                    case "/help":
                    case "/帮助":
                        Plugin.EnqueueSpeak("直接打字和我聊天；/clear 清空记忆，/help 看这条");
                        break;

                    default:
                        Plugin.EnqueueSpeak("不认识的命令，试试 /clear 或 /help");
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Dbg("ChatFlow.HandleCommand: " + e.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _inFlight, 0);
            }
        }

        // ------------------------------------------------------------- the request

        private static void Send(string userText)
        {
            Plugin.Info("[OcAI] chat -> '" + userText + "' (" + HistoryCount / 2 + " turns of memory)");
            Plugin.Dbg("=== chat -> '" + userText + "' (" + HistoryCount / 2 + " turns of memory) ===");
            if (Plugin.Cfg.EnableCallSound.Value) CallSound.PlayOk();

            // The history is recorded here, once. Search results go into the SYSTEM
            // prompt of this single request instead, so the next turn does not carry
            // stale web pages around (and does not pay for them).
            Remember("user", userText);

            var token = Plugin.ResetToken();
            float temperature = Plugin.Cfg.Temperature.Value;
            int maxTokens = Plugin.Cfg.ChatMaxTokens.Value;

            bool wantWeb = Plugin.Cfg.EnableWebSearch.Value &&
                           (Plugin.Cfg.SearchAlways.Value || WebSearch.LooksLikeNeedsWeb(userText));

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    string webContext = null;
                    if (wantWeb)
                    {
                        try
                        {
                            Plugin.Dbg("WebSearch: querying for '" + userText + "'");
                            var sr = await WebSearch.SearchAsync(userText, token).ConfigureAwait(false);
                            Plugin.Dbg("WebSearch: " + WebSearch.Describe(sr));
                            webContext = WebSearch.FormatForPrompt(userText, sr);
                            if (webContext == null) Plugin.Dbg("WebSearch: no usable context, answering offline");
                        }
                        catch (Exception e)
                        {
                            Plugin.Dbg("WebSearch failed: " + e.GetType().Name + ": " + e.Message);
                        }
                    }

                    var messages = BuildMessages(userText, webContext);
                    var r = await Plugin.Api.ChatAsync(messages, temperature, maxTokens, token)
                                        .ConfigureAwait(false);

                    if (r.Ok)
                    {
                        string answer = CleanAnswer(r.Text);
                        Plugin.Info("[OcAI] chat <- '" + Shorten(answer, 120) + "' tokens=" + r.TotalTokens);
                        Plugin.Dbg("=== chat <- '" + Shorten(answer, 200) + "' tokens=" + r.TotalTokens + " ===");

                        Remember("assistant", answer);
                        DisplayAnswer(answer);
                    }
                    else
                    {
                        Plugin.Info("[OcAI] chat failed: " + r.Error);
                        if (Plugin.Cfg.EnableCallSound.Value) CallSound.PlayFail();
                        Plugin.EnqueueSpeak(Plugin.ShortErrorPublic(r.Error));
                    }
                }
                catch (Exception e)
                {
                    Plugin.Dbg("ChatFlow.Send task: " + e.GetType().Name + ": " + e.Message);
                    Plugin.EnqueueSpeak("网络好像不太顺");
                }
                finally
                {
                    Interlocked.Exchange(ref _inFlight, 0);
                }
            }, token);
        }

        /// <summary>
        /// Persona + trimmed history + the new line. The history cap keeps the prompt
        /// (and the bill) small, which is the whole point of a desktop pet.
        /// </summary>
        /// <summary>
        /// System prompt + trimmed history + the new line.
        ///
        /// webContext, when present, carries the freshly searched pages for THIS request.
        /// It is appended to the system prompt rather than to the history, so the next
        /// turn neither reuses stale web text nor pays for it again.
        ///
        /// The caller is responsible for Remember("user", ...): the search happens
        /// asynchronously between recording the question and building the request.
        /// </summary>
        private static List<Dictionary<string, string>> BuildMessages(string userText, string webContext)
        {
            var list = new List<Dictionary<string, string>>();

            string system = BuildSystemPrompt();

            // Her own game state, but only when the line is actually about it. Reading
            // it on every message would burn tokens on small talk and invite the model
            // to volunteer numbers nobody asked for.
            string gameCtx = null;
            if (GameContext.WantsGameContext(userText))
            {
                gameCtx = GameContext.Build();
                if (!string.IsNullOrEmpty(gameCtx)) system = system + "\n\n" + gameCtx;
            }

            if (!string.IsNullOrEmpty(webContext)) system = system + "\n\n" + webContext;
            list.Add(new Dictionary<string, string> { { "role", "system" }, { "content", system } });

            // Log what is actually sent, so the prompt can be reviewed without reading the
            // source. The system text is small; the history count is what matters.
            Plugin.Dbg("messages: system=" + system.Length + " chars" +
                       (string.IsNullOrEmpty(webContext) ? " (no web context)" : " (web context included)") +
                       (string.IsNullOrEmpty(gameCtx) ? "" : " (game context included)"));
            Plugin.Dbg("messages system text >>> " + system);

            int turns = Math.Max(0, Plugin.Cfg.HistoryTurns.Value);
            int keep = turns * 2;

            var all = new List<Dictionary<string, string>>(History);
            int from = Math.Max(0, all.Count - keep);
            for (int i = from; i < all.Count; i++) list.Add(all[i]);

            // Guard against a double-send of the same line (the history already holds it
            // when this is called from the search path).
            bool lastIsUser = list.Count > 0 &&
                              list[list.Count - 1]["role"] == "user" &&
                              list[list.Count - 1]["content"] == userText;
            if (!lastIsUser)
                list.Add(new Dictionary<string, string> { { "role", "user" }, { "content", userText } });

            return list;
        }

        /// <summary>
        /// System prompt: a plain assistant instruction plus a length limit (the bubble
        /// is small, and a paragraph of Markdown reads badly on it).
        ///
        /// The identity line is DERIVED FROM THE CONFIGURED ENDPOINT rather than
        /// hard-coded. It used to say "你是由杭州深度求索（DeepSeek）公司开发的 AI 助手
        /// DeepSeek" unconditionally, which becomes a lie the moment the plugin is
        /// pointed at another provider -- and providers ask who they are, so the wrong
        /// answer makes the character sound confused.
        ///
        /// Character setup is opt-in through [Persona] SystemPromptExtra, appended at
        /// the end only when the user actually filled it in.
        /// </summary>
        private static string BuildSystemPrompt()
        {
            var sb = new StringBuilder();

            string provider = ProviderDisplayName(
                Plugin.Cfg != null && Plugin.Cfg.BaseUrl != null ? Plugin.Cfg.BaseUrl.Value : null);
            string model = Plugin.Cfg != null && Plugin.Cfg.Model != null
                ? (Plugin.Cfg.Model.Value ?? "").Trim() : "";

            if (!string.IsNullOrEmpty(model))
                sb.Append("你运行在 ").Append(model)
                  .Append(provider.Length > 0 ? "（" + provider + "）" : "")
                  .Append(" 上，正作为她桌面上的伙伴和她说话。");
            else
                sb.Append(provider.Length > 0
                    ? "你是 " + provider + " 提供支持的桌面伙伴，正和她说话。"
                    : "你是她桌面上的伙伴，正和她说话。");

            sb.Append("请用中文回答。");
            sb.Append("要正面回答她的问题，不要绕开。");
            sb.Append("如果不确定就直说不确定，需要的话反问一句。");
            sb.Append("这是随口聊天的场合：说人话，别端着，不要客套开场白，不要复述她的问题。");
            sb.Append("话要短，一两句说完；不要用 Markdown、不要列条目、不要代码块。");

            string extra = Plugin.Cfg != null && Plugin.Cfg.SystemPromptExtra != null
                ? Plugin.Cfg.SystemPromptExtra.Value : null;
            if (!string.IsNullOrEmpty(extra)) sb.Append("补充要求：").Append(extra);
            return sb.ToString();
        }

        /// <summary>
        /// Human-readable provider name for the system prompt, guessed from the API
        /// base URL. Unknown endpoints get an empty string so the prompt stays neutral
        /// instead of claiming to be something it is not.
        /// </summary>
        private static string ProviderDisplayName(string baseUrl)
        {
            var b = (baseUrl ?? "").ToLowerInvariant();
            if (b.Length == 0) return "";
            if (b.Contains("api.deepseek.com")) return "DeepSeek";
            if (b.Contains("bigmodel.cn") || b.Contains("zhipu")) return "智谱 GLM";
            if (b.Contains("moonshot")) return "Kimi（月之暗面）";
            if (b.Contains("siliconflow")) return "硅基流动 SiliconFlow";
            if (b.Contains("dashscope") || b.Contains("aliyun")) return "通义千问";
            if (b.Contains("volces.com") || b.Contains("volcengine")) return "火山方舟";
            if (b.Contains("generativelanguage.googleapis.com") || b.Contains("googleapis")) return "Google Gemini";
            if (b.Contains("groq.com")) return "Groq";
            if (b.Contains("openrouter.ai")) return "OpenRouter";
            if (b.Contains("openai.com")) return "OpenAI";
            if (b.Contains("localhost") || b.Contains("127.0.0.1")) return "本地模型";
            return "";
        }

        // ------------------------------------------------------------- web self test
        //
        // Same idea as the memory self-test, for the 联网 path: run a real search from
        // inside the game process once, and write the outcome to the log. This makes the
        // network path verifiable without anyone typing into the modal.

        private static bool _webSelfTested;

        internal static async void WebSelfTest()
        {
            if (_webSelfTested) return;
            _webSelfTested = true;

            try
            {
                Plugin.Dbg("=== web search self-test: start ===");
                var token = Plugin.ResetToken();
                var r = await WebSearch.SearchAsync("DeepSeek 最新消息", token).ConfigureAwait(false);

                Plugin.Dbg("web search self-test: " + WebSearch.Describe(r));
                for (int i = 0; i < r.Hits.Count && i < 3; i++)
                {
                    var h = r.Hits[i];
                    Plugin.Dbg("  hit[" + i + "] " + h.Title + " | " + (h.Url ?? "-"));
                }

                string ctx = WebSearch.FormatForPrompt("DeepSeek 最新消息", r);
                Plugin.Dbg("web search self-test: prompt context = " +
                           (ctx == null ? "<none>" : ctx.Length + " chars"));
                Plugin.Info("[OcAI] web search self-test: ok=" + r.Ok + " provider=" + r.Provider +
                            " hits=" + r.Hits.Count + " trail=[" + r.Trail + "]");

                // With the search context in hand, also prove the model can answer from it.
                if (r.Ok && ctx != null && Plugin.Cfg != null && Plugin.Cfg.HasKey)
                {
                    var msgs = BuildMessages("DeepSeek 最新消息", ctx);
                    var chat = await Plugin.Api.ChatAsync(msgs, 0.8f, 200, token).ConfigureAwait(false);
                    Plugin.Dbg("web search self-test: model answer = " +
                               (chat.Ok ? Shorten(chat.Text, 200) : "FAILED: " + chat.Error));
                }
            }
            catch (Exception e)
            {
                Plugin.Dbg("web search self-test FAILED: " + e.GetType().Name + ": " + e.Message);
                Plugin.Info("[OcAI] web search self-test failed: " + e.Message);
            }
        }

        private static void Remember(string role, string content)
        {
            if (string.IsNullOrEmpty(content)) return;
            History.Enqueue(new Dictionary<string, string> { { "role", role }, { "content", content } });

            // Hard cap so an old session cannot grow the prompt without bound. The
            // prompt itself is trimmed to HistoryTurns; this only stops the store.
            int max = Math.Max(4, Plugin.Cfg.HistoryTurns.Value) * 2 * 4;
            Dictionary<string, string> drop;
            while (History.Count > max && History.TryDequeue(out drop)) { }
        }

        // ----------------------------------------------------------------- self test
        //
        // Runs once a few seconds after startup. The chat memory lifecycle is the one
        // part of this feature that cannot be checked from a screenshot -- it lives
        // entirely inside the process -- so it is exercised and written to the debug log
        // instead. This is also how "clear" was verified without a manual keypress:
        // seed two turns, confirm the count, clear, confirm it is empty.

        private static bool _selfTested;

        internal static void SelfTest()
        {
            if (_selfTested) return;
            _selfTested = true;

            try
            {
                Plugin.Dbg("=== chat memory self-test: start, count=" + History.Count + " ===");

                Remember("user", "selftest-user-1");
                Remember("assistant", "selftest-assistant-1");
                Plugin.Dbg("chat memory self-test: after 2 remembers, count=" + History.Count +
                           " (expect 2)");

                bool had = ClearHistory();
                Plugin.Dbg("chat memory self-test: clear returned had=" + had +
                           " count=" + History.Count + " (expect had=True count=0)");

                bool hadAgain = ClearHistory();
                Plugin.Dbg("chat memory self-test: second clear had=" + hadAgain +
                           " count=" + History.Count + " (expect had=False count=0)");

                // Leave the store exactly as it was before the test.
                ClearHistory();

                // Prove the request builder still works with an empty store.
                var msgs = BuildMessages("selftest-probe", null);
                Plugin.Dbg("chat memory self-test: BuildMessages -> " + msgs.Count +
                           " message(s) (expect 2 = system + user)");
                ClearHistory();

                Plugin.Dbg("=== chat memory self-test: PASSED ===");
                Plugin.Info("[OcAI] chat memory self-test passed (seed/clear/rebuild ok)");
            }
            catch (Exception e)
            {
                Plugin.Dbg("chat memory self-test FAILED: " + e.GetType().Name + ": " + e.Message);
                Plugin.Info("[OcAI] chat memory self-test FAILED: " + e.Message);
            }
        }

        // -------------------------------------------------------------- the answer

        /// <summary>
        /// Puts the answer on screen. Long answers are split at sentence enders and
        /// queued, so each bubble stays readable instead of becoming a wall of text the
        /// panel has to shrink to fit.
        /// </summary>
        private static void DisplayAnswer(string answer)
        {
            if (string.IsNullOrEmpty(answer)) return;

            string[] parts = Plugin.Cfg.ChatSplitLongReplies.Value
                ? SplitSentences(answer)
                : new[] { answer };

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.IsNullOrEmpty(part)) continue;
                Plugin.EnqueueSpeak(part);
            }
        }

        /// <summary>Strips what reads badly in a bubble and normalises whitespace.</summary>
        private static string CleanAnswer(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("*", "").Replace("`", "").Replace("#", "").Replace("\r", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\n{2,}", "\n");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[ \t]{2,}", " ");
            return s.Trim();
        }

        /// <summary>Splits on CJK/ASCII sentence enders, keeping the punctuation.</summary>
        private static string[] SplitSentences(string text)
        {
            var parts = new List<string>();
            var cur = new StringBuilder();

            foreach (char c in text)
            {
                if (c == '\n')
                {
                    Flush(parts, cur);
                    continue;
                }

                cur.Append(c);

                if (c == '。' || c == '！' || c == '？' || c == '；' ||
                    c == '!' || c == '?' || c == ';')
                    Flush(parts, cur);
            }
            Flush(parts, cur);

            if (parts.Count == 0) parts.Add(text);
            return parts.ToArray();
        }

        private static void Flush(List<string> parts, StringBuilder cur)
        {
            string s = cur.ToString().Trim();
            cur.Length = 0;
            if (s.Length > 0) parts.Add(s);
        }

        private static string Shorten(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// Shows a line through the normal bubble path, with a duration that suits her
        /// own lines. Kept here so the chat feature has one place that decides how long
        /// an answer stays readable.
        /// </summary>
        internal static void SayLong(string text, float seconds)
        {
            BubbleUi.NextShowSeconds = seconds;
            Plugin.EnqueueSpeak(text);
        }
    }
}
