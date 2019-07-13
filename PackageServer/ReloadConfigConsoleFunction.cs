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

namespace PackageServer
{
    [RenSharpIgnore]
    internal sealed class ReloadConfigConsoleFunction : RenSharpConsoleFunctionClass
    {
        public delegate void ConfigReloadedDelegate();
        public event ConfigReloadedDelegate ConfigReloaded;

        private static ReloadConfigConsoleFunction instance;

        private bool disposedResources;
        private IntPtr originalPointer;
        private bool originalIsManagedFunction;

        private ReloadConfigConsoleFunction(IConsoleFunctionClass originalCommand)
            : base(originalCommand.Name, originalCommand.Help)
        {
            disposedResources = false;
            originalPointer = originalCommand.ConsoleFunctionClassPointer;
            originalIsManagedFunction = originalCommand.IsManagedConsoleFunction;

            OriginalFunction = originalCommand;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposedResources)
            {
                return;
            }

            if (disposing)
            {
                // Only re-add the original back when it was an unmanaged function or if it still exists as a managed function
                if (!originalIsManagedFunction || Engine.IsManagedConsoleFunction(originalPointer))
                {
                    Engine.ConsoleFunctionList.Add(OriginalFunction);

                    Engine.SortFunctionList();
                    Engine.VerboseHelpFile();
                }
            }

            disposedResources = true;

            base.Dispose();
        }

        public static ReloadConfigConsoleFunction Init()
        {
            if (instance != null)
            {
                throw new InvalidOperationException();
            }

            // We have to replace the original function with our custom one
            // So we first need to find it in the console function list
            var consoleFunctionList = Engine.ConsoleFunctionList;
            for (int x = 0; x < consoleFunctionList.Count; x++)
            {
                IConsoleFunctionClass currentConsoleFunction = ((IVectorClass<IConsoleFunctionClass>)consoleFunctionList)[x];

                // Check if the current console function is the reloadconfig function
                if (currentConsoleFunction.Name.Equals("reloadconfig", StringComparison.InvariantCultureIgnoreCase))
                {
                    // Ok, instantiate replacement
                    instance = new ReloadConfigConsoleFunction(currentConsoleFunction);
                    instance.AttachToUnmanagedObject();
                    instance.RegisterManagedObject();

                    // It is ok to replace the function now
                    ((IVectorClass<IConsoleFunctionClass>)consoleFunctionList)[x] = instance.AsUnmanagedObject();

                    return instance;
                }
            }

            throw new InvalidOperationException("Console functions not loaded");
        }

        public override void Activate(string pArgs)
        {
            ReloadConfig(true);
        }

        public void ReloadConfig(bool triggerEvent)
        {
            // Activate original
            OriginalFunction.Activate(string.Empty);

            if (triggerEvent)
            {
                ConfigReloaded?.Invoke();
            }
        }

        public IConsoleFunctionClass OriginalFunction
        {
            get;
            private set;
        }

        public static ReloadConfigConsoleFunction Instance
        {
            get
            {
                return instance;
            }
        }
    }
}
