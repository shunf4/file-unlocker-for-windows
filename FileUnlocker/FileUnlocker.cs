using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;

namespace FileUnlocker
{
    public static class FileUnlocker
    {
        public static int Unlock(string path, bool silent, bool console)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                if (!silent)
                {
                    if (console)
                    {
                        Console.Error.WriteLine("No file or directory path was provided.", "Unlock");
                    } else
                    {
                        Message.Show("No file or directory path was provided.", "Unlock");
                    }
                        
                }
                return 3;
            }

            var processes = path.Exist() && path.IsDirectoryPath()
                ? GetProcessesFromDirectoryPath(path)
                : GetProcessesFromFilePath(path);

            if (IsProcessArrayEmpty(processes, path, silent, console))
            {
                return 2;
            }

            if (silent)
            {
                KillProcesses(processes);
                return 0;
            }
            else if (console)
            {
                GenPrintProcessLockHint(path, processes);
                return 0;
            }
            else
            {
                return ShowDialog(path, processes);
            }
        }

        private static Process[] GetProcessesFromDirectoryPath(string directoryPath)
        {
            string[] filePaths = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories);

            var processesById = new Dictionary<int, Process>();

            foreach (string path in filePaths)
            {
                foreach (var process in RestartManager.GetProcesses(path))
                {
                    if (!processesById.ContainsKey(process.Id))
                    {
                        processesById[process.Id] = process;
                    }
                }
            }

            return new List<Process>(processesById.Values).ToArray();
        }

        private static Process[] GetProcessesFromFilePath(string filePath)
        {
            var processesById = new Dictionary<int, Process>();

            foreach (var process in RestartManager.GetProcesses(filePath))
            {
                if (!processesById.ContainsKey(process.Id))
                {
                    processesById[process.Id] = process;
                }
            }

            return new List<Process>(processesById.Values).ToArray();
        }

        private static int ShowDialog(string path, Process[] processes)
        {
            string hint = GenProcessLockHint(path, true, processes);

            var ret = 0;
            Message.ShowYesNoDialog(hint, "Unlock", (processes1) => { KillProcesses(processes1); ret = 0; }, (processes1) => { ret = 4; }, processes);

            return ret;
        }

        private static void GenPrintProcessLockHint(string path, Process[] processes)
        {
            string hint = GenProcessLockHint(path, false, processes);

            Console.Error.WriteLine(hint);
        }

        private static string GenProcessLockHint(string path, bool ask, Process[] processes)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{Path.GetFileName(path)} is locked by:");

            foreach (Process process in processes)
            {
                sb.AppendLine($"{process.ProcessName} ({process.Id})");
            }

            if (ask)
            {
                sb.AppendLine($"Kill {(processes.Length > 1 ? "processes" : "process")}?");
            } else
            {
                sb.Append("\n");
            }

            var hint = sb.ToString();
            return hint;
        }

        // https://stackoverflow.com/questions/5901679/kill-process-tree-programmatically-in-c-sharp
        private static void KillProcessAndChildren(int pid)
        {
            // Cannot close 'system idle process'.
            if (pid == 0)
            {
                return;
            }
            ManagementObjectSearcher searcher = new ManagementObjectSearcher
                    ("Select * From Win32_Process Where ParentProcessID=" + pid);
            ManagementObjectCollection moc = searcher.Get();
            foreach (ManagementObject mo in moc)
            {
                KillProcessAndChildren(Convert.ToInt32(mo["ProcessID"]));
            }
            try
            {
                Process proc = Process.GetProcessById(pid);
                proc.Kill();
            }
            catch (ArgumentException)
            {
                // Process already exited.
            }
        }

        private static void KillProcesses(Process[] processes)
        {
            foreach (Process process in processes)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    //process.Kill(entireProcessTree: true);

                    KillProcessAndChildren(process.Id);

                    process.WaitForExit(2000);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private static bool IsProcessArrayEmpty(Process[] processes, string path, bool silent, bool console)
        {
            if (processes == null || processes.Length == 0)
            {
                if (!silent)
                {
                    var hint = $"{Path.GetFileName(path)} is not currently locked by any process.";
                    if (console)
                    {
                        Console.Error.WriteLine(hint, "Unlock");
                    }
                    else
                    {
                        Message.Show(hint, "Unlock");
                    }
                }

                return true;
            }

            return false;
        }
    }
}
