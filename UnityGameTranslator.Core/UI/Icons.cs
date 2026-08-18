using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseLib.Runtime;

namespace UnityGameTranslator.Core.UI
{
    /// <summary>
    /// The mod's small marks, built as pixels at run time.
    ///
    /// ⚠ **Why not an image file.** A PNG cannot be decoded here: `UnityEngine.ImageConversionModule`
    /// is not among the assemblies this project compiles against, UniverseLib exposes only
    /// `EncodeToPNG` and no decoder, and on IL2CPP the byte array would need converting before the
    /// call anyway. Every route through a file format is closed, so the pixels are computed instead.
    ///
    /// ⚠ **Why that is not a workaround.** These three marks are discs, boxes and capsules — the very
    /// shapes the manager draws as vector paths, from the same numbers in the same 16x16 box. Written
    /// this way the two products cannot drift apart, and nothing has to be embedded, merged by
    /// ILRepack or shipped beside the DLL.
    ///
    /// ⚠ **What this does NOT solve: flags.** A flag is picture data, not geometry, and it will have
    /// to arrive as an embedded raster. That is why the pixels and the drawing are separate below:
    /// <see cref="FromCoverage"/> takes coverage bytes from wherever they come from, and only
    /// <see cref="Draw"/> knows they were computed. A flag atlas plugs into the first without
    /// touching the second.
    ///
    /// 🔴 Every Unity call here goes through <see cref="TextureHelper"/>. `SetPixels32` takes a
    /// managed array, which on IL2CPP is not the array the engine expects — the same class of trap
    /// as AddListener, and UniverseLib has already solved it. Never call the engine directly from
    /// this file.
    /// </summary>
    public static class Icons
    {
        /// <summary>The box every mark is drawn in — the manager's, so the shapes match.</summary>
        private const double Box = 16;

        /// <summary>Pixels per box unit. Two gives a 32x32 mark, ample for a 14pt row.</summary>
        private const int Scale = 2;

        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        /// <summary>
        /// The mark for an identity handed out by <see cref="Common.EditScope.Mark"/>.
        ///
        /// ⚠ Returns null rather than a placeholder for an unknown name. A mark nobody recognises
        /// on a control that means "where this writes" would be worse than none: the caller can see
        /// a missing icon, it cannot see a wrong one.
        /// </summary>
        public static Sprite Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            Sprite cached;
            if (_cache.TryGetValue(id, out cached)) return cached;

            var shape = Draw(id);
            if (shape == null) return null;

            var sprite = TryBuild(() => FromCoverage(shape, Box * Scale, Box * Scale));
            _cache[id] = sprite;   // null cached on purpose: a runtime that cannot make one never will
            return sprite;
        }

        /// <summary>Set once a texture could not be made, so the reason is stated a single time.</summary>
        private static bool _texturesUnavailable;

        /// <summary>Which of the two ways worked, so the fallback is not re-tried on every icon.</summary>
        private static bool _cloneInsteadOfConstruct;

