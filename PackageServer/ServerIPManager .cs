using RenSharp;
using System;
using System.Collections.Generic;
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

using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PackageServer
{
    [RenSharpIgnore]
    internal sealed class ServerIPManager : RenSharpEventClass
    {
        public delegate void IPAddressChangedDelegate(ServerIPManager manager, IPAddress oldAddress, IPAddress newAddress);
        public event IPAddressChangedDelegate IPAddressChanged;

        private static readonly int FetchIPTimerNumber = 23489723;

        private static readonly int DefaultIPFetchSecondsInterval = 5;

        private static ServerIPManager instance;

        private bool disposedResources;
        private bool isDisposing;
        private readonly bool allowIPv4;
        private readonly bool allowIPv6;
        private readonly WebClient webClient;
        private volatile int downloadsPending;
        private readonly object stateObject;
        private int currentFetchURLIndex;

        private ServerIPManager(bool allowIPv4, bool allowIPv6)
        {
            this.disposedResources = false;
            this.isDisposing = false;
            this.allowIPv4 = allowIPv4;
            this.allowIPv6 = allowIPv6;
            this.webClient = new WebClient();
            this.webClient.DownloadStringCompleted += DownloadStringCompleted;
            this.downloadsPending = 0;
            this.stateObject = new object();
            this.currentFetchURLIndex = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposedResources)
            {
                return;
            }

            if (disposing)
            {
                webClient.CancelAsync();

                lock (stateObject)
                {
                    isDisposing = true;

                    while (downloadsPending > 0)
                    {
                        Monitor.Wait(stateObject);
                    }
                }

                webClient.Dispose();
            }

            disposedResources = true;

            base.Dispose(disposing);
        }


        public static ServerIPManager Init(bool allowIPv4, bool allowIPv6)
        {
            if (instance != null)
            {
                throw new InvalidOperationException();
            }

            instance = new ServerIPManager(allowIPv4, allowIPv6);
            instance.AttachToUnmanagedObject();
            instance.RegisterManagedObject();

            return instance;
        }

        public override void UnmanagedAttach()
        {
            RegisterEvent(DAEventType.SettingsLoaded);
        }

        public override void SettingsLoadedEvent()
        {
            lock (stateObject)
            {
                StopTimer(FetchIPTimerNumber);
                currentFetchURLIndex = 0;

                IDASettingsClass settings = DASettingsManager.GetSettings(PackageServerSettingsManager.SettingsFilename);
                if (settings == null)
                {
                    return;
                }

                IPFetchURLs = new Uri[0];

                IINISection ipFetchURLsSection = settings.GetSection(nameof(IPFetchURLs));
                if (ipFetchURLsSection != null)
                {
                    ICollection<Uri> urls = new LinkedList<Uri>();
                    foreach (IINIEntry entry in ipFetchURLsSection.EntryList)
                    {
                        string currentValue = entry.Value;
                        if (currentValue != null && Uri.TryCreate(currentValue.Trim(), UriKind.Absolute, out Uri currentIPFetchURL))
                        {
                            urls.Add(currentIPFetchURL);
                        }
                    }

                    IPFetchURLs = new Uri[urls.Count];
                    urls.CopyTo(IPFetchURLs, 0);
                }

                if (IPFetchURLs.Length <= 0)
                {
                    Engine.ConsoleOutput($"[{nameof(PackageServer)}]: No valid IP fetch URLs defined\n");
                }

                IPFetchSecondsInterval = settings.GetInt(nameof(IPFetchSecondsInterval), DefaultIPFetchSecondsInterval);
                if (IPFetchSecondsInterval <= 0)
                {
                    Engine.ConsoleOutput($"[{nameof(PackageServer)}]: No valid IP fetch interval defined, defaulting to {DefaultIPFetchSecondsInterval} second(s)\n");

                    IPFetchSecondsInterval = DefaultIPFetchSecondsInterval;
                }

                if (LastIPAddress == null)
                {
                    FetchIPAsync();
                }

                if (IPFetchURLs.Length > 0)
                {
                    StartTimer(FetchIPTimerNumber, TimeSpan.FromSeconds(IPFetchSecondsInterval), true);
                }
            }
        }

        public override void TimerExpired(int number, object data)
        {
            if (number == FetchIPTimerNumber)
            {
                FetchIPAsync();
            }
        }

        private void DownloadStringCompleted(object sender, DownloadStringCompletedEventArgs args)
        {
            lock (stateObject)
            {
                try
                {
                    if (args.Cancelled || isDisposing) // Must've been a shutdown
                    {
                        return;
                    }

                    if (args.Error != null || 
                        !IPAddress.TryParse(args.Result.Trim(), out IPAddress currentIPAddress) ||
                        (allowIPv4 && currentIPAddress.AddressFamily != AddressFamily.InterNetwork) ||
                        (allowIPv6 && currentIPAddress.AddressFamily != AddressFamily.InterNetworkV6))
                    {
                        // Something went wrong, try a different URL
                        if (IPFetchURLs.Length > 1) // If we only have one URL, we'll try next time
                        {
                            IncreaseFetchURLIndex();
                            FetchIPAsync();
                        }

                        return;       
                    }

                    // Everything ok
                    if (!currentIPAddress.Equals(LastIPAddress)) // Check if we have a new IP address
                    {
                        IPAddress oldIPAddress = LastIPAddress;
                        IPAddress newIPAddress = currentIPAddress;

                        LastIPAddress = currentIPAddress;

                        Engine.Dispatcher.InvokeAsync(() =>
                        {
                            IPAddressChanged?.Invoke(this, oldIPAddress, newIPAddress);
                        });
                    }
                }
                finally
                {
                    downloadsPending--;

                    Monitor.PulseAll(stateObject);
                }
            }
        }

        private void IncreaseFetchURLIndex()
        {
            lock (stateObject)
            {
                currentFetchURLIndex++;
                if (currentFetchURLIndex >= IPFetchURLs.Length)
                {
                    currentFetchURLIndex = 0;
                }
            }
        }

        private void FetchIPAsync()
        {
            lock (stateObject)
            {
                if (isDisposing || currentFetchURLIndex >= IPFetchURLs.Length)
                {
                    return;
                }

                try
                {
                    webClient.DownloadStringAsync(IPFetchURLs[currentFetchURLIndex]);

                    downloadsPending++;
                }
                catch (WebException)
                {
                    if (IPFetchURLs.Length > 1) // If we only have one URL, we'll try next time
                    {
                        IncreaseFetchURLIndex();
                        FetchIPAsync();
                    }
                }
            }
        }

        public static ServerIPManager Instance
        {
            get
            {
                return instance;
            }
        }

        public IPAddress LastIPAddress
        {
            get;
            private set;
        }

        private Uri[] IPFetchURLs
        {
            get;
            set;
        }

        private int IPFetchSecondsInterval
        {
            get;
            set;
        }
    }
}
