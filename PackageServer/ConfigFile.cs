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
using System.IO;
using System.Runtime.InteropServices;

namespace PackageServer
{
    internal sealed class ConfigFile : IDisposable
    {
        public class ConfigSetting
        {
            public ConfigSetting(IntPtr settingPointer)
            {
                if (settingPointer == IntPtr.Zero)
                {
                    throw new ArgumentNullException(nameof(settingPointer));
                }

                SettingPointer = settingPointer;
            }

            public IntPtr SettingPointer
            {
                get;
                private set;
            }
        }

        private bool disposedResources;
        private IntPtr configPointer;

        public ConfigFile()
        {
            disposedResources = false;
            configPointer = Marshal.AllocHGlobal(Marshal.SizeOf<config_t>());

            try
            {
                Imports.config_init(configPointer);
            }
            catch (Exception)
            {
                Marshal.FreeHGlobal(configPointer);

                throw;
            }
        }

        ~ConfigFile()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposedResources)
            {
                return;
            }

            Imports.config_destroy(configPointer);
            Marshal.FreeHGlobal(configPointer);
            configPointer = IntPtr.Zero;

            disposedResources = true;
        }

        public void ReadFile(string filename)
        {
            if (filename == null)
            {
                throw new ArgumentNullException(nameof(filename));
            }

            if (Imports.config_read_file(configPointer, filename) == Imports.CONFIG_TRUE)
            {
                return;
            }

            ThrowException();
        }

        public void WriteFile(string filename)
        {
            if (filename == null)
            {
                throw new ArgumentNullException(nameof(filename));
            }

            if (Imports.config_write_file(configPointer, filename) == Imports.CONFIG_TRUE)
            {
                return;
            }

            ThrowException();
        }

        public ConfigSetting GetSettingMember(ConfigSetting setting, string name)
        {
            if (setting == null)
            {
                throw new ArgumentNullException(nameof(setting));
            }
            else if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            IntPtr configSettingPointer = Imports.config_setting_get_member(setting.SettingPointer, name);
            if (configSettingPointer == IntPtr.Zero)
            {
                return null;
            }
            else
            {
                return new ConfigSetting(configSettingPointer);
            }
        }

        public ConfigSetting AddSetting(ConfigSetting parent, string name, int type)
        {
            if (parent == null)
            {
                throw new ArgumentNullException(nameof(parent));
            }
            else if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            IntPtr configSettingPointer = Imports.config_setting_add(parent.SettingPointer, name, type);
            if (configSettingPointer == IntPtr.Zero)
            {
                return null;
            }
            else
            {
                return new ConfigSetting(configSettingPointer);
            }
        }

        public ConfigSetting AddStringSetting(ConfigSetting parent, string name)
        {
            return AddSetting(parent, name, Imports.CONFIG_TYPE_STRING);
        }

        public void SetSetting(ConfigSetting setting, string value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (Imports.config_setting_set_string(setting.SettingPointer, value) == Imports.CONFIG_TRUE)
            {
                return;
            }

            ThrowException();
        }

        public ConfigSetting RootSetting
        {
            get
            {
                config_t config = Marshal.PtrToStructure<config_t>(configPointer);
                if (config.root == IntPtr.Zero)
                {
                    return null;
                }
                else
                {
                    return new ConfigSetting(config.root);
                }
            }
        }

        private void ThrowException()
        {
            config_t config = Marshal.PtrToStructure<config_t>(configPointer);

            string errorText = Marshal.PtrToStringAnsi(config.error_text);

            throw new IOException($"{config.error_type}: {errorText}");
        }
    }
}
