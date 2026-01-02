using System;

namespace Fish.Shared
{
    public static class Logger
    {
        public static void Info(string message) => Console.WriteLine($"[*] {message}");

        public static void Success(string message) => Console.WriteLine($"[+] {message}");

        public static void Warning(string message) => Console.WriteLine($"[!] {message}");

        public static void Error(string message) => Console.WriteLine($"[!] {message}");

        public static void Stage(string name) => Console.WriteLine($"  > {name}");

        public static void StageError(string message) => Console.WriteLine($"    [!] {message}");

        public static void Detail(string message) => Console.WriteLine($"    {message}");

        public static void Line() => Console.WriteLine();
    }
}
