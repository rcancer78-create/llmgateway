using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LLMGateway.Native;

/// <summary>Windows 10+: creates the process inside a private job before any user code runs.</summary>
internal sealed class WindowsNativeProcess : IDisposable
{
    private readonly SafeFileHandle _job;
    public Process Process { get; }
    public StreamWriter Input { get; }
    public StreamReader Output { get; }
    public StreamReader Error { get; }

    private WindowsNativeProcess(Process process, SafeFileHandle job, Pipe input, Pipe output, Pipe error)
    {
        Process = process;
        _job = job;
        var encoding = new UTF8Encoding(false);
        Input = new StreamWriter(input.Server, encoding);
        Output = new StreamReader(output.Server, encoding);
        Error = new StreamReader(error.Server, encoding);
    }

    public static WindowsNativeProcess Start(ProcessStartInfo info)
    {
        using var input = Pipe.Create(PipeDirection.Out);
        using var output = Pipe.Create(PipeDirection.In);
        using var error = Pipe.Create(PipeDirection.In);
        var job = CreateJob();
        Process? process = null;
        try
        {
            using var attributes = new Attributes(input.ChildHandle, output.ChildHandle, error.ChildHandle, job);
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    StandardInput = input.ChildHandle.DangerousGetHandle(),
                    StandardOutput = output.ChildHandle.DangerousGetHandle(),
                    StandardError = error.ChildHandle.DangerousGetHandle()
                },
                AttributeList = attributes.List
            };
            var command = new StringBuilder(Quote(info.FileName));
            foreach (var argument in info.ArgumentList) command.Append(' ').Append(Quote(argument));
            var environment = string.Join('\0', info.Environment
                .Where(pair => pair.Value is not null)
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
            var environmentPointer = Marshal.StringToHGlobalUni(environment);
            ProcessInformation created;
            try
            {
                // JOB_LIST makes containment atomic with creation. Suspension only lets us retain
                // the managed process handle before a very short-lived executable can exit.
                if (!Methods.CreateProcess(info.FileName, command, IntPtr.Zero, IntPtr.Zero, true,
                        0x08000000 | 0x00080000 | 0x00000400 | 0x00000004, environmentPointer,
                        info.WorkingDirectory, ref startup, out created)) ThrowLastError();
            }
            finally { Marshal.FreeHGlobal(environmentPointer); }
            using var processHandle = new SafeProcessHandle(created.Process, true);
            using var threadHandle = new SafeFileHandle(created.Thread, true);
            process = Process.GetProcessById(checked((int)created.ProcessId));
            _ = process.SafeHandle;
            if (Methods.ResumeThread(threadHandle) == uint.MaxValue) ThrowLastError();
            var result = new WindowsNativeProcess(process, job, input, output, error);
            input.TransferServer(); output.TransferServer(); error.TransferServer();
            return result;
        }
        catch
        {
            // The job is already associated even if resumption or managed handle acquisition fails.
            job.Dispose();
            process?.Dispose();
            throw;
        }
    }

    public void Kill()
    {
        if (!Methods.TerminateJobObject(_job, 1)) ThrowLastError();
    }

    public bool IsEmpty
    {
        get
        {
            if (!Methods.QueryInformationJobObject(_job, 1, out var accounting,
                    (uint)Marshal.SizeOf<JobAccounting>(), IntPtr.Zero)) ThrowLastError();
            return accounting.ActiveProcesses == 0;
        }
    }

    public void Dispose()
    {
        // Non-inheritable job handle: host crash/exit also closes its last handle.
        _job.Dispose();
        Input.Dispose(); Output.Dispose(); Error.Dispose(); Process.Dispose();
    }

    private static SafeFileHandle CreateJob()
    {
        var job = new SafeFileHandle(Methods.CreateJobObject(IntPtr.Zero, null), true);
        if (job.IsInvalid) { job.Dispose(); ThrowLastError(); }
        try
        {
            var limits = new JobLimits { Basic = new JobBasicLimits { Flags = 0x2000 } };
            if (!Methods.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>())) ThrowLastError();
            return job;
        }
        catch { job.Dispose(); throw; }
    }

    private static string Quote(string argument)
    {
        if (argument.Contains('\0')) throw new ArgumentException("Native arguments cannot contain NUL.");
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void ThrowLastError() => throw new Win32Exception(Marshal.GetLastWin32Error());

    private sealed class Pipe : IDisposable
    {
        private bool _transferred;
        public NamedPipeServerStream Server { get; }
        public SafeFileHandle ChildHandle { get; }
        private Pipe(NamedPipeServerStream server, SafeFileHandle child) { Server = server; ChildHandle = child; }
        public static Pipe Create(PipeDirection direction)
        {
            var name = "llmgateway-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, direction, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var connection = server.WaitForConnectionAsync(deadline.Token);
                using var client = new NamedPipeClientStream(".", name,
                    direction == PipeDirection.In ? PipeDirection.Out : PipeDirection.In);
                client.Connect(5000);
                connection.GetAwaiter().GetResult();
                var current = Methods.GetCurrentProcess();
                if (!Methods.DuplicateHandle(current, client.SafePipeHandle, current, out var inherited,
                        0, true, 2)) ThrowLastError();
                return new Pipe(server, inherited);
            }
            catch { server.Dispose(); throw; }
        }
        public void TransferServer() => _transferred = true;
        public void Dispose() { ChildHandle.Dispose(); if (!_transferred) Server.Dispose(); }
    }

    private sealed class Attributes : IDisposable
    {
        private readonly IntPtr _handles;
        private readonly IntPtr _jobs;
        private bool _initialized;
        public IntPtr List { get; }
        public Attributes(SafeFileHandle input, SafeFileHandle output, SafeFileHandle error, SafeFileHandle job)
        {
            nuint size = 0;
            Methods.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            List = Marshal.AllocHGlobal(checked((int)size));
            _handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            _jobs = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                if (!Methods.InitializeProcThreadAttributeList(List, 2, 0, ref size)) ThrowLastError();
                _initialized = true;
                Marshal.WriteIntPtr(_handles, 0, input.DangerousGetHandle());
                Marshal.WriteIntPtr(_handles, IntPtr.Size, output.DangerousGetHandle());
                Marshal.WriteIntPtr(_handles, 2 * IntPtr.Size, error.DangerousGetHandle());
                Marshal.WriteIntPtr(_jobs, job.DangerousGetHandle());
                if (!Methods.UpdateProcThreadAttribute(List, 0, 0x00020002, _handles, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero)
                    || !Methods.UpdateProcThreadAttribute(List, 0, 0x0002000D, _jobs, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) ThrowLastError();
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (_initialized) Methods.DeleteProcThreadAttributeList(List);
            Marshal.FreeHGlobal(List); Marshal.FreeHGlobal(_handles); Marshal.FreeHGlobal(_jobs);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits { public JobBasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        public long UserTime, KernelTime, ThisPeriodUserTime, ThisPeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    private static class Methods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobLimits limits, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out JobAccounting accounting, uint size, IntPtr length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string directory,
            ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(SafeFileHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DuplicateHandle(IntPtr sourceProcess, SafePipeHandle source, IntPtr targetProcess,
            out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    }
}
