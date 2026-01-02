using System;
using System.Collections.Generic;
using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Writer;
using Fish_DeObfuscator.core.DeObfuscation.DotNet.Armdot;
using Fish_DeObfuscator.core.Utils;
using Fish.Shared;

namespace Fish_DeObfuscator.UI
{
    public class UIOptions : IOptions
    {
        public string AssemblyPath { get; set; }
        public string AssemblyName { get; set; }
        public string AssemblyExtension { get; set; }
        public string AssemblyOutput { get; set; }
        public string AssemblyDirectory { get; set; }
        public List<IStage> Stages { get; private set; } = new List<IStage>();

        public UIOptions(string filePath, List<string> selectedStages)
        {
            AssemblyPath = Path.GetFullPath(filePath);
            AssemblyName = Path.GetFileNameWithoutExtension(filePath);
            AssemblyExtension = Path.GetExtension(filePath);
            AssemblyDirectory = Path.GetDirectoryName(filePath);
            AssemblyOutput = Path.Combine(AssemblyDirectory, $"{AssemblyName}_deobf{AssemblyExtension}");

            foreach (var stage in selectedStages)
            {
                switch (stage.ToLower())
                {
                    case "string":
                        Stages.Add(new Fish_DeObfuscator.core.DeObfuscation.DotNet.Armdot.String());
                        break;
                    case "virtualization":
                        Stages.Add(new Virtualization());
                        break;
                    case "calli":
                        Stages.Add(new Calli());
                        break;
                    case "controlflow":
                        Stages.Add(new ControlFlow());
                        break;
                    case "localcleaner":
                        Stages.Add(new LocalCleaner());
                        break;
                }
            }
        }
    }

    public class UIContext : IContext
    {
        private ModuleDefMD _moduleDefinition;
        private readonly Action<string> _logger;

        public UIContext(IOptions options, Action<string> logger)
        {
            Options = options;
            _logger = logger;
        }

        public IOptions Options { get; }

        public ModuleDefMD ModuleDefinition
        {
            get => _moduleDefinition;
            set => _moduleDefinition = value;
        }

        public bool IsInitialized()
        {
            if (string.IsNullOrEmpty(Options.AssemblyPath))
                return false;

            try
            {
                _logger?.Invoke($"Loading assembly: {Options.AssemblyPath}");
                _moduleDefinition = ModuleDefMD.Load(Options.AssemblyPath);
                _logger?.Invoke($"Loaded assembly with {_moduleDefinition.Types.Count} types");
                return _moduleDefinition != null;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error loading assembly: {ex.Message}");
                return false;
            }
        }

        public void SaveContext(bool log = true)
        {
            if (_moduleDefinition == null)
                throw new InvalidOperationException("Module definition is null.");

            if (log) _logger?.Invoke($"Saving to: {Path.GetFileName(Options.AssemblyOutput)}");

            if (log) _logger?.Invoke("Fixing method bodies...");
            int fixedMethods = MethodBodyFixer.FixAllMethods(_moduleDefinition);
            if (log && fixedMethods > 0)
                _logger?.Invoke($"Fixed {fixedMethods} method(s)");

            var opts = new ModuleWriterOptions(_moduleDefinition);
            opts.MetadataOptions.Flags |= MetadataFlags.KeepOldMaxStack;
            opts.Logger = DummyLogger.NoThrowInstance;

            _moduleDefinition.Write(Options.AssemblyOutput, opts);

            if (log) _logger?.Invoke("Assembly saved successfully!");
        }
    }
}
