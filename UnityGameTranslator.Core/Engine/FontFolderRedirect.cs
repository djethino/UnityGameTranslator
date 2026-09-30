using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Shows the font files of the mod's fonts/ folder to the Unity engine AS IF they were in the
    /// system's font folder — inside this game only, nothing written on the computer.
    ///
    /// 🔴 Why (user, 2026-09-28): legacy text (UI.Text) finds a font by NAME, and the engine builds
    /// its name → file list by walking a folder written into UnityPlayer.dll: "C:\Windows\Fonts"
    /// (measured on 16 engines, Unity 2018.4 → 6000.5: FindFirstFile(Ex)W / FindNextFileW /
    /// CreateFileW, all imported from KERNEL32 by UnityPlayer.dll itself; no registry, no GDI).
    /// Registering a font with Windows for this process, or loading it as a Font, is not seen —
    /// both tried and measured the same day. So the engine's own questions are answered here:
    ///
    /// - listing that folder also returns our files, after the real ones;
    /// - opening (or asking the attributes of) "<that folder>\ours.ttf" opens fonts/ours.ttf.
    ///
    /// ⚠ Only UnityPlayer.dll's import table is changed: every other module of the process, the
    /// mod and the loader included, keeps the real functions. A file with the name of a real
    /// installed font is never shown: the installed one stays.
    ///
    /// ⚠ Measured on screen the same day, on MelonLoader IL2CPP and BepInEx 6 Mono/IL2CPP: the engine
    /// lists the folder a few seconds after the mod installs this (logged once, "engine lists the font
    /// folder"), so fonts/ is in its list; it does not list it again, so a font added while the game
    /// runs is seen at the next launch. Windows, and Proton (whose Wine provides the same calls).
    ///
    /// ⚠ Native Linux and macOS do the same with other folders and other calls — read in their
    /// engines, not yet run anywhere (FontFolderRedirect.Unix.cs; analyse/polices-custom-ui-text.md).
    /// </summary>
    internal static partial class FontFolderRedirect
    {
        // ── Win32 ────────────────────────────────────────────────────────────────────────────────
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll")] private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);
        [DllImport("kernel32.dll")] private static extern void SetLastError(uint code);
        [DllImport("kernel32.dll")] private static extern uint GetLastError();

        private const uint PAGE_READWRITE = 0x04;
        private const uint ERROR_NO_MORE_FILES = 18;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr FindFirstFileExW_(IntPtr name, int level, IntPtr data, int op, IntPtr filter, int flags);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr FindFirstFileW_(IntPtr name, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int FindNextFileW_(IntPtr handle, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int FindClose_(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr CreateFileW_(IntPtr name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint GetFileAttributesW_(IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GetFileAttributesExW_(IntPtr name, int level, IntPtr info);

        // The real functions, and our replacements — kept referenced so they are never collected
        // while the engine holds their addresses.
        private static FindFirstFileExW_ _realFindFirstEx; private static readonly FindFirstFileExW_ OurFindFirstEx = FindFirstEx;
        private static FindFirstFileW_ _realFindFirst; private static readonly FindFirstFileW_ OurFindFirst = FindFirst;
        private static FindNextFileW_ _realFindNext; private static readonly FindNextFileW_ OurFindNext = FindNext;
        private static FindClose_ _realFindClose; private static readonly FindClose_ OurFindClose = Close;
        private static CreateFileW_ _realCreateFile; private static readonly CreateFileW_ OurCreateFile = CreateFile;
        private static GetFileAttributesW_ _realGetAttributes; private static readonly GetFileAttributesW_ OurGetAttributes = GetAttributes;
        private static GetFileAttributesExW_ _realGetAttributesEx; private static readonly GetFileAttributesExW_ OurGetAttributesEx = GetAttributesEx;

        private static string _systemFonts;                                   // the folder our files are shown in, no trailing separator
        private static string[] _engineFolders;                               // every folder the engine walks for fonts
        private static StringComparison _pathCase = StringComparison.OrdinalIgnoreCase;
        private static Dictionary<string, string> _ours;                      // file name → full path in fonts/
        private static readonly Dictionary<string, Reach> Reached = new Dictionary<string, Reach>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<IntPtr, Queue<string>> Pending = new Dictionary<IntPtr, Queue<string>>();
        private static readonly object Gate = new object();
        private static bool _installed, _sawListing;

        /// <summary>
        /// Whether the engine is shown a fonts/ file for this font name (the file's name without its
        /// extension, exactly) — i.e. whether legacy text can be drawn from it in this session.
        /// </summary>
        public static bool Shows(string fontName)
        {
            if (string.IsNullOrEmpty(fontName) || _ours == null || Patched.Count == 0) return false;
            foreach (var file in _ours.Keys)
                if (UnityGameTranslator.Common.AssetPacks.IsFontFileFor(file, fontName)) return true;
            return false;
        }

        /// <summary>When legacy text can be drawn from a fonts/ file.</summary>
        public enum Reach
        {
            /// <summary>In this session: the engine is shown the file, or an installed file of the same name.</summary>
            Now,
            /// <summary>From the next launch: the file came after the engine listed its font folder, which it does once.</summary>
            NextLaunch,
            /// <summary>Not in this game: no font file (an atlas font), or the engine's imports could not be changed.</summary>
            Never
        }

        /// <summary>
        /// When legacy text can be drawn from this fonts/ file (<paramref name="fontFile"/>, null for an
        /// atlas font): the engine finds a font by name only in the list it made at start (see the
        /// class summary), so a file added while the game runs waits for the next launch.
        ///
        /// ⚠ NextLaunch when nothing was shown at start is what the mod WILL do, not something measured
        /// in this session — there was nothing to redirect. If the next launch cannot redirect, its own
        /// answer is Never and says so.
        /// </summary>
        public static Reach ReachOf(string fontName, string fontFile)
        {
            if (Shows(fontName)) return Reach.Now;
            if (string.IsNullOrEmpty(fontFile) || _engineFolders == null) return Reach.Never;

            var file = Path.GetFileName(fontFile);
            if (Reached.TryGetValue(file, out var known)) return known;

            // A file named like an installed one is never shown (Install): the engine opens the installed one.
            var reach = Reach.NextLaunch;
            foreach (var folder in _engineFolders)
                if (File.Exists(Path.Combine(folder, file))) reach = Reach.Now;
            if (reach == Reach.NextLaunch && Patched.Count == 0 && (_ours == null || _ours.Count > 0))
                reach = Reach.Never;   // there were files to show and the engine could not be changed

            Reached[file] = reach;
            return reach;
        }

        internal enum Os { Windows, Linux, Mac }

        /// <summary>
        /// The system this game runs on, as far as font folders go; null when unknown. Read without
        /// Unity, so any thread may ask (the mod's System font search runs off the main one too).
        /// </summary>
        internal static Os? Current()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) return Os.Windows;   // Proton included
            if (Environment.OSVersion.Platform == PlatformID.MacOSX || File.Exists("/System/Library/CoreServices/SystemVersion.plist")) return Os.Mac;
            if (Environment.OSVersion.Platform == PlatformID.Unix) return Os.Linux;
            return null;
        }

        /// <summary>
        /// Changes the engine's own imports so it sees fonts/ as installed fonts. Once; says what it did;
        /// changes nothing when there is nothing to show or anything looks unexpected.
        /// </summary>
        public static void Install(string fontsFolder)
        {
            if (_installed) return;
            _installed = true;

            try
            {
                var os = Current();
                if (os == null) return;

                // The folders the engine walks, read in UnityPlayer: Windows' own font folder; on Linux
                // /usr/share/fonts; on macOS three, of which ours join the last.
                string[] engineFolders;
                switch (os.Value)
                {
                    case Os.Windows:
                        engineFolders = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts") };
                        break;
                    case Os.Linux:
                        engineFolders = new[] { "/usr/share/fonts" };
                        _pathCase = StringComparison.Ordinal;
                        break;
                    default:
                        engineFolders = new[] { "/System/Library/Fonts/Supplemental", "/System/Library/Fonts", "/Library/Fonts" };
                        break;
                }
                _systemFonts = engineFolders[engineFolders.Length - 1].TrimEnd('\\', '/');
                _engineFolders = engineFolders;

                _ours = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(fontsFolder))
                {
                    foreach (var file in Directory.GetFiles(fontsFolder))
                    {
                        var name = Path.GetFileName(file);
                        if (!UnityGameTranslator.Common.AssetPacks.IsFontFile(name)) continue;
                        bool installed = false;
                        foreach (var folder in engineFolders) installed |= File.Exists(Path.Combine(folder, name));
                        if (installed) continue;   // the installed one stays
                        _ours[name] = file;
                    }
                }

                // Nothing to show: the engine is left exactly as it is.
                if (_ours.Count == 0) return;

                int patched = os == Os.Windows ? InstallWindows()
                            : os == Os.Linux ? InstallLinux()
                            : InstallMac();
                if (patched <= 0) return;   // each says why

                TranslatorCore.LogInfo($"[FontFolder] {patched} engine import(s) redirected ({os}); {_ours.Count} font file(s) shown in {_systemFonts}: {string.Join(", ", _ours.Keys)}");
            }
            catch (Exception ex)
            {
                // The boundary with the process's own code: an unexpected layout is said, never hidden.
                TranslatorCore.LogWarning($"[FontFolder] Could not redirect the engine's font folder: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static int InstallWindows()
        {
                var module = GetModuleHandleW("UnityPlayer.dll");
                if (module == IntPtr.Zero)
                {
                    TranslatorCore.LogInfo("[FontFolder] No UnityPlayer.dll in this process — nothing to redirect");
                    return 0;
                }

                return Patch(module, "kernel32.dll", new Dictionary<string, Func<IntPtr, IntPtr>>
                {
                    ["FindFirstFileExW"] = real => { _realFindFirstEx = Marshal.GetDelegateForFunctionPointer<FindFirstFileExW_>(real); return Marshal.GetFunctionPointerForDelegate(OurFindFirstEx); },
                    ["FindFirstFileW"] = real => { _realFindFirst = Marshal.GetDelegateForFunctionPointer<FindFirstFileW_>(real); return Marshal.GetFunctionPointerForDelegate(OurFindFirst); },
                    ["FindNextFileW"] = real => { _realFindNext = Marshal.GetDelegateForFunctionPointer<FindNextFileW_>(real); return Marshal.GetFunctionPointerForDelegate(OurFindNext); },
                    ["FindClose"] = real => { _realFindClose = Marshal.GetDelegateForFunctionPointer<FindClose_>(real); return Marshal.GetFunctionPointerForDelegate(OurFindClose); },
                    ["CreateFileW"] = real => { _realCreateFile = Marshal.GetDelegateForFunctionPointer<CreateFileW_>(real); return Marshal.GetFunctionPointerForDelegate(OurCreateFile); },
                    ["GetFileAttributesW"] = real => { _realGetAttributes = Marshal.GetDelegateForFunctionPointer<GetFileAttributesW_>(real); return Marshal.GetFunctionPointerForDelegate(OurGetAttributes); },
                    ["GetFileAttributesExW"] = real => { _realGetAttributesEx = Marshal.GetDelegateForFunctionPointer<GetFileAttributesExW_>(real); return Marshal.GetFunctionPointerForDelegate(OurGetAttributesEx); },
                });
        }

        /// <summary>Replaces, in a module's import table, the named functions of one DLL. Returns how many.</summary>
        private static int Patch(IntPtr module, string dll, Dictionary<string, Func<IntPtr, IntPtr>> replacements)
        {
            int pe = Marshal.ReadInt32(module, 0x3C);
            bool is64 = Marshal.ReadInt16(module, pe + 24) == 0x20B;
            int dataDirectory = pe + 24 + (is64 ? 112 : 96);
            int importRva = Marshal.ReadInt32(module, dataDirectory + 8);
            int slot = IntPtr.Size;
            int count = 0;

            for (int descriptor = importRva; ; descriptor += 20)
            {
                int nameRva = Marshal.ReadInt32(module, descriptor + 12);
                if (nameRva == 0) break;
                if (!string.Equals(Marshal.PtrToStringAnsi(module + nameRva), dll, StringComparison.OrdinalIgnoreCase)) continue;

                int lookup = Marshal.ReadInt32(module, descriptor);        // OriginalFirstThunk: names
                int address = Marshal.ReadInt32(module, descriptor + 16);  // FirstThunk: the table the code calls through
                if (lookup == 0) continue;

                for (int i = 0; ; i++)
                {
                    long entry = is64 ? Marshal.ReadInt64(module, lookup + i * slot) : (uint)Marshal.ReadInt32(module, lookup + i * slot);
                    if (entry == 0) break;
                    bool byOrdinal = is64 ? entry < 0 : (entry & 0x80000000) != 0;
                    if (byOrdinal) continue;

                    string name = Marshal.PtrToStringAnsi(module + (int)(entry & 0x7FFFFFFF) + 2);
                    if (name == null || !replacements.TryGetValue(name, out var replace)) continue;

                    IntPtr cell = module + address + i * slot;
                    IntPtr real = Marshal.ReadIntPtr(cell);
                    Write(cell, replace(real));
                    Patched.Add(new KeyValuePair<IntPtr, IntPtr>(cell, real));
                    count++;
                }
            }

            return count;
        }

        /// <summary>The cells changed, with what they held — what Uninstall puts back.</summary>
        private static readonly List<KeyValuePair<IntPtr, IntPtr>> Patched = new List<KeyValuePair<IntPtr, IntPtr>>();

        private static void Write(IntPtr cell, IntPtr value)
        {
            if (Current() != Os.Windows) { WriteUnix(cell, value); return; }

            var size = (UIntPtr)(uint)IntPtr.Size;
            VirtualProtect(cell, size, PAGE_READWRITE, out uint old);
            Marshal.WriteIntPtr(cell, value);
            VirtualProtect(cell, size, old, out _);
        }

        /// <summary>
        /// Gives the engine its real functions back — at shutdown, before this code can go away while
        /// the engine still opens files.
        /// </summary>
        public static void Uninstall()
        {
            foreach (var cell in Patched) Write(cell.Key, cell.Value);
            if (Patched.Count > 0) TranslatorCore.LogInfo($"[FontFolder] {Patched.Count} engine import(s) given back");
            Patched.Clear();
        }

        // ── Answers ──────────────────────────────────────────────────────────────────────────────

        /// <summary>The fonts/ file standing for a path in the folder ours are shown in, or null.</summary>
        private static string OursForPath(string path)
        {
            if (string.IsNullOrEmpty(path) || _ours == null || _ours.Count == 0) return null;
            var dir = Path.GetDirectoryName(path);
            if (dir == null || !string.Equals(dir.TrimEnd('\\', '/'), _systemFonts, _pathCase)) return null;
            return _ours.TryGetValue(Path.GetFileName(path), out var file) ? file : null;
        }

        /// <summary>Whether a folder (or a Windows listing pattern inside it) is the one ours are shown in.</summary>
        private static bool IsOurFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || _ours == null) return false;
            return string.Equals(folder.TrimEnd('\\', '/'), _systemFonts, _pathCase);
        }

        private static void SawListing(string what)
        {
            if (_sawListing) return;
            _sawListing = true;
            TranslatorCore.LogInfo($"[FontFolder] engine lists the font folder ({what}) — our {_ours.Count} file(s) added");
        }

        // Windows: the engine's strings are UTF-16.
        private static string OursFor(IntPtr namePtr) =>
            namePtr == IntPtr.Zero ? null : OursForPath(Marshal.PtrToStringUni(namePtr));

        private static bool IsFontFolderListing(IntPtr namePtr)
        {
            if (namePtr == IntPtr.Zero) return false;
            var pattern = Marshal.PtrToStringUni(namePtr);
            return pattern != null && IsOurFolder(Path.GetDirectoryName(pattern));
        }

        private static void Track(IntPtr handle, IntPtr pattern)
        {
            if (handle == InvalidHandle || !IsFontFolderListing(pattern)) return;
            lock (Gate) Pending[handle] = new Queue<string>(_ours.Keys);
            SawListing(Marshal.PtrToStringUni(pattern));
        }

        private static IntPtr FindFirstEx(IntPtr name, int level, IntPtr data, int op, IntPtr filter, int flags)
        {
            var handle = _realFindFirstEx(name, level, data, op, filter, flags);
            uint error = GetLastError();
            try { Track(handle, name); } catch (Exception ex) { Faults.Say("FontFolderRedirect.FindFirstEx", ex); }
            SetLastError(error);
            return handle;
        }

        private static IntPtr FindFirst(IntPtr name, IntPtr data)
        {
            var handle = _realFindFirst(name, data);
            uint error = GetLastError();
            try { Track(handle, name); } catch (Exception ex) { Faults.Say("FontFolderRedirect.FindFirst", ex); }
            SetLastError(error);
            return handle;
        }

        private static int FindNext(IntPtr handle, IntPtr data)
        {
            int found = _realFindNext(handle, data);
            uint error = GetLastError();
            if (found != 0 || error != ERROR_NO_MORE_FILES) { SetLastError(error); return found; }

            try
            {
                string next = null;
                lock (Gate)
                {
                    if (Pending.TryGetValue(handle, out var queue) && queue.Count > 0) next = queue.Dequeue();
                }

                if (next != null)
                {
                    Describe(data, next, new FileInfo(_ours[next]).Length);
                    SetLastError(0);
                    return 1;
                }
            }
            catch (Exception ex) { Faults.Say("FontFolderRedirect.FindNext", ex); }

            SetLastError(error);
            return found;
        }

        private static int Close(IntPtr handle)
        {
            lock (Gate) Pending.Remove(handle);
            return _realFindClose(handle);
        }

        /// <summary>WIN32_FIND_DATAW for one of our files: attributes, size, name — times left at zero.</summary>
        private static void Describe(IntPtr data, string name, long length)
        {
            for (int i = 0; i < 592; i += 4) Marshal.WriteInt32(data, i, 0);   // sizeof(WIN32_FIND_DATAW)
            Marshal.WriteInt32(data, 0, (int)FILE_ATTRIBUTE_NORMAL);
            Marshal.WriteInt32(data, 28, (int)(length >> 32));
            Marshal.WriteInt32(data, 32, (int)(length & 0xFFFFFFFF));
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length && i < 259; i++) Marshal.WriteInt16(data, 44 + i * 2, chars[i]);
        }

        /// <summary>A file of ours the engine could not open, said once each — the one failure worth a line.</summary>
        private static readonly HashSet<string> Refused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static IntPtr CreateFile(IntPtr name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template)
        {
            string ours = null;
            try { ours = OursFor(name); } catch (Exception ex) { Faults.Say("FontFolderRedirect.CreateFile", ex); }
            if (ours == null) return _realCreateFile(name, access, share, security, disposition, flags, template);

            var redirected = Marshal.StringToHGlobalUni(ours);
            try
            {
                var handle = _realCreateFile(redirected, access, share, security, disposition, flags, template);
                if (handle != InvalidHandle) return handle;

                uint error = GetLastError();
                bool first;
                lock (Gate) first = Refused.Add(ours);
                if (first) TranslatorCore.LogWarning($"[FontFolder] The engine could not open {Path.GetFileName(ours)} (Windows error {error})");
                SetLastError(error);
                return handle;
            }
            finally { Marshal.FreeHGlobal(redirected); }
        }

        private static uint GetAttributes(IntPtr name)
        {
            string ours = null;
            try { ours = OursFor(name); } catch (Exception ex) { Faults.Say("FontFolderRedirect.GetAttributes", ex); }
            if (ours == null) return _realGetAttributes(name);

            var redirected = Marshal.StringToHGlobalUni(ours);
            try { return _realGetAttributes(redirected); }
            finally { Marshal.FreeHGlobal(redirected); }
        }

        private static int GetAttributesEx(IntPtr name, int level, IntPtr info)
        {
            string ours = null;
            try { ours = OursFor(name); } catch (Exception ex) { Faults.Say("FontFolderRedirect.GetAttributesEx", ex); }
            if (ours == null) return _realGetAttributesEx(name, level, info);

            var redirected = Marshal.StringToHGlobalUni(ours);
            try { return _realGetAttributesEx(redirected, level, info); }
            finally { Marshal.FreeHGlobal(redirected); }
        }
    }
}
