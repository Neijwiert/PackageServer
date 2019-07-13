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

namespace PackageServer
{
    public sealed class PackageServerSettingsManager : RenSharpEventClass
    {
        public static readonly string SettingsFilename = "PackageServer.ini";

        private bool disposedResources;

        public PackageServerSettingsManager()
        {
            disposedResources = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposedResources)
            {
                return;
            }

            if (disposing)
            {
                DASettingsManager.RemoveSettings(SettingsFilename);
            }

            disposedResources = true;

            base.Dispose(disposing);
        }

        public override void ManagedRegistered()
        {
            DASettingsManager.AddSettings(SettingsFilename);

            RegisterEvent(DAEventType.SettingsLoaded);
        }

        public override void SettingsLoadedEvent()
        {
            if (DASettingsManager.GetSettings(SettingsFilename) == null)
            {
                Engine.ConsoleOutput($"[{nameof(PackageServer)}]: Missing settings file '{SettingsFilename}'\n");
            }
        }
    }
}
