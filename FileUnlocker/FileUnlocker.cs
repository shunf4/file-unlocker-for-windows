using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Linq;

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

        public static void Scan(SortedSet<string> fullPaths, Dictionary<int, Process> procDict, bool silent, int overallTimeoutMs)
        {
            List<string> devicePaths = new List<string>(fullPaths.Select(p => GetDevicePath(p)));
            SortedSet<string> devicePathsLower = new SortedSet<string>(devicePaths.Select(p => p.ToLower()));
            List<string> devicePathsLowerWithSlash = new List<string>(devicePathsLower.Select(p => p + "\\"));
            if (!silent)
            {
                Console.Error.WriteLine("EnhancedHandleScanner Searching for NT Path:");
                foreach (var p in devicePaths)
                {
                    Console.Error.WriteLine(p);
                }
            }

            int length = 0x10000;
            IntPtr ptr = IntPtr.Zero;
            int returnLength = 0;

            try
            {
                ptr = Marshal.AllocHGlobal(length);

                // 1. Get Extended Handle Information
                while (NtQuerySystemInformation(SystemExtendedHandleInformation, ptr, length, ref returnLength) == STATUS_INFO_LENGTH_MISMATCH)
                {
                    length = returnLength;
                    Marshal.FreeHGlobal(ptr);
                    ptr = IntPtr.Zero;
                    ptr = Marshal.AllocHGlobal(length);
                }

                // In the EX structure, NumberOfHandles is an IntPtr (8 bytes on x64)
                long handleCount = Marshal.ReadIntPtr(ptr).ToInt64();

                if (!silent)
                {
                    Console.Error.WriteLine("[SCAN] Total system handles: {0}, timeout: {1}ms", handleCount, overallTimeoutMs);
                }

                if (handleCount == 0)
                {
                    return;
                }

                // Offset starts after the header (NumberOfHandles + Reserved)
                IntPtr offset = new IntPtr(ptr.ToInt64() + (IntPtr.Size * 2));
                int structSize = Marshal.SizeOf(typeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));

                // Overall timeout
                Stopwatch sw = Stopwatch.StartNew();
                long scannedCount = 0;
                long lastProgressTime = 0;

                for (long i = 0; i < handleCount; i++)
                {
                    if (sw.ElapsedMilliseconds > overallTimeoutMs)
                    {
                        if (!silent)
                        {
                            Console.Error.WriteLine("[SCAN] Timeout ({0}ms) reached after scanning {1}/{2} handles. Stopping.",
                                overallTimeoutMs, scannedCount, handleCount);
                        }
                        break;
                    }

                    var handleInfo = (SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX)Marshal.PtrToStructure(offset, typeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));
                    offset = new IntPtr(offset.ToInt64() + structSize);
                    scannedCount++;

                    // Progress: every 5000 handles, or every 3 seconds (whichever triggers first)
                    if (!silent && (scannedCount % 5000 == 0 || sw.ElapsedMilliseconds - lastProgressTime >= 3000))
                    {
                        Console.Error.WriteLine("[SCAN] Progress: {0}/{1} handles ({2:F1}%), elapsed: {3:F1}s",
                            scannedCount, handleCount, (double)scannedCount / handleCount * 100, sw.Elapsed.TotalSeconds);
                        lastProgressTime = sw.ElapsedMilliseconds;
                    }

                    // 2. Open process (casting IntPtr PID to uint)
                    uint pid = (uint)handleInfo.UniqueProcessId.ToInt32();
                    IntPtr processHandle = OpenProcess(0x40, false, pid);
                    if (processHandle == IntPtr.Zero) continue;

                    // 3. Duplicate and Query
                    if (DuplicateHandle(processHandle, handleInfo.HandleValue, Process.GetCurrentProcess().Handle, out IntPtr localHandle, 0, false, 2))
                    {
                        if (GetFileType(localHandle) == FILE_TYPE_DISK)
                        {
                            // Adaptive per-handle timeout: reduce as we approach overall timeout
                            int remaining = (int)(overallTimeoutMs - sw.ElapsedMilliseconds);
                            if (remaining <= 0) { CloseHandle(localHandle); CloseHandle(processHandle); break; }
                            int perHandleTimeout = Math.Min(100, remaining);

                            string fileName = GetFileNameWithTimeout(localHandle, perHandleTimeout);
                            string fileNameLower = fileName == null ? null : fileName.ToLower();
                            if (!string.IsNullOrEmpty(fileNameLower) && (
                                devicePathsLower.Contains(fileNameLower) ||
                                devicePathsLowerWithSlash.Any(p => fileNameLower.StartsWith(p))
                            ))
                            {
                                var proc = Process.GetProcessById((int)pid);
                                if (!silent)
                                {
                                    Console.Error.WriteLine("[MATCH] PID: {0} | Handle: 0x{1:X} | Path: {2}",
                                        pid, handleInfo.HandleValue.ToInt64(), fileName);
                                }
                                if (!procDict.ContainsKey(proc.Id)) {
                                    procDict.Add(proc.Id, proc);
                                }
                            }
                        }
                        CloseHandle(localHandle);
                    }
                    CloseHandle(processHandle);
                }

                if (!silent)
                {
                    Console.Error.WriteLine("[SCAN] Done. Scanned {0}/{1} handles in {2:F1}s, found {3} process(es).",
                        scannedCount, handleCount, sw.Elapsed.TotalSeconds, procDict.Count);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
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
        public static int Unlock(List<string> paths, bool silent, bool console, bool noHandleFullScan, bool noRestartManagerDetect,
            int rmTimeoutMs, int dirRmTimeoutMs, int enumTimeoutMs, int scanTimeoutMs)
        {
            if (paths == null || paths.Count == 0)
            {
                if (!silent)
                {
                    if (console)
                    {
                        Console.Error.WriteLine("No file or directory path was provided.");
                    } else
                    {
                        Message.Show("No file or directory path was provided.", "Unlock");
                    }

                }
                return 3;
            }

            Dictionary<int, Process> procDict = new Dictionary<int, Process>();
            SortedSet<string> fullPaths = new SortedSet<string>();

            if (!noRestartManagerDetect)
            {
                if (!silent)
                {
                    Console.Error.WriteLine("[RM] Restart Manager detection enabled. Timeout per file: {0}ms, dir scan: {1}ms, enum: {2}ms",
                        rmTimeoutMs, dirRmTimeoutMs, enumTimeoutMs);
                }

                Stopwatch rmSw = Stopwatch.StartNew();
                int pathIdx = 0;
                foreach (var path_ in paths)
                {
                    pathIdx++;
                    var path = path_;
                    while (true)
                    {
                        if (path.EndsWith("\\"))
                        {
                            path = path.Substring(0, path.Length - 1);
                        }
                        else
                        {
                            break;
                        }
                    }
                    fullPaths.Add(Path.GetFullPath(path));

                    if (!silent)
                    {
                        Console.Error.WriteLine("[RM] Scanning path {0}/{1}: {2}", pathIdx, paths.Count, path);
                    }

                    var currFileProcesses = path.Exist() && path.IsDirectoryPath()
                        ? GetProcessesFromDirectoryPath(path, rmTimeoutMs, dirRmTimeoutMs, enumTimeoutMs, silent)
                        : GetProcessesFromFilePath(path, rmTimeoutMs);

                    foreach (Process proc in currFileProcesses)
                    {
                        if (!procDict.ContainsKey(proc.Id))
                        {
                            procDict.Add(proc.Id, proc);
                        }
                    }
                }

                if (!silent)
                {
                    Console.Error.WriteLine("[RM] Restart Manager detection completed in {0:F1}s, found {1} process(es).",
                        rmSw.Elapsed.TotalSeconds, procDict.Count);
                }
            }
            else
            {
                // Still build fullPaths even when RM is disabled
                foreach (var path_ in paths)
                {
                    var path = path_;
                    while (true)
                    {
                        if (path.EndsWith("\\"))
                        {
                            path = path.Substring(0, path.Length - 1);
                        }
                        else
                        {
                            break;
                        }
                    }
                    fullPaths.Add(Path.GetFullPath(path));
                }
            }

            if (!noHandleFullScan)
            {
                EnhancedHandleScanner.Scan(fullPaths, procDict, silent, scanTimeoutMs);
            }

            var processes = new List<Process>(procDict.Values).ToArray();

            if (IsProcessArrayEmpty(processes, paths.Count == 1 ? paths[0] : null, silent, console))
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
                GenPrintProcessLockHint(paths.Count == 1 ? paths[0] : null, processes);
                return 0;
            }
            else
            {
                return ShowDialog(paths.Count == 1 ? paths[0] : null, processes);
            }
        }

        private static Process[] GetProcessesFromDirectoryPath(string directoryPath, int rmTimeoutMs, int dirRmTimeoutMs, int enumTimeoutMs, bool silent)
        {
            var processesById = new Dictionary<int, Process>();

            // First, check the directory itself
            try
            {
                foreach (var process in RestartManager.GetProcessesWithTimeout(directoryPath, rmTimeoutMs))
                {
                    if (!processesById.ContainsKey(process.Id))
                    {
                        processesById[process.Id] = process;
                    }
                }
            }
            catch { }

            // Enumerate files lazily with timeout
            // Use a thread to avoid blocking forever on large/network directories
            ConcurrentQueue<string> fileQueue = new ConcurrentQueue<string>();
            bool enumerationDone = false;
            Exception enumError = null;

            Thread enumThread = new Thread(() =>
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
                    {
                        fileQueue.Enqueue(file);
                    }
                }
                catch (Exception ex)
                {
                    enumError = ex;
                }
                enumerationDone = true;
            });
            enumThread.IsBackground = true;
            enumThread.Start();

            if (!silent)
            {
                Console.Error.WriteLine("[RM] Enumerating files in directory (timeout: {0}ms)...", enumTimeoutMs);
            }

            // Wait for enumeration
            enumThread.Join(enumTimeoutMs);

            if (enumError != null && fileQueue.Count == 0)
            {
                if (!silent)
                {
                    Console.Error.WriteLine("[RM] Directory enumeration failed: {0}", enumError.Message);
                }
                return new List<Process>(processesById.Values).ToArray();
            }

            int totalFiles = fileQueue.Count;
            if (!silent)
            {
                if (!enumerationDone)
                    Console.Error.WriteLine("[RM] Enumeration still running, ~{0} files found so far. Querying Restart Manager (timeout: {1}ms)...",
                        totalFiles, dirRmTimeoutMs);
                else
                    Console.Error.WriteLine("[RM] Found {0} files, querying Restart Manager (timeout: {1}ms)...",
                        totalFiles, dirRmTimeoutMs);
            }

            // Process files from the queue with an overall timeout
            Stopwatch sw = Stopwatch.StartNew();
            int filesProcessed = 0;
            long lastProgressTime = 0;

            while (sw.ElapsedMilliseconds < dirRmTimeoutMs)
            {
                if (fileQueue.TryDequeue(out string path))
                {
                    filesProcessed++;
                    int remaining = (int)(dirRmTimeoutMs - sw.ElapsedMilliseconds);
                    if (remaining <= 0) break;

                    // Progress: every 50 files or every 2 seconds
                    if (!silent && (filesProcessed % 50 == 0 || sw.ElapsedMilliseconds - lastProgressTime >= 2000))
                    {
                        Console.Error.WriteLine("[RM] Progress: queried {0}/{1} files, elapsed: {2:F1}s",
                            filesProcessed, totalFiles, sw.Elapsed.TotalSeconds);
                        lastProgressTime = sw.ElapsedMilliseconds;
                    }

                    try
                    {
                        foreach (var process in RestartManager.GetProcessesWithTimeout(path, Math.Min(rmTimeoutMs, remaining)))
                        {
                            if (!processesById.ContainsKey(process.Id))
                            {
                                processesById[process.Id] = process;
                            }
                        }
                    }
                    catch { }
                }
                else if (enumerationDone || !enumThread.IsAlive)
                {
                    break;
                }
                else
                {
                    Thread.Sleep(50);
                }
            }

            if (!enumerationDone && enumThread.IsAlive)
            {
                if (!silent)
                {
                    Console.Error.WriteLine("[RM] Enumeration still running, aborting. Processed {0} files.", filesProcessed);
                }
                try { enumThread.Abort(); } catch { }
            }

            if (!silent)
            {
                Console.Error.WriteLine("[RM] Directory scan done. Queried {0} files in {1:F1}s, found {2} process(es).",
                    filesProcessed, sw.Elapsed.TotalSeconds, processesById.Count);
            }

            return new List<Process>(processesById.Values).ToArray();
        }

        private static Process[] GetProcessesFromFilePath(string filePath, int rmTimeoutMs)
        {
            var processesById = new Dictionary<int, Process>();

            foreach (var process in RestartManager.GetProcessesWithTimeout(filePath, rmTimeoutMs))
            {
                if (!processesById.ContainsKey(process.Id))
                {
                    processesById[process.Id] = process;
                }
            }

            return new List<Process>(processesById.Values).ToArray();
        }

        private static int ShowDialog(string pathNullable, Process[] processes)
        {
            string hint = GenProcessLockHint(pathNullable, true, processes).Item1;

            var ret = 0;
            Message.ShowYesNoDialog(hint, "Unlock", (processes1) => { KillProcesses(processes1); ret = 0; }, (processes1) => { ret = 4; }, processes);

            return ret;
        }

        private static void GenPrintProcessLockHint(string pathNullable, Process[] processes)
        {
            Tuple<string, string> hint = GenProcessLockHint(pathNullable, false, processes);

            Console.Error.WriteLine(hint.Item1);
            Console.Out.WriteLine(hint.Item2);
        }

        private static Tuple<string, string> GenProcessLockHint(string pathNullable, bool ask, Process[] processes)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{(pathNullable == null ? "These files" : pathNullable)} is locked by:\n");
            List<string> processIdStrList = new List<string>();

            foreach (Process process in processes)
            {
                var procName = "(unknown name)";
                try
                {
                    procName = process.ProcessName;
                }
                catch { }
                sb.AppendLine($"{procName} ({process.Id})");
                processIdStrList.Add(process.Id.ToString());
            }

            if (ask)
            {
                sb.AppendLine($"\nKill {(processes.Length > 1 ? "processes" : "process")}?");
            } else
            {
                sb.Append("\n");
            }

            var hint = sb.ToString();
            return Tuple.Create(hint, string.Join(" ", processIdStrList));
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

        private static bool IsProcessArrayEmpty(Process[] processes, string pathNullable, bool silent, bool console)
        {
            if (processes == null || processes.Length == 0)
            {
                if (!silent)
                {
                    var hint = $"{(pathNullable == null ? "These files" : pathNullable)} is not currently locked by any process.";
                    if (console)
                    {
                        Console.Error.WriteLine(hint);
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
