using System;
using System.Collections.Generic;
using System.IO;

namespace Fish.Shared
{
    public class CommandLineArgs
    {
        public string FilePath { get; set; }
        public string Preset { get; set; } = "full";
        public string Output { get; set; }

        public string AssemblyPath => FilePath;
        public string AssemblyName { get; set; }
        public string AssemblyExtension { get; set; }
        public string AssemblyDirectory { get; set; }
        public string AssemblyOutput { get; set; }

        public static CommandLineArgs Parse(string[] args, string outputSuffix)
        {
            if (args == null || args.Length == 0)
                return null;

            var result = new CommandLineArgs();
            string filePath = null;
            string preset = "full";
            string output = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                if (arg == "-p" || arg == "--preset")
                {
                    if (i + 1 < args.Length)
                    {
                        preset = args[++i].ToLower();
                    }
                    else
                    {
                        Logger.Error("Missing value for --preset");
                        return null;
                    }
                }
                else if (arg == "-o" || arg == "--output")
                {
                    if (i + 1 < args.Length)
                    {
                        output = args[++i];
                    }
                    else
                    {
                        Logger.Error("Missing value for --output");
                        return null;
                    }
                }
                else if (!arg.StartsWith("-"))
                {
                    if (filePath == null)
                        filePath = arg;
                    else
                        preset = arg.ToLower();
                }
            }

            if (string.IsNullOrEmpty(filePath))
            {
                Logger.Error("No input file specified");
                return null;
            }

            if (!File.Exists(filePath))
            {
                Logger.Error($"File not found: {filePath}");
                return null;
            }

            result.FilePath = Path.GetFullPath(filePath);
            result.Preset = preset;
            result.AssemblyName = Path.GetFileNameWithoutExtension(filePath);
            result.AssemblyExtension = Path.GetExtension(filePath);
            result.AssemblyDirectory = Path.GetDirectoryName(result.FilePath);

            if (!string.IsNullOrEmpty(output))
            {
                result.Output = output;
                result.AssemblyOutput = Path.IsPathRooted(output) ? output : Path.Combine(result.AssemblyDirectory, output);
            }
            else
            {
                result.AssemblyOutput = Path.Combine(result.AssemblyDirectory, $"{result.AssemblyName}{outputSuffix}{result.AssemblyExtension}");
            }

            return result;
        }
    }
}
