/*
 * Author: Misaka Mikoto
 * email: easy66@live.com
 * github: https://github.com/Misaka-Mikoto-Tech/UnityScriptHotReload
 */
using Mono.Cecil;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ScriptHotReload
{
    public class HotReloadAssemblyResolver : IAssemblyResolver
    {
        public string baseDir { get; private set; }
        private bool disposedValue;

        public HotReloadAssemblyResolver(string baseDir)
        {
            this.baseDir = baseDir;
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            var assm = AssemblyDefinition.ReadAssembly($"{baseDir}/{name.Name}.dll");
            return assm;
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            var assm = AssemblyDefinition.ReadAssembly($"{baseDir}/{name.Name}.dll", parameters);
            return assm;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~HotReloadAssemblyResolver()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }

}
