using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OcAI
{
    /// <summary>
    /// Captures the game's dialogue widget and, optionally, redirects what the
    /// character is about to say through the DeepSeek API.
    ///
    /// Targets on UI.UiPreviewDialogue:
    ///   void ShowText(OCData ocData, string textContent, Action onComplete)
    ///   void ShowDialogue(OCData ocData, string langId, Action onComplete)
    ///
    /// ShowText is where already-resolved text lands, which is what we can rewrite.
    /// The instance is remembered so the plugin can push its own lines later.
    /// </summary>
    internal static class DialoguePatch
    {
        internal static MethodBase TargetShowText;
        internal static MethodBase TargetShowDialogue;

        /// <summary>Last live UiPreviewDialogue instance.</summary>
        internal static UnityEngine.MonoBehaviour LiveWidget;

        internal static void Capture(object instance)
        {
            try
            {
                var mb = instance as UnityEngine.MonoBehaviour;
                if (mb != null)
                {
                    LiveWidget = mb;
                    // This widget is a live game MonoBehaviour, so it can drive our
                    // coroutine pump without injecting any type into IL2CPP.
                    Plugin.RegisterPump(mb);
                }
            }
            catch { }
        }

        internal static void PostfixShowText(object __instance, string textContent)
        {
            try
            {
                Capture(__instance);
                Plugin.Dbg("ShowText: '" + (textContent ?? "<null>") + "'");
                Plugin.NoteGameUtterance(textContent);
            }
            catch (Exception e)
            {
                Plugin.Dbg("ShowText postfix error: " + e.GetType().Name + ": " + e.Message);
            }
        }

        internal static void PostfixShowDialogue(object __instance, string langId)
        {
            try
            {
                Capture(__instance);
                Plugin.Dbg("ShowDialogue: langId='" + (langId ?? "<null>") + "'");
            }
            catch (Exception e)
            {
                Plugin.Dbg("ShowDialogue postfix error: " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
