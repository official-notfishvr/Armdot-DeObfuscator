using System;
using System.Collections.Generic;
using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Writer;
using Fish_DeObfuscator.core.DeObfuscation.DotNet.Armdot;
using Fish.Shared;

namespace Fish_DeObfuscator.core.Utils
{
    public class Options : IOptions
    {
        public string AssemblyPath { get; set; }
        public string AssemblyName { get; set; }
        public string AssemblyExtension { get; set; }
        public string AssemblyOutput { get; set; }
        public string AssemblyDirectory { get; set; }
        public List<IStage> Stages { get; private set; } = new List<IStage>();

        private Options() { }

        public static Options Parse(string[] args)
        {
            var parsed = CommandLineArgs.Parse(args, "_deobf");
            if (parsed == null)
                return null;

            var options = new Options
            {
                AssemblyPath = parsed.AssemblyPath,
                AssemblyName = parsed.AssemblyName,
                AssemblyExtension = parsed.AssemblyExtension,
                AssemblyDirectory = parsed.AssemblyDirectory,
                AssemblyOutput = parsed.AssemblyOutput,
            };

            options.SetStages(parsed.Preset);
            return options;
        }

        private void SetStages(string preset)
        {
            switch (preset)
            {
                case "armdot":
                    Stages.Add(new Fish_DeObfuscator.core.DeObfuscation.DotNet.Armdot.String());
                    Stages.Add(new Fish_DeObfuscator.core.DeObfuscation.DotNet.Armdot.ControlFlow());
                    Stages.Add(new Calli()); // mostly virtualization does 99% of them
                    Stages.Add(new Virtualization { EnableVMStringDecoding = false }); // VM Strings will wipe ALL other code that the void had if it did have anything
                    Stages.Add(new LocalCleaner());
                    break;
                default:
                    Logger.Warning($"Unknown preset: {preset}");
                    break;
            }
        }
    }

    public class Context : IContext
    {
        private ModuleDefMD moduleDefinition;

        public Context(IOptions options) => Options = options;

        public bool IsInitialized()
        {
            if (string.IsNullOrEmpty(Options.AssemblyPath))
                return false;

            try
            {
                moduleDefinition = ModuleDefMD.Load(Options.AssemblyPath);
                return moduleDefinition != null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Load error: {ex.Message}");
                return false;
            }
        }

        public void SaveContext(bool log = true)
        {
            if (moduleDefinition == null)
                throw new InvalidOperationException("Module not loaded");

            try
            {
                int fixedMethods = MethodBodyFixer.FixAllMethods(moduleDefinition);
                if (log && fixedMethods > 0)
                    Logger.Info($"Fixed {fixedMethods} method(s)");

                var opts = new ModuleWriterOptions(moduleDefinition);
                opts.MetadataOptions.Flags |= MetadataFlags.KeepOldMaxStack;
                opts.Logger = DummyLogger.NoThrowInstance;

                moduleDefinition.Write(Options.AssemblyOutput, opts);
            }
            catch (Exception ex)
            {
                Logger.Error($"Save error: {ex.Message}");
                throw;
            }
        }

        public IOptions Options { get; }
        public ModuleDefMD ModuleDefinition
        {
            get => moduleDefinition;
            set => moduleDefinition = value;
        }
    }
}
