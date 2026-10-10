using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Windows engines linked to the C library as a DLL: the same answers, given to its file calls.
    ///
    /// 🔴 Measured 2026-10-10: up to 6000.6 UnityPlayer.dll carries the C library inside it (it imports
    /// none of it), so its fopen ends in UnityPlayer's own CreateFileW/ReadFile — the imports
    /// FontFolderRedirect replaces. Unity 6000.7 links the Universal CRT as a DLL (api-ms-win-crt-*,
    /// ucrtbase): the engine still LISTS the font folder through kernel32, but opens and reads the fonts
    /// it found through fopen/_wfopen/_wsopen_s and fread/_read, whose CreateFileW and ReadFile are
    /// ucrtbase's — so our files were listed and never opened, and every legacy text drew nothing.
    ///
    /// ⚠ Still only UnityPlayer.dll's import table: ucrtbase's own imports are never changed (every
    /// module of the process shares it). A pool name still empty is opened as the template, and what is
    /// read through that stream is the slot's own empty font at the same place — same length, so the
    /// stream's size and positions stay the C library's own answers (ReadFile does it for the kernel32 path).
    /// </summary>
    internal static partial class FontFolderRedirect
    {
        // The engine's C library (the api-ms-win-crt-* sets resolve to it): the same instance, so a
        // stream opened here is one the engine can read and close.
        private static class Ucrt
        {
            private const string Lib = "ucrtbase.dll";
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr _wfopen(IntPtr name, IntPtr mode);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern long _ftelli64(IntPtr stream);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern long _telli64(int fd);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CrtFOpen_(IntPtr name, IntPtr mode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CrtWfsOpen_(IntPtr name, IntPtr mode, int share);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CrtWsOpenS_(IntPtr fd, IntPtr name, int flags, int share, int mode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CrtWStat64_(IntPtr name, IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate UIntPtr CrtFRead_(IntPtr buffer, UIntPtr size, UIntPtr count, IntPtr stream);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CrtRead_(int fd, IntPtr buffer, uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CrtFClose_(IntPtr stream);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CrtClose_(int fd);

        private static CrtFOpen_ _realFOpen; private static readonly CrtFOpen_ OurFOpen = CrtFOpen;
        private static CrtFOpen_ _realWFOpen; private static readonly CrtFOpen_ OurWFOpen = CrtWFOpen;
        private static CrtWfsOpen_ _realWfsOpen; private static readonly CrtWfsOpen_ OurWfsOpen = CrtWfsOpen;
        private static CrtWsOpenS_ _realWsOpenS; private static readonly CrtWsOpenS_ OurWsOpenS = CrtWsOpenS;
        private static CrtWStat64_ _realWStat64; private static readonly CrtWStat64_ OurWStat64 = CrtWStat64;
        private static CrtFRead_ _realFRead; private static readonly CrtFRead_ OurFRead = CrtFRead;
        private static CrtRead_ _realRead; private static readonly CrtRead_ OurRead = CrtRead;
        private static CrtFClose_ _realFClose; private static readonly CrtFClose_ OurFClose = CrtFClose;
        private static CrtClose_ _realClose; private static readonly CrtClose_ OurClose = CrtClose;

        // Streams and descriptors opened on the template under an empty pool name, until closed.
        private static readonly ConcurrentDictionary<IntPtr, ServedFile> ServedStreams = new ConcurrentDictionary<IntPtr, ServedFile>();
        private static readonly ConcurrentDictionary<int, ServedFile> ServedDescriptors = new ConcurrentDictionary<int, ServedFile>();
        private static int _crtPoolSaid;

        /// <summary>The engine's C-library imports, where it has them. Returns how many were replaced.</summary>
        private static int PatchCrt(IntPtr module)
        {
            var replacements = new Dictionary<string, Func<IntPtr, IntPtr>>
            {
                ["fopen"] = real => { _realFOpen = Marshal.GetDelegateForFunctionPointer<CrtFOpen_>(real); return Marshal.GetFunctionPointerForDelegate(OurFOpen); },
                ["_wfopen"] = real => { _realWFOpen = Marshal.GetDelegateForFunctionPointer<CrtFOpen_>(real); return Marshal.GetFunctionPointerForDelegate(OurWFOpen); },
                ["_wfsopen"] = real => { _realWfsOpen = Marshal.GetDelegateForFunctionPointer<CrtWfsOpen_>(real); return Marshal.GetFunctionPointerForDelegate(OurWfsOpen); },
                ["_wsopen_s"] = real => { _realWsOpenS = Marshal.GetDelegateForFunctionPointer<CrtWsOpenS_>(real); return Marshal.GetFunctionPointerForDelegate(OurWsOpenS); },
                ["_wstat64"] = real => { _realWStat64 = Marshal.GetDelegateForFunctionPointer<CrtWStat64_>(real); return Marshal.GetFunctionPointerForDelegate(OurWStat64); },
            };
            // Only for pool names, as ReadFile: what is read on the template is the slot's empty font.
            if (_virtual != null)
            {
                replacements["fread"] = real => { _realFRead = Marshal.GetDelegateForFunctionPointer<CrtFRead_>(real); return Marshal.GetFunctionPointerForDelegate(OurFRead); };
                replacements["_read"] = real => { _realRead = Marshal.GetDelegateForFunctionPointer<CrtRead_>(real); return Marshal.GetFunctionPointerForDelegate(OurRead); };
                replacements["fclose"] = real => { _realFClose = Marshal.GetDelegateForFunctionPointer<CrtFClose_>(real); return Marshal.GetFunctionPointerForDelegate(OurFClose); };
                replacements["_close"] = real => { _realClose = Marshal.GetDelegateForFunctionPointer<CrtClose_>(real); return Marshal.GetFunctionPointerForDelegate(OurClose); };
            }
            int count = 0;
            foreach (var dll in new[] { "api-ms-win-crt-stdio-l1-1-0.dll", "api-ms-win-crt-filesystem-l1-1-0.dll", "ucrtbase.dll" })
                count += Patch(module, dll, replacements);
            return count;
        }

        /// <summary>Which of the C library's calls opened one of ours — said once per call.</summary>
        private static void SayCrtOpen(string call)
        {
            if (DiagnosticOnce.First("FontFolder.crt", call))
                TranslatorCore.LogInfo($"[FontFolder] the engine opens our fonts through the C library ({call})");
        }

        /// <summary>
        /// A narrow path as the engine wrote it: UTF-8 when its bytes are UTF-8 (Unity's own strings),
        /// the system's code page otherwise (the C library's reading of a narrow name).
        /// </summary>
        private static string NarrowPath(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            int length = 0;
            while (Marshal.ReadByte(p, length) != 0) length++;
            var bytes = new byte[length];
            Marshal.Copy(p, bytes, 0, length);
            var utf8 = Encoding.UTF8.GetString(bytes);
            var back = Encoding.UTF8.GetBytes(utf8);
            bool isUtf8 = back.Length == bytes.Length;
            for (int i = 0; isUtf8 && i < bytes.Length; i++) isUtf8 = back[i] == bytes[i];
            return isUtf8 ? utf8 : Marshal.PtrToStringAnsi(p);
        }

        private static string OursForWide(IntPtr name, string call, out string virtualName)
        {
            virtualName = null;
            try { return name == IntPtr.Zero ? null : OursForPath(Marshal.PtrToStringUni(name), out virtualName); }
            catch (Exception ex) { Faults.Say("FontFolderRedirect." + call, ex); return null; }
        }

        /// <summary>A stream opened on the template under an empty pool name: its reads are the slot's font.</summary>
        private static void ServeStream(IntPtr stream, string virtualName)
        {
            if (stream == IntPtr.Zero || virtualName == null || _virtual == null) return;
            try
            {
                var content = _virtual.ContentOf(virtualName);
                if (content == null) { ServedStreams.TryRemove(stream, out _); return; }   // a filled slot: its real file
                ServedStreams[stream] = new ServedFile { Content = content, Name = virtualName };
                NoteOpened(virtualName);
            }
            catch (Exception ex) { Faults.Say("FontFolderRedirect.ServeStream", ex, virtualName); }
        }

        private static IntPtr CrtFOpen(IntPtr name, IntPtr mode)
        {
            string ours = null, virtualName = null;
            try { ours = OursForPath(NarrowPath(name), out virtualName); }
            catch (Exception ex) { Faults.Say("FontFolderRedirect.fopen", ex); }
            if (ours == null) return _realFOpen(name, mode);

            // Our path may hold what the code page cannot write: opened by its wide name, in the
            // same C library, so the engine reads and closes this stream as its own.
            SayCrtOpen("fopen");
            var wideName = Marshal.StringToHGlobalUni(ours);
            var wideMode = Marshal.StringToHGlobalUni(Marshal.PtrToStringAnsi(mode) ?? "rb");
            try
            {
                var stream = Ucrt._wfopen(wideName, wideMode);
                ServeStream(stream, virtualName);
                return stream;
            }
            finally { Marshal.FreeHGlobal(wideName); Marshal.FreeHGlobal(wideMode); }
        }

        private static IntPtr CrtWFOpen(IntPtr name, IntPtr mode)
        {
            var ours = OursForWide(name, "_wfopen", out var virtualName);
            if (ours == null) return _realWFOpen(name, mode);
            SayCrtOpen("_wfopen");
            var redirected = Marshal.StringToHGlobalUni(ours);
            try
            {
                var stream = _realWFOpen(redirected, mode);
                ServeStream(stream, virtualName);
                return stream;
            }
            finally { Marshal.FreeHGlobal(redirected); }
        }

        private static IntPtr CrtWfsOpen(IntPtr name, IntPtr mode, int share)
        {
            var ours = OursForWide(name, "_wfsopen", out var virtualName);
            if (ours == null) return _realWfsOpen(name, mode, share);
            SayCrtOpen("_wfsopen");
            var redirected = Marshal.StringToHGlobalUni(ours);
            try
            {
                var stream = _realWfsOpen(redirected, mode, share);
                ServeStream(stream, virtualName);
                return stream;
            }
            finally { Marshal.FreeHGlobal(redirected); }
        }

        private static int CrtWsOpenS(IntPtr fd, IntPtr name, int flags, int share, int mode)
        {
            var ours = OursForWide(name, "_wsopen_s", out var virtualName);
            if (ours == null) return _realWsOpenS(fd, name, flags, share, mode);
            SayCrtOpen("_wsopen_s");
            var redirected = Marshal.StringToHGlobalUni(ours);
            try
            {
                int result = _realWsOpenS(fd, redirected, flags, share, mode);
                if (result == 0 && fd != IntPtr.Zero && virtualName != null && _virtual != null)
                {
                    try
                    {
                        int opened = Marshal.ReadInt32(fd);
                        var content = _virtual.ContentOf(virtualName);
                        if (content == null) ServedDescriptors.TryRemove(opened, out _);
                        else { ServedDescriptors[opened] = new ServedFile { Content = content, Name = virtualName }; NoteOpened(virtualName); }
                    }
                    catch (Exception ex) { Faults.Say("FontFolderRedirect._wsopen_s", ex, virtualName); }
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(redirected); }
        }

        private static int CrtWStat64(IntPtr name, IntPtr buffer)
        {
            var ours = OursForWide(name, "_wstat64", out _);
            if (ours == null) return _realWStat64(name, buffer);
            var redirected = Marshal.StringToHGlobalUni(ours);
            try { return _realWStat64(redirected, buffer); }
            finally { Marshal.FreeHGlobal(redirected); }
        }

        /// <summary>
        /// Replaces what was just read at <paramref name="position"/> by the slot's own bytes — the
        /// template and the slot's font have the same length.
        /// </summary>
        private static void PutSlotBytes(ServedFile served, long position, IntPtr buffer, long read, string call)
        {
            if (position < 0 || read <= 0) return;
            int count = (int)Math.Max(0, Math.Min(read, served.Content.Length - position));
            if (count > 0) Marshal.Copy(served.Content, (int)position, buffer, count);
            if (CountServedRead(served.Name) && System.Threading.Interlocked.Exchange(ref _crtPoolSaid, 1) == 0)
                TranslatorCore.LogInfo($"[FontFolder] the engine has read the pool through the C library ({call}: {_servedReads} reads of empty names served, {_readsPerName} per name, {ListingClock.ElapsedMilliseconds} ms after it began listing)");
        }

        private static UIntPtr CrtFRead(IntPtr buffer, UIntPtr size, UIntPtr count, IntPtr stream)
        {
            ServedFile served;
            if (ServedStreams.IsEmpty || !ServedStreams.TryGetValue(stream, out served))
                return _realFRead(buffer, size, count, stream);
            long position = Ucrt._ftelli64(stream);
            var got = _realFRead(buffer, size, count, stream);
            try { PutSlotBytes(served, position, buffer, (long)got.ToUInt64() * (long)size.ToUInt64(), "fread"); }
            catch (Exception ex) { Faults.Say("FontFolderRedirect.fread", ex, served.Name); }
            return got;
        }

        private static int CrtRead(int fd, IntPtr buffer, uint count)
        {
            ServedFile served;
            if (ServedDescriptors.IsEmpty || !ServedDescriptors.TryGetValue(fd, out served))
                return _realRead(fd, buffer, count);
            long position = Ucrt._telli64(fd);
            int got = _realRead(fd, buffer, count);
            try { PutSlotBytes(served, position, buffer, got, "_read"); }
            catch (Exception ex) { Faults.Say("FontFolderRedirect._read", ex, served.Name); }
            return got;
        }

        private static int CrtFClose(IntPtr stream)
        {
            if (!ServedStreams.IsEmpty) ServedStreams.TryRemove(stream, out _);
            return _realFClose(stream);
        }

        private static int CrtClose(int fd)
        {
            if (!ServedDescriptors.IsEmpty) ServedDescriptors.TryRemove(fd, out _);
            return _realClose(fd);
        }
    }
}
