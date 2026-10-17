using System;
using System.Reflection;
using UnityEngine;
using BepInEx.Unity.IL2CPP;

namespace OcAI
{
    /// <summary>
    /// A custom speech bubble that looks like the reference pet's: a rounded
    /// rounded panel that auto-sizes to its text, centred above the character's head.
    ///
    /// Why not keep using UI.TipsWidget: that is the game's one-line toast strip --
    /// small type, no wrapping, and the user reported it was tiring to read. This
    /// builds a real bubble instead, and deliberately REUSES the game's own
    /// DialogueBubble prefab (cloned, never the live instance) so the panel art,
    /// font and show animation stay native.
    /// </summary>
    internal static class BubbleUi
    {
        private static GameObject _bubble;         // our single reusable bubble
        private static MonoBehaviour _bubbleMb;
        private static object _bubbleTyped;        // UI.DialogueBubble
        private static Type _bubbleType;
        private static DateTime _shownAt = DateTime.MinValue;
        private static bool _built;

        /// <summary>Seconds to keep a bubble on screen (the reference pet uses 8).</summary>
        private const float ShowSeconds = 8f;

        /// <summary>
        /// Duration for the NEXT bubble only, set by the pump per line: an AI answer
        /// needs longer on screen than the pet's own one-liners. Left at 0 (meaning
        /// "use the default") between lines.
        /// </summary>
        internal static float NextShowSeconds;

        /// <summary>How long the bubble currently on screen was given.</summary>
        private static float _showSeconds = ShowSeconds;

        /// <summary>
        /// Screen position for the bubble: the game's own idea of where the
        /// character's head is.
        ///
        /// UI.TopicImaginationInputWidget exposes static GetHeadWorldPosition(BongoOC)
        /// and HeadHeightOffset, which is exactly what the game uses to park its own
        /// panels beside the character. Deriving this from sprite bounds did not work
        /// (the character is a Spine skeleton, and GetComponentInChildren found a
        /// small accessory instead, giving world (-9,-7)).
        /// </summary>
        internal static UnityEngine.Vector2 HeadUiPosition(UnityEngine.Camera cam)
        {
            try
            {
                UnityEngine.MonoBehaviour anchor = Plugin.SpeakAnchorMb;
                if (anchor != null && _widgetType != null && cam != null)
                {
                    var bongo = Plugin.ResolveBongo();
                    if (bongo == null) { Plugin.Dbg("BubbleUi: ResolveBongo returned null"); return Plugin.FallbackSpeakPos(); }

                    // Anchor on the TOP of her rendered bounds, not on her transform
                    // position. The transform origin sits near her feet (measured:
                    // origin uiY=1133 while the window is 1080 tall, i.e. at the very
                    // bottom edge), so anchoring there parked the bubble at the top of
                    // the window -- the opposite end from her head.
                    //
                    // A Spine skeleton's renderers carry real world bounds, so the
                    // highest one is the top of her hair, whatever she is doing.
                    Vector3 topWorld = HeadTopWorld(bongo, 300f);

                    var csp = cam.WorldToScreenPoint(topWorld);
                    if (csp.z < 0f) { Plugin.Dbg("BubbleUi: character behind camera"); return Plugin.FallbackSpeakPos(); }

                    // Learn how many pixels one world unit occupies for the CURRENT
                    // body size, so every offset can be scaled to match.
                    MeasurePixelScale(bongo, cam, topWorld);

                    // Convert Unity screen space (origin bottom-left) to the UI space
                    // used by anchoredPosition (origin top-left): y flips.
                    float x = csp.x;
                    float uiY = UnityEngine.Screen.height - csp.y;

                    // Fine trim above her head. In UI space "up" means SMALLER y, so
                    // this subtracts. (An earlier version added, which threw the bubble
                    // to the opposite end of the screen.)
                    float lift = 26f;
                    try { if (Plugin.Cfg != null) lift = Plugin.Cfg.BubbleLift.Value; }
                    catch { }
                    lift *= SizeScale;

                    // Emergency trim downwards, for cases where the bounds are taller
                    // than her visible body (a big accessory, an effect). In UI space
                    // "down" means LARGER y.
                    float drop = 0f;
                    try { if (Plugin.Cfg != null) drop = Plugin.Cfg.BubbleDropDown.Value; } catch { }

                    Plugin.Dbg(string.Format(
                        "BubbleUi anchor headBone='{0}' topWorld=({1:F2},{2:F2},{3:F2}) charScreen=({4:F0},{5:F0}) uiY={6:F0} lift={7:F0} drop={8:F0} -> anchor=({9:F0},{10:F0})",
                        _headBoneName ?? "<none>", topWorld.x, topWorld.y, topWorld.z,
                        csp.x, csp.y, uiY, lift, drop, x, uiY - lift + drop));

                    return new UnityEngine.Vector2(x, uiY - lift + drop);
                }
            }
            catch (Exception e) { Plugin.Dbg("BubbleUi.HeadUiPosition: " + e.Message); }

            return Plugin.FallbackSpeakPos();
        }

