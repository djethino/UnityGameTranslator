using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The same redirection on native Linux and macOS: the engine walks folders written into its
    /// library and reads what it finds itself, so the calls it makes to walk and to open are answered
    /// here, inside the game only.
    ///
    /// 🔴 Read in the engines of Unity 2021.3.6f1, fetched from Unity (2026-09-28): Linux
    /// `UnityPlayer.so` walks "/usr/share/fonts" (no fontconfig) and imports opendir / readdir(64) /
    /// closedir / ftw64 / open(64) / fopen(64) / __xstat(64) / __lxstat64 / access / realpath; macOS
    /// `UnityPlayer.dylib` walks "/System/Library/Fonts/Supplemental", "/System/Library/Fonts" and
    /// "/Library/Fonts" (no CoreText) with opendir / readdir / closedir / open / fopen / stat. So the
    /// system's own font-registration calls would change nothing — the engine asks the system nothing.
    ///
    /// ⚠ NOT RUN ANYWHERE YET — written from that reading (the user's decision, 2026-09-28: "on
    /// compte sur les retours"). Every step says what it did, and anything unexpected changes nothing:
    /// the game then runs exactly as without it.
    ///
    /// ⚠ How: the engine's own import slots, found in its dynamic relocations (Linux, ELF: JUMP_SLOT /
    /// GLOB_DAT) or its indirect symbol table (macOS, Mach-O: lazy / non-lazy symbol pointers — the
    /// "fishhook" technique). No other module of the process is touched.
    ///
    /// ⚠ On arm64 macOS, open() is not taken: a variadic argument travels on the stack there, and a
    /// replacement declared with fixed arguments would hand files it creates a wrong mode. fopen and
    /// stat cover the reading of a font.
    /// </summary>
    internal static partial class FontFolderRedirect
    {
        // ── libc ─────────────────────────────────────────────────────────────────────────────────
        private static class LinuxC
        {
            private const string Lib = "libc.so.6";
            [DllImport(Lib)] public static extern int mprotect(IntPtr addr, UIntPtr len, int prot);
            [DllImport(Lib)] public static extern int getpagesize();
            [DllImport(Lib)] public static extern IntPtr __errno_location();
            [DllImport(Lib, EntryPoint = "stat64")] public static extern int stat64(IntPtr path, IntPtr buf);
            [DllImport(Lib, EntryPoint = "__xstat64")] public static extern int xstat64(int version, IntPtr path, IntPtr buf);
            [DllImport(Lib, EntryPoint = "dlsym")] public static extern IntPtr dlsym(IntPtr handle, string name);
            [DllImport(Lib)] public static extern IntPtr gnu_get_libc_version();
            [DllImport(Lib, SetLastError = true)] public static extern int memfd_create(string name, uint flags);
            [DllImport(Lib, SetLastError = true)] public static extern int mkstemp(byte[] template);
            [DllImport(Lib)] public static extern int unlink(byte[] path);
            [DllImport(Lib)] public static extern IntPtr write(int fd, byte[] buffer, UIntPtr count);
            [DllImport(Lib)] public static extern long lseek(int fd, long offset, int whence);
            [DllImport(Lib)] public static extern int close(int fd);
            [DllImport(Lib)] public static extern IntPtr fdopen(int fd, IntPtr mode);
        }

        /// <summary>
        /// Whether this glibc is at least <paramref name="major"/>.<paramref name="minor"/> — asked of it,
        /// rather than tried and caught: stat64 is exported from 2.33, dlsym lives in libc from 2.34.
        /// </summary>
        private static bool GlibcAtLeast(int major, int minor)
        {
            var parts = (PathOf(LinuxC.gnu_get_libc_version()) ?? "0.0").Split('.');
            int ma = parts.Length > 0 && int.TryParse(parts[0], out var a) ? a : 0;
            int mi = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 0;
            return ma > major || (ma == major && mi >= minor);
        }

        // dlsym lived in libdl before glibc 2.34.
        private static class LinuxDl
        {
            [DllImport("libdl.so.2", EntryPoint = "dlsym")] public static extern IntPtr dlsym(IntPtr handle, string name);
        }

        private static class MacC
        {
            private const string Lib = "/usr/lib/libSystem.B.dylib";
            [DllImport(Lib)] public static extern int mprotect(IntPtr addr, UIntPtr len, int prot);
            [DllImport(Lib)] public static extern int getpagesize();
            [DllImport(Lib)] public static extern IntPtr __error();
            [DllImport(Lib)] public static extern uint _dyld_image_count();
            [DllImport(Lib)] public static extern IntPtr _dyld_get_image_name(uint index);
            [DllImport(Lib)] public static extern IntPtr _dyld_get_image_header(uint index);
            [DllImport(Lib)] public static extern IntPtr _dyld_get_image_vmaddr_slide(uint index);
            [DllImport(Lib)] public static extern IntPtr dlsym(IntPtr handle, string name);
            [DllImport(Lib, SetLastError = true)] public static extern int mkstemp(byte[] template);
            [DllImport(Lib)] public static extern int unlink(byte[] path);
            [DllImport(Lib)] public static extern IntPtr write(int fd, byte[] buffer, UIntPtr count);
            [DllImport(Lib)] public static extern long lseek(int fd, long offset, int whence);
            [DllImport(Lib)] public static extern int close(int fd);
            [DllImport(Lib)] public static extern IntPtr fdopen(int fd, IntPtr mode);
        }

        /// <summary>
        /// A pool name still empty, opened by the engine: a file that exists nowhere — its bytes in
        /// memory (memfd_create, glibc 2.27+), otherwise a temporary file removed as soon as it is open
        /// — read by the engine like any file. -1 when the path is not such a name (or, said, when no
        /// such file could be made: the engine then opens the template, whose family repeats).
        /// </summary>
        private static int AnonymousFont(IntPtr path)
        {
            if (_virtual == null) return -1;
            string name;
            try
            {
                OursForPath(PathOf(path), out name);
                if (name == null) return -1;
            }
            catch (Exception ex) { Faults.Say("FontFolderRedirect.AnonymousFont", ex); return -1; }
            var content = _virtual.ContentOf(name);
            if (content == null) return -1;   // a filled name: its real file

            bool mac = Current() == Os.Mac;
            int fd = -1;
            if (!mac && GlibcAtLeast(2, 27)) fd = LinuxC.memfd_create("ugt-pool", 0);
            if (fd < 0)
            {
                var template = Encoding.UTF8.GetBytes(Path.Combine(Path.GetTempPath(), "ugt-pool-XXXXXX") + "\0");
                fd = mac ? MacC.mkstemp(template) : LinuxC.mkstemp(template);
                if (fd >= 0) { if (mac) MacC.unlink(template); else LinuxC.unlink(template); }
            }
            if (fd < 0)
            {
                if (System.Threading.Interlocked.Exchange(ref _anonymousSaid, 1) == 0)
                    TranslatorCore.LogWarning($"[FontFolder] No in-memory file could be made for the pool (errno {Marshal.GetLastWin32Error()})");
                return -1;
            }
            long written = mac ? MacC.write(fd, content, (UIntPtr)(uint)content.Length).ToInt64()
                               : LinuxC.write(fd, content, (UIntPtr)(uint)content.Length).ToInt64();
            if (written != content.Length) { if (mac) MacC.close(fd); else LinuxC.close(fd); return -1; }
            if (mac) MacC.lseek(fd, 0, 0); else LinuxC.lseek(fd, 0, 0);
            System.Threading.Interlocked.Increment(ref _servedReads);
            return fd;
        }

        private static int _anonymousSaid;

        /// <summary>
        /// The real function for an imported name, asked of the dynamic linker — never read from the
        /// slot: a lazily-bound slot still points at a stub that, called, binds the symbol and writes
        /// the slot again, over the replacement. Zero when the linker does not know it.
        /// </summary>
        private static IntPtr RealFunction(string importedName)
        {
            try
            {
                if (Current() == Os.Mac) return MacC.dlsym(new IntPtr(-2), importedName);   // RTLD_DEFAULT
                return GlibcAtLeast(2, 34) ? LinuxC.dlsym(IntPtr.Zero, importedName)       // RTLD_DEFAULT
                                           : LinuxDl.dlsym(IntPtr.Zero, importedName);
            }
            catch (Exception ex)
            {
                Faults.Say("FontFolderRedirect.dlsym", ex, importedName);
                return IntPtr.Zero;
            }
        }

        private const int PROT_READ = 1, PROT_WRITE = 2;

        /// <summary>
        /// Writes one import slot, the page made writable first. Left writable afterwards: its original
        /// protection is not known here, and making a lazily-bound table read-only would break the
        /// engine's next first call.
        /// </summary>
        private static void WriteUnix(IntPtr cell, IntPtr value)
        {
            bool mac = Current() == Os.Mac;
            long page = mac ? MacC.getpagesize() : LinuxC.getpagesize();
            long start = cell.ToInt64() & ~(page - 1);
            long length = (cell.ToInt64() + IntPtr.Size) - start;
            int result = mac ? MacC.mprotect(new IntPtr(start), (UIntPtr)(ulong)length, PROT_READ | PROT_WRITE)
                             : LinuxC.mprotect(new IntPtr(start), (UIntPtr)(ulong)length, PROT_READ | PROT_WRITE);
            if (result != 0) throw new InvalidOperationException("mprotect refused the engine's import table");
            Marshal.WriteIntPtr(cell, value);
        }

        private static int Errno
        {
            get { var p = Current() == Os.Mac ? MacC.__error() : LinuxC.__errno_location(); return Marshal.ReadInt32(p); }
            set { var p = Current() == Os.Mac ? MacC.__error() : LinuxC.__errno_location(); Marshal.WriteInt32(p, value); }
        }

        private static IntPtr At(IntPtr p, long offset) => new IntPtr(p.ToInt64() + offset);

        // ── The replacements ─────────────────────────────────────────────────────────────────────
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr OpenDir_(IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ReadDir_(IntPtr dir);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CloseDir_(IntPtr dir);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Ftw_(IntPtr dir, IntPtr fn, int fds);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FtwCallback_(IntPtr path, IntPtr stat, int flag);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Open_(IntPtr path, int flags, int mode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FOpen_(IntPtr path, IntPtr mode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int XStat_(int version, IntPtr path, IntPtr buf);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Stat_(IntPtr path, IntPtr buf);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Access_(IntPtr path, int mode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr RealPath_(IntPtr path, IntPtr resolved);

        // The real function behind each slot, by name — several slots may share a signature (readdir,
        // readdir64), so each replacement is a closure over its own real function, and kept alive here.
        private static readonly List<Delegate> Keep = new List<Delegate>();

        /// <summary>A directory being listed by the engine, with our files still to hand out and the buffer they are handed in.</summary>
        private sealed class Listing
        {
            public readonly Queue<string> Left;
            public readonly IntPtr Entry;
            public Listing(IEnumerable<string> names, int size) { Left = new Queue<string>(names); Entry = Marshal.AllocHGlobal(size); }
        }

        private static readonly Dictionary<IntPtr, Listing> Listings = new Dictionary<IntPtr, Listing>();

        // struct dirent: Linux (dirent and dirent64 on 64-bit) and macOS (64-bit inodes).
        private static int DirentSize => Current() == Os.Mac ? 1048 : 280;

        /// <summary>Writes one of our file names as the next entry of a listing.</summary>
        private static void DescribeEntry(IntPtr entry, string name)
        {
            for (int i = 0; i < DirentSize; i += 4) Marshal.WriteInt32(entry, i, 0);
            var bytes = Encoding.UTF8.GetBytes(name);
            Marshal.WriteInt64(entry, 0, 1);   // d_ino: never 0, which some walkers skip
            if (Current() == Os.Mac)
            {
                Marshal.WriteInt16(entry, 16, (short)DirentSize);       // d_reclen
                Marshal.WriteInt16(entry, 18, (short)bytes.Length);     // d_namlen
                Marshal.WriteByte(entry, 20, 8);                        // d_type = DT_REG
                Marshal.Copy(bytes, 0, At(entry, 21), Math.Min(bytes.Length, 1023));
            }
            else
            {
                Marshal.WriteInt16(entry, 16, (short)DirentSize);       // d_reclen
                Marshal.WriteByte(entry, 18, 8);                        // d_type = DT_REG
                Marshal.Copy(bytes, 0, At(entry, 19), Math.Min(bytes.Length, 255));
            }
        }

        private static string PathOf(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);

        /// <summary>Calls <paramref name="call"/> with our file's real path in place of the engine's, when the engine names one of ours.</summary>
        private static T Redirected<T>(IntPtr path, Func<IntPtr, T> call)
        {
            string ours = null;
            try { ours = OursForPath(PathOf(path)); } catch (Exception ex) { Faults.Say("FontFolderRedirect.Redirected", ex); }
            if (ours == null) return call(path);

            var mine = Marshal.StringToHGlobalAnsi(ours);
            try { return call(mine); }
            finally { Marshal.FreeHGlobal(mine); }
        }

        private static Func<IntPtr, IntPtr> OpenDirHook() => real =>
        {
            var callReal = Marshal.GetDelegateForFunctionPointer<OpenDir_>(real);
            OpenDir_ ours = name =>
            {
                var dir = callReal(name);
                int saved = Errno;
                try
                {
                    var path = PathOf(name);
                    if (dir != IntPtr.Zero && IsOurFolder(path))
                    {
                        lock (Gate) Listings[dir] = new Listing(ShownNames(), DirentSize);
                        SawListing(path);
                    }
                }
                catch (Exception ex) { Faults.Say("FontFolderRedirect.opendir", ex); }
                Errno = saved;
                return dir;
            };
            Keep.Add(ours);
            return Marshal.GetFunctionPointerForDelegate(ours);
        };

        private static Func<IntPtr, IntPtr> ReadDirHook() => real =>
        {
            var callReal = Marshal.GetDelegateForFunctionPointer<ReadDir_>(real);
            ReadDir_ ours = dir =>
            {
                var entry = callReal(dir);
                if (entry != IntPtr.Zero) return entry;

                int saved = Errno;
                try
                {
                    // The real entries are done: ours follow, then the end.
                    lock (Gate)
                    {
                        if (Listings.TryGetValue(dir, out var listing) && listing.Left.Count > 0)
                        {
                            DescribeEntry(listing.Entry, listing.Left.Dequeue());
                            Errno = saved;
                            return listing.Entry;
                        }
                    }
                }
                catch (Exception ex) { Faults.Say("FontFolderRedirect.readdir", ex); }
                Errno = saved;
                return IntPtr.Zero;
            };
            Keep.Add(ours);
            return Marshal.GetFunctionPointerForDelegate(ours);
        };

        private static Func<IntPtr, IntPtr> CloseDirHook() => real =>
        {
            var callReal = Marshal.GetDelegateForFunctionPointer<CloseDir_>(real);
            CloseDir_ ours = dir =>
            {
                lock (Gate)
                {
                    if (Listings.TryGetValue(dir, out var listing))
                    {
                        Listings.Remove(dir);
                        Marshal.FreeHGlobal(listing.Entry);
                    }
                }
                return callReal(dir);
            };
            Keep.Add(ours);
            return Marshal.GetFunctionPointerForDelegate(ours);
        };

        /// <summary>ftw64: the engine's callback is called for our files too, after the real walk, as regular files.</summary>
        private static Func<IntPtr, IntPtr> FtwHook() => real =>
        {
            var callReal = Marshal.GetDelegateForFunctionPointer<Ftw_>(real);
            Ftw_ ours = (dir, fn, fds) =>
            {
                int result = callReal(dir, fn, fds);
                if (result != 0) return result;

                int saved = Errno;
                try
                {
                    var path = PathOf(dir);
                    if (!IsOurFolder(path)) return result;
                    SawListing(path);

                    var callback = Marshal.GetDelegateForFunctionPointer<FtwCallback_>(fn);
                    foreach (var name in ShownNames())
                    {
                        var fake = Marshal.StringToHGlobalAnsi(_systemFonts + "/" + name);
                        var real64 = Marshal.StringToHGlobalAnsi(_ours.TryGetValue(name, out var file) ? file : _virtual.PathOf(name));
                        var stat = Marshal.AllocHGlobal(256);
                        try
                        {
                            if (!StatOurs(real64, stat)) continue;
                            int stop = callback(fake, stat, 0);   // FTW_F
                            if (stop != 0) return stop;
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(fake); Marshal.FreeHGlobal(real64); Marshal.FreeHGlobal(stat);
                        }
                    }
                }
                catch (Exception ex) { Faults.Say("FontFolderRedirect.ftw", ex); }
                finally { Errno = saved; }
                return result;
            };
            Keep.Add(ours);
            return Marshal.GetFunctionPointerForDelegate(ours);
        };

        /// <summary>struct stat64 of one of our files, as glibc gives it (stat64 since 2.33, __xstat64 before).</summary>
        private static bool StatOurs(IntPtr path, IntPtr buf) =>
            (GlibcAtLeast(2, 33) ? LinuxC.stat64(path, buf) : LinuxC.xstat64(1, path, buf)) == 0;

        private static Func<IntPtr, IntPtr> OpenHook() => real =>
        {
            var callReal = Marshal.GetDelegateForFunctionPointer<Open_>(real);
            Open_ ours = (path, flags, mode) =>
            {
                int anonymous = AnonymousFont(path);
                return anonymous >= 0 ? anonymous : Redirected(path, p => callReal(p, flags, mode));
            };
            Keep.Add(ours);
            return Marshal.GetFunctionPointerForDelegate(ours);
        };

        private static Func<IntPtr, IntPtr> FOpenHook() => real =>
        {
            var callReal = Marshal.GetDelegateForFunctionPointer<FOpen_>(real);
            FOpen_ ours = (path, mode) =>
            {
                int anonymous = AnonymousFont(path);
                if (anonymous < 0) return Redirected(path, p => callReal(p, mode));
                var file = Current() == Os.Mac ? MacC.fdopen(anonymous, mode) : LinuxC.fdopen(anonymous, mode);
                if (file == IntPtr.Zero) { if (Current() == Os.Mac) MacC.close(anonymous); else LinuxC.close(anonymous); }
                return file;
            };
            Keep.Add(ours);
            return Marshal.GetFunctionPointerForDelegate(ours);
        };

        private static Func<IntPtr, IntPtr> XStatHook() => real =>
        {
            var callReal = Marshal.GetDelegateForFunctionPointer<XStat_>(real);
            XStat_ ours = (version, path, buf) => Redirected(path, p => callReal(version, p, buf));
            Keep.Add(ours);
            return Marshal.GetFunctionPointerForDelegate(ours);
        };

        private static Func<IntPtr, IntPtr> StatHook() => real =>
        {
            var callReal = Marshal.GetDelegateForFunctionPointer<Stat_>(real);
            Stat_ ours = (path, buf) => Redirected(path, p => callReal(p, buf));
            Keep.Add(ours);
            return Marshal.GetFunctionPointerForDelegate(ours);
        };

        private static Func<IntPtr, IntPtr> AccessHook() => real =>
        {
            var callReal = Marshal.GetDelegateForFunctionPointer<Access_>(real);
            Access_ ours = (path, mode) => Redirected(path, p => callReal(p, mode));
            Keep.Add(ours);
            return Marshal.GetFunctionPointerForDelegate(ours);
        };

        private static Func<IntPtr, IntPtr> RealPathHook() => real =>
        {
            var callReal = Marshal.GetDelegateForFunctionPointer<RealPath_>(real);
            RealPath_ ours = (path, resolved) =>
            {
                // A pool name resolves to ITSELF: resolved to the template, it would be opened as the
                // template and read under the template's family.
                string name = null;
                try { OursForPath(PathOf(path), out name); } catch (Exception ex) { Faults.Say("FontFolderRedirect.realpath", ex); }
                if (name == null) return Redirected(path, p => callReal(p, resolved));
                var bytes = Encoding.UTF8.GetBytes(PathOf(path) + "\0");
                var target = resolved != IntPtr.Zero ? resolved : Marshal.AllocHGlobal(bytes.Length);   // malloc: the caller frees it
                Marshal.Copy(bytes, 0, target, bytes.Length);
                return target;
            };
            Keep.Add(ours);
            return Marshal.GetFunctionPointerForDelegate(ours);
        };

        /// <summary>What to replace, by imported name (version and "$INODE64" suffixes stripped).</summary>
        private static Dictionary<string, Func<IntPtr, IntPtr>> UnixReplacements(bool takeOpen)
        {
            var map = new Dictionary<string, Func<IntPtr, IntPtr>>(StringComparer.Ordinal)
            {
                ["opendir"] = OpenDirHook(), ["readdir"] = ReadDirHook(), ["readdir64"] = ReadDirHook(),
                ["closedir"] = CloseDirHook(), ["ftw64"] = FtwHook(),
                ["fopen"] = FOpenHook(), ["fopen64"] = FOpenHook(),
                ["__xstat"] = XStatHook(), ["__xstat64"] = XStatHook(), ["__lxstat64"] = XStatHook(),
                ["stat"] = StatHook(), ["stat64"] = StatHook(), ["lstat"] = StatHook(), ["lstat64"] = StatHook(),
                ["access"] = AccessHook(), ["realpath"] = RealPathHook(),
            };
            if (takeOpen) { map["open"] = OpenHook(); map["open64"] = OpenHook(); }
            return map;
        }

        // ── Linux: the engine's ELF relocations ─────────────────────────────────────────────────

        private static int InstallLinux()
        {
            if (IntPtr.Size != 8)
            {
                TranslatorCore.LogInfo("[FontFolder] 32-bit Linux player — not handled, nothing redirected");
                return 0;
            }

            // Where the engine is loaded: its first mapping, the one at file offset 0.
            string path = null; long start = 0;
            foreach (var line in File.ReadAllLines("/proc/self/maps"))
            {
                var parts = line.Split(new[] { ' ' }, 6, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 6 || !parts[5].EndsWith("/UnityPlayer.so", StringComparison.Ordinal)) continue;
                if (Convert.ToInt64(parts[2], 16) != 0) continue;
                path = parts[5].Trim();
                start = Convert.ToInt64(parts[0].Split('-')[0], 16);
                break;
            }
            if (path == null)
            {
                TranslatorCore.LogInfo("[FontFolder] No UnityPlayer.so in this process — nothing to redirect");
                return 0;
            }

            var elf = File.ReadAllBytes(path);
            if (elf.Length < 64 || elf[0] != 0x7F || elf[1] != (byte)'E' || elf[2] != (byte)'L' || elf[3] != (byte)'F' || elf[4] != 2 || elf[5] != 1)
            {
                TranslatorCore.LogWarning("[FontFolder] UnityPlayer.so is not a 64-bit little-endian ELF — nothing redirected");
                return 0;
            }

            ushort machine = BitConverter.ToUInt16(elf, 0x12);
            uint jumpSlot, globDat;
            if (machine == 62) { jumpSlot = 7; globDat = 6; }              // x86-64
            else if (machine == 183) { jumpSlot = 1026; globDat = 1025; }  // aarch64
            else
            {
                TranslatorCore.LogWarning($"[FontFolder] UnityPlayer.so for machine {machine} — not handled, nothing redirected");
                return 0;
            }

            // The load bias: where the first loadable segment's page landed, minus its address in the file.
            long phoff = BitConverter.ToInt64(elf, 0x20);
            int phentsize = BitConverter.ToUInt16(elf, 0x36), phnum = BitConverter.ToUInt16(elf, 0x38);
            long firstLoad = -1;
            for (int i = 0; i < phnum && firstLoad < 0; i++)
            {
                long ph = phoff + (long)i * phentsize;
                if (BitConverter.ToUInt32(elf, (int)ph) == 1) firstLoad = BitConverter.ToInt64(elf, (int)ph + 0x10) & ~0xFFFL;   // PT_LOAD p_vaddr
            }
            long bias = start - Math.Max(0, firstLoad);

            long shoff = BitConverter.ToInt64(elf, 0x28);
            int shentsize = BitConverter.ToUInt16(elf, 0x3A), shnum = BitConverter.ToUInt16(elf, 0x3C);
            long Section(int index, int field8) => BitConverter.ToInt64(elf, (int)(shoff + (long)index * shentsize + field8));
            uint SectionType(int index) => BitConverter.ToUInt32(elf, (int)(shoff + (long)index * shentsize + 4));
            uint SectionLink(int index) => BitConverter.ToUInt32(elf, (int)(shoff + (long)index * shentsize + 0x28));

            var replacements = UnixReplacements(takeOpen: true);
            var done = new HashSet<long>();
            int count = 0;

            for (int s = 0; s < shnum; s++)
            {
                if (SectionType(s) != 4) continue;                 // SHT_RELA
                int symtab = (int)SectionLink(s);
                int strtab = (int)SectionLink(symtab);
                long relOff = Section(s, 0x18), relSize = Section(s, 0x20);
                long symOff = Section(symtab, 0x18), strOff = Section(strtab, 0x18);

                for (long r = relOff; r + 24 <= relOff + relSize; r += 24)
                {
                    long offset = BitConverter.ToInt64(elf, (int)r);
                    ulong info = BitConverter.ToUInt64(elf, (int)r + 8);
                    uint type = (uint)(info & 0xFFFFFFFF);
                    if (type != jumpSlot && type != globDat) continue;

                    long sym = symOff + (long)(info >> 32) * 24;
                    uint nameOff = BitConverter.ToUInt32(elf, (int)sym);
                    int nameStart = (int)(strOff + nameOff), nameEnd = nameStart;
                    while (elf[nameEnd] != 0) nameEnd++;
                    var name = Encoding.ASCII.GetString(elf, nameStart, nameEnd - nameStart).Split('@')[0];

                    if (!replacements.TryGetValue(name, out var replace)) continue;
                    long slot = bias + offset;
                    if (!done.Add(slot)) continue;

                    var cell = new IntPtr(slot);
                    var real = RealFunction(name);
                    if (real == IntPtr.Zero) continue;   // unknown to the linker: that slot is left as it is
                    var previous = Marshal.ReadIntPtr(cell);
                    Write(cell, replace(real));
                    Patched.Add(new KeyValuePair<IntPtr, IntPtr>(cell, previous));
                    count++;
                }
            }

            return count;
        }

        // ── macOS: the engine's indirect symbol table ────────────────────────────────────────────

        /// <summary>A section of symbol pointers: where it is, how long, and where its names start in the indirect table.</summary>
        private sealed class PointerSection
        {
            public readonly long Addr, Size;
            public readonly uint Reserved1;
            public PointerSection(long addr, long size, uint reserved1) { Addr = addr; Size = size; Reserved1 = reserved1; }
        }

        private static int InstallMac()
        {
            int image = -1;
            uint images = MacC._dyld_image_count();
            for (uint i = 0; i < images; i++)
            {
                var name = PathOf(MacC._dyld_get_image_name(i));
                if (name != null && name.EndsWith("/UnityPlayer.dylib", StringComparison.Ordinal)) { image = (int)i; break; }
            }
            if (image < 0)
            {
                TranslatorCore.LogInfo("[FontFolder] No UnityPlayer.dylib in this process — nothing to redirect");
                return 0;
            }

            var header = MacC._dyld_get_image_header((uint)image);
            long slide = MacC._dyld_get_image_vmaddr_slide((uint)image).ToInt64();
            if ((uint)Marshal.ReadInt32(header) != 0xFEEDFACF)
            {
                TranslatorCore.LogWarning("[FontFolder] UnityPlayer.dylib is not a 64-bit Mach-O — nothing redirected");
                return 0;
            }

            // The load commands: segments (for __LINKEDIT and the pointer sections), the symbol tables.
            int ncmds = Marshal.ReadInt32(header, 16);
            long linkeditVm = -1, linkeditFile = 0, symoff = 0, stroff = 0, indirectOff = 0;
            var pointerSections = new List<PointerSection>();

            var cmd = At(header, 32);
            for (int c = 0; c < ncmds; c++)
            {
                uint kind = (uint)Marshal.ReadInt32(cmd);
                int size = Marshal.ReadInt32(cmd, 4);

                if (kind == 0x19)   // LC_SEGMENT_64
                {
                    var segname = Marshal.PtrToStringAnsi(At(cmd, 8), 16).TrimEnd('\0');
                    if (segname == "__LINKEDIT")
                    {
                        linkeditVm = Marshal.ReadInt64(cmd, 24);
                        linkeditFile = Marshal.ReadInt64(cmd, 40);
                    }
                    else if (segname == "__DATA" || segname == "__DATA_CONST")
                    {
                        int nsects = Marshal.ReadInt32(cmd, 64);
                        for (int n = 0; n < nsects; n++)
                        {
                            var sect = At(cmd, 72 + n * 80);
                            uint flags = (uint)Marshal.ReadInt32(sect, 64);
                            uint type = flags & 0xFF;
                            if (type == 6 || type == 7)   // S_NON_LAZY_SYMBOL_POINTERS, S_LAZY_SYMBOL_POINTERS
                                pointerSections.Add(new PointerSection(Marshal.ReadInt64(sect, 32), Marshal.ReadInt64(sect, 40), (uint)Marshal.ReadInt32(sect, 68)));
                        }
                    }
                }
                else if (kind == 0x2)   // LC_SYMTAB
                {
                    symoff = (uint)Marshal.ReadInt32(cmd, 8);
                    stroff = (uint)Marshal.ReadInt32(cmd, 16);
                }
                else if (kind == 0xB)   // LC_DYSYMTAB
                {
                    indirectOff = (uint)Marshal.ReadInt32(cmd, 56);
                }

                cmd = At(cmd, size);
            }

            if (linkeditVm < 0 || symoff == 0 || stroff == 0 || indirectOff == 0)
            {
                TranslatorCore.LogWarning("[FontFolder] UnityPlayer.dylib carries no readable symbol tables — nothing redirected");
                return 0;
            }

            long linkedit = slide + linkeditVm - linkeditFile;
            var symbols = new IntPtr(linkedit + symoff);
            var strings = new IntPtr(linkedit + stroff);
            var indirect = new IntPtr(linkedit + indirectOff);

            bool arm64 = false;
            try { arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64; }
            catch (Exception ex) { Faults.Say("FontFolderRedirect.arch", ex); arm64 = true; }   // unknown: the safe side

            var replacements = UnixReplacements(takeOpen: !arm64);
            int count = 0;

            foreach (var section in pointerSections)
            {
                for (long i = 0; i < section.Size / 8; i++)
                {
                    uint symIndex = (uint)Marshal.ReadInt32(indirect, (int)((section.Reserved1 + i) * 4));
                    if ((symIndex & 0xC0000000) != 0) continue;   // INDIRECT_SYMBOL_LOCAL / ABS

                    uint strx = (uint)Marshal.ReadInt32(symbols, (int)(symIndex * 16));
                    var name = Marshal.PtrToStringAnsi(At(strings, strx));
                    if (string.IsNullOrEmpty(name) || name[0] != '_') continue;
                    var key = name.Substring(1).Split('$')[0];
                    if (!replacements.TryGetValue(key, out var replace)) continue;

                    var cell = new IntPtr(slide + section.Addr + i * 8);
                    var real = RealFunction(name.Substring(1));   // "readdir$INODE64" as the linker knows it
                    if (real == IntPtr.Zero) continue;
                    var previous = Marshal.ReadIntPtr(cell);
                    Write(cell, replace(real));
                    Patched.Add(new KeyValuePair<IntPtr, IntPtr>(cell, previous));
                    count++;
                }
            }

            return count;
        }
    }
}
