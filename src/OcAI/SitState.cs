using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace OcAI
{
    /// <summary>
    /// "Is she sitting?" -- the gate for the F-key AI chat.
    ///
    /// Two independent sources, both read from the live game, in this order:
    ///
    ///   1. THE SPINE ANIMATION NAME. This is the primary signal because it is the one
    ///      that actually works. Measured live: the current track's clip name reads
    ///      cleanly as "new/standing-e", "new/walking-a", "new/standing-b". The game
    ///      swaps the animation when she sits, so "does the current clip look like a
    ///      sitting clip" is a direct answer to "is she sitting".
    ///
    ///   2. The StateMachine's own state dictionary. The obvious accessors do NOT work
    ///      here and the attempts are kept in the log as evidence:
    ///        - GetCurrentStateType() returns an Il2CppSystem.Type wrapper whose
    ///          ToString() is the literal string "Il2CppSystem.Type";
    ///        - GetCurrentState() returns a wrapper generated from the DECLARED return
    ///          type (IState), so .GetType().Name is "IState", never "OcSittingState".
    ///      So the state is identified by diffing the dictionary's keys instead, which
    ///      does give the real state class names.
    ///
    /// Anything else (rotation-based pose guess, then FailOpen) is a last resort.
    ///
    /// Everything is reflection-based, following the same policy as SettingsUi: interop
    /// member names are known from the dump, but binding to private game members at
    /// compile time breaks on a game update, whereas a missing member here only
    /// downgrades the feature instead of killing the whole plugin.
    /// </summary>
    internal static class SitState
    {
        /// <summary>State that identifies the sitting family in the state dictionary.</summary>
        private const string SitStateName = "OcSittingState";

        /// <summary>Substrings that mark a clip as a sitting clip.</summary>
        private static readonly string[] SittingClipHints =
        {
            "sit", "seat", "chair", "desk"
        };

        /// <summary>Clip name substrings that positively mean NOT sitting.</summary>
        private static readonly string[] StandingClipHints =
        {
            "stand", "walk", "idle", "run", "jump", "hide", "drag", "sleep"
        };

        /// <summary>When nothing can be read, assume sitting (see class comment).</summary>
        internal static volatile bool FailOpen = true;

        /// <summary>Last computed verdict, exposed for the settings row / diagnostics.</summary>
        private static volatile bool _lastSitting;

        /// <summary>Cached clip names that look like sitting clips.</summary>
        private static List<string> _sittingClips;
        private static bool _clipsScanned;

        /// <summary>Cached list of all clip names, for the first diagnostic dump.</summary>
        private static int _clipDumpCountdown = 2;

        // ------------------------------------------- 0. state enter/exit tracking
        //
        // The one signal that is NOT a guess: Harmony postfixes on OcSittingState's own
        // OnEnter / OnExit. The state class is a plain public class with public
        // OnEnter()/OnExit() (see the interop dump), so it can be patched directly
        // instead of inferring the pose from the animation or the state machine.
        //
        // A depth counter is used rather than a bool so a missed OnExit cannot leave the
        // flag stuck on: OnEnter increments, OnExit decrements, and "sitting" is
        // depth > 0.

        private static int _sittingDepth;
        private static bool _enterHooked;
        private static bool _exitHooked;
        private static DateTime _sittingSince = DateTime.MinValue;

        /// <summary>True when at least one sitting state is currently entered.</summary>
        internal static bool TrackedAsSitting { get { return System.Threading.Volatile.Read(ref _sittingDepth) > 0; } }

        /// <summary>Whether the OnEnter/OnExit hooks were installed (diagnostics).</summary>
        internal static bool HooksInstalled { get { return _enterHooked || _exitHooked; } }

        /// <summary>Harmony postfix on OcSittingState.OnEnter().</summary>
        internal static void OnSittingEnter()
        {
            int now = System.Threading.Interlocked.Increment(ref _sittingDepth);
            if (now == 1) _sittingSince = DateTime.UtcNow;
            Plugin.Dbg("SitState: OcSittingState.OnEnter -> depth=" + now);
        }

        /// <summary>Harmony postfix on OcSittingState.OnExit().</summary>
        internal static void OnSittingExit()
        {
            int now = System.Threading.Interlocked.Decrement(ref _sittingDepth);
            if (now < 0)
            {
                // More exits than enters: clamp instead of going negative forever.
                System.Threading.Interlocked.Exchange(ref _sittingDepth, 0);
                now = 0;
            }
            Plugin.Dbg("SitState: OcSittingState.OnExit -> depth=" + now);
        }

        /// <summary>
        /// Hooks the sitting state's enter/exit. Called once from Plugin.ApplyPatches.
        /// Failure is not fatal: the clip/pose/settings fallbacks still run.
        /// </summary>
        internal static void Attach(System.Reflection.Assembly game)
        {
            try
            {
                if (game == null) { Plugin.Dbg("SitState.Attach: no assembly"); return; }

                var t = game.GetType("OcSittingState");
                if (t == null) { Plugin.Dbg("SitState.Attach: OcSittingState not found"); return; }

                var harmony = new HarmonyLib.Harmony(MyPluginInfo.PLUGIN_GUID + ".sit");

                var onEnter = t.GetMethod("OnEnter",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (onEnter != null)
                {
                    harmony.Patch(onEnter, postfix: new HarmonyLib.HarmonyMethod(
                        typeof(SitState).GetMethod("OnSittingEnter",
                            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)));
                    _enterHooked = true;
                    Plugin.Dbg("SitState.Attach: patched OcSittingState.OnEnter");
                }
                else Plugin.Dbg("SitState.Attach: OnEnter not found");

                var onExit = t.GetMethod("OnExit",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (onExit != null)
                {
                    harmony.Patch(onExit, postfix: new HarmonyLib.HarmonyMethod(
                        typeof(SitState).GetMethod("OnSittingExit",
                            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)));
                    _exitHooked = true;
                    Plugin.Dbg("SitState.Attach: patched OcSittingState.OnExit");
                }
                else Plugin.Dbg("SitState.Attach: OnExit not found");

                Plugin.Info("[OcAI] sitting hooks: enter=" + _enterHooked + " exit=" + _exitHooked);
            }
            catch (Exception e)
            {
                Plugin.Dbg("SitState.Attach failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        internal static bool IsSitting()
        {
            // 0) The state's own enter/exit hooks -- the only non-inferred signal.
            if (TrackedAsSitting)
            {
                _lastSitting = true;
                return true;
            }
            if (HooksInstalled)
            {
                // Hooks are live and depth is 0: that IS authoritative evidence.
                _lastSitting = false;
                return false;
            }

            bool? byClip = VerdictByClip();
            if (byClip.HasValue)
            {
                _lastSitting = byClip.Value;
                return byClip.Value;
            }

            bool? byState = VerdictByStateDict();
            if (byState.HasValue)
            {
                _lastSitting = byState.Value;
                return byState.Value;
            }

            bool? pose = PoseVerdict();
            if (pose.HasValue)
            {
                _lastSitting = pose.Value;
                return pose.Value;
            }

            _lastSitting = FailOpen;
            return FailOpen;
        }

        internal static bool LastVerdict { get { return _lastSitting; } }

        // ------------------------------------------------------- 1. animation name

        /// <summary>Name of the clip on animation track 0, e.g. "new/standing-b".</summary>
        internal static string CurrentClipName()
        {
            try
            {
                var bongo = Plugin.ResolveBongo();
                if (bongo == null) return null;

                var sa = bongo.GetComponentInChildren<Spine.Unity.SkeletonAnimation>();
                if (sa == null) return null;
                if (sa.AnimationState == null) return null;

                var track = sa.AnimationState.GetCurrent(0);
                if (track == null) return null;

                var clip = track.Animation;
                if (clip == null) return null;

                return clip.Name;
            }
            catch (Exception e)
            {
                Plugin.Dbg("SitState.CurrentClipName: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Verdict from the current clip. Returns null when no clip name is readable, or
        /// when the clip is one we have never seen before and cannot classify.
        /// </summary>
        private static bool? VerdictByClip()
        {
            // Always touch the clip table: it dumps every clip name once per process, so
            // the log shows the game's real animation naming even when the CURRENT clip
            // cannot be read (that dump is what makes a failed judgement diagnosable).
            var sitting = SittingClips();

            string clip = CurrentClipName();
            if (string.IsNullOrEmpty(clip)) return null;

            string lower = clip.ToLowerInvariant();

            // Positive: a known sitting clip, or one matching the sitting hints.
            if (sitting != null)
            {
                for (int i = 0; i < sitting.Count; i++)
                {
                    if (lower.IndexOf(sitting[i], StringComparison.Ordinal) >= 0)
                    {
                        Plugin.Dbg("SitState clip '" + clip + "' -> SITTING (known sit clip)");
                        return true;
                    }
                }
            }

            for (int i = 0; i < SittingClipHints.Length; i++)
            {
                if (lower.IndexOf(SittingClipHints[i], StringComparison.Ordinal) >= 0)
                {
                    Plugin.Dbg("SitState clip '" + clip + "' -> SITTING (hint '" + SittingClipHints[i] + "')");
                    return true;
                }
            }

            // Negative: a clip that positively reads as upright / moving.
            for (int i = 0; i < StandingClipHints.Length; i++)
            {
                if (lower.IndexOf(StandingClipHints[i], StringComparison.Ordinal) >= 0)
                {
                    Plugin.Dbg("SitState clip '" + clip + "' -> not sitting (hint '" + StandingClipHints[i] + "')");
                    return false;
                }
            }

            Plugin.Dbg("SitState clip '" + clip + "' -> unclassified");
            return null;
        }

        /// <summary>
        /// All clip names in the skeleton, filtered to the ones that look like sitting
        /// clips. Also dumps the full clip list once per process, so the real naming
        /// scheme is visible in the log instead of guessed.
        /// </summary>
        private static List<string> SittingClips()
        {
            if (_clipsScanned) return _sittingClips;
            _clipsScanned = true;
            _sittingClips = new List<string>();

            try
            {
                var names = AllClipNames();
                if (names == null || names.Count == 0)
                {
                    Plugin.Dbg("SitState: no clip names readable");
                    return _sittingClips;
                }

                if (_clipDumpCountdown > 0)
                {
                    _clipDumpCountdown--;
                    Plugin.Dbg("=== SitState clips (" + names.Count + "): " + string.Join(", ", names.ToArray()));
                }

                for (int i = 0; i < names.Count; i++)
                {
                    string n = names[i];
                    if (string.IsNullOrEmpty(n)) continue;
                    string lower = n.ToLowerInvariant();
                    for (int h = 0; h < SittingClipHints.Length; h++)
                    {
                        if (lower.IndexOf(SittingClipHints[h], StringComparison.Ordinal) >= 0)
                        {
                            _sittingClips.Add(lower);
                            break;
                        }
                    }
                }

                Plugin.Dbg("SitState: " + _sittingClips.Count + " sitting-looking clip(s)" +
                           (_sittingClips.Count > 0 ? ": " + string.Join(", ", _sittingClips.ToArray()) : ""));
            }
            catch (Exception e)
            {
                Plugin.Dbg("SitState.SittingClips: " + e.GetType().Name + ": " + e.Message);
            }

            return _sittingClips;
        }

        /// <summary>
        /// Enumerates the skeleton's clip names. spine-unity types are referenced at
        /// compile time, but the collection members are reached reflectively so a Spine
        /// version change degrades to "no clip names" instead of a load failure.
        /// </summary>
        private static List<string> AllClipNames()
        {
            var result = new List<string>();
            try
            {
                var bongo = Plugin.ResolveBongo();
                if (bongo == null) return result;

                var sa = bongo.GetComponentInChildren<Spine.Unity.SkeletonAnimation>();
                if (sa == null) return result;

                var state = sa.AnimationState;
                if (state == null) return result;

                object data = GetMember(state, "Data");
                if (data == null) return result;

                object anims = GetMember(data, "Animations");
                if (anims == null) return result;

                object countObj = GetMember(anims, "Count");
                int count = countObj is int ? (int)countObj : 0;
                if (count <= 0) return result;

                var indexer = anims.GetType().GetProperty("Item",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (indexer == null) return result;

                for (int i = 0; i < count && i < 400; i++)
                {
                    object clip;
                    try { clip = indexer.GetValue(anims, new object[] { i }); }
                    catch { continue; }
                    if (clip == null) continue;

                    object name = GetMember(clip, "Name");
                    if (name != null) result.Add(name.ToString());
                }
            }
            catch (Exception e)
            {
                Plugin.Dbg("SitState.AllClipNames: " + e.GetType().Name + ": " + e.Message);
            }
            return result;
        }

        /// <summary>Reads a property or field by name, through interop if needed.</summary>
        private static object GetMember(object target, string name)
        {
            if (target == null) return null;
            var t = target.GetType();

            try
            {
                var p = t.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanRead) return p.GetValue(target);
            }
            catch { }

            try
            {
                var f = t.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f.GetValue(target);
            }
            catch { }

            return null;
        }

        // ------------------------------------------------ 2. state dictionary probe

        private static bool _stateKeysDumped;

        /// <summary>
        /// The state dictionary keys are real System.Type objects, so their .Name is
        /// trustworthy (unlike the current-state wrappers). Reading the dictionary tells
        /// us the sitting state exists; whether it is the ACTIVE one is inferred from the
        /// clip, which is why this is only the second source.
        /// </summary>
        private static bool? VerdictByStateDict()
        {
            try
            {
                var bongo = Plugin.ResolveBongo();
                if (bongo == null) return null;

                var machine = FindOcMachine(bongo);
                if (machine == null) return null;

                var dict = GetMember(machine, "stateDict");
                if (dict == null) return null;

                var keys = GetMember(dict, "Keys");
                if (keys == null) return null;

                // IEnumerable of keys -> names
                var enumerable = keys as System.Collections.IEnumerable;
                if (enumerable == null) return null;

                bool hasSittingState = false;
                var names = new List<string>();
                foreach (var k in enumerable)
                {
                    if (k == null) continue;
                    string n = null;
                    try { n = k.ToString(); } catch { }
                    if (string.IsNullOrEmpty(n)) continue;
                    names.Add(n);
                    if (n.IndexOf(SitStateName, StringComparison.Ordinal) >= 0) hasSittingState = true;
                }

                if (!_stateKeysDumped && names.Count > 0)
                {
                    _stateKeysDumped = true;
                    Plugin.Dbg("=== SitState stateDict keys (" + names.Count + "): " +
                               string.Join(", ", names.ToArray()));
                }

                if (!hasSittingState)
                {
                    Plugin.Dbg("SitState: stateDict has no sitting state -> not sitting");
                    return false;
                }

                // The state exists but we cannot see which entry is active: no verdict.
                Plugin.Dbg("SitState: stateDict has " + SitStateName + " but active state is unknown");
                return null;
            }
            catch (Exception e)
            {
                Plugin.Dbg("SitState.VerdictByStateDict: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// BongoOC owns the state machine in the private field 'ocMachine'. Try the
        /// property/field by name first; a component lookup is the last resort.
        /// </summary>
        private static object FindOcMachine(UnityEngine.MonoBehaviour bongo)
        {
            if (bongo == null) return null;

            var viaMember = GetMember(bongo, "ocMachine");
            if (viaMember != null) return viaMember;

            try
            {
                var comps = bongo.GetComponents<UnityEngine.MonoBehaviour>();
                if (comps != null)
                {
                    for (int i = 0; i < comps.Length; i++)
                    {
                        var c = comps[i];
                        if (c == null) continue;
                        if (c.GetType().Name.IndexOf("OcMachine", StringComparison.Ordinal) >= 0)
                            return c;
                    }
                }
            }
            catch (Exception e) { Plugin.Dbg("SitState sibling scan: " + e.Message); }

            return null;
        }

        // ------------------------------------------------------------ 3. pose guess

        /// <summary>
        /// Rotation fallback for the case where the skeleton is unreachable: she turns
        /// towards a desk when she sits. Returns null when the pose cannot be read.
        /// </summary>
        internal static bool? PoseVerdict()
        {
            try
            {
                var bongo = Plugin.ResolveBongo();
                if (bongo == null) return null;
                var sa = bongo.GetComponentInChildren<Spine.Unity.SkeletonAnimation>();
                if (sa == null) return null;

                float z = sa.transform.eulerAngles.z;
                if (z > 180f) z -= 360f;
                var pr = sa.transform.parent;
                if (pr != null)
                {
                    float pz = pr.eulerAngles.z;
                    if (pz > 180f) pz -= 360f;
                    z += pz;
                }
                Plugin.Dbg("SitState pose z=" + z.ToString("F1"));
                return Mathf.Abs(z) > 75f;
            }
            catch (Exception e)
            {
                Plugin.Dbg("SitState.PoseVerdict: " + e.Message);
                return null;
            }
        }

        // ---------------------------------------------------------------- diagnostics

        /// <summary>
        /// One full diagnostic line for the log. Pressing F always writes this, so a
        /// refused request always leaves the reason behind.
        /// </summary>
        internal static string Describe()
        {
            bool? byClip = null;
            bool? byState = null;
            string clip = "<unavailable>";
            string stateKeys = "<unavailable>";

            try { clip = CurrentClipName() ?? "<null>"; } catch { }
            try { byClip = VerdictByClip(); } catch { }
            try { byState = VerdictByStateDict(); } catch { }

            try
            {
                var bongo = Plugin.ResolveBongo();
                var machine = bongo != null ? FindOcMachine(bongo) : null;
                if (machine != null)
                {
                    var dict = GetMember(machine, "stateDict");
                    var keys = dict != null ? GetMember(dict, "Keys") : null;
                    var en = keys as System.Collections.IEnumerable;
                    if (en != null)
                    {
                        var list = new List<string>();
                        foreach (var k in en) { if (k != null) list.Add(k.ToString()); }
                        stateKeys = list.Count + " keys";
                    }
                }
            }
            catch { }

            bool sitting = IsSitting();
            return "sit probe: hooks=" + HooksInstalled + " depth=" + _sittingDepth +
                   " clip=" + clip +
                   " byClip=" + (byClip.HasValue ? byClip.Value.ToString() : "?") +
                   " byState=" + (byState.HasValue ? byState.Value.ToString() : "?") +
                   " stateDict=" + stateKeys +
                   " sitting=" + sitting + " failOpen=" + FailOpen;
        }
    }
}
