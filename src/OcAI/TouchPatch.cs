using System;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace OcAI
{
    /// <summary>
    /// Head-pat detection.
    ///
    /// History / why OnMouseOver and not TriggerOcTouching:
    ///   TriggerOcTouching() is only called once the game recognises a *swipe
    ///   pattern* or a completed touch gesture, so a plain click on the head
    ///   never reached it (probe evidence: Player.log showed ClickThrough toggling
    ///   while ocai_debug.txt stayed empty). OnMouseOver fires as soon as the
    ///   pointer is over the character collider, which is what a head pat is.
    ///
    /// So: OnMouseOver (cursor confirmed over the character) is the trigger, and a
    /// real mouse-button edge (tracked through GetAsyncKeyState) confirms it was a
    /// click and not just a hover.
    /// </summary>
    internal static class TouchPatch
    {
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private const int VK_LBUTTON = 0x01;

        internal static MethodBase Target;

        private static bool _lastLButton;
        private static DateTime _lastPat = DateTime.MinValue;

        /// <summary>
        /// Last time OnMouseOver proved the cursor was on her, and the component it came
        /// from. The keyboard chat trigger (F6/F7) needs to know which character the
        /// cursor is pointing at, and OnMouseOver is the only reliable source for that
        /// (the collider wins over raycasts).
        /// </summary>
        private static DateTime _hoverAt = DateTime.MinValue;
        private static UnityEngine.MonoBehaviour _hoverMb;

        /// <summary>Seconds the hover memory stays valid without a fresh OnMouseOver.</summary>
        private const double HoverValidSeconds = 0.4;

        /// <summary>True when the cursor was on her within the last fraction of a second.</summary>
        internal static bool IsHoveringRecently
        {
            get
            {
                return _hoverMb != null &&
                       (DateTime.UtcNow - _hoverAt).TotalSeconds <= HoverValidSeconds;
            }
        }

        /// <summary>
        /// Hover resolved from the OS cursor position instead of Unity's OnMouseOver.
        /// Used as a second chance when the key press lands in a frame where Unity did
        /// not dispatch OnMouseOver (or when the game swallowed the hover).
        /// </summary>
        internal static bool IsCursorOverCharacterByGeometry()
        {
            try
            {
                var bongo = Plugin.ResolveBongo();
                if (bongo == null) return false;
                if (!IsCursorOnHead(bongo.transform)) return false;

                // A geometry hit also refreshes the hover memory, so the bubble anchor
                // is up to date for the answer.
                _hoverMb = bongo;
                _hoverAt = DateTime.UtcNow;
                Plugin.RegisterPump(bongo);
                Plugin.SetSpeakAnchor(bongo);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Dbg("IsCursorOverCharacterByGeometry: " + e.Message);
                return false;
            }
        }

        /// <summary>Screen-space test: is the cursor inside the character's head band?</summary>
        private static bool IsCursorOnHead(UnityEngine.Transform tr)
        {
            if (tr == null) return false;

            POINT p;
            if (!GetCursorPos(out p)) return false;

            float mx = p.X;
            float my = UnityEngine.Screen.height - p.Y;   // Win32 top-left -> Unity bottom-left

            var cam = UnityEngine.Camera.main;
            if (cam == null) return false;

            UnityEngine.Vector3 sp = cam.WorldToScreenPoint(tr.position);
            if (sp.z < 0f) return false;

            // Measure the character from its renderer so the head band tracks the
            // real on-screen size instead of a hard-coded guess.
            float halfHeightPx = 220f;
            try
            {
                var sr = tr.GetComponentInChildren<UnityEngine.SpriteRenderer>();
                if (sr != null && sr.sprite != null)
                {
                    var b = sr.sprite.bounds;
                    var top = cam.WorldToScreenPoint(sr.transform.TransformPoint(new UnityEngine.Vector3(0f, b.max.y, 0f)));
                    var bot = cam.WorldToScreenPoint(sr.transform.TransformPoint(new UnityEngine.Vector3(0f, b.min.y, 0f)));
                    float h = UnityEngine.Mathf.Abs(top.y - bot.y);
                    if (h > 20f) halfHeightPx = h * 0.5f;
                }
            }
            catch { }

            float frac = Plugin.Cfg != null ? Plugin.Cfg.HeadRegionTop.Value : 1.0f;
            if (frac < 0.05f) frac = 0.05f;
            if (frac > 1f) frac = 1f;

            float headTop = sp.y + halfHeightPx;
            float bandHeight = (2f * halfHeightPx) * frac;

            // Vertical slack. The anchor is the collider, which sits slightly off
            // the visible sprite, so a click clearly on the character could land a
            // few pixels outside a tight band and be rejected (observed: cursor
            // y=695 vs band top 681). A pet should not be that fussy.
            float slack = UnityEngine.Mathf.Max(120f, halfHeightPx * 0.7f);

            float headBottom = headTop - bandHeight - slack;
            headTop += slack;

            // Horizontal slack as well: OnMouseOver already proved the cursor is on
            // the character, so this only guards against wild outliers.
            float padX = halfHeightPx * 2.5f;

            bool insideX = mx >= sp.x - padX && mx <= sp.x + padX;
            bool insideY = my >= headBottom && my <= headTop;

            Plugin.Dbg(string.Format(
                "headtest cursor=({0:F0},{1:F0}) anchor=({2:F0},{3:F0}) halfH={4:F0} band=[{5:F0},{6:F0}] -> x={7} y={8}",
                mx, my, sp.x, sp.y, halfHeightPx, headBottom, headTop, insideX, insideY));

            return insideX && insideY;
        }

        /// <summary>Harmony postfix on OcTouchTrigger.OnMouseOver().</summary>
        internal static void Postfix(object __instance)
        {
            try
            {
                if (Plugin.Cfg == null) return;

                // The pump / speak anchor must be captured even when the balance
                // feature is off: the right-click chat needs them too.
                var mb = __instance as UnityEngine.MonoBehaviour;
                UnityEngine.Transform tr = null;
                if (mb != null)
                {
                    tr = mb.transform;
                    Plugin.RegisterPump(mb);      // live game MonoBehaviour -> coroutine pump
                    Plugin.SetSpeakAnchor(mb);    // bubbles pop above the character

                    // Remember the hover: the keyboard chat trigger asks "is the cursor
                    // on her", and this is the game's own answer to that question.
                    _hoverMb = mb;
                    _hoverAt = DateTime.UtcNow;
                }

                // Remember where the cursor is, as a bubble-position fallback.
                POINT cp;
                if (GetCursorPos(out cp))
                    Plugin.SetSpeakScreenPos(cp.X, UnityEngine.Screen.height - cp.Y);

                // The left button is read every call so that the state really tracks the
                // physical button even when the cursor left the character in between.
                //
                // One physical click is routed by Plugin.PatRouter: a double click means
                // "talk to her", a single click keeps the original balance bubble. The
                // game's own bindings do not use left-double-click, so nothing fights us
                // for it (unlike the right button, which stands her up).
                bool lbtn = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
                bool leftEdge = lbtn && !_lastLButton;
                _lastLButton = lbtn;

                if (!leftEdge) return;

                // A click anywhere on her counts; IsCursorOnHead is kept only as a
                // sanity band, matching the pre-chat behaviour.
                if (!IsCursorOnHead(tr))
                {
                    Plugin.Dbg("left click ignored: cursor outside her clickable band");
                    return;
                }

                if ((DateTime.UtcNow - _lastPat).TotalMilliseconds < 120)
                {
                    Plugin.Dbg("left click ignored: within debounce window");
                    return;   // ignore contact bounce / duplicated edges
                }
                _lastPat = DateTime.UtcNow;

                Plugin.PatRouter();
            }
            catch (Exception e)
            {
                Plugin.Dbg("TouchPatch.Postfix error: " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
