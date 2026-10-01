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
    /// folder"), so fonts/ is in its list; it does not list it again — a font added while the game
    /// runs reaches it under a name of the pool listed at start (FontPool, DerivedFonts.LateCopy).
    /// Windows, and Proton (whose Wine provides the same calls).
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
        [DllImport("kernel32.dll")] private static extern bool SetFilePointerEx(IntPtr file, long distance, out long newPointer, uint method);
        [DllImport("kernel32.dll")] private static extern bool GetFileInformationByHandle(IntPtr file, IntPtr info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

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
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int ReadFile_(IntPtr file, IntPtr buffer, uint toRead, IntPtr read, IntPtr overlapped);

        // The real functions, and our replacements — kept referenced so they are never collected
        // while the engine holds their addresses.
        private static FindFirstFileExW_ _realFindFirstEx; private static readonly FindFirstFileExW_ OurFindFirstEx = FindFirstEx;
        private static FindFirstFileW_ _realFindFirst; private static readonly FindFirstFileW_ OurFindFirst = FindFirst;
        private static FindNextFileW_ _realFindNext; private static readonly FindNextFileW_ OurFindNext = FindNext;
        private static FindClose_ _realFindClose; private static readonly FindClose_ OurFindClose = Close;
        private static CreateFileW_ _realCreateFile; private static readonly CreateFileW_ OurCreateFile = CreateFile;
        private static GetFileAttributesW_ _realGetAttributes; private static readonly GetFileAttributesW_ OurGetAttributes = GetAttributes;
        private static GetFileAttributesExW_ _realGetAttributesEx; private static readonly GetFileAttributesExW_ OurGetAttributesEx = GetAttributesEx;
        private static ReadFile_ _realReadFile; private static readonly ReadFile_ OurReadFile = ReadFile;

        private static string _systemFonts;                                   // the folder our files are shown in, no trailing separator
        private static string[] _engineFolders;                               // every folder the engine walks for fonts
        private static StringComparison _pathCase = StringComparison.OrdinalIgnoreCase;
        private static Dictionary<string, string> _ours;                      // file name → full path in fonts/
        private static IVirtualFonts _virtual;                                // names shown that are no file of their own (FontPool)
        private static readonly Dictionary<string, Reach> Reached = new Dictionary<string, Reach>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<IntPtr, Queue<string>> Pending = new Dictionary<IntPtr, Queue<string>>();
        private static readonly object Gate = new object();
        private static bool _installed, _sawListing;
        private static readonly System.Diagnostics.Stopwatch ListingClock = new System.Diagnostics.Stopwatch();

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
        /// class summary), so a file added while the game runs is not in it — FontManager.LegacyReach
        /// adds what the pool listed at start can still show.
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
        /// Whether a path the engine reports as an installed font is one the mod shows it — a fonts/
        /// file, a derived copy, a pool name: not a System font.
        /// </summary>
        public static bool IsShownByUs(string path) =>
            !string.IsNullOrEmpty(path) && _ours != null && Patched.Count > 0
            && OursForPath(path) != null;

        /// <summary>Every name the engine is shown in its font folder: fonts/ and the copies, then the pool's.</summary>
        private static IEnumerable<string> ShownNames()
        {
            foreach (var name in _ours.Keys) yield return name;
            if (_virtual != null) foreach (var name in _virtual.Names()) yield return name;
        }

        /// <summary>Whether the engine is shown this exact file name (a derived copy, a pool name — DerivedFonts).</summary>
        public static bool ShowsFile(string fileName) =>
            !string.IsNullOrEmpty(fileName) && _ours != null && Patched.Count > 0
            && (_ours.ContainsKey(fileName) || (_virtual != null && _virtual.Has(fileName)));

        /// <summary>
        /// Changes the engine's own imports so it sees fonts/ as installed fonts. Once; says what it did;
        /// changes nothing when there is nothing to show or anything looks unexpected.
        /// <paramref name="alsoShown"/>: files outside fonts/ shown the same way — the derived copies
        /// (DerivedFonts), written before this is called since the engine lists its folder once.
        /// <paramref name="virtualFonts"/>: names shown with no file of their own (FontPool).
        /// </summary>
        public static void Install(string fontsFolder, IList<string> alsoShown = null, IVirtualFonts virtualFonts = null)
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
                if (alsoShown != null)
                    foreach (var file in alsoShown)
                        if (File.Exists(file)) _ours[Path.GetFileName(file)] = file;

                // Nothing to show: the engine is left exactly as it is.
                if (_ours.Count == 0 && virtualFonts == null) return;
                if (virtualFonts != null && !OpenTemplateIdentity(virtualFonts)) virtualFonts = null;
                _virtual = virtualFonts;
                if (_ours.Count == 0 && _virtual == null) return;

                int patched = os == Os.Windows ? InstallWindows()
                            : os == Os.Linux ? InstallLinux()
                            : InstallMac();
                if (patched <= 0) return;   // each says why

                // The pool's names are counted, not listed: thousands of names would bury the rest.
                int pool = 0;
                if (_virtual != null) foreach (var _ in _virtual.Names()) pool++;
                TranslatorCore.LogInfo($"[FontFolder] {patched} engine import(s) redirected ({os}); {_ours.Count} font file(s) shown in {_systemFonts}: "
                    + (_ours.Count > 0 ? string.Join(", ", _ours.Keys) : "none") + $" + {pool} pool name(s) with no file of their own");
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
                    // Only for pool names: what the engine reads from the template is the empty font
                    // of the slot it opened (see ReadFile). Not taken when there is no pool.
                    ["ReadFile"] = real =>
                    {
                        if (_virtual == null) return real;
                        _realReadFile = Marshal.GetDelegateForFunctionPointer<ReadFile_>(real);
                        return Marshal.GetFunctionPointerForDelegate(OurReadFile);
                    },
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
                    if (name == "ReadFile") _readFileCell = new KeyValuePair<IntPtr, IntPtr>(cell, real);
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

        /// <summary>
        /// The real file standing for a path in the folder ours are shown in, or null: a fonts/ file,
        /// or what a pool name opens (<see cref="IVirtualFonts.PathOf"/>).
        /// </summary>
        private static string OursForPath(string path) => OursForPath(path, out _);

        private static string OursForPath(string path, out string virtualName)
        {
            virtualName = null;
            if (string.IsNullOrEmpty(path) || _ours == null || (_ours.Count == 0 && _virtual == null)) return null;
            var dir = Path.GetDirectoryName(path);
            if (dir == null || !string.Equals(dir.TrimEnd('\\', '/'), _systemFonts, _pathCase)) return null;
            string name = Path.GetFileName(path);
            if (_ours.TryGetValue(name, out var file)) return file;
            if (_virtual == null || !_virtual.Has(name)) return null;
            virtualName = name;
            return _virtual.PathOf(name);
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
            ListingClock.Start();
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
            var queue = new Queue<string>(ShownNames());
            lock (Gate) Pending[handle] = queue;
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
                    Describe(data, next, _ours.TryGetValue(next, out var real) ? new FileInfo(real).Length : _virtual.ListedLength(next));
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
            string ours = null, virtualName = null;
            try { ours = name == IntPtr.Zero ? null : OursForPath(Marshal.PtrToStringUni(name), out virtualName); }
            catch (Exception ex) { Faults.Say("FontFolderRedirect.CreateFile", ex); }
            if (ours == null) return _realCreateFile(name, access, share, security, disposition, flags, template);

            var redirected = Marshal.StringToHGlobalUni(ours);
            try
            {
                var handle = _realCreateFile(redirected, access, share, security, disposition, flags, template);
                if (handle != InvalidHandle)
                {
                    if (virtualName != null)
                    {
                        uint lastError = GetLastError();
                        try { Serve(handle, virtualName, flags); }
                        catch (Exception ex) { Faults.Say("FontFolderRedirect.Serve", ex, virtualName); }
                        SetLastError(lastError);
                    }
                    return handle;
                }

                uint error = GetLastError();
                bool first;
                lock (Gate) first = Refused.Add(ours);
                if (first) TranslatorCore.LogWarning($"[FontFolder] The engine could not open {Path.GetFileName(ours)} (Windows error {error})");
                SetLastError(error);
                return handle;
            }
            finally { Marshal.FreeHGlobal(redirected); }
        }

        // ── Pool names: the template opened, the slot's own empty font read ──────────────────────

        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        // The template's identity (volume serial, file index): a handle in Served whose value the
        // system gave since to another file is never touched.
        private static uint _templateVolume, _templateIndexHigh, _templateIndexLow;
        private sealed class ServedFile { public byte[] Content; public string Name; }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, ServedFile> Served =
            new System.Collections.Concurrent.ConcurrentDictionary<IntPtr, ServedFile>();
        private static int _servedReads, _asyncSaid;
        // The engine's ReadFile cell and its real function: given back once the pool has been read.
        private static KeyValuePair<IntPtr, IntPtr> _readFileCell;
        // When the engine has read the whole pool: every name opened, and the last one read as many
        // times as the first (all are the same file to it, so it reads each the same way).
        private static readonly HashSet<string> OpenedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> ReadsOfName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static string _firstName, _lastName;
        private static int _readsPerName = -1, _poolSize = -1;

        /// <summary>Reads the template's identity once; false (said) when it cannot be — no pool then.</summary>
        private static bool OpenTemplateIdentity(IVirtualFonts virtualFonts)
        {
            string template = null;
            foreach (var name in virtualFonts.Names()) { template = virtualFonts.PathOf(name); break; }
            if (template == null || !File.Exists(template)) return false;
            // Linux and macOS: an empty name is opened as an in-memory file of its own (AnonymousFont).
            if (Current() != Os.Windows) return true;
            var handle = CreateFileW(template, 0x80000000 /* GENERIC_READ */, 1 /* FILE_SHARE_READ */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (handle == InvalidHandle)
            {
                TranslatorCore.LogWarning($"[FontFolder] The pool's template could not be opened (Windows error {GetLastError()}) — no pool this session");
                return false;
            }
            try { return Identity(handle, out _templateVolume, out _templateIndexHigh, out _templateIndexLow); }
            finally { CloseHandle(handle); }
        }

        private static bool Identity(IntPtr handle, out uint volume, out uint indexHigh, out uint indexLow)
        {
            volume = indexHigh = indexLow = 0;
            var info = Marshal.AllocHGlobal(52);   // BY_HANDLE_FILE_INFORMATION
            try
            {
                if (!GetFileInformationByHandle(handle, info)) return false;
                volume = (uint)Marshal.ReadInt32(info, 28);
                indexHigh = (uint)Marshal.ReadInt32(info, 44);
                indexLow = (uint)Marshal.ReadInt32(info, 48);
                return true;
            }
            finally { Marshal.FreeHGlobal(info); }
        }

        /// <summary>An empty slot's handle (the template): what is read through it is the slot's own font.</summary>
        private static void Serve(IntPtr handle, string virtualName, uint flags)
        {
            var content = _virtual.ContentOf(virtualName);
            if (content == null) { Served.TryRemove(handle, out _); return; }   // a filled slot: its real file as it is
            if ((flags & FILE_FLAG_OVERLAPPED) != 0 && System.Threading.Interlocked.Exchange(ref _asyncSaid, 1) == 0)
                TranslatorCore.LogWarning("[FontFolder] The engine opens a pool name for asynchronous reads — what it reads there is not replaced");
            Served[handle] = new ServedFile { Content = content, Name = virtualName };
            lock (Gate)
            {
                if (_poolSize < 0) { _poolSize = 0; foreach (var _ in _virtual.Names()) _poolSize++; }
                if (OpenedNames.Count == 0) _firstName = virtualName;
                else if (OpenedNames.Count == 1 && !OpenedNames.Contains(virtualName))
                    ReadsOfName.TryGetValue(_firstName, out _readsPerName);   // the first name is read in full by now
                OpenedNames.Add(virtualName);
                _lastName = virtualName;
            }
        }

        /// <summary>
        /// The engine's ReadFile: untouched for every file but the template opened under a pool name,
        /// whose bytes are replaced by the slot's empty font at the same place — same length, so the
        /// file's size, position and every other question stay the system's own answers.
        /// </summary>
        private static int ReadFile(IntPtr file, IntPtr buffer, uint toRead, IntPtr read, IntPtr overlapped)
        {
            ServedFile served;
            if (Served.IsEmpty || !Served.TryGetValue(file, out served))
                return _realReadFile(file, buffer, toRead, read, overlapped);
            byte[] content = served.Content;

            long position = -1;
            if (overlapped != IntPtr.Zero)
                position = (uint)Marshal.ReadInt32(overlapped, 2 * IntPtr.Size) | ((long)Marshal.ReadInt32(overlapped, 2 * IntPtr.Size + 4) << 32);
            else if (!SetFilePointerEx(file, 0, out position, 1 /* FILE_CURRENT */)) position = -1;

            int ok = _realReadFile(file, buffer, toRead, read, overlapped);
            uint error = GetLastError();
            try
            {
                if (ok != 0 && read != IntPtr.Zero && position >= 0)
                {
                    if (Identity(file, out uint volume, out uint high, out uint low)
                        && volume == _templateVolume && high == _templateIndexHigh && low == _templateIndexLow)
                    {
                        int n = Marshal.ReadInt32(read);
                        int count = (int)Math.Max(0, Math.Min(n, content.Length - position));
                        if (count > 0) Marshal.Copy(content, (int)position, buffer, count);
                        System.Threading.Interlocked.Increment(ref _servedReads);
                        bool done;
                        lock (Gate)
                        {
                            ReadsOfName.TryGetValue(served.Name, out int reads);
                            ReadsOfName[served.Name] = ++reads;
                            done = OpenedNames.Count == _poolSize && _readsPerName > 0
                                   && served.Name == _lastName && reads >= _readsPerName;
                        }
                        // The whole pool read: no empty name is opened again (a name is filled before
                        // the mod hands out its family), so the engine gets its own ReadFile back.
                        if (done) GiveReadFileBack();
                    }
                    else Served.TryRemove(file, out _);   // the value now names another file
                }
            }
            catch (Exception ex) { Faults.Say("FontFolderRedirect.ReadFile", ex); }
            SetLastError(error);
            return ok;
        }

        private static void GiveReadFileBack()
        {
            if (_readFileCell.Key == IntPtr.Zero) return;
            Write(_readFileCell.Key, _readFileCell.Value);
            _readFileCell = default(KeyValuePair<IntPtr, IntPtr>);
            Served.Clear();
            lock (Gate) { ReadsOfName.Clear(); OpenedNames.Clear(); }
            TranslatorCore.LogInfo($"[FontFolder] the engine has read the pool ({_servedReads} reads of empty names served, {_readsPerName} per name, {ListingClock.ElapsedMilliseconds} ms after it began listing) — its ReadFile given back");
        }

        /// <summary>How many reads were served a slot's empty font — the log's proof the pool was read through.</summary>
        internal static int ServedReads => _servedReads;

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
