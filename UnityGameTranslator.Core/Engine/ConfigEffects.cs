using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Whether what somebody just applied is something a text on screen SHOWS — the one question
    /// that decides if a settings Apply has to redraw the game.
    ///
    /// 🔴 **Because an Apply redrew everything, whatever had changed** (2026-09-20). Changing a
    /// hotkey rebuilt the mesh of every text in the scene — a pass measured at 689 ms on a large
    /// IL2CPP game — plus a walk of the mod's own interface and a drop of every processing cache,
    /// for a value no text displays. Reported as: *"quand j'apply une hotkey […] j'ai des freeze"*.
    ///
    /// 🔴 **The answer is written in the SAFE direction, and that is the whole design.** The rule
    /// is not "these settings need a redraw" — one forgotten and a game silently keeps showing the
    /// old text, which is exactly what this project has already paid for (issue #21,
    /// analyse/refresh-not-reaching-screen.md). It is the reverse: **everything redraws, EXCEPT
    /// what is named in <see cref="NeverOnScreen"/>.** A setting added tomorrow, or one nobody
    /// classified, keeps today's behaviour. The only mistake this can make is redrawing for
    /// nothing.
    ///
    /// ⚠ The comparison is made by reflection over <see cref="ModConfig"/> rather than from a list
    /// of fields somebody maintains: a list would be a second thing to forget, and forgetting it
    /// would fail silently in the dangerous direction.
    ///
    /// Pure by contract: no Unity, no state, no clock — see tests/UnityGameTranslator.Core.Checks.
    /// </summary>
    public static class ConfigEffects
    {
        /// <summary>
        /// The settings NO text on screen shows — the only ones an Apply may skip the redraw for.
        ///
        /// Each name is a property of <see cref="ModConfig"/>, or "sync.x" for one of its sync
        /// block. A name that is not one fails the checks, so a rename cannot leave a dead entry
        /// silently widening what gets skipped.
        ///
        /// ⚠ Each of these was read for one thing: nothing in the text path — the patches, the
        /// scanner, the font manager — consults it. The three kinds:
        /// · **the keys** — what opens a window, never what a window shows;
        /// · **the network** — proxy, the sync rhythm, the notices; the overlay's own position is
        ///   applied by its own act, not by a redraw of the game;
        /// · **the translator's own dials** — where requests go and how they are phrased. They
        ///   decide what the NEXT translation asks for, and change nothing already on screen.
        /// </summary>
        public static readonly IReadOnlyList<string> NeverOnScreen = new[]
        {
            // The keys.
            "settings_hotkey", "toggle_translations_hotkey", "toggle_ai_hotkey",
            "toggle_images_hotkey", "toggle_fonts_hotkey", "toggle_overlay_hotkey",
            "open_inspector_hotkey", "open_upload_hotkey", "open_exclusion_mode_hotkey",
            "open_text_editor_hotkey", "force_scan_hotkey",

            // The network, and what this program says to itself about it.
            "proxy_mode", "proxy_url", "proxy_username", "proxy_password", "proxy_bypass_local",
            "api_token", "api_user", "api_token_server", "api_base_url", "website_base_url",
            "sse_base_url", "online_mode",
            "sync.update_check_frequency", "sync.realtime_own_translation", "sync.auto_download",
            "sync.notify_updates", "sync.check_mod_updates", "sync.notify_prereleases",
            "sync.notifications_enabled", "sync.notification_position", "sync.merge_strategy",
            "sync.ignored_uuids", "sync.dismissed_notices", "sync.check_update_on_start",

            // The translator's dials: they shape the next request, not the text already drawn.
            "ai_url", "ai_model", "ai_api_key", "google_api_key", "deepl_api_key", "deepl_use_free",
            "game_context", "timeout_ms", "rate_limit_retry_delay", "ai_max_attempts",
            "ai_temperature", "ai_temperature_repair", "ai_temperature_retranslate",
            "ai_seed", "ai_seed_repair", "ai_seed_retranslate", "preload_model",
            "cache_new_translations",

            // What the game gives us of the keyboard and the mouse, and whether it pauses: input,
            // never text.
            "capture_keyboard", "capture_keyboard_focus_only", "capture_game_menus",
            "capture_game_clicks", "capture_mouse_axes", "pause_game",

            // ⚠ Not a ModConfig field: it lives in the translation's own settings, and the screen
            // that writes it adds this name to the changes by hand. Named here for the same reason
            // as the rest — so that skipping the redraw for it is a decision somebody wrote down.
            // It answers a defect of a game's EventSystem; no text depends on it.
            "disable_eventsystem_override",

            // Said in a log, about our own windows, or about how often we look.
            "debug", "debug_ai", "config_version", "first_run_completed", "window_preferences",
            "panel_opacity_focused", "panel_opacity_unfocused",
            "max_text_detection_latency_seconds",
        };

        /// <summary>
        /// Every setting's value as text, so two moments can be compared without cloning anything.
        ///
        /// ⚠ Invariant culture: a float rendered "0,3" here and "0.3" there would read as a change
        /// on a French machine and nowhere else.
        /// </summary>
        public static Dictionary<string, string> Snapshot(ModConfig config)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (config == null) return values;

            foreach (var property in Fields(typeof(ModConfig)))
            {
                object value = Read(config, property);

                if (property.Name == "sync" && value != null)
                {
                    foreach (var inner in Fields(value.GetType()))
                        values["sync." + inner.Name] = Text(Read(value, inner));
                    continue;
                }

                values[property.Name] = Text(value);
            }

            return values;
        }

        /// <summary>What moved between two snapshots — added, removed or different.</summary>
        public static List<string> Changed(IDictionary<string, string> before,
                                           IDictionary<string, string> after)
        {
            var changed = new List<string>();
            if (before == null || after == null) return changed;

            foreach (var pair in after)
            {
                string was;
                if (!before.TryGetValue(pair.Key, out was) || !string.Equals(was, pair.Value, StringComparison.Ordinal))
                    changed.Add(pair.Key);
            }

            foreach (var pair in before)
                if (!after.ContainsKey(pair.Key)) changed.Add(pair.Key);

            return changed;
        }

        /// <summary>
        /// Whether these changes have to reach the screen. True unless EVERY one of them is named
        /// in <see cref="NeverOnScreen"/> — and true when nothing changed is not asked here: with
        /// no change at all there is nothing to apply, which the caller decides.
        /// </summary>
        public static bool NeedsRedraw(IEnumerable<string> changed)
        {
            if (changed == null) return true;

            var invisible = new HashSet<string>(NeverOnScreen, StringComparer.Ordinal);

            foreach (var key in changed)
                if (!invisible.Contains(key)) return true;

            return false;
        }

        private static IEnumerable<PropertyInfo> Fields(Type type)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite) continue;
                if (property.GetIndexParameters().Length > 0) continue;
                yield return property;
            }
        }

        private static object Read(object holder, PropertyInfo property)
        {
            try { return property.GetValue(holder, null); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// One value as text. A list is joined rather than compared by reference: two lists holding
        /// the same names are the same answer, and ToString() on them says neither.
        /// </summary>
        private static string Text(object value)
        {
            if (value == null) return null;

            var list = value as System.Collections.IEnumerable;
            if (list != null && !(value is string))
            {
                var parts = new List<string>();
                foreach (var item in list) parts.Add(Text(item) ?? "");
                return string.Join("\u001f", parts.ToArray());
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
