using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace TSDraw
{
    // Monitors in DXGI adapter/output order. WebRTC's DirectX screen capturer (which TeamSpeak
    // streams with) numbers screens this way, so "screen 1" in a TeamSpeak session is Screens()[1].
    static class Dxgi
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumFn(IntPtr self, uint index, out IntPtr item);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OutputDescFn(IntPtr self, out OUTPUT_DESC desc);

        static T Method<T>(IntPtr com, int slot) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(com);
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size), typeof(T)) as T;
        }

        public class Screen { public string Device; public Rectangle Bounds; }

        public static List<Screen> Screens()
        {
            var list = new List<Screen>();
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
            IntPtr factory;
            if (CreateDXGIFactory1(ref iid, out factory) != 0) return list;
            try
            {
                var enumAdapters = Method<EnumFn>(factory, 12);           // IDXGIFactory1::EnumAdapters1
                for (uint a = 0; ; a++)
                {
                    IntPtr adapter;
                    if (enumAdapters(factory, a, out adapter) != 0) break;
                    try
                    {
                        var enumOutputs = Method<EnumFn>(adapter, 7);     // IDXGIAdapter::EnumOutputs
                        for (uint o = 0; ; o++)
                        {
                            IntPtr output;
                            if (enumOutputs(adapter, o, out output) != 0) break;
                            try
                            {
                                OUTPUT_DESC d;
                                if (Method<OutputDescFn>(output, 7)(output, out d) == 0 && d.AttachedToDesktop != 0)
                                    list.Add(new Screen { Device = d.DeviceName, Bounds = Rectangle.FromLTRB(d.Left, d.Top, d.Right, d.Bottom) });
                            }
                            finally { Marshal.Release(output); }
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
            return list;
        }
    }
}
