using System;
using System.Runtime.InteropServices;

namespace Notchly
{
    /// <summary>CPU/RAM readings plus a few Win32 helpers (volume keys, lock, snip, fullscreen detection).</summary>
    public static class SystemStats
    {
        // ----- CPU -----
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
        static ulong _pIdle, _pKernel, _pUser;
        static bool _cpuInit;

        public static double CpuPercent()
        {
            if (!GetSystemTimes(out var i, out var k, out var u)) return 0;
            if (!_cpuInit) { _pIdle = i; _pKernel = k; _pUser = u; _cpuInit = true; return 0; }
            ulong di = i - _pIdle, dk = k - _pKernel, du = u - _pUser;
            _pIdle = i; _pKernel = k; _pUser = u;
            ulong total = dk + du; // kernel time already includes idle time
            return total == 0 ? 0 : Math.Clamp((1.0 - (double)di / total) * 100, 0, 100);
        }

        // ----- RAM -----
        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX s);

        public static double MemoryPercent()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref m) ? m.dwMemoryLoad : 0;
        }

        // ----- keys / actions -----
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern bool LockWorkStation();
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

        const uint KeyUp = 2;
        static void Tap(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, KeyUp, UIntPtr.Zero); }

        public static void VolumeUp() => Tap(0xAF);
        public static void VolumeDown() => Tap(0xAE);
        public static void VolumeMute() => Tap(0xAD);
        public static void Lock() => LockWorkStation();

        /// <summary>Sends Win+Shift+S (Windows screenshot tool).</summary>
        public static void Snip()
        {
            keybd_event(0x5B, 0, 0, UIntPtr.Zero);
            keybd_event(0x10, 0, 0, UIntPtr.Zero);
            Tap(0x53);
            keybd_event(0x10, 0, KeyUp, UIntPtr.Zero);
            keybd_event(0x5B, 0, KeyUp, UIntPtr.Zero);
        }

        /// <summary>True when a fullscreen app/game/presentation is in front.</summary>
        public static bool FullscreenAppActive() =>
            SHQueryUserNotificationState(out var s) == 0 && (s == 2 || s == 3 || s == 4);
    }
}