        /// <summary>
        /// A blank texture of this size, however this runtime is willing to give one.
        ///
        /// 🔴 **IL2CPP can strip every Texture2D constructor.** They are kept only if the game
        /// itself calls one; Il2CppInterop still declares them from the metadata, so the call
        /// compiles and then finds nothing behind it. Seen on Unity 2022.3.62f2 and 6000.0.77f1:
        /// the six-argument form is gone, and so is the four-argument one.
        ///
        /// So the second way does not CONSTRUCT a texture, it COPIES one Unity already made.
        /// <c>Texture2D.whiteTexture</c> is a built-in the engine always has, <c>Instantiate</c> is
        /// in every game there is, and resizing the copy is what makes it ours. Nothing here is an
        /// ICall: the engine's internal creation entry point exists, but its signature moves
        /// between Unity versions and calling it with the wrong one corrupts memory rather than
        /// throwing — a crash with no message, which is exactly what must not be traded for a
        /// missing icon.
        ///
        /// ⚠ Every step is a direct call, deliberately. A stripped member then raises
        /// <see cref="MissingMethodException"/>, which the caller can survive; reflection over the
        /// same member would hand back a declaration with no pointer and take the game down.
        ///
        /// ⚠ Which way worked is remembered: it follows from how the game was built and cannot
        /// change while it runs.
        /// </summary>
        private static Texture2D NewTexture(int width, int height)
        {
            if (!_cloneInsteadOfConstruct)
            {
                try
                {
                    return TextureHelper.NewTexture2D(width, height, TextureFormat.RGBA32, false);
                }
                catch (Exception ex)
                {
                    _cloneInsteadOfConstruct = true;
                    TranslatorCore.LogInfo(
                        $"[Icons] No Texture2D constructor in this build ({ex.GetType().Name}) — "
                        + "copying a built-in texture instead.");
                }
            }

            ReportWhatExists();

            // 🔴 **The overload matters, and a name-only check cannot see it.** The report says
            // Reinitialize, Instantiate and whiteTexture are all real on the games that crashed —
            // so the member was never the problem: the SIGNATURE was. Il2CppInterop names its
            // pointer fields after the full signature, and a check on the name alone matches any
            // overload, including ones the build dropped.
            //
            // Two traps, both avoided below:
            //   • `Instantiate(seed)` binds to the GENERIC Instantiate<T>. A generic has to be
            //     instantiated per type on IL2CPP and Instantiate<Texture2D> may exist nowhere.
            //     The cast forces the plain Object overload.
            //   • `Reinitialize(w, h, format, mipChain)` is the four-argument form; only the
            //     two-argument one is asked for here, and it is verified by signature.
            if (!HasNativeSignature(typeof(Texture2D), "Reinitialize", "Int32_Int32")
                || !HasNativeSignature(typeof(UnityEngine.Object), "Instantiate", "Object"))
            {
                throw new InvalidOperationException(
                    "no Texture2D constructor, and no usable overload to copy one");
            }

            var seed = Texture2D.whiteTexture;
            if (seed == null) throw new InvalidOperationException("No built-in texture to copy.");

            // The cast is what picks the non-generic overload — see above.
            var made = UnityEngine.Object.Instantiate((UnityEngine.Object)seed);

            var copy = made as Texture2D
                       ?? TypeHelper.Il2CppCast(made, typeof(Texture2D)) as Texture2D;

            if (copy == null) throw new InvalidOperationException("Instantiate returned no texture.");

            copy.Reinitialize(width, height);
            return copy;
        }

        /// <summary>Names every real overload of one method, so a signature need not be guessed.</summary>
        private static void ReportSignatures(Type type, string method)
        {
            if (TranslatorCore.Adapter?.IsIL2CPP != true) return;

            try
            {
                var found = new List<string>();

                foreach (var field in type.GetFields(System.Reflection.BindingFlags.Static
                                                     | System.Reflection.BindingFlags.NonPublic
                                                     | System.Reflection.BindingFlags.Public))
                {
                    if (!field.Name.StartsWith("NativeMethodInfoPtr_" + method + "_",
                                               StringComparison.Ordinal)) continue;

                    bool live = field.GetValue(null) is IntPtr p && p != IntPtr.Zero;
                    found.Add((live ? "" : "(dead) ") + field.Name);
                }

                TranslatorCore.LogWarning($"[Icons] {type.Name}.{method} overloads: "
                                          + (found.Count == 0 ? "none" : string.Join(" | ", found)));
            }
            catch { }
        }

        /// <summary>
        /// Whether a specific OVERLOAD is real, not merely a method of that name.
        ///
        /// ⚠ Il2CppInterop names each pointer field after the whole signature —
        /// <c>NativeMethodInfoPtr_Reinitialize_Public_Boolean_Int32_Int32_</c> — so matching the
        /// name alone accepts an overload the build dropped, and calling that one is a null jump.
        /// <paramref name="signature"/> is the parameter part to look for.
        /// </summary>
        private static bool HasNativeSignature(Type type, string method, string signature)
        {
            if (TranslatorCore.Adapter?.IsIL2CPP != true) return true;

            try
            {
                foreach (var field in type.GetFields(System.Reflection.BindingFlags.Static
                                                     | System.Reflection.BindingFlags.NonPublic
                                                     | System.Reflection.BindingFlags.Public))
                {
                    if (!field.Name.StartsWith("NativeMethodInfoPtr_" + method + "_",
                                               StringComparison.Ordinal)) continue;

                    if (field.Name.IndexOf(signature, StringComparison.Ordinal) < 0) continue;

                    if (field.GetValue(null) is IntPtr pointer && pointer != IntPtr.Zero)
                        return true;
                }
            }
            catch { }

            return false;
        }