        /// <summary>
        /// World position of the top of the character's head.
        ///
        /// Three attempts, best first:
        ///
        ///  1. The Spine skeleton's own "head" bone. This is the only source that
        ///     really knows where her head is; it follows every animation, and the
        ///     bubble tails off it naturally.
        ///  2. Renderer bounds. Kept as a fallback, but it measured badly on this
        ///     character: the highest bounds sat only ~1.7 world units above her
        ///     transform origin while the visible body is several units tall, i.e.
        ///     the renderers reported something much smaller than the whole girl.
        ///  3. A fixed rise above the transform origin, capped so an oversized
        ///     accessory cannot push the bubble away.
        /// </summary>
        private static Vector3 HeadTopWorld(UnityEngine.MonoBehaviour bongo, float fallbackRise)
        {
            Vector3 origin = bongo.transform.position;

            // 1) Spine bone.
            try
            {
                var sa = bongo.GetComponentInChildren<Spine.Unity.SkeletonAnimation>();
                if (sa != null && sa.Skeleton != null)
                {
                    var skel = sa.Skeleton;
                    float best = float.MinValue;
                    string bestName = null;
                    var bones = skel.Bones;
                    if (bones != null)
                    {
                        for (int i = 0; i < bones.Count; i++)
                        {
                            var bone = bones.Items[i];
                            if (bone == null) continue;
                            string n = (bone.Data != null ? bone.Data.Name : null) ?? "";
                            if (!LooksLikeHead(n)) continue;
                            float wy = sa.transform.TransformPoint(new Vector3(bone.WorldX, bone.WorldY, 0f)).y;
                            if (wy > best) { best = wy; bestName = n; }
                        }
                    }
                    if (best != float.MinValue)
                    {
                        _headBoneName = bestName;
                        return new Vector3(origin.x, best, origin.z);
                    }
                }
            }
            catch (Exception e) { Plugin.Dbg("HeadTopWorld spine: " + e.GetType().Name + ": " + e.Message); }

            // 2) Renderer bounds.
            float bestTop = float.MinValue;
            try
            {
                var rends = bongo.GetComponentsInChildren<UnityEngine.Renderer>();
                if (rends != null)
                {
                    for (int i = 0; i < rends.Length; i++)
                    {
                        var r = rends[i];
                        if (r == null || !r.enabled) continue;
                        var b = r.bounds;
                        if (b.size.x <= 0.0001f && b.size.y <= 0.0001f) continue;
                        float top = b.center.y + b.extents.y;
                        if (top > bestTop) bestTop = top;
                    }
                }
            }
            catch (Exception e) { Plugin.Dbg("HeadTopWorld bounds: " + e.GetType().Name + ": " + e.Message); }

            if (bestTop == float.MinValue || bestTop <= origin.y + 0.05f)
                return origin + new Vector3(0f, fallbackRise, 0f);

            // Sanity cap: at most 1.5x the fallback above her origin.
            float cap = origin.y + fallbackRise * 1.5f;
            if (bestTop > cap)
            {
                Plugin.Dbg(string.Format("HeadTopWorld: bounds top {0:F1} capped to {1:F1}", bestTop, cap));
                bestTop = cap;
            }
            return new Vector3(origin.x, bestTop, origin.z);
        }

        /// <summary>Last head bone name we matched, for the diagnostics line.</summary>
        private static string _headBoneName;

        /// <summary>
        /// Whether a Spine bone name looks like the head. Excludes the accessory
        /// bones (hat, hair, ear, horn) that would sit above the real head and drag
        /// the bubble up with them.
        /// </summary>
        private static bool LooksLikeHead(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            if (n.IndexOf("head", StringComparison.Ordinal) < 0 &&
                n.IndexOf("face", StringComparison.Ordinal) < 0) return false;
            return n.IndexOf("hat", StringComparison.Ordinal) < 0 &&
                   n.IndexOf("hair", StringComparison.Ordinal) < 0 &&
                   n.IndexOf("ear", StringComparison.Ordinal) < 0 &&
                   n.IndexOf("horn", StringComparison.Ordinal) < 0;
        }

        private static Type _widgetType;

        /// <summary>The Bubble_Img node: the visible panel that must grow with the text.</summary>
        private static RectTransform _panelRt;

        internal static void Init(Type dialogueBubbleType, Type widgetType)
        {
            _bubbleType = dialogueBubbleType;
            _widgetType = widgetType;
            Plugin.Dbg("BubbleUi.Init bubble=" + (dialogueBubbleType != null) +
                       " headProvider=" + (widgetType != null));
        }

        /// <summary>Hide the bubble once its time is up. Called every pump tick.</summary>
        internal static void Tick()
        {
            if (_bubble == null || _hidden) return;
            try
            {
                if ((DateTime.UtcNow - _shownAt).TotalSeconds > _showSeconds)
                {
                    _bubble.SetActive(false);
                    _hidden = true;
                    Plugin.Dbg("BubbleUi: auto-hid");
                    return;
                }

                // Still visible: follow her. She keeps walking while the bubble is
                // up, and a bubble left at the position she was standing at when it
                // appeared ends up pointing at empty desktop -- or, at the screen
                // edge, half outside the window where it is not drawn at all.
                var cam = UnityEngine.Camera.main;
                if (cam != null && Plugin.SpeakAnchorMb != null &&
                    Plugin.ResolveBongo() != null)
                    ReanchorTo(HeadUiPosition(cam), false);
            }
            catch { }
        }

        private static bool _hidden = true;

        /// <summary>Hides the bubble right away, e.g. when the master switch is turned off.</summary>
        internal static void HideNow()
        {
            try
            {
                if (_bubble != null) _bubble.SetActive(false);
                _hidden = true;
                Plugin.Dbg("BubbleUi: hidden by request");
            }
            catch { }
        }

        /// <summary>Gap between the bubble's bottom edge and the character, in pixels.</summary>
        private const float HeadGap = 12f;

