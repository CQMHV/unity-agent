#if UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AjisaiFlow.UnityAgent.Editor.Tools
{
    /// <summary>
    /// Win32-only P/Invoke layer for window / screen capture.
    /// Constraint: asmdef has allowUnsafeCode=false → use Marshal.Copy semantics only,
    /// no unsafe pointer ops. GDI handles MUST be released in try/finally.
    /// </summary>
    internal static class WindowCaptureNative
    {
        // ─── BitBlt ROP codes ───
        private const uint SRCCOPY = 0x00CC0020;
        private const uint CAPTUREBLT = 0x40000000;

        // ─── DIB constants ───
        private const uint BI_RGB = 0;
        private const uint DIB_RGB_COLORS = 0;

        // ─── MONITORINFOF flags ───
        private const uint MONITORINFOF_PRIMARY = 0x00000001;

        // SetThreadDpiAwarenessContext: Per-Monitor V2 (Win10 1703+).
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        // ─── Structs ───
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        // For 32-bit BI_RGB the color table is unused but the struct still needs one slot.
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors0;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        // ─── P/Invoke ───
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX mi);
        [DllImport("Shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
        private const int MDT_EFFECTIVE_DPI = 0;

        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, [Out] byte[] lpvBits, ref BITMAPINFO bmi, uint usage);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        // ─── DPI Awareness Scope ───
        // Wrap any block that reads physical screen coordinates so DPI scaling
        // matches between EditorGUIUtility.pixelsPerPoint and Win32 results.
        public sealed class DpiScope : IDisposable
        {
            private IntPtr _previous;
            private bool _applied;

            public DpiScope()
            {
                try
                {
                    _previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                    _applied = true;
                }
                catch (EntryPointNotFoundException)
                {
                    // Pre-Win10 1703: function unavailable. Continue without DPI override.
                    _applied = false;
                }
            }

            public void Dispose()
            {
                if (!_applied) return;
                try { SetThreadDpiAwarenessContext(_previous); } catch { /* swallow */ }
                _applied = false;
            }
        }

        // ─── Monitor descriptor ───
        public struct MonitorDescriptor
        {
            public string DeviceName;   // e.g. \\.\DISPLAY1
            public bool IsPrimary;
            public int X;
            public int Y;
            public int Width;
            public int Height;
            public uint DpiX;
            public uint DpiY;
        }

        public static List<MonitorDescriptor> EnumerateMonitors()
        {
            var result = new List<MonitorDescriptor>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT lprc, IntPtr data) =>
            {
                var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMon, ref mi))
                {
                    uint dpiX = 96, dpiY = 96;
                    try { GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out dpiX, out dpiY); }
                    catch { /* Shcore.dll missing on Win7 — keep default 96 */ }

                    result.Add(new MonitorDescriptor
                    {
                        DeviceName = mi.szDevice ?? string.Empty,
                        IsPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                        X = mi.rcMonitor.left,
                        Y = mi.rcMonitor.top,
                        Width = mi.rcMonitor.right - mi.rcMonitor.left,
                        Height = mi.rcMonitor.bottom - mi.rcMonitor.top,
                        DpiX = dpiX,
                        DpiY = dpiY,
                    });
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        // ─── Resolve scale for a Unity-reported window rect ───
        // Unity's EditorWindow.position uses per-monitor logical coordinates: each monitor's
        // values are scaled by that monitor's DPI factor (NOT by EditorGUIUtility.pixelsPerPoint
        // which is a single global value). To find the physical screen rect, we try each monitor's
        // scale and pick the one whose resulting physical rect's center falls inside that monitor.
        public static (int x, int y, int w, int h) UnityRectToPhysical(
            float unityX, float unityY, float unityW, float unityH, List<MonitorDescriptor> monitors)
        {
            foreach (var m in monitors)
            {
                float scale = m.DpiX > 0 ? m.DpiX / 96f : 1f;
                int px = (int)System.Math.Round(unityX * scale);
                int py = (int)System.Math.Round(unityY * scale);
                int pw = (int)System.Math.Round(unityW * scale);
                int ph = (int)System.Math.Round(unityH * scale);
                int cx = px + pw / 2;
                int cy = py + ph / 2;
                if (cx >= m.X && cx < m.X + m.Width && cy >= m.Y && cy < m.Y + m.Height)
                {
                    return (px, py, pw, ph);
                }
            }
            // No monitor matched — fall back to no scaling (1.0).
            return ((int)unityX, (int)unityY, (int)unityW, (int)unityH);
        }

        // ─── Resolve monitor by id ───
        // monitorId: "primary" | integer index | device name like "\\.\DISPLAY1"
        public static MonitorDescriptor? ResolveMonitor(string monitorId, List<MonitorDescriptor> monitors)
        {
            if (monitors == null || monitors.Count == 0) return null;
            if (string.IsNullOrEmpty(monitorId) || string.Equals(monitorId, "primary", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var m in monitors)
                {
                    if (m.IsPrimary) return m;
                }
                return monitors[0];
            }
            if (int.TryParse(monitorId, out int idx))
            {
                if (idx >= 0 && idx < monitors.Count) return monitors[idx];
                return null;
            }
            foreach (var m in monitors)
            {
                if (string.Equals(m.DeviceName, monitorId, StringComparison.OrdinalIgnoreCase)) return m;
            }
            return null;
        }

        // ─── Capture an arbitrary screen rect ───
        // Returns BGRA32 top-down byte[width*height*4]. Throws on failure.
        // includeLayeredWindows=true uses CAPTUREBLT (needed for some overlay/transparent windows on full-screen capture; may include cursor).
        public static byte[] CaptureScreenRect(int x, int y, int width, int height, bool includeLayeredWindows)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException($"Invalid capture rect: {width}x{height}");

            IntPtr screenDc = IntPtr.Zero;
            IntPtr memDc = IntPtr.Zero;
            IntPtr hbm = IntPtr.Zero;
            IntPtr oldBmp = IntPtr.Zero;
            try
            {
                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) throw new InvalidOperationException("GetDC(NULL) returned NULL.");

                memDc = CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed.");

                hbm = CreateCompatibleBitmap(screenDc, width, height);
                if (hbm == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleBitmap failed.");

                oldBmp = SelectObject(memDc, hbm);

                uint rop = SRCCOPY | (includeLayeredWindows ? CAPTUREBLT : 0u);
                if (!BitBlt(memDc, 0, 0, width, height, screenDc, x, y, rop))
                    throw new InvalidOperationException("BitBlt failed.");

                byte[] pixels = new byte[width * height * 4];
                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = width,
                        biHeight = -height, // negative = top-down DIB (we flip rows below for Unity)
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = BI_RGB,
                    }
                };
                int scanned = GetDIBits(memDc, hbm, 0, (uint)height, pixels, ref bmi, DIB_RGB_COLORS);
                if (scanned == 0) throw new InvalidOperationException("GetDIBits returned 0.");

                // Unity's Texture2D expects bottom-up row order (graphics API convention),
                // but our DIB is top-down. Flip rows in place.
                int stride = width * 4;
                byte[] tmp = new byte[stride];
                for (int row = 0; row < height / 2; row++)
                {
                    int top = row * stride;
                    int bot = (height - 1 - row) * stride;
                    System.Buffer.BlockCopy(pixels, top, tmp, 0, stride);
                    System.Buffer.BlockCopy(pixels, bot, pixels, top, stride);
                    System.Buffer.BlockCopy(tmp, 0, pixels, bot, stride);
                }
                return pixels;
            }
            finally
            {
                if (oldBmp != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, oldBmp);
                if (hbm != IntPtr.Zero) DeleteObject(hbm);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
#endif
