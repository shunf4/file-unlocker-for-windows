using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace FileUnlocker
{

    // gemini part!
    public class EnhancedHandleScanner
    {
        #region Native Definitions
        // Info class 64 provides 64-bit compatible handle information
        private const int SystemExtendedHandleInformation = 64;
        private const uint ObjectNameInformation = 1;
        private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
        private const uint STATUS_SUCCESS = 0;
        private const int FILE_TYPE_DISK = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
        {
            public IntPtr Object;
            public IntPtr UniqueProcessId;
            public IntPtr HandleValue;
            public uint GrantedAccess;
            public ushort CreatorBackTraceIndex;
            public ushort ObjectTypeIndex;
            public uint HandleAttributes;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_HANDLE_INFORMATION_EX
        {
            public IntPtr NumberOfHandles;
            public IntPtr Reserved;
            // The entries follow immediately in memory
        }

        [DllImport("ntdll.dll")]
        private static extern uint NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, ref int ReturnLength);

        [DllImport("ntdll.dll")]
        private static extern uint NtQueryObject(IntPtr Handle, uint ObjectInformationClass, IntPtr ObjectInformation, int ObjectInformationLength, ref int ReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

        [DllImport("kernel32.dll")]
        private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        private static extern uint GetFileType(IntPtr hFile);

        [StructLayout(LayoutKind.Sequential)]
        public struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OBJECT_NAME_INFORMATION
        {
            public UNICODE_STRING Name;
        }
        #endregion

        public static void Scan(string filePath, Dictionary<int, Process> procDict)
        {
            string devicePath = GetDevicePath(filePath);
            Console.Error.WriteLine("EnhancedHandleScanner Searching for NT Path: {0}", devicePath);

            int length = 0x10000;
            IntPtr ptr = Marshal.AllocHGlobal(length);
            int returnLength = 0;

            // 1. Get Extended Handle Information
            while (NtQuerySystemInformation(SystemExtendedHandleInformation, ptr, length, ref returnLength) == STATUS_INFO_LENGTH_MISMATCH)
            {
                length = returnLength;
                Marshal.FreeHGlobal(ptr);
                ptr = Marshal.AllocHGlobal(length);
            }

            // In the EX structure, NumberOfHandles is an IntPtr (8 bytes on x64)
            long handleCount = Marshal.ReadIntPtr(ptr).ToInt64();

            // Offset starts after the header (NumberOfHandles + Reserved)
            IntPtr offset = new IntPtr(ptr.ToInt64() + (IntPtr.Size * 2));
            int structSize = Marshal.SizeOf(typeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));

            for (long i = 0; i < handleCount; i++)
            {
                var handleInfo = (SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX)Marshal.PtrToStructure(offset, typeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));
                offset = new IntPtr(offset.ToInt64() + structSize);

                // 2. Open process (casting IntPtr PID to uint)
                uint pid = (uint)handleInfo.UniqueProcessId.ToInt32();
                IntPtr processHandle = OpenProcess(0x40, false, pid);
                if (processHandle == IntPtr.Zero) continue;

                // 3. Duplicate and Query
                if (DuplicateHandle(processHandle, handleInfo.HandleValue, Process.GetCurrentProcess().Handle, out IntPtr localHandle, 0, false, 2))
                {
                    if (GetFileType(localHandle) == FILE_TYPE_DISK)
                    {
                        string fileName = GetFileNameWithTimeout(localHandle, 100);
                        //Console.Error.WriteLine("[SCAN] PID: {0} | Handle: 0x{1:X} | Path: {2}",
                                //pid, handleInfo.HandleValue.ToInt64(), fileName);
                        if (!string.IsNullOrEmpty(fileName) && fileName.StartsWith(devicePath, StringComparison.OrdinalIgnoreCase))
                        {
                            var proc = Process.GetProcessById((int)pid);
                            Console.Error.WriteLine("[MATCH] PID: {0} | Handle: 0x{1:X} | Path: {2}",
                            pid, handleInfo.HandleValue.ToInt64(), fileName);
                            if (!procDict.ContainsKey(proc.Id)) {
                                procDict.Add(proc.Id, proc);
                            }
                        }
                    }
                    CloseHandle(localHandle);
                }
                CloseHandle(processHandle);
            }
            Marshal.FreeHGlobal(ptr);
        }

        private static string GetFileNameWithTimeout(IntPtr handle, int timeoutMs)
        {
            string result = null;
            Thread t = new Thread(() =>
            {
                int length = 0x1000; // Increased buffer for long paths
                IntPtr ptr = Marshal.AllocHGlobal(length);
                int returnLength = 0;

                try
                {
                    uint status = NtQueryObject(handle, ObjectNameInformation, ptr, length, ref returnLength);
                    if (status == STATUS_SUCCESS)
                    {
                        // 1. Marshal the pointer into our structure
                        var oni = (OBJECT_NAME_INFORMATION)Marshal.PtrToStructure(ptr, typeof(OBJECT_NAME_INFORMATION));

                        // 2. Use the Buffer pointer provided by the kernel, not our local ptr
                        if (oni.Name.Buffer != IntPtr.Zero && oni.Name.Length > 0)
                        {
                            // Length is in bytes; PtrToStringUni expects number of characters
                            result = Marshal.PtrToStringUni(oni.Name.Buffer, oni.Name.Length / 2);
                        }
                    }
                }
                catch { /* Handle potential access violations silently */ }
                finally { Marshal.FreeHGlobal(ptr); }
            });

            t.IsBackground = true;
            t.Start();
            if (!t.Join(timeoutMs))
            {
                try { t.Abort(); } catch { }
                return null;
            }
            return result;
        }

        private static string GetDevicePath(string path)
        {
            try
            {
                string root = Path.GetPathRoot(path).TrimEnd('\\');
                StringBuilder sb = new StringBuilder(512);
                if (QueryDosDevice(root, sb, sb.Capacity) != 0)
                {
                    return path.Replace(root, sb.ToString());
                }
            }
            catch { }
            return path;
        }
    }

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

            while (true)
            {
                if (path.EndsWith("\\"))
                {
                    path = path.Substring(0, path.Length - 1);
                } else
                {
                    break;
                }
            }

            var processes = path.Exist() && path.IsDirectoryPath()
                ? GetProcessesFromDirectoryPath(path)
                : GetProcessesFromFilePath(path);

            Dictionary<int, Process> procDict = new Dictionary<int, Process>();
            foreach (Process proc in processes)
            {
                procDict.Add(proc.Id, proc);
            }
            EnhancedHandleScanner.Scan(Path.GetFullPath(path), procDict);

            processes = new List<Process>(procDict.Values).ToArray();

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
            List<string> filePaths1 = new List<string>(filePaths);
            filePaths1.Add(directoryPath);

            var processesById = new Dictionary<int, Process>();

            foreach (string path in filePaths1)
            {
                try
                {
                    foreach (var process in RestartManager.GetProcesses(path))
                    {
                        if (!processesById.ContainsKey(process.Id))
                        {
                            processesById[process.Id] = process;
                        }
                    }


                } catch (Exception e)
                {
                    continue;
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
                var procName = "(unknown name)";
                try
                {
                    procName = process.ProcessName;
                } catch (Exception e) { }
                sb.AppendLine($"{procName} ({process.Id})");
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
