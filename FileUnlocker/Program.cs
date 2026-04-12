using System;

namespace FileUnlocker
{
    class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string path = args[0];
            bool silent = args.Length > 1 && args[1].EqualsAny(StringComparison.OrdinalIgnoreCase, "-silent", "-s") || args.Length > 2 && args[2].EqualsAny(StringComparison.OrdinalIgnoreCase, "-silent", "-s");
            bool console = args.Length > 1 && args[1].EqualsAny(StringComparison.OrdinalIgnoreCase, "-console", "-c") || args.Length > 2 && args[2].EqualsAny(StringComparison.OrdinalIgnoreCase, "-console", "-c");

            return FileUnlocker.Unlock(path, silent, console);
        }
    }
}
