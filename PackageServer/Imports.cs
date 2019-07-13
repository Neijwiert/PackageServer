/*
Copyright 2019 Neijwiert

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.Runtime.InteropServices;

namespace PackageServer
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct config_t
    {
        public IntPtr root;
        public IntPtr destructor;
        public ushort flags;
        public ushort tab_width;
        public short default_format;
        public IntPtr include_dir;
        public IntPtr error_text;
        public IntPtr error_file;
        public int error_line;
        public int error_type;
        public IntPtr filenames;
        public uint num_filenames;
    }

    internal static class Imports
    {
        public const int CONFIG_TRUE = 1;
        public const int CONFIG_FALSE = 0;
        public const int CONFIG_TYPE_STRING = 5;

        [DllImport("libconfig.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void config_init(IntPtr config);

        [DllImport("libconfig.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void config_destroy(IntPtr config);

        [DllImport("libconfig.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int config_read_file(IntPtr config, [MarshalAs(UnmanagedType.LPStr)] string filename);

        [DllImport("libconfig.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int config_write_file(IntPtr config, [MarshalAs(UnmanagedType.LPStr)] string filename);

        [DllImport("libconfig.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr config_setting_get_member(IntPtr setting, [MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport("libconfig.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr config_setting_add(IntPtr parent, [MarshalAs(UnmanagedType.LPStr)] string name, int type);

        [DllImport("libconfig.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int config_setting_set_string(IntPtr setting, [MarshalAs(UnmanagedType.LPStr)] string value);
    }
}