        /// <summary>
        /// Screen pixels per world unit, measured from the character's rendered size.
        ///
        /// Offsets like Lift / HeadGap / StandOffsetX are authored in pixels for the
        /// 1x body. The game lets the player pick a larger body (1.5x, 2x ...) and the
        /// character is scaled rather than the camera, so a head that is twice as tall
        /// needs twice the clearance -- otherwise the bubble clips into her hair
        /// ("1x 完全适配，1.5x 就会顶到").
        ///
        /// IMPORTANT: a pixels-per-world-unit ratio CANNOT detect this. Measured
        /// behaviour: worldSpan 1.34 / pxSpan 134 gives exactly 100 at 1x, and a bigger
        /// body scales both numbers together, so the ratio stays 100 and the derived
        /// scale stays 1.00 forever. The absolute world-space height is what changes,
        /// so that is what we watch: the first reading becomes the baseline and later
        /// readings are compared against it.
        /// </summary>
        private static float _baselineWorldHeight;
        private static bool _pxMeasured;

        private static float SizeScale
        {
            get
            {
                // An explicit override always wins. Auto-detection watches her
                // world-space height, but that measurement rides on an animated bone
                // and wobbles (observed 1.342 -> 1.513 mid-idle), so anyone who knows
                // their body size can pin it instead.
                try
                {
                    if (Plugin.Cfg != null && Plugin.Cfg.SizeScale.Value > 0.01f)
                        return Plugin.Cfg.SizeScale.Value;
                }
                catch { }

                if (!_pxMeasured || _baselineWorldHeight <= 0.01f) return 1f;
                float s = _currentWorldHeight / _baselineWorldHeight;

                // Idle animation moves the head bone a little, so treat small
                // deviations as noise rather than a size change.
                if (Mathf.Abs(s - 1f) < 0.15f) return 1f;

                if (s < 0.5f) s = 0.5f;
                if (s > 4f) s = 4f;
                return s;
            }
        }

        private static float _currentWorldHeight = 1f;

        /// <summary>
        /// Tracks the character's absolute world-space size. The first reading is the
        /// baseline (whatever size the player had at launch); a later reading that
        /// differs means the body size changed, and all bubble offsets scale with it.
        /// </summary>
        private static void MeasurePixelScale(UnityEngine.MonoBehaviour bongo, UnityEngine.Camera cam, Vector3 topWorld)
        {
            try
            {
                if (bongo == null) return;

                float h = Mathf.Abs(topWorld.y - bongo.transform.position.y);
                if (h <= 0.05f)
                {
                    // head bone sat on the origin; use renderer bounds instead
                    var rends = bongo.GetComponentsInChildren<UnityEngine.Renderer>();
                    if (rends != null && rends.Length > 0)
                    {
                        float best = 0f;
                        for (int i = 0; i < rends.Length; i++)
                        {
                            var r = rends[i];
                            if (r == null || !r.enabled) continue;
                            var b = r.bounds;
                            if (b.size.y > best) best = b.size.y;
                        }
                        h = best;
                    }
                }

                if (h <= 0.05f) return;

                _currentWorldHeight = h;
                if (!_pxMeasured)
                {
                    _baselineWorldHeight = h;
                    _pxMeasured = true;
                    Plugin.Dbg(string.Format(
                        "BubbleUi size baseline worldHeight={0:F3} (body size at launch = 1.00)", h));
                    return;
                }

                float s = SizeScale;
                if (Mathf.Abs(s - 1f) > 0.02f)
                {
                    Plugin.Dbg(string.Format(
                        "BubbleUi size changed worldHeight={0:F3} baseline={1:F3} -> sizeScale={2:F2}",
                        h, _baselineWorldHeight, s));
                }
            }
            catch (Exception e) { Plugin.Dbg("BubbleUi.MeasurePixelScale: " + e.Message); }
        }

        /// <summary>Throttle for the anchor log line: the follow loop runs every frame.</summary>
        private static DateTime _lastAnchorLog = DateTime.MinValue;
        private static float _lastLoggedY = float.NaN;

        /// <summary>Cached canvas the bubble lives under, used to pick a conversion camera.</summary>
        private static UnityEngine.Canvas _canvas;

        /// <summary>
        /// The camera that renders the bubble's canvas.
        ///
        /// This matters more than it looks: the dialogue bubble lives under
        /// "NotTurnCanvas", which is a WORLD SPACE canvas with its RectTransform
        /// scaled to 0.01. For a screen-space overlay canvas the conversion camera is
        /// null, but for a world-space one it is mandatory -- with a null camera the
        /// conversion silently produces garbage, which is why earlier attempts to
        /// place the bubble by screen position threw it off-screen entirely.
        /// </summary>
        private static UnityEngine.Camera CanvasCamera(RectTransform rt)
        {
            try
            {
                if (_canvas == null) _canvas = rt.GetComponentInParent<UnityEngine.Canvas>();
                if (_canvas == null) return UnityEngine.Camera.main;
                if (_canvas.renderMode == UnityEngine.RenderMode.ScreenSpaceOverlay) return null;
                if (_canvas.worldCamera != null) return _canvas.worldCamera;
            }
            catch (Exception e) { Plugin.Dbg("CanvasCamera: " + e.Message); }
            return UnityEngine.Camera.main;
        }