        private static bool _reported;

        /// <summary>
        /// Lists which texture-related members are real in this build, once.
        ///
        /// ⚠ Reads only. Every member here is one a texture path might need, and any of them may
        /// have been stripped: the point is to learn which, without calling a single one.
        /// </summary>
        private static void ReportWhatExists()
        {
            if (_reported) return;
            _reported = true;

            try
            {
                var lines = new List<string>();

                foreach (var member in new[]
                         {
                             "get_whiteTexture", "get_blackTexture", "Reinitialize", "Resize",
                             "SetPixels32", "SetPixels", "Apply", "GetRawTextureData",
                             "LoadRawTextureData",
                         })
                {
                    lines.Add($"Texture2D.{member}={NativeMethodExists(typeof(Texture2D), member)}");
                }

                lines.Add($"Object.Instantiate={NativeMethodExists(typeof(UnityEngine.Object), "Instantiate")}");
                lines.Add($"Sprite.Create={NativeMethodExists(typeof(Sprite), "Create")}");
                lines.Add($"Texture2D.ctor={NativeMethodExists(typeof(Texture2D), ".ctor")}");

                TranslatorCore.LogWarning("[Icons] What this build really has — " + string.Join(", ", lines));

                // ⚠ The signatures too, not just the names. The names alone said "Reinitialize
                // exists" about a build whose four-argument form did not — which is what crashed.
                // Printed so a failure needs no further round trip to diagnose.
                ReportSignatures(typeof(Texture2D), "Reinitialize");
                ReportSignatures(typeof(UnityEngine.Object), "Instantiate");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Icons] Could not read what this build has: {ex.Message}");
            }
        }

        /// <summary>
        /// Whether a method exists as native code, not merely as a declaration.
        ///
        /// ⚠ On Mono there is nothing to check and everything declared is real. On IL2CPP,
        /// Il2CppInterop emits a static <c>NativeMethodInfoPtr_&lt;name&gt;…</c> field per generated
        /// method: absent or zero means the game's build stripped it. Reading that field is safe;
        /// calling the method it describes is not, which is the whole reason to look first.
        ///
        /// ⚠ Deliberately a field READ and never an Invoke — reflection that ends in a call has the
        /// same fate as the direct call.
        /// </summary>
        private static bool NativeMethodExists(Type type, string method)
        {
            if (TranslatorCore.Adapter?.IsIL2CPP != true) return true;

            try
            {
                var fields = type.GetFields(System.Reflection.BindingFlags.Static
                                            | System.Reflection.BindingFlags.NonPublic
                                            | System.Reflection.BindingFlags.Public);

                foreach (var field in fields)
                {
                    if (!field.Name.StartsWith("NativeMethodInfoPtr_" + method, StringComparison.Ordinal))
                        continue;

                    if (field.GetValue(null) is IntPtr pointer && pointer != IntPtr.Zero)
                        return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Builds a sprite, or gives up and says so.
        ///
        /// 🔴 **An icon is optional; the window it decorates is not.** Making a Texture2D can fail
        /// outright on IL2CPP — a game whose build stripped the Texture2D constructors leaves
        /// Il2CppInterop declaring one with nothing behind it. That throw used to escape into
        /// <c>MainPanel</c>'s construction and abort <c>CreatePanels()</c>: panels built before it
        /// stayed on screen, panels after it never existed, the tick loop never started. What the
        /// user saw was one oversized window; what it was, was the mod dead at startup.
        ///
        /// ⚠ This is NOT the "wrap each panel in a try/catch" that CLAUDE.md rules out — that would
        /// hide a missing panel. This is one optional resource declining to be built, at the only
        /// place that can tell the difference, and saying which runtime refused it.
        ///
        /// ⚠ The failure is remembered rather than retried: it depends on how the game was built,
        /// which does not change while it runs, and every mark and flag would otherwise throw again.
        /// </summary>
        private static Sprite TryBuild(Func<Sprite> build)
        {
            if (_texturesUnavailable) return null;

            try
            {
                return build();
            }
            catch (Exception ex)
            {
                _texturesUnavailable = true;
                TranslatorCore.LogWarning(
                    $"[Icons] This game cannot create textures at runtime ({ex.GetType().Name}: "
                    + $"{ex.Message}). Marks and flags will not be drawn; everything else works.");
                return null;
            }
        }

        /// <summary>
        /// A language's flag, drawn from the pixels the socle holds.
        ///
        /// 🔴 **Not the same path as a mark, and it cannot be.** <see cref="FromCoverage"/> makes a
        /// WHITE sprite whose alpha carries the shape, exactly so that one texture serves every
        /// colour a mark is ever shown in. A flag carries its own colours, so it needs a texture of
        /// its own — and ⚠ **the Image that shows it must be left white**, or the button's tint
        /// repaints the flag.
        ///
        /// ⚠ Point filtering, deliberately: these are drawn as pixels and a blurred sixteen-pixel
        /// flag is worse than a crisp one, since half of them are only told apart by an edge.
        ///
        /// Returns null for a language whose flag has not been drawn yet — ordinary, and the caller
        /// shows the tag alone.
        /// </summary>
        public static Sprite Flag(string flagId)
        {
            if (string.IsNullOrEmpty(flagId)) return null;

            var key = "flag:" + flagId;

            Sprite cached;
            if (_cache.TryGetValue(key, out cached)) return cached;

            var pixels = Common.Flags.Pixels(flagId);
            if (pixels == null) return null;

            int w = Common.Flags.Width, h = Common.Flags.Height;
            var colours = new Color32[w * h];

            foreach (var pixel in pixels)
            {
                // ⚠ **Unity's textures start at the BOTTOM, the catalogue describes flags from the
                // top.** Forgetting this ships every flag upside down — which for a good half of
                // them (Poland and Indonesia, Netherlands and Croatia) is another country's flag,
                // not an obvious glitch.
                int at = (h - 1 - pixel.Y) * w + pixel.X;

                colours[at] = pixel.Transparent
                    ? new Color32(0, 0, 0, 0)
                    : new Color32((byte)((pixel.Rgb >> 16) & 0xFF),
                                  (byte)((pixel.Rgb >> 8) & 0xFF),
                                  (byte)(pixel.Rgb & 0xFF), 255);
            }

            // Same guard as a mark: a runtime that cannot build a texture must cost a flag, not the
            // window the flag sits in. See TryBuild.
            var sprite = TryBuild(() =>
            {
                var texture = NewTexture(w, h);
                texture.filterMode = FilterMode.Point;
                texture.wrapMode = TextureWrapMode.Clamp;

                TextureHelper.SetPixels32Safe(texture, colours);
                texture.Apply(false, false);

                // The cache outlives every panel that asked for it.
                UnityEngine.Object.DontDestroyOnLoad(texture);

                return TextureHelper.CreateSprite(texture);
            });

            _cache[key] = sprite;
            return sprite;
        }

        /// <summary>
        /// Turns coverage — one byte of "how much of this pixel is inside the shape" — into a white
        /// sprite whose transparency carries the drawing.
        ///
        /// White on purpose: uGUI multiplies a sprite by its Image's colour, so one texture serves
        /// every shade the mark is ever shown in. Baking the colour in would mean one texture per
        /// state, and a mark that could not follow a theme.
        /// </summary>
        private static Sprite FromCoverage(byte[] coverage, double width, double height)
        {
            int w = (int)width, h = (int)height;

            var pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(255, 255, 255, coverage[i]);

            var texture = NewTexture(w, h);
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;

            TextureHelper.SetPixels32Safe(texture, pixels);
            texture.Apply(false, false);

            // Not destroyed with the panel that first asked for it: the cache outlives every panel,
            // and a texture collected while another still points at it renders as a pink square.
            UnityEngine.Object.DontDestroyOnLoad(texture);

            return TextureHelper.CreateSprite(texture);
        }

        /// <summary>
        /// The three marks, in the manager's coordinates.
        ///
        /// ⚠ These numbers are the ones in `Glyphs.cs`. A mark changed on one side and not the other
        /// is a control that stops being recognisable between two products — which is the only
        /// reason this control exists at all.
        /// </summary>
        private static byte[] Draw(string id)
        {
            int w = (int)(Box * Scale), h = (int)(Box * Scale);
            var coverage = new byte[w * h];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // Pixel centres, back in box units. Unity's textures start at the bottom, the
                    // drawings are described from the top — hence the flip, and forgetting it turns
                    // the screen's stand into an aerial.
                    double px = (x + 0.5) / Scale;
                    double py = Box - (y + 0.5) / Scale;

                    double distance;
                    switch (id)
                    {
                        case "cloud": distance = Cloud(px, py); break;
                        case "display": distance = Display(px, py); break;
                        case "link": distance = Link(px, py); break;
                        default: return null;
                    }

                    // One pixel of softening across the edge. Without it a 32-pixel mark shown at
                    // 14 comes out ragged, and three ragged marks in a row read as a rendering
                    // fault rather than as a control.
                    double alpha = 0.5 - distance * Scale;
                    if (alpha < 0) alpha = 0;
                    else if (alpha > 1) alpha = 1;

                    coverage[y * w + x] = (byte)(alpha * 255 + 0.5);
                }
            }

            return coverage;
        }

        // ── The three marks ───────────────────────────────────────────────────────────────────

        /// <summary>Three puffs resting on a bar, their bottoms aligned so the base reads flat.</summary>
        private static double Cloud(double x, double y)
        {
            return Min(Disc(x, y, 5.2, 9.6, 3),
                   Min(Disc(x, y, 9.6, 7.6, 3.8),
                   Min(Disc(x, y, 11.9, 10, 2.6),
                       Box_(x, y, 5.2, 9.6, 11.9, 12.6))));
        }

        /// <summary>A bezel with its screen punched out, on a neck and a foot.</summary>
        private static double Display(double x, double y)
        {
            var bezel = Max(Box_(x, y, 1.6, 2.8, 14.4, 11.2),
                           -Box_(x, y, 3.1, 4.3, 12.9, 9.7));

            return Min(bezel,
                   Min(Box_(x, y, 6.6, 11.2, 9.4, 13),
                       Box_(x, y, 4.6, 13, 11.4, 14.4)));
        }

        /// <summary>Two links of a chain, each a capsule with a capsule taken out of it.</summary>
        private static double Link(double x, double y)
        {
            var left = Max(Capsule(x, y, 3.9, 8, 6.6, 8, 2.6),
                          -Capsule(x, y, 3.9, 8, 6.6, 8, 1.1));

            var right = Max(Capsule(x, y, 9.4, 8, 12.1, 8, 2.6),
                           -Capsule(x, y, 9.4, 8, 12.1, 8, 1.1));

            return Min(left, right);
        }

        // ── Distances ─────────────────────────────────────────────────────────────────────────
        //
        // Signed: negative inside, zero on the edge. Union is the nearer of two, subtraction the
        // farther of one and the other reversed — which is why a hole is written as a negative.

        private static double Min(double a, double b) { return a < b ? a : b; }
        private static double Max(double a, double b) { return a > b ? a : b; }

        private static double Disc(double x, double y, double cx, double cy, double r)
        {
            double dx = x - cx, dy = y - cy;
            return Math.Sqrt(dx * dx + dy * dy) - r;
        }

        /// <summary>Named with a trailing underscore: <c>Box</c> is already the drawing's size.</summary>
        private static double Box_(double x, double y, double x0, double y0, double x1, double y1)
        {
            // Distance to a rectangle, from its centre outward, so a point inside comes back
            // negative rather than zero — the edge has to stay soft on the inside too.
            double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            double hx = (x1 - x0) / 2, hy = (y1 - y0) / 2;

            double dx = Math.Abs(x - cx) - hx;
            double dy = Math.Abs(y - cy) - hy;

            double outside = Math.Sqrt(Math.Max(dx, 0) * Math.Max(dx, 0)
                                     + Math.Max(dy, 0) * Math.Max(dy, 0));

            return outside + Math.Min(Math.Max(dx, dy), 0);
        }

        private static double Capsule(double x, double y, double ax, double ay,
                                      double bx, double by, double r)
        {
            double pax = x - ax, pay = y - ay;
            double bax = bx - ax, bay = by - ay;

            double length = bax * bax + bay * bay;
            double t = length <= 0 ? 0 : (pax * bax + pay * bay) / length;
            if (t < 0) t = 0;
            else if (t > 1) t = 1;

            double dx = pax - bax * t, dy = pay - bay * t;
            return Math.Sqrt(dx * dx + dy * dy) - r;
        }
    }
}
