module CaptureFixture.Handles

open System.Runtime.InteropServices

[<DllImport("kernel32.dll")>]
extern nativeint private GetStdHandle(int kind)

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool private SetHandleInformation(nativeint handle, uint32 mask, uint32 flags)

let ClearInheritance (kind) =
    SetHandleInformation(GetStdHandle kind, 1u, 0u)
