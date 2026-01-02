using System;
using System.IO;
using dnlib.DotNet;

namespace Fish.Shared
{
    public static class ProgramBase
    {
        public static readonly string[] UnityMethods = new[]
        {
            "Start",
            "Update",
            "LateUpdate",
            "FixedUpdate",
            "Awake",
            "OnEnable",
            "OnDisable",
            "OnDestroy",
            "OnGUI",
            "OnCollisionEnter",
            "OnCollisionExit",
            "OnCollisionStay",
            "OnTriggerEnter",
            "OnTriggerExit",
            "OnTriggerStay",
            "OnMouseDown",
            "OnMouseUp",
            "OnMouseEnter",
            "OnMouseExit",
            "OnMouseOver",
            "OnMouseDrag",
            "OnBecameVisible",
            "OnBecameInvisible",
            "OnPreRender",
            "OnPostRender",
            "OnRenderObject",
            "OnWillRenderObject",
            "OnDrawGizmos",
            "OnDrawGizmosSelected",
            "OnApplicationFocus",
            "OnApplicationPause",
            "OnApplicationQuit",
            "Prefix",
            "Postfix",
            "Transpiler",
            "Finalizer",
            "Main",
            "DllMain",
            "WinMain",
            "_Main",
            "EntryPoint",
            "Initialize",
            "InitializeModule",
        };

        public static void Initialize(string title)
        {
            Console.Title = title;
            Utils.UnitySpecialMethods = UnityMethods;
        }

        public static bool HasHelpFlag(string[] args)
        {
            foreach (var arg in args)
                if (arg == "-h" || arg == "--help" || arg == "/?")
                    return true;
            return false;
        }

        public static bool Run(IContext context)
        {
            if (context == null)
                return false;

            try
            {
                Logger.Info($"Loading: {Path.GetFileName(context.Options.AssemblyPath)}");

                if (!context.IsInitialized())
                {
                    Logger.Error("Failed to load assembly");
                    return false;
                }

                LogAssemblyInfo(context.ModuleDefinition);

                if (context.Options.Stages.Count == 0)
                {
                    Logger.Warning("No stages configured");
                    context.ModuleDefinition?.Dispose();
                    return false;
                }

                Logger.Info($"Running {context.Options.Stages.Count} stage(s)...");
                Logger.Line();

                foreach (var stage in context.Options.Stages)
                {
                    Logger.Stage(stage.GetType().Name);
                    try
                    {
                        stage.Execute(context);
                    }
                    catch (Exception ex)
                    {
                        Logger.StageError(ex.Message);
                    }
                }

                Logger.Line();
                context.SaveContext();
                Logger.Success($"Saved: {context.Options.AssemblyOutput}");

                context.ModuleDefinition?.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message);
                return false;
            }
        }

        private static void LogAssemblyInfo(ModuleDefMD module)
        {
            if (module == null)
                return;

            try
            {
                Logger.Info($"Assembly: {module.Assembly?.Name ?? "Unknown"}");
                Logger.Info($"Types: {module.Types.Count}");
                if (module.EntryPoint != null)
                    Logger.Info($"Entry: {module.EntryPoint.DeclaringType?.Name}.{module.EntryPoint.Name}");
                Logger.Line();
            }
            catch { }
        }

        public static void PrintUsage(string title, string exeName, string[] presets, string[] examples)
        {
            Console.WriteLine();
            Console.WriteLine($"  {title}");
            Console.WriteLine($"  {new string('=', title.Length)}");
            Console.WriteLine();
            Console.WriteLine("  Usage:");
            Console.WriteLine($"    {exeName} <file> [options]");
            Console.WriteLine();
            Console.WriteLine("  Options:");
            Console.WriteLine("    -p, --preset <name>  Preset to use (default: full)");
            Console.WriteLine("    -o, --output <path>  Output file path");
            Console.WriteLine("    -h, --help           Show this help");
            Console.WriteLine();
            Console.WriteLine("  Presets:");
            foreach (var preset in presets)
                Console.WriteLine($"    {preset}");
            Console.WriteLine();
            Console.WriteLine("  Examples:");
            foreach (var example in examples)
                Console.WriteLine($"    {example}");
            Console.WriteLine();
        }
    }
}
