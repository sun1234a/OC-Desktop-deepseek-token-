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
                if (Plugin.Cfg == null || !Plugin.Cfg.EnableHeadPatBalance.Value) return;

                // Hovering is not patting: require a fresh left-button press edge.
                bool lbtn = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
                bool pressed = lbtn && !_lastLButton;
                _lastLButton = lbtn;
                if (!pressed) return;

                // De-bounce: one pat per 1.2 s even if the postfix runs many times.
                if ((DateTime.UtcNow - _lastPat).TotalMilliseconds < 1200) return;

                UnityEngine.Transform tr = null;
                var mb = __instance as UnityEngine.MonoBehaviour;
                if (mb != null)
                {
                    tr = mb.transform;
                    Plugin.RegisterPump(mb);      // live game MonoBehaviour -> coroutine pump
                    Plugin.SetSpeakAnchor(mb);    // bubbles pop above the character
                }

                // Remember where the click landed, as a bubble-position fallback.
                POINT cp;
                if (GetCursorPos(out cp))
                    Plugin.SetSpeakScreenPos(cp.X, UnityEngine.Screen.height - cp.Y);

                if (!IsCursorOnHead(tr)) return;

                _lastPat = DateTime.UtcNow;
                Plugin.Dbg("HEAD PAT detected -> requesting balance");
                Plugin.OnHeadPat();
            }
            catch (Exception e)
            {
                Plugin.Dbg("TouchPatch.Postfix error: " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
