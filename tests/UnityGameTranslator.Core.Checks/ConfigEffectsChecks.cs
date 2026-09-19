using System;
using System.Collections.Generic;
using System.Linq;
using UnityGameTranslator.Core;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// What an Apply has to redraw, and — the half that matters — what it must never skip.
    ///
    /// 🔴 The rule is written in the safe direction (<see cref="ConfigEffects"/>): everything
    /// redraws except what is named. These cases hold that direction, because the cheap mistake
    /// (redraw for nothing) and the expensive one (a game left showing the old text — issue #21)
    /// are not symmetrical and only the second is invisible.
    /// </summary>
    internal static class ConfigEffectsChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            var fresh = new ModConfig();
            var snapshot = ConfigEffects.Snapshot(fresh);

            // Every name is a real setting — a renamed field must not leave a dead entry behind,
            // silently widening what an Apply is allowed to skip.
            var ghosts = ConfigEffects.NeverOnScreen
                .Where(name => name != "disable_eventsystem_override" && !snapshot.ContainsKey(name))
                .ToList();

            check(ghosts.Count == 0, "every named setting exists",
                ghosts.Count == 0
                    ? "a renamed field would otherwise leave an entry that skips a redraw for nothing"
                    : "unknown: " + string.Join(", ", ghosts.ToArray()));

            check(snapshot.ContainsKey("settings_hotkey") && snapshot.ContainsKey("sync.notify_updates")
                  && snapshot.ContainsKey("target_language"),
                "the snapshot reads the config and its sync block",
                "a comparison that misses a block reports no change and skips the redraw");

            // The safe direction, three ways.
            check(ConfigEffects.NeedsRedraw(new[] { "a_setting_nobody_classified" }),
                "an unclassified setting redraws", "a field added later keeps today's behaviour");
            check(ConfigEffects.NeedsRedraw(new[] { "settings_hotkey", "target_language" }),
                "one visible setting among invisible ones redraws", "the whole set has to be invisible");
            check(ConfigEffects.NeedsRedraw(null), "and an answer nobody could compute redraws",
                "no list of changes means no reason to be confident");

            // What the freeze was about.
            check(!ConfigEffects.NeedsRedraw(new[] { "settings_hotkey", "open_upload_hotkey" }),
                "keys alone redraw nothing", "no text on screen shows a keyboard shortcut");
            check(!ConfigEffects.NeedsRedraw(new[] { "proxy_url", "sync.update_check_frequency", "ai_model" }),
                "nor the network and the translator's dials",
                "they shape the next request, not the text already drawn");

            // The settings a text DOES show — each one named, because skipping any of them is the
            // defect this project has already paid for.
            foreach (var visible in new[]
                     {
                         "target_language", "source_language", "strict_source_language",
                         "translation_backend", "enable_ai", "enable_translations",
                         "translate_mod_ui", "interface_font", "enable_font_replacement",
                         "enable_image_replacement", "max_font_atlas_size", "normalize_numbers",
                         "capture_keys_only", "translate_localization_fallback",
                     })
            {
                check(snapshot.ContainsKey(visible) && ConfigEffects.NeedsRedraw(new[] { visible }),
                    $"{visible} redraws", "a text on screen shows what this decides");
            }

            // The comparison itself: it is what makes an unclassified field fail safe.
            var before = ConfigEffects.Snapshot(fresh);
            check(ConfigEffects.Changed(before, ConfigEffects.Snapshot(fresh)).Count == 0,
                "an untouched config shows no change", "or every Apply would redraw whatever it did");

            var moved = new ModConfig { settings_hotkey = "Ctrl+F9" };
            var changed = ConfigEffects.Changed(before, ConfigEffects.Snapshot(moved));
            check(changed.Count == 1 && changed[0] == "settings_hotkey",
                "a changed setting is reported by name, alone",
                "a comparison that reports its neighbours too would redraw for all of them");

            var listMoved = new ModConfig();
            listMoved.sync.dismissed_notices = new List<string> { "a-notice" };
            check(ConfigEffects.Changed(before, ConfigEffects.Snapshot(listMoved))
                    .Contains("sync.dismissed_notices"),
                "a list is compared by what it holds", "two lists with the same names are one answer");
        }
    }
}
