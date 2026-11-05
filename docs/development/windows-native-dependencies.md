# Windows native dependency audit

The 2026-11-05 import audit found no additional Visual C++ redistributable prerequisite for the pinned x64 application components. No elevated prerequisite installer was added. The development VM has a Visual C++ runtime installed for its tools, so launching successfully there alone would not establish this result.

Normal and delay-load PE import directories were inspected directly in these artifacts:

| Component | Inspected PE files | External VC/GCC runtime imports |
|---|---:|---|
| Official MKVToolNix portable 103.0 archive | 9 | None |
| VideoLAN.LibVLC.Windows 3.0.24, `build/x64`, including plugins | 323 | None |
| Microsoft.NETCore.App.Runtime.win-x64 10.0.14, native runtime files | 15 | None |
| SkiaSharp.NativeAssets.Win32 3.119.4, x64 | 1 | None |
| HarfBuzzSharp.NativeAssets.Win32 8.3.1.3, x64 | 1 | None |

The official [MKVToolNix archive](https://mkvtoolnix.download/windows/releases/103.0/mkvtoolnix-64-bit-103.0.zip) was verified against SHA-256 `c860a10c707cb1ee587d1464f6171842f2ada3bb6f031dfff55fec75542f0417` before inspection. It contains executables without separate runtime DLLs. `mkvmerge.exe` imports Windows OS libraries and the system `msvcrt.dll`; it does not import `MSVCP*`, `VCRUNTIME*`, `CONCRT*`, or GCC runtime DLLs. The NuGet native files came from the restored, version-pinned packages.

Windows CRT components must be distinguished from compiler redistributables: the Universal CRT is an OS component on Windows 10 and later, while compiler-specific VC runtime libraries are separate components. See Microsoft's [CRT deployment documentation](https://learn.microsoft.com/en-us/cpp/windows/universal-crt-deployment) and [Windows CRT distinction](https://learn.microsoft.com/en-us/windows-hardware/drivers/develop/using-the-microsoft-c-runtime-with-user-mode-drivers-and-apps).

This audit covers imported DLL dependencies, including delay imports. It does not prove every dynamically loaded plugin or hardware driver path. A clean Windows installer test remains part of release acceptance. Recheck imports after native dependency updates; do not infer a new prerequisite solely from the development VM's installed tools.
