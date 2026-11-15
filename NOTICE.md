# Notice

## Provenance

Meliora continues from this chain of projects:

```
Ryujinx  ->  MeloNX (Stossy11 and contributors)  ->  MeloVertex (VertexSelection)  ->  Meliora
```

Original copyright and license notices are kept in [LICENSE.txt](LICENSE.txt) and in the headers of source files. Names of upstream developers and contributors are used only to credit their work, never to endorse or promote Meliora.

## Third-party components

| Component | Where | License / note |
|---|---|---|
| SDL2 | `Dependencies/XCFrameworks/SDL2.xcframework` | zlib license (see SDL's own license text) |
| MoltenVK | `Dependencies/Dynamic Libraries/libMoltenVK.dylib` | Apache-2.0 (upstream: KhronosGroup/MoltenVK) |
| FFmpeg libraries (libavcodec, libavformat, libavutil, libavfilter, libswscale, libswresample) | `Dependencies/XCFrameworks/` and `Dependencies/Dynamic Libraries/` | LGPL. The bundled `libavcodec` reports "LGPL version 3 or later"; each framework carries a LICENSE file. Source: <https://ffmpeg.org> |
| libteakra, libSPIRV | `Dependencies/XCFrameworks/` | See the respective upstream projects |
| navigation-stack-backport, SwiftSVG | Swift Package Manager (`Package.resolved`) | See the respective upstream repositories |
| AirKit | credited in `App/UI/Emulation/AirPlay/Air.swift` | See <https://github.com/heestand-xyz/AirKit> |
| .NET packages (Silk.NET, OpenTK, SDL2-CS, and others) | `Directory.Packages.props` | See each package on nuget.org |

If you spot a missing or inaccurate attribution, please open an issue.
