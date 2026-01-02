using Fish_DeObfuscator.core.Utils;
using Fish.Shared;

namespace Fish_DeObfuscator
{
    internal class Program
    {
        static int Main(string[] args)
        {
            ProgramBase.Initialize("Fish DeObfuscator");

            if (args.Length == 0 || ProgramBase.HasHelpFlag(args))
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            var options = Options.Parse(args);
            if (options == null)
                return 1;

            return ProgramBase.Run(new Context(options)) ? 0 : 1;
        }

        private static void PrintUsage()
        {
            ProgramBase.PrintUsage(
                "Fish DeObfuscator",
                "Fish_DeObfuscator.exe",
                new[] { "full      General deobfuscation", "armdot    ArmDot deobfuscation", "costura   Extract Costura resources", "rika      Rika.NET math deobfuscation" },
                new[] { "Fish_DeObfuscator.exe MyApp.dll", "Fish_DeObfuscator.exe MyApp.dll -p armdot", "Fish_DeObfuscator.exe MyApp.dll -p costura -o Cleaned.dll", "Fish_DeObfuscator.exe MyApp.dll -p rika" }
            );
        }
    }
}
