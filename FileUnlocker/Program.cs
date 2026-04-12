using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;

namespace FileUnlocker
{
    class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool noAdmin = args.Length > 1 && args[1].EqualsAny(StringComparison.OrdinalIgnoreCase, "-noadmin", "-na") || args.Length > 2 && args[2].EqualsAny(StringComparison.OrdinalIgnoreCase, "-noadmin", "-na") || args.Length > 3 && args[3].EqualsAny(StringComparison.OrdinalIgnoreCase, "-noadmin", "-na");

            if (!noAdmin)
            {
                RelaunchIfNotAdmin(args);
            }
            string path = args.Length > 0 ? args[0] : "";
            bool silent = args.Length > 1 && args[1].EqualsAny(StringComparison.OrdinalIgnoreCase, "-silent", "-s") || args.Length > 2 && args[2].EqualsAny(StringComparison.OrdinalIgnoreCase, "-silent", "-s") || args.Length > 3 && args[3].EqualsAny(StringComparison.OrdinalIgnoreCase, "-silent", "-s");
            bool console = args.Length > 1 && args[1].EqualsAny(StringComparison.OrdinalIgnoreCase, "-console", "-c") || args.Length > 2 && args[2].EqualsAny(StringComparison.OrdinalIgnoreCase, "-console", "-c") || args.Length > 3 && args[3].EqualsAny(StringComparison.OrdinalIgnoreCase, "-console", "-c");
            
            return FileUnlocker.Unlock(path, silent, console);
        }

        public static class CommandLineHelper
        {
            /// <summary>
            /// Escapes and joins multiple arguments into a single command-line string.
            /// </summary>
            public static string PrepareArguments(string[] args)
            {
                return string.Join(" ", args.Select(EscapeArgument));
            }

            /// <summary>
            /// Escapes a single argument according to the rules used by CommandLineToArgvW.
            /// </summary>
            public static string EscapeArgument(string arg)
            {
                if (string.IsNullOrEmpty(arg)) return "\"\"";

                // Only quote if necessary, but it's often safer to quote everything
                bool needsQuotes = arg.Any(c => char.IsWhiteSpace(c) || c == '"');
                if (!needsQuotes) return arg;

                var sb = new StringBuilder();
                sb.Append('"');

                for (int i = 0; i < arg.Length; i++)
                {
                    int backslashCount = 0;
                    while (i < arg.Length && arg[i] == '\\')
                    {
                        backslashCount++;
                        i++;
                    }

                    if (i == arg.Length)
                    {
                        // Rule: If we reach the end, double the backslashes to avoid 
                        // escaping the closing quote.
                        sb.Append('\\', backslashCount * 2);
                    }
                    else if (arg[i] == '"')
                    {
                        // Rule: For internal quotes, double the preceding backslashes 
                        // and add one more to escape the quote.
                        sb.Append('\\', backslashCount * 2 + 1);
                        sb.Append('"');
                    }
                    else
                    {
                        // Normal characters: just add the backslashes literally.
                        sb.Append('\\', backslashCount);
                        sb.Append(arg[i]);
                    }
                }

                sb.Append('"');
                return sb.ToString();
            }
        }


        public static void RelaunchIfNotAdmin(string[] args)
        {
            if (!RunningAsAdmin())
            {
                Console.Error.WriteLine("Running as admin required!");
                ProcessStartInfo proc = new ProcessStartInfo();
                proc.UseShellExecute = true;
                proc.WorkingDirectory = Environment.CurrentDirectory;
                proc.FileName = Assembly.GetEntryAssembly().CodeBase;
                proc.Verb = "runas";
                proc.Arguments = CommandLineHelper.PrepareArguments(args);
                try
                {
                    Process.Start(proc);
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("This program must be run as an administrator! \n\n" + ex.ToString());
                    Environment.Exit(0);
                }
            }
        }

        private static bool RunningAsAdmin()
        {
            WindowsIdentity id = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(id);

            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
