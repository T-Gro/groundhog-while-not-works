open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Runtime.InteropServices
open System.Text
open Microsoft.Win32.SafeHandles

module private Native =
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type StartupInfo =
        val mutable Size: int
        val mutable Reserved: nativeint
        val mutable Desktop: nativeint
        val mutable Title: nativeint
        val mutable X: int
        val mutable Y: int
        val mutable XSize: int
        val mutable YSize: int
        val mutable XChars: int
        val mutable YChars: int
        val mutable Fill: int
        val mutable Flags: int
        val mutable Show: int16
        val mutable ReservedSize: int16
        val mutable ReservedBytes: nativeint
        val mutable Input: nativeint
        val mutable Output: nativeint
        val mutable Error: nativeint
        val mutable Attributes: nativeint

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type ProcessInfo =
        val mutable Process: nativeint
        val mutable Thread: nativeint
        val mutable Pid: int
        val mutable Tid: int

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint CreateJobObjectW(nativeint attributes, nativeint name)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, byte[] info, int size)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, byte[] info, int size, nativeint returned)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool TerminateJobObject(SafeFileHandle job, uint32 code)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool InitializeProcThreadAttributeList(nativeint list, int count, int flags, nativeint& size)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool UpdateProcThreadAttribute(
        nativeint list,
        uint32 flags,
        nativeint attribute,
        nativeint value,
        nativeint size,
        nativeint previous,
        nativeint returned
    )

    [<DllImport("kernel32.dll")>]
    extern void DeleteProcThreadAttributeList(nativeint list)

    [<DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool CreateProcessW(
        string application,
        StringBuilder command,
        nativeint processAttributes,
        nativeint threadAttributes,
        bool inheritHandles,
        uint32 flags,
        nativeint environment,
        string directory,
        StartupInfo& startup,
        ProcessInfo& child
    )

    [<DllImport("kernel32.dll")>]
    extern nativeint GetStdHandle(int kind)

    [<DllImport("kernel32.dll")>]
    extern nativeint GetCurrentProcess()

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool DuplicateHandle(
        nativeint sourceProcess,
        nativeint source,
        nativeint targetProcess,
        nativeint& target,
        uint32 access,
        bool inheritHandle,
        uint32 options
    )

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern uint32 ResumeThread(SafeFileHandle thread)

    let require operation ok =
        if not ok then
            raise (Win32Exception(Marshal.GetLastWin32Error(), operation))

    type Buffer(size: int) =
        let address = Marshal.AllocHGlobal size
        member _.Address = address

        interface IDisposable with
            member _.Dispose() = Marshal.FreeHGlobal address

let private quote (argument: string) =
    let result = StringBuilder("\"")
    let mutable slashes = 0

    for c in argument do
        if c = '\\' then
            slashes <- slashes + 1
        else
            result.Append('\\', (if c = '"' then slashes * 2 + 1 else slashes)).Append(c)
            |> ignore

            slashes <- 0

    result.Append('\\', slashes * 2).Append('"').ToString()

/// Job-list assignment is part of CreateProcess, before any target code runs.
/// An explicit handle list prevents other concurrent captures' pipes leaking in.
type OwnedProcess(psi: ProcessStartInfo) =
    do
        if not (OperatingSystem.IsWindowsVersionAtLeast(10)) || IntPtr.Size <> 8 then
            raise (PlatformNotSupportedException("Process capture requires 64-bit Windows 10+ job-list containment"))

        if not (String.IsNullOrEmpty psi.UserName) then
            invalidArg "psi" "Capture does not support alternate Windows credentials"

        if psi.ArgumentList.Count > 0 && not (String.IsNullOrEmpty psi.Arguments) then
            invalidArg "psi" "Arguments and ArgumentList cannot both be set"

    let job = new SafeFileHandle(Native.CreateJobObjectW(0n, 0n), true)

    let stdout =
        new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable)

    let stderr =
        new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable)

    let stdin =
        if psi.RedirectStandardInput then
            Some(new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable))
        else
            None

    let mutable child: Process = null

    do
        try
            Native.require "CreateJobObject" (not job.IsInvalid)
            // JOBOBJECT_EXTENDED_LIMIT_INFORMATION, 64-bit Windows ABI.
            let limits = Array.zeroCreate<byte> 144
            BitConverter.GetBytes(0x2000u).CopyTo(limits, 16) // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            Native.require "SetInformationJobObject" (Native.SetInformationJobObject(job, 9, limits, limits.Length))
            let mutable inheritedInput = 0n

            if stdin.IsNone then
                let original = Native.GetStdHandle(-10)

                if original <> 0n && original <> -1n then
                    let current = Native.GetCurrentProcess()

                    Native.require
                        "DuplicateHandle(stdin)"
                        (Native.DuplicateHandle(current, original, current, &inheritedInput, 0u, true, 2u))

            use inputHandle = new SafeFileHandle(inheritedInput, true)

            let input =
                stdin
                |> Option.map (fun s -> s.ClientSafePipeHandle.DangerousGetHandle())
                |> Option.defaultValue inheritedInput

            let handles =
                [| input
                   stdout.ClientSafePipeHandle.DangerousGetHandle()
                   stderr.ClientSafePipeHandle.DangerousGetHandle() |]
                |> Array.filter (fun h -> h <> 0n)

            use handleList = new Native.Buffer(handles.Length * IntPtr.Size)

            for i in 0 .. handles.Length - 1 do
                Marshal.WriteIntPtr(handleList.Address, i * IntPtr.Size, handles[i])

            use jobList = new Native.Buffer(IntPtr.Size)
            Marshal.WriteIntPtr(jobList.Address, job.DangerousGetHandle())
            let mutable attributeSize = 0n
            Native.InitializeProcThreadAttributeList(0n, 2, 0, &attributeSize) |> ignore
            use attributes = new Native.Buffer(int attributeSize)

            Native.require
                "InitializeProcThreadAttributeList"
                (Native.InitializeProcThreadAttributeList(attributes.Address, 2, 0, &attributeSize))

            try
                Native.require
                    "UpdateProcThreadAttribute(handle list)"
                    (Native.UpdateProcThreadAttribute(
                        attributes.Address,
                        0u,
                        0x20002n,
                        handleList.Address,
                        nativeint (handles.Length * IntPtr.Size),
                        0n,
                        0n
                    ))

                Native.require
                    "UpdateProcThreadAttribute(job list)"
                    (Native.UpdateProcThreadAttribute(
                        attributes.Address,
                        0u,
                        0x2000Dn,
                        jobList.Address,
                        nativeint IntPtr.Size,
                        0n,
                        0n
                    ))

                let environment =
                    psi.Environment
                    |> Seq.sortWith (fun a b -> StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key))
                    |> Seq.map (fun pair -> pair.Key + "=" + pair.Value)
                    |> String.concat "\000"
                    |> fun text -> Encoding.Unicode.GetBytes(text + "\000\000")

                use environmentBlock = new Native.Buffer(environment.Length)
                Marshal.Copy(environment, 0, environmentBlock.Address, environment.Length)

                let args =
                    if psi.ArgumentList.Count > 0 then
                        psi.ArgumentList |> Seq.map quote |> String.concat " "
                    else
                        psi.Arguments

                let command = StringBuilder(quote psi.FileName + " " + args)
                let mutable startup = Unchecked.defaultof<Native.StartupInfo>
                startup.Size <- Marshal.SizeOf<Native.StartupInfo>()
                startup.Flags <- 0x100 // STARTF_USESTDHANDLES
                startup.Input <- input
                startup.Output <- stdout.ClientSafePipeHandle.DangerousGetHandle()
                startup.Error <- stderr.ClientSafePipeHandle.DangerousGetHandle()
                startup.Attributes <- attributes.Address
                let mutable created = Unchecked.defaultof<Native.ProcessInfo>

                let flags =
                    0x80000u ||| 0x400u ||| 0x4u ||| (if psi.CreateNoWindow then 0x8000000u else 0u)

                let cwd =
                    if String.IsNullOrEmpty psi.WorkingDirectory then
                        null
                    else
                        psi.WorkingDirectory

                Native.require
                    "CreateProcess(job contained)"
                    (Native.CreateProcessW(
                        null,
                        command,
                        0n,
                        0n,
                        true,
                        flags,
                        environmentBlock.Address,
                        cwd,
                        &startup,
                        &created
                    ))

                use processHandle = new SafeFileHandle(created.Process, true)
                use threadHandle = new SafeFileHandle(created.Thread, true)
                child <- Process.GetProcessById created.Pid
                // Keep a managed handle while the original creation handle still owns the identity.
                child.SafeHandle |> ignore
                Native.require "ResumeThread" (Native.ResumeThread(threadHandle) <> UInt32.MaxValue)
            finally
                Native.DeleteProcThreadAttributeList attributes.Address

            stdout.DisposeLocalCopyOfClientHandle()
            stderr.DisposeLocalCopyOfClientHandle()
            stdin |> Option.iter (fun s -> s.DisposeLocalCopyOfClientHandle())
        with _ ->
            job.Dispose()
            stdout.Dispose()
            stderr.Dispose()
            stdin |> Option.iter (fun s -> s.Dispose())

            if not (isNull child) then
                child.Dispose()

            reraise ()

    member _.Process = child
    member _.Stdout = stdout
    member _.Stderr = stderr
    member _.Stdin = stdin

    member _.Terminate() =
        Native.require "TerminateJobObject" (Native.TerminateJobObject(job, 1u))

    member _.ActiveCount =
        let accounting = Array.zeroCreate<byte> 48

        Native.require
            "QueryInformationJobObject"
            (Native.QueryInformationJobObject(job, 1, accounting, accounting.Length, 0n))

        BitConverter.ToUInt32(accounting, 40)

    interface IDisposable with
        member _.Dispose() =
            job.Dispose()
            stdout.Dispose()
            stderr.Dispose()
            stdin |> Option.iter (fun s -> s.Dispose())
            child.Dispose()
