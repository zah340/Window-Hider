using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StreamHider
{
    public class Injector
    {
        // Windows API Constants & Imports
        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll")]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll")]
        private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        // Access Rights
        private const uint PROCESS_CREATE_THREAD = 0x0002;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_VM_OPERATION = 0x0008;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint PROCESS_VM_READ = 0x0010;

        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        // Display Affinity Constants
        private const uint WDA_NONE = 0x00000000;               // Visible
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011; // Hidden

        public static bool HideWindowFromProcess(uint processId, IntPtr windowHandle)
        {
            // Call with 0x11 (Hide)
            return InjectDisplayAffinityChange(processId, windowHandle, WDA_EXCLUDEFROMCAPTURE);
        }

        public static bool ShowWindowFromProcess(uint processId, IntPtr windowHandle)
        {
            // Call with 0x00 (Show/Visible)
            return InjectDisplayAffinityChange(processId, windowHandle, WDA_NONE);
        }

        private static bool InjectDisplayAffinityChange(uint processId, IntPtr windowHandle, uint affinityMode)
        {
            IntPtr hProcess = IntPtr.Zero;
            IntPtr pRemoteMem = IntPtr.Zero;

            try
            {
                // 1. Open target process
                hProcess = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, false, (int)processId);

                if (hProcess == IntPtr.Zero) return false;

                // 2. Get address of SetWindowDisplayAffinity
                IntPtr hUser32 = GetModuleHandle("user32.dll");
                IntPtr pFunc = GetProcAddress(hUser32, "SetWindowDisplayAffinity");

                if (pFunc == IntPtr.Zero) return false;

                // 3. Create Shellcode (Pass the desired affinityMode)
                byte[] shellcode = CreateShellcode(windowHandle, pFunc, affinityMode);

                // 4. Allocate memory
                pRemoteMem = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)shellcode.Length, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);

                if (pRemoteMem == IntPtr.Zero) return false;

                // 5. Write Shellcode
                if (!WriteProcessMemory(hProcess, pRemoteMem, shellcode, (uint)shellcode.Length, out _))
                    return false;

                // 6. Execute Remote Thread
                IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, pRemoteMem, IntPtr.Zero, 0, IntPtr.Zero);

                if (hThread != IntPtr.Zero)
                {
                    WaitForSingleObject(hThread, 2000); // Wait for execution
                    CloseHandle(hThread);
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
            }
        }

        private static byte[] CreateShellcode(IntPtr hWnd, IntPtr pFunc, uint affinityMode)
        {
            if (IntPtr.Size != 8)
            {
                throw new InvalidOperationException("Application must be compiled as x64 to patch modern 64-bit processes.");
            }

            // x64 Assembly Shellcode
            // Calls pFunc(hWnd, affinityMode)

            using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
            using (System.IO.BinaryWriter writer = new System.IO.BinaryWriter(ms))
            {
                writer.Write(new byte[] { 0x48, 0x83, 0xEC, 0x28 });      // sub rsp, 28h (Stack align)

                // 1st Arg: hWnd (RCX)
                writer.Write(new byte[] { 0x48, 0xB9 });                  // mov rcx, ...
                writer.Write(hWnd.ToInt64());                             // ... hWnd

                // 2nd Arg: Affinity Mode (RDX)
                // We write the specific mode (0x11 for hide, 0x00 for show)
                writer.Write(new byte[] { 0xBA });                        // mov edx, ...
                writer.Write(affinityMode);                               // ... mode (4 bytes)

                // Function Address (RAX)
                writer.Write(new byte[] { 0x48, 0xB8 });                  // mov rax, ...
                writer.Write(pFunc.ToInt64());                            // ... Address

                writer.Write(new byte[] { 0xFF, 0xD0 });                  // call rax

                writer.Write(new byte[] { 0x48, 0x83, 0xC4, 0x28 });      // add rsp, 28h
                writer.Write((byte)0xC3);                                 // ret

                return ms.ToArray();
            }
        }
    }
}
