# Third-party components

FRITZ TV source and corresponding build instructions are available at
https://github.com/jan-guenter/fritz-tv-app under GPL-3.0-or-later. GitHub Releases
include the source archive for each tagged version. No broadcaster content is distributed.

Release packages include a `licenses` directory with the restored NuGet package
inventory, attribution metadata, package licence text and available original notices.
The self-contained .NET runtime licence and third-party notices are retained there.
Build-only packages may appear in this inventory as well.

- [Avalonia](https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md) and
  [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet/blob/main/License.md): MIT.
- [.NET and Microsoft.Extensions](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT): MIT;
  their original third-party notices identify included native components.
- [LibVLCSharp](https://github.com/videolan/libvlcsharp/blob/3.x/LICENSE): LGPL-2.1.
- [LibVLC](https://www.videolan.org/legal.html): LGPL-2.1-or-later for the library,
  with additional licences for bundled plugins. Windows packages retain the native
  distribution's licence files. Linux obtains these components from the distribution.
- [MKVToolNix](https://mkvtoolnix.download/source.html): GPL-2.0-or-later. Windows
  includes the unmodified official portable distribution, including its original
  licence files and shared libraries, under `tools/mkvtoolnix`. Source for version
  103.0 is available at https://mkvtoolnix.download/sources/mkvtoolnix-103.0.tar.xz.
  Linux uses the separately installed MKVToolNix package.

Native media libraries and command line tools remain separate replaceable files.
The authoritative component notices accompany each distributed dependency.
