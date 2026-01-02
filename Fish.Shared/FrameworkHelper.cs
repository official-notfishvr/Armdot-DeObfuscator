using System;
using System.Linq;
using dnlib.DotNet;

namespace Fish.Shared
{
    // made by chatgpt :sob and idk if even works
    public static class FrameworkHelper
    {
        public static IResolutionScope GetSystemAssemblyRef(ModuleDefMD module)
        {
            var systemRef = module.GetAssemblyRefs().FirstOrDefault(a => a.Name == "System.Runtime" || a.Name == "System" || a.Name == "netstandard");

            if (systemRef != null)
                return systemRef;

            return module.CorLibTypes.AssemblyRef;
        }

        public static IResolutionScope GetComponentModelAssemblyRef(ModuleDefMD module)
        {
            var componentModelRef = module.GetAssemblyRefs().FirstOrDefault(a => a.Name == "System.ComponentModel.Primitives" || a.Name == "System.ComponentModel" || a.Name == "System");

            if (componentModelRef != null)
                return componentModelRef;

            return GetOrCreateSystemAssemblyRef(module);
        }

        public static IResolutionScope GetOrCreateSystemAssemblyRef(ModuleDefMD module)
        {
            if (IsNetCoreOrNewer(module))
            {
                var systemRuntime = module.GetAssemblyRefs().FirstOrDefault(a => a.Name == "System.Runtime");

                if (systemRuntime != null)
                    return systemRuntime;

                return new AssemblyRefUser("System.Runtime");
            }
            else
            {
                var systemRef = module.GetAssemblyRefs().FirstOrDefault(a => a.Name == "System");

                if (systemRef != null)
                    return systemRef;

                return new AssemblyRefUser("System", new Version(4, 0, 0, 0), new PublicKeyToken("b77a5c561934e089"));
            }
        }

        public static bool IsNetCoreOrNewer(ModuleDefMD module)
        {
            var corLibName = module.CorLibTypes.AssemblyRef.Name;

            if (corLibName == "System.Private.CoreLib" || corLibName == "System.Runtime")
                return true;

            if (module.GetAssemblyRefs().Any(a => a.Name == "netstandard"))
                return true;

            if (module.GetAssemblyRefs().Any(a => a.Name == "System.Runtime"))
                return true;

            return false;
        }

        public static bool IsNetFramework(ModuleDefMD module)
        {
            var corLibName = module.CorLibTypes.AssemblyRef.Name;
            return corLibName == "mscorlib";
        }

        public static TypeRef GetTypeRef(ModuleDefMD module, string ns, string name, TypeRefLocation location)
        {
            IResolutionScope scope = location switch
            {
                TypeRefLocation.CorLib => module.CorLibTypes.AssemblyRef,
                TypeRefLocation.System => GetOrCreateSystemAssemblyRef(module),
                TypeRefLocation.ComponentModel => GetComponentModelAssemblyRef(module),
                _ => module.CorLibTypes.AssemblyRef,
            };

            return new TypeRefUser(module, ns, name, scope);
        }

        public static TypeRef CreateCrossFrameworkTypeRef(ModuleDefMD module, string ns, string typeName)
        {
            foreach (var typeRef in module.GetTypeRefs())
            {
                if (typeRef.Namespace == ns && typeRef.Name == typeName)
                    return typeRef as TypeRef;
            }

            IResolutionScope scope;

            if (ns.StartsWith("System.ComponentModel"))
                scope = GetComponentModelAssemblyRef(module);
            else if (ns.StartsWith("System.Diagnostics"))
                scope = module.CorLibTypes.AssemblyRef;
            else if (ns.StartsWith("System.Runtime.CompilerServices"))
                scope = module.CorLibTypes.AssemblyRef;
            else if (ns == "System")
                scope = module.CorLibTypes.AssemblyRef;
            else
                scope = GetOrCreateSystemAssemblyRef(module);

            return new TypeRefUser(module, ns, typeName, scope);
        }

        public static FrameworkInfo GetFrameworkInfo(ModuleDefMD module)
        {
            var info = new FrameworkInfo();

            var corLibName = module.CorLibTypes.AssemblyRef.Name;
            var corLibVersion = module.CorLibTypes.AssemblyRef.Version;

            if (corLibName == "System.Private.CoreLib" || corLibName == "System.Runtime")
            {
                info.FrameworkType = FrameworkType.NetCore;
                info.Version = corLibVersion;
            }
            else if (module.GetAssemblyRefs().Any(a => a.Name == "netstandard"))
            {
                info.FrameworkType = FrameworkType.NetStandard;
                var netstdRef = module.GetAssemblyRefs().First(a => a.Name == "netstandard");
                info.Version = netstdRef.Version;
            }
            else if (corLibName == "mscorlib")
            {
                info.FrameworkType = FrameworkType.NetFramework;
                info.Version = corLibVersion;
            }
            else
            {
                info.FrameworkType = FrameworkType.Unknown;
                info.Version = corLibVersion;
            }

            return info;
        }
    }

    public enum TypeRefLocation
    {
        CorLib,
        System,
        ComponentModel,
    }

    public enum FrameworkType
    {
        Unknown,
        NetFramework,
        NetCore,
        NetStandard,
    }

    public class FrameworkInfo
    {
        public FrameworkType FrameworkType { get; set; }
        public Version Version { get; set; }

        public override string ToString()
        {
            return $"{FrameworkType} {Version}";
        }
    }
}
