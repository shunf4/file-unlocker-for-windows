using System;
using System.Collections.Generic;
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
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

            string helpText = "FileUnlocker.exe [options] path1 [path2 [path3 [...]]]\n\n"
                + "Options:\n"
                + "  -silent   [-s]   Silent kill mode\n"
                + "  -console  [-c]   Console output mode\n"
                + "  -noadmin  [-na]  Skip admin relaunch\n"
                + "  -nohandlefullscan       [-nh]  Disable handle scanner\n"
                + "  -norestartmanagerdetect [-nr]  Disable Restart Manager\n"
                + "  -rmtimeout  <ms>  Per-file Restart Manager query timeout (default: 5000)\n"
                + "  -dirmtimeout <ms> Directory RM scan overall timeout (default: 20000)\n"
                + "  -enumtimeout <ms> Directory file enumeration timeout (default: 10000)\n"
                + "  -scantimeout <ms> Handle scanner overall timeout (default: 30000)\n";

            if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h" || args[0] == "/h" || args[0] == "-?" || args[0] == "/?"))
            {
                Console.Out.WriteLine(helpText);
                return 0;
            }
            if (args.Length == 0)
            {
                Console.Error.WriteLine(helpText);
                return 1;
            }

            List<string> paths = new List<string>();
            bool noAdmin = false;
            bool silent = false;
            bool console = false;
            bool noRestartManagerDetect = false;
            bool noHandleFullScan = false;
            int rmTimeoutMs = 5000;
            int dirRmTimeoutMs = 20000;
            int enumTimeoutMs = 10000;
            int scanTimeoutMs = 30000;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.EqualsAny(StringComparison.OrdinalIgnoreCase, "-noadmin", "-na"))
                {
                    noAdmin = true;
                }
                else if (a.EqualsAny(StringComparison.OrdinalIgnoreCase, "-silent", "-s"))
                {
                    silent = true;
                }
                else if (a.EqualsAny(StringComparison.OrdinalIgnoreCase, "-console", "-c"))
                {
                    console = true;
                }
                else if (a.EqualsAny(StringComparison.OrdinalIgnoreCase, "-nohandlefullscan", "-nh"))
                {
                    noHandleFullScan = true;
                }
                else if (a.EqualsAny(StringComparison.OrdinalIgnoreCase, "-norestartmanagerdetect", "-nr"))
                {
                    noRestartManagerDetect = true;
                }
                else if (a.EqualsAny(StringComparison.OrdinalIgnoreCase, "-rmtimeout") && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out int v) && v > 0) rmTimeoutMs = v;
                }
                else if (a.EqualsAny(StringComparison.OrdinalIgnoreCase, "-dirmtimeout") && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out int v) && v > 0) dirRmTimeoutMs = v;
                }
                else if (a.EqualsAny(StringComparison.OrdinalIgnoreCase, "-enumtimeout") && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out int v) && v > 0) enumTimeoutMs = v;
                }
                else if (a.EqualsAny(StringComparison.OrdinalIgnoreCase, "-scantimeout") && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out int v) && v > 0) scanTimeoutMs = v;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(a))
                    {
                        paths.Add(a);
                    }
                }
            }

            if (!noAdmin)
            {
                RelaunchIfNotAdmin(args);
            }

            return FileUnlocker.Unlock(paths, silent, console, noHandleFullScan, noRestartManagerDetect,
                rmTimeoutMs, dirRmTimeoutMs, enumTimeoutMs, scanTimeoutMs);
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
