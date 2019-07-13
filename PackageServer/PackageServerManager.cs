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

using RenSharp;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace PackageServer
{
    public sealed class PackageServerManager : RenSharpEventClass
    {
        private static readonly TimeSpan TTFileServerStopTimeout = TimeSpan.FromSeconds(20); // Should be enough
        private static readonly string TTConfigFilename = "tt";
        private static readonly string TTConfigFileExtension = ".cfg";
        private static readonly string TTConfigDownloaderSectionName = "downloader";
        private static readonly string TTConfigRepositoryUrlSettingName = "repositoryUrl";

        private static readonly string DefaultTTFSPath = Path.Combine(Engine.AppDataPath, "ttfs");
        private static readonly int DefaultMaxClientConnections = 10;
        private static readonly int DefaultClientTimeout = 60;
        private static readonly IPAddress DefaultLocalIPAddress = IPAddress.Any;

        private bool disposedResources = false;
        private bool initialized;
        private IPAddress serverIPAddress;
        private TTFileServer fileServer;
        private bool validConfig;

        public PackageServerManager()
        {
            disposedResources = false;
            initialized = false;
            validConfig = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposedResources)
            {
                return;
            }

            if (disposing)
            {
                StopTTFileServer();
            }

            disposedResources = true;

            base.Dispose(disposing);
        }


        public override void ManagedRegistered()
        {
            RegisterEvent(DAEventType.SettingsLoaded);
        }

        public override void SettingsLoadedEvent()
        {
            validConfig = false;

            if (!initialized)
            {
                initialized = true;

                ServerIPManager.Init(true, false).IPAddressChanged += IPAddressChanged;
                ReloadConfigConsoleFunction.Init().ConfigReloaded += ConfigReloaded;
            }

            IDASettingsClass settings = DASettingsManager.GetSettings(PackageServerSettingsManager.SettingsFilename);
            if (settings == null)
            {
                return;
            }

            string ttfsPath = settings.GetString(nameof(TTFSPath), DefaultTTFSPath);
            if (ttfsPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                Engine.ConsoleOutput($"[{nameof(PackageServer)}]: {nameof(TTFSPath)} '{ttfsPath}' is invalid, using default value\n");

                ttfsPath = DefaultTTFSPath;
            }

            int maxClientConnections = settings.GetInt(nameof(MaxClientConnections), DefaultMaxClientConnections);
            int clientTimeout = Math.Max(settings.GetInt(nameof(ClientTimeout), DefaultClientTimeout), 0);

            string localIPAddressString = settings.GetString(nameof(LocalIPAddress), DefaultLocalIPAddress.ToString());
            if (!IPAddress.TryParse(localIPAddressString, out IPAddress localIPAddress))
            {
                Engine.ConsoleOutput($"[{nameof(PackageServer)}]: {nameof(LocalIPAddress)} '{localIPAddressString}' is invalid, using default value\n");

                localIPAddress = DefaultLocalIPAddress;
            }

            int port = 0;

            string portString = settings.GetString(nameof(Port), null);
            if (
                string.IsNullOrWhiteSpace(portString) || 
                !int.TryParse(portString, out port) ||
                port < IPEndPoint.MinPort ||
                port > IPEndPoint.MaxPort)
            {
                Engine.ConsoleOutput($"[{nameof(PackageServer)}]: {nameof(Port)} '{portString}' is invalid, unable to start package server\n");
            }
            else
            {
                validConfig = true;
            }

            if (
                (!ttfsPath.Equals(TTFSPath, StringComparison.InvariantCultureIgnoreCase) ||
                !maxClientConnections.Equals(MaxClientConnections) ||
                !clientTimeout.Equals(ClientTimeout) ||
                !localIPAddress.Equals(LocalIPAddress) ||
                !port.Equals(Port)))
            {
                bool wasStarted = StopTTFileServer();

                TTFSPath = ttfsPath;
                MaxClientConnections = maxClientConnections;
                ClientTimeout = clientTimeout;
                LocalIPAddress = localIPAddress;
                Port = port;

                if (wasStarted)
                {
                    Engine.ConsoleOutput($"[{nameof(PackageServer)}]: New configuration detected\n");
                }

                RestartTTFileServer();
            }
        }

        private void IPAddressChanged(ServerIPManager manager, IPAddress oldAddress, IPAddress newAddress)
        {
            Engine.ConsoleOutput($"[{nameof(PackageServer)}]: External IP address changed to {newAddress}\n");

            serverIPAddress = newAddress;
            
            RestartTTFileServer();
        }

        private void ConfigReloaded()
        {
            Engine.ConsoleOutput($"[{nameof(PackageServer)}]: The tt.cfg file has been reloaded\n");

            RestartTTFileServer();
        }

        private bool StopTTFileServer()
        {
            if (fileServer != null)
            {
                Engine.ConsoleOutput($"[{nameof(PackageServer)}]: Stopping the package server...\n");

                fileServer.Stop();
                fileServer.Join(TTFileServerStopTimeout);
                fileServer.Dispose();
                fileServer = null;

                Engine.ConsoleOutput($"[{nameof(PackageServer)}]: Package server has been stopped\n");

                return true;
            }

            return false;
        }

        private void RestartTTFileServer()
        {
            StopTTFileServer();

            if (!validConfig)
            {
                return;
            }

            Engine.ConsoleOutput($"[{nameof(PackageServer)}]: Restaring the package server...\n");

            if (serverIPAddress == null) // We haven't got an IP address yet, wait until we got one
            {
                Engine.ConsoleOutput($"[{nameof(PackageServer)}]: No external IP address known, postponing restart\n");

                return;
            }

            if (ApplyRepositoryUrl())
            {
                try
                {
                    fileServer = new TTFileServer(TTFSPath, serverIPAddress, MaxClientConnections, TimeSpan.FromMinutes(ClientTimeout), LocalIPAddress, Port);
                    fileServer.Start();

                    Engine.ConsoleOutput($"[{nameof(PackageServer)}]: Package server has been started\n");
                }
                catch (SocketException ex)
                {
                    Engine.ConsoleOutput($"[{nameof(PackageServer)}]: Failed to start the package server '{ex.Message}'\n");

                    fileServer.Dispose();
                    fileServer = null;

                    ReloadConfigConsoleFunction.Instance.ReloadConfig(false); // Restore old config, if any
                }
            }
        }

        private bool ApplyRepositoryUrl()
        {
            bool succesfullyWroteToConfig = false;
            IOException exception = null;

            try
            {
                byte[] originalTTConfig = File.ReadAllBytes($"{TTConfigFilename}{TTConfigFileExtension}");

                string backupTTConfigFilename = $"{TTConfigFilename}_{Guid.NewGuid()}{TTConfigFileExtension}";
                using (FileStream backupTTConfigFile = new FileStream(backupTTConfigFilename, FileMode.Create))
                {
                    backupTTConfigFile.Write(originalTTConfig, 0, originalTTConfig.Length);
                }

                using (ConfigFile temporaryTTConfigFile = new ConfigFile())
                {
                    temporaryTTConfigFile.ReadFile($"{TTConfigFilename}{TTConfigFileExtension}");

                    var downloaderSetting = temporaryTTConfigFile.GetSettingMember(temporaryTTConfigFile.RootSetting, TTConfigDownloaderSectionName);
                    if (downloaderSetting == null)
                    {
                        downloaderSetting = temporaryTTConfigFile.AddStringSetting(temporaryTTConfigFile.RootSetting, TTConfigDownloaderSectionName);
                    }

                    if (downloaderSetting != null)
                    {
                        var repositoryUrlSetting = temporaryTTConfigFile.GetSettingMember(downloaderSetting, TTConfigRepositoryUrlSettingName);
                        if (repositoryUrlSetting == null)
                        {
                            repositoryUrlSetting = temporaryTTConfigFile.AddStringSetting(downloaderSetting, TTConfigRepositoryUrlSettingName);
                        }

                        if (repositoryUrlSetting != null)
                        {
                            temporaryTTConfigFile.SetSetting(repositoryUrlSetting, $"http://{serverIPAddress}:{Port}");
                            temporaryTTConfigFile.WriteFile($"{TTConfigFilename}{TTConfigFileExtension}");

                            succesfullyWroteToConfig = true;
                        }
                    }
                }

                ReloadConfigConsoleFunction.Instance.ReloadConfig(false);

                File.WriteAllBytes($"{TTConfigFilename}{TTConfigFileExtension}", originalTTConfig);
                File.Delete(backupTTConfigFilename);
            }
            catch (IOException ex)
            {
                exception = ex;
            }

            if (exception != null)
            {
                Engine.ConsoleOutput($"[{nameof(PackageServer)}]: Failed to set the package server repository URL '{exception.Message}'\n");

                return false;
            }
            else if (!succesfullyWroteToConfig)
            {
                Engine.ConsoleOutput($"[{nameof(PackageServer)}]: Failed to set the package server repository URL\n");

                return false;
            }

            return true;
        }

        private string TTFSPath
        {
            get;
            set;
        }

        private int MaxClientConnections
        {
            get;
            set;
        }

        private int ClientTimeout
        {
            get;
            set;
        }

        private IPAddress LocalIPAddress
        {
            get;
            set;
        }

        private int Port
        {
            get;
            set;
        }
    }
}