        /// <summary>
        /// Projects the bubble's world position back to screen coordinates using every
        /// camera in the scene, and returns the first plausible reading.
        ///
        /// Reading it with the wrong camera put the result at the exact centre of the
        /// screen (960,540) for every position, which made the self-check useless. So
        /// rather than assume which camera renders this canvas, try them.
        /// </summary>
        private static Vector2 BubbleScreenPosVerified(RectTransform rt)
        {
            try
            {
                var world = rt.position;
                UnityEngine.Camera[] cams = null;
                try { cams = UnityEngine.Camera.allCameras; } catch { }
                if (cams != null)
                {
                    for (int i = 0; i < cams.Length; i++)
                    {
                        var c = cams[i];
                        if (c == null) continue;
                        var sp = c.WorldToScreenPoint(world);
                        if (sp.z <= 0f) continue;                       // behind this camera
                        if (sp.x < -1f || sp.x > UnityEngine.Screen.width + 1f) continue;
                        if (sp.y < -1f || sp.y > UnityEngine.Screen.height + 1f) continue;
                        return new Vector2(sp.x, UnityEngine.Screen.height - sp.y);
                    }
                }
                var main = UnityEngine.Camera.main;
                if (main != null)
                {
                    var sp = main.WorldToScreenPoint(world);
                    return new Vector2(sp.x, UnityEngine.Screen.height - sp.y);
                }
            }
            catch (Exception e) { Plugin.Dbg("BubbleScreenPosVerified: " + e.Message); }
            return new Vector2(float.NaN, float.NaN);
        }

        /// <summary>
        /// Moves the bubble to a position given in the UI space used everywhere else
        /// in this file: origin at the window's TOP-left, y growing downwards.
        ///
        /// The conversion goes through screen space rather than anchoredPosition,
        /// because the bubble's parent is a world-space canvas: there the local axes
        /// are scaled by 0.01 and rotated into world space, so an anchoredPosition
        /// value has no useful relationship to screen pixels (measured slope 0.0000).
        /// </summary>
        private static void PlaceAtScreenPos(RectTransform rt, Vector2 uiPos)
        {
            float screenX = uiPos.x;
            float screenY = UnityEngine.Screen.height - uiPos.y;
            var cam = CanvasCamera(rt);

            // Preferred: intersect the camera ray with the canvas plane. This does not
            // depend on the rect's layout at all, so it also works before the canvas
            // has been laid out for the first time.
            if (cam == null) cam = UnityEngine.Camera.main;
            if (cam != null)
            {
                var ray = cam.ScreenPointToRay(new Vector3(screenX, screenY, 0f));
                var plane = new UnityEngine.Plane(rt.forward, rt.position);
                float dist;
                if (plane.Raycast(ray, out dist))
                {
                    rt.position = ray.GetPoint(dist);
                    return;
                }
            }

            // Fallback: the engine's own conversion, which needs the right camera.
            Vector3 world;
            if (UnityEngine.RectTransformUtility.ScreenPointToWorldPointInRectangle(
                    rt, new Vector2(screenX, screenY), cam, out world))
            {
                rt.position = world;
                return;
            }

            // Last resort: keep the previous behaviour so the bubble at least shows.
            rt.anchoredPosition = uiPos;
        }

        private static bool _sceneDumped;
        private static int _sceneDumpFrames;

        /// <summary>
        /// One-shot dump of everything that could be moving the bubble behind our
        /// back: both position properties, the anchors that decide what they mean,
        /// the parent chain, and whether a tween or layout group is in play.
        ///
        /// Written after setting both properties to a deliberately different value
        /// and finding the bubble did not move on screen at all (slope 0.0000 for
        /// anchoredPosition AND localPosition), which rules out a coordinate-space
        /// misunderstanding and points at something overwriting the transform.
        /// </summary>
        private static void DumpBubbleScene(RectTransform rt)
        {
            try
            {
                var parent = rt.parent;
                var t = rt.transform;

                string parentInfo = "<none>";
                if (parent != null)
                {
                    var prt = parent.GetComponent<RectTransform>();
                    parentInfo = string.Format(
                        "{0} rect={1} parentSize={2} scale={3:F3} active={4}",
                        parent.name,
                        prt != null ? prt.rect.size.ToString() : "n/a",
                        prt != null ? prt.sizeDelta.ToString() : "n/a",
                        parent.localScale.x,
                        parent.gameObject.activeInHierarchy);
                }

                var canvas = rt.GetComponentInParent<UnityEngine.Canvas>();
                string canvasInfo = "<none>";
                if (canvas != null)
                    canvasInfo = string.Format("{0} mode={1} scale={2:F3} pixel={3}x{4}",
                        canvas.name, canvas.renderMode, canvas.scaleFactor,
                        canvas.pixelRect.width, canvas.pixelRect.height);

                // Which components could drive the position?
                var comps = new System.Text.StringBuilder();
                try
                {
                    var all = rt.GetComponents<UnityEngine.Component>();
                    if (all != null)
                        for (int i = 0; i < all.Length && i < 24; i++)
                            if (all[i] != null) comps.Append(all[i].GetType().Name).Append(' ');
                }
                catch { }
                string parentComps = "";
                try
                {
                    if (parent != null)
                    {
                        var pall = parent.GetComponents<UnityEngine.Component>();
                        if (pall != null)
                            for (int i = 0; i < pall.Length && i < 24; i++)
                                if (pall[i] != null) parentComps += pall[i].GetType().Name + " ";
                    }
                }
                catch { }

                var children = new System.Text.StringBuilder();
                for (int i = 0; i < rt.childCount && i < 12; i++)
                {
                    var c = rt.GetChild(i);
                    var crt = c.GetComponent<RectTransform>();
                    children.Append(c.name)
                            .Append(crt != null
                                ? "(" + crt.anchoredPosition.x.ToString("F0") + "," + crt.anchoredPosition.y.ToString("F0") + ")"
                                : "")
                            .Append(' ');
                }

                Plugin.Dbg("BubbleUi SCENE pos=" + t.position + " local=" + t.localPosition +
                           " anchored=" + rt.anchoredPosition +
                           " anchorMin=" + rt.anchorMin + " anchorMax=" + rt.anchorMax +
                           " pivot=" + rt.pivot + " size=" + rt.sizeDelta);
                Plugin.Dbg("BubbleUi SCENE parent=" + parentInfo);
                Plugin.Dbg("BubbleUi SCENE canvas=" + canvasInfo);
                Plugin.Dbg("BubbleUi SCENE comps=[ " + comps + "] parentComps=[ " + parentComps + "]");
                Plugin.Dbg("BubbleUi SCENE children=[ " + children + "]");

                // Does writing a position change the transform at all?
                var before = t.position;
                rt.anchoredPosition = new Vector2(17f, 23f);
                var afterAnchored = t.position;
                t.localPosition = new Vector3(111f, 222f, t.localPosition.z);
                var afterLocal = t.position;
                Plugin.Dbg(string.Format(
                    "BubbleUi SCENE response before={0} afterAnchored={1} afterLocal={2}",
                    before, afterAnchored, afterLocal));
            }
            catch (Exception e) { Plugin.Dbg("DumpBubbleScene: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>Smoothed bubble position, so the idle animation does not make it jitter.</summary>
        private static Vector2 _smoothed;
        private static bool _hasSmoothed;
        private static float _lastReanchorTime = -1f;

        /// <summary>
        /// Screen position (UI space, origin at the window's top-left) of the bubble's
        /// rect centre, or NaN when it cannot be read.
        ///
        /// The camera matters: the bubble lives under a WORLD SPACE canvas, so its
        /// world position has to be projected by the camera that renders that canvas,
        /// not necessarily Camera.main.
        /// </summary>
        private static Vector2 BubbleScreenPos(RectTransform rt)
        {
            try
            {
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                var cam = CanvasCamera(rt);
                if (cam == null) cam = UnityEngine.Camera.main;
                if (cam == null) return new Vector2(float.NaN, float.NaN);
                float sx = 0f, sy = 0f;
                for (int i = 0; i < 4; i++)
                {
                    var sp = cam.WorldToScreenPoint(corners[i]);
                    sx += sp.x;
                    sy += UnityEngine.Screen.height - sp.y;
                }
                return new Vector2(sx / 4f, sy / 4f);
            }
            catch { return new Vector2(float.NaN, float.NaN); }
        }

        /// <summary>
        /// Moves the bubble to <paramref name="want"/> (UI space: origin top-left,
        /// y growing downwards).
        ///
        /// Screen-space placement is the ONLY thing that works for this bubble. It is
        /// parented to "NotTurnCanvas", a world-space canvas whose RectTransform is
        /// scaled to 0.01: measured in game, changing anchoredPosition AND
        /// localPosition by 120 units moved the bubble 0 pixels on screen, so both of
        /// those properties are meaningless here.
        /// </summary>
        private static void PlaceBubble(RectTransform rt, Vector2 want)
        {
            PlaceAtScreenPos(rt, want);
        }


        /// <summary>
        /// Where the bubble wants to be for the given head position, before smoothing.
        /// </summary>
        /// <summary>
        /// True when the character is upright.
        ///
        /// Gives the bubble two different homes: beside her head while she stands (so
        /// it does not cover her face) and directly above while she lies along the
        /// screen edge, where there is room and nothing to obscure. Read from the Spine
        /// skeleton's rotation, because a lying pose rotates the skeleton rather than
        /// the rect the bubble hangs off.
        /// </summary>
        private static bool IsStanding(UnityEngine.MonoBehaviour bongo)
        {
            if (bongo == null) return true;
            try
            {
                var sa = bongo.GetComponentInChildren<Spine.Unity.SkeletonAnimation>();
                if (sa != null)
                {
                    float z = sa.transform.eulerAngles.z;
                    if (z > 180f) z -= 360f;
                    var pr = sa.transform.parent;
                    if (pr != null)
                    {
                        float pz = pr.eulerAngles.z;
                        if (pz > 180f) pz -= 360f;
                        z += pz;
                    }
                    bool standing = Mathf.Abs(z) < 40f;
                    Plugin.Dbg(string.Format("BubbleUi pose spineZ={0:F0} -> {1}", z,
                        standing ? "STANDING" : "LYING"));
                    return standing;
                }
                Plugin.Dbg("BubbleUi pose: no SkeletonAnimation; assuming standing");
            }
            catch (Exception e) { Plugin.Dbg("BubbleUi.IsStanding: " + e.Message); }
            return true;
        }

        private static Vector2 DesiredBubblePos(Vector2 uiPos)
        {
            float offX = 0f;
            try { if (Plugin.Cfg != null) offX = Plugin.Cfg.BubbleOffsetX.Value; } catch { }

            // Standing: shift clear of her head so it does not cover her face.
            // Lying on the screen edge: stay centred above.
            bool standing = true;
            try { standing = IsStanding(Plugin.ResolveBongo()); } catch { }

            if (standing)
            {
                float side = 130f;
                try { if (Plugin.Cfg != null) side = Plugin.Cfg.BubbleStandOffsetX.Value; } catch { }
                offX += side * SizeScale;
            }

            // Every pixel offset is authored for the 1x body, so scale them all: a
            // 1.5x or 2x body needs proportionally more clearance or the bubble clips
            // into her hair.
            float scale = SizeScale;
            float wantX = uiPos.x + offX;
            float wantY = uiPos.y - _lastPanelH * 0.5f - HeadGap * scale;
            return KeepOnScreen(wantX, wantY);
        }

        /// <summary>
        /// Puts the bubble where it belongs for the given head position.
        ///
        /// Called when a line is spoken and then every frame while it is visible (see
        /// Tick), so the bubble travels with her instead of being frozen at the spot
        /// she happened to occupy when the line appeared. The travel is eased so a
        /// bobbing idle animation does not turn into a shaking bubble.
        /// </summary>
        private static void ReanchorTo(Vector2 uiPos, bool verbose)
        {
            try
            {
                if (_bubbleMb == null) return;
                var rt = _bubbleMb.GetComponent<RectTransform>();
                if (rt == null) return;

                // Centre the pivot. The prefab ships pivot=(0.40,0.40), so an
                // anchoredPosition placed the pivot point at the head and the
                // bubble's visual centre landed off to the side (the user saw the
                // bubble displaced to the right of the character).
                rt.pivot = new Vector2(0.5f, 0.5f);

                Vector2 want = DesiredBubblePos(uiPos);
                Vector2 placed = want;

                float now = UnityEngine.Time.unscaledTime;
                if (verbose || !_hasSmoothed)
                {
                    // A fresh line appears exactly where it belongs -- no gliding in
                    // from wherever the previous bubble was.
                    _smoothed = want;
                    _hasSmoothed = true;
                }
                else
                {
                    float dt = (_lastReanchorTime < 0f) ? 0f : (now - _lastReanchorTime);
                    if (dt < 0f || dt > 0.25f) dt = 0f;   // game was paused / frame hitch
                    float k = 1f - Mathf.Exp(-10f * dt);   // ~63% of the gap per 100 ms
                    _smoothed = new Vector2(_smoothed.x + (want.x - _smoothed.x) * k,
                                            _smoothed.y + (want.y - _smoothed.y) * k);
                    placed = _smoothed;
                }
                _lastReanchorTime = now;

                // Placement itself lives in PlaceBubble, which measures the coordinate
                // mapping once and then uses it (see the comment there).
                PlaceBubble(rt, placed);

                // One-shot forensic dump, a few frames in so the game's own show
                // animation has had its chance to run.
                if (!_sceneDumped && ++_sceneDumpFrames > 2)
                {
                    _sceneDumped = true;
                    DumpBubbleScene(rt);
                }

                // ---------------------------------------------------------- self-check
                // Screen-space placement on a world-space canvas depends on getting the
                // camera right, so verify the result instead of trusting it.
                if (verbose)
                {
                    try
                    {
                        var verify = BubbleScreenPosVerified(rt);
                        var cam = CanvasCamera(rt);
                        Plugin.Dbg(string.Format(
                            "BubbleUi PLACED target=({0:F0},{1:F0}) verify=({2:F1},{3:F1}) err=({4:F1},{5:F1}) canvasCam={6} worldCam={7} world={8}",
                            want.x, want.y, verify.x, verify.y,
                            verify.x - want.x, verify.y - want.y,
                            cam != null ? cam.name : "<null>",
                            _canvas != null && _canvas.worldCamera != null ? _canvas.worldCamera.name : "<null>",
                            rt.position));
                    }
                    catch (Exception e) { Plugin.Dbg("BubbleUi PLACED: " + e.Message); }
                }

                // Throttle the per-frame follow log in BOTH modes. It used to print every
                // frame while a bubble was visible (8-40 s per line), which grew the debug
                // file by megabytes for no extra information: the same numbers were being
                // repeated. verbose (a fresh line) still logs immediately.
                double sinceLog = (DateTime.UtcNow - _lastAnchorLog).TotalSeconds;
                if (sinceLog < 2.0 && Mathf.Abs(placed.y - _lastLoggedY) < 4f) return;
                if (!verbose && sinceLog < 0.5) return;

                _lastAnchorLog = DateTime.UtcNow;
                _lastLoggedY = placed.y;
                Plugin.Dbg(string.Format(
                    "BubbleUi anchor want=({0:F0},{1:F0}) -> ({2:F0},{3:F0}) clamp=({4:F0},{5:F0})",
                    want.x, want.y, placed.x, placed.y, placed.x - want.x, placed.y - want.y));
            }
            catch (Exception e) { Plugin.Dbg("BubbleUi.ReanchorTo: " + e.Message); }
        }

        /// <summary>
        /// Shows text in the bubble, positioned above the character's head.
        /// Returns false when no bubble could be built, so the caller can fall back.
        /// </summary>
        internal static bool Show(string text, Vector2 uiPos)
        {
            try
            {
                if (!EnsureBuilt()) return false;

                _bubble.SetActive(true);
                _hidden = false;

                // Order matters: fit the text FIRST so we know the bubble's real
                // size, and only then place it.
                SetText(text);
                FitToText();
                ReanchorTo(uiPos, true);

                // let the prefab play its own pop-in
                try
                {
                    var play = _bubbleType.GetMethod("PlayShowAnim",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (play != null) play.Invoke(_bubbleTyped, null);
                }
                catch (Exception e) { Plugin.Dbg("BubbleUi PlayShowAnim: " + e.Message); }

                _shownAt = DateTime.UtcNow;
                _showSeconds = NextShowSeconds > 0.1f ? NextShowSeconds : ShowSeconds;
                NextShowSeconds = 0f;
                Plugin.Dbg("BubbleUi.Show (" + _showSeconds.ToString("F1") + "s) at " + uiPos + ": " + text);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Dbg("BubbleUi.Show failed: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        private static bool EnsureBuilt()
        {
            if (_built && _bubble != null) return true;
            _built = true;
            try
            {
                if (_bubbleType == null) { Plugin.Dbg("BubbleUi: no type"); return false; }

                // Clone one of the game's pooled bubbles. Cloning (rather than
                // reusing an instance) keeps the game's own logic untouched.
                var il2 = Il2CppInterop.Runtime.Il2CppType.From(_bubbleType);
                var arr = UnityEngine.Resources.FindObjectsOfTypeAll(il2);
                if (arr == null || arr.Length == 0) { Plugin.Dbg("BubbleUi: no template"); return false; }

                var templateBase = arr[0] as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                if (templateBase == null) { Plugin.Dbg("BubbleUi: template not Il2CppObjectBase"); return false; }
                var template = templateBase.TryCast<UI.DialogueBubble>();
                if (template == null) { Plugin.Dbg("BubbleUi: template cast failed"); return false; }

                // Pick a parent that is on screen: walk up from the template until an
                // active canvas is found, else fall back to the template's own parent.
                Transform parent = template.transform.parent;
                try
                {
                    var probe = template.transform;
                    int guard = 0;
                    while (probe != null && guard++ < 12)
                    {
                        var c = probe.GetComponent<Canvas>();
                        if (c != null && c.gameObject.activeInHierarchy) { parent = probe; break; }
                        probe = probe.parent;
                    }
                }
                catch { }

                if (parent == null) { Plugin.Dbg("BubbleUi: no parent"); return false; }
                if (!parent.gameObject.activeInHierarchy)
                {
                    Plugin.Dbg("BubbleUi: parent '" + parent.name + "' is not active; bubble would be invisible");
                    return false;
                }

                // Clone via the template's Transform, then fetch typed components
                // from the clone. Instantiate's return value is an untyped interop
                // wrapper here and every TryCast on it failed, so we never use it:
                // Transform.Instantiate keeps the reference usable and
                // GetComponent<T> hands back a properly typed instance.
                var cloneTf = UnityEngine.Object.Instantiate(
                    template.transform, parent, false);
                if (cloneTf == null) { Plugin.Dbg("BubbleUi: Instantiate returned null"); return false; }

                cloneTf.name = "OcAIBubble";
                cloneTf.gameObject.SetActive(true);

                var typed = cloneTf.GetComponent<UI.DialogueBubble>();
                var mb2 = cloneTf.GetComponent<UnityEngine.MonoBehaviour>();
                if (typed == null) { Plugin.Dbg("BubbleUi: clone has no DialogueBubble"); return false; }

                _bubbleTyped = typed;
                _bubbleMb = typed;                  // DialogueBubble is a MonoBehaviour
                _bubble = typed.gameObject;

                // Our clone shares the prefab with the game's own bubbles, so it has to
                // be excluded from the dialogue harvester by identity -- otherwise the
                // plugin's replies would be filed as things the game character said.
                GameContext.IgnoreBubble(typed);
                if (mb2 == null) Plugin.Dbg("BubbleUi: note - no plain MonoBehaviour on clone");

                // The clone inherits the prefab's Button; remove the click handler so
                // tapping the bubble cannot trigger game behaviour.
                try
                {
                    var btn = cloneTf.GetComponent<UnityEngine.UI.Button>();
                    if (btn != null) btn.onClick.RemoveAllListeners();
                }
                catch { }

                Plugin.Dbg("BubbleUi: built under '" + parent.name + "'");
                DumpHierarchy(cloneTf, 0);

                // cache the panel node: this is the RectTransform that actually has to
                // grow, not the root
                try
                {
                    var panelTf = cloneTf.Find("Bubble_Img");
                    if (panelTf != null)
                    {
                        _panelRt = panelTf.TryCast<RectTransform>();
                        Plugin.Dbg("BubbleUi: panel rect = " + (_panelRt != null));
                    }
                    else Plugin.Dbg("BubbleUi: child 'Bubble_Img' not found");
                }
                catch (Exception e) { Plugin.Dbg("BubbleUi: panel lookup failed: " + e.Message); }

                _bubble.SetActive(false);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Dbg("BubbleUi.EnsureBuilt: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// One-shot structural dump of the cloned bubble, so the sizing maths can be
        /// based on the prefab's real layout instead of assumptions.
        /// </summary>
        private static void DumpHierarchy(Transform root, int depth)
        {
            try
            {
                if (root == null || depth > 4) return;
                string indent = new string(' ', depth * 2);

                string info = indent + root.name;
                var rt = root.GetComponent<RectTransform>();
                if (rt != null)
                {
                    info += string.Format(" [rect {0:F0}x{1:F0} anchor={2} pivot={3}]",
                        rt.sizeDelta.x, rt.sizeDelta.y, rt.anchorMin, rt.pivot);
                }

                var img = root.GetComponent<UnityEngine.UI.Image>();
                if (img != null)
                {
                    info += " [Image sprite=" + (img.sprite != null ? img.sprite.name : "null") +
                            " type=" + img.type + " preserveAspect=" + img.preserveAspect + "]";
                }

                var tmp = root.GetComponent<TMPro.TMP_Text>();
                if (tmp != null)
                {
                    info += " [TMP size=" + tmp.fontSize + " align=" + tmp.alignment +
                            " wrap=" + tmp.enableWordWrapping + "]";
                }

                Plugin.Dbg("BubbleUi.hier " + info);

                for (int i = 0; i < root.childCount; i++)
                    DumpHierarchy(root.GetChild(i), depth + 1);
            }
            catch (Exception e) { Plugin.Dbg("BubbleUi.DumpHierarchy: " + e.Message); }
        }

        private static void SetText(string text)
        {
            try
            {
                var b = _bubbleTyped as UI.DialogueBubble;
                if (b == null) { Plugin.Dbg("BubbleUi.SetText: no typed ref"); return; }
                var t = b._strTxt;
                if (t == null) { Plugin.Dbg("BubbleUi.SetText: _strTxt null"); return; }
                t.text = text;
            }
            catch (Exception e) { Plugin.Dbg("BubbleUi.SetText: " + e.Message); }
        }

        /// <summary>Size of the last fitted bubble, used to sit it above the head
        /// and to keep it inside the window.</summary>
        private static float _lastPanelH = 120f;
        private static float _lastPanelW = 180f;

        internal static float LastPanelHeight { get { return _lastPanelH; } }

        /// <summary>
        /// Nudges a desired bubble position back inside the window.
        ///
        /// The pet window is transparent: everything outside it is simply not drawn,
        /// so a bubble anchored to a character standing at the screen edge used to
        /// vanish ("褰撳ス鍦ㄥ彸杈规垨鑰呮壎鐫€灞忓箷姝ご鏃跺氨浼氱湅涓嶈浜?). Clamping to the
        /// window minus half the bubble keeps the whole panel visible, with a little
        /// breathing room at the very top.
        ///
        /// UI space has its origin at the TOP-left, so y grows downwards.
        /// </summary>
        private static Vector2 KeepOnScreen(float wantX, float wantY)
        {
            float halfW, halfH;
            if (_panelRt != null)
            {
                halfW = _panelRt.sizeDelta.x * 0.5f;
                halfH = _panelRt.sizeDelta.y * 0.5f;
            }
            else
            {
                halfW = _lastPanelW * 0.5f;
                halfH = _lastPanelH * 0.5f;
            }
            if (halfW < 1f) halfW = _lastPanelW * 0.5f;
            if (halfH < 1f) halfH = _lastPanelH * 0.5f;

            // Room to leave at each screen edge.
            //
            // Reported bug: "气泡的头已经到外面去了" -- the bubble's own tail (Point1_Img /
            // Point2_Img hang BELOW the panel) plus a mere 8 px of air was not enough, so
            // the top of the bubble could be pushed past the window edge even though the
            // panel's centre was clamped. The top therefore reserves the tail height as
            // well; sides and bottom keep a small margin.
            const float edge = 12f;
            const float tailHeight = 45f;

            float w = UnityEngine.Screen.width;
            float h = UnityEngine.Screen.height;
            if (w <= 0f || h <= 0f) return new Vector2(wantX, wantY);

            // Bubble wider than the space available: there is no position that shows
            // all of it, so keep as much as possible instead of centring it. Centring
            // was the bug behind "角色挂右边会出错": with her at x=1897 the bubble
            // jumped to the middle of the screen, nowhere near her.
            float minX = halfW + edge, maxX = w - halfW - edge;
            float x;
            if (minX <= maxX)
                x = Mathf.Clamp(wantX, minX, maxX);
            else
                x = Mathf.Min(wantX, Mathf.Max(halfW * 0.5f, w * 0.5f));

            // Top of the window is ui y = 0. The panel's centre must stay at least
            // half its height PLUS the tail away from the top, or the bubble is clipped.
            float minY = halfH + tailHeight;
            float maxY = h - halfH - edge;
            float y = (minY <= maxY) ? Mathf.Clamp(wantY, minY, maxY) : minY;

            return new Vector2(x, y);
        }

        /// <summary>
        /// Sizes the bubble to its text.
        ///
        /// Layout facts learned from the prefab dump (this is what the earlier
        /// attempts got wrong -- they resized the ROOT, but the visible panel is a
        /// child):
        ///
        ///   OcAIBubble           183x166   root, also carries a think-bubble image
        ///     Point2_Img          40x40
        ///     Bubble_Img        172x114   &lt;- the actual bubble panel, Sliced sprite
        ///       Str_Txt         -40x-40   TMP text, centred, wraps
        ///     Point1_Img          25x25
        ///
        /// So: measure the text at a fixed wrap width, then size Bubble_Img to the
        /// text plus padding and stretch Str_Txt to fill it. The panel sprite is
        /// 9-sliced, so growing it keeps the corners crisp.
        /// </summary>
        private static void FitToText()
        {
            try
            {
                var b = _bubbleTyped as UI.DialogueBubble;
                if (b == null) { Plugin.Dbg("BubbleUi.FitToText: no typed bubble"); return; }
                var txt = b._strTxt;
                if (txt == null) { Plugin.Dbg("BubbleUi.FitToText: no _strTxt"); return; }

                if (_panelRt == null) { Plugin.Dbg("BubbleUi.FitToText: no panel rect"); return; }
                var trt = txt.GetComponent<RectTransform>();
                if (trt == null) { Plugin.Dbg("BubbleUi.FitToText: text has no rect"); return; }

                // A slightly wider panel means fewer wrapped lines and a shorter
                // bubble, which keeps it visually close to the character.
                const float MaxWrap = 230f;
                const float PadX = 26f;         // inner padding of the panel
                const float PadY = 22f;

                // Measure the text UNCONSTRAINED first. Asking for preferredWidth
                // while the rect is pinned to a width returns that pinned width, so
                // short lines ("杩樺墿 1.39 CNY") were reported as 230 and produced a
                // panel that was far wider than its text.
                txt.enableWordWrapping = false;
                trt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 2000f);
                float natural = txt.preferredWidth;

                float wrap = Mathf.Min(natural, MaxWrap);
                txt.enableWordWrapping = natural > MaxWrap;
                trt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, wrap);
                float th = txt.preferredHeight;

                float pw = wrap + PadX;
                float ph = th + PadY;

                // 2) size the panel, then stretch the text to fill its inner box
                _panelRt.sizeDelta = new Vector2(pw, ph);
                trt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, pw - PadX);
                trt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, ph - PadY);
                trt.anchoredPosition = Vector2.zero;

                _lastPanelH = ph;
                _lastPanelW = pw;

                Plugin.Dbg(string.Format(
                    "BubbleUi.FitToText natural={0:F0} wrap={1:F0} text={2:F0}x{3:F0} -> panel={4:F0}x{5:F0}",
                    natural, wrap, wrap, th, pw, ph));
            }
            catch (Exception e) { Plugin.Dbg("BubbleUi.FitToText: " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
