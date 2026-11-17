# Documents Index

This repo includes documents that explain both high-level and low-level concepts of the codebase. They are useful for contributors to get context that is difficult to acquire from just reading code.

Intro to Meliora
=================

Meliora is an unofficial Nintendo Switch emulator for iPhone and iPad, focused on stability and measurable performance on real devices. It is built on the work of [MeloVertex](https://github.com/VertexSelection/MeloVertex), MeloNX and [Ryujinx](https://github.com/ryujinx) — an open-source Switch emulator created by gdkchan, written in C#.

* The CPU emulator, ARMeilleure, emulates an ARMv8 CPU and supports most 64-bit ARMv8 and some ARMv7 instructions.
* The GPU emulator emulates the Switch's Maxwell GPU; on iOS it runs on Vulkan via a custom MoltenVK, driving Metal.
* Audio output is handled through C# wrappers for SDL2.

Meliora-specific behaviour — adaptive render scaling, memory governor, thread scheduling, frame pacing — is documented in the [README](../README.md) and the [changelog](../CHANGELOG.md).

Getting Started
===============

- [Building Meliora](../Compile.md)
- [Installing the .NET SDK](https://dotnet.microsoft.com/download)
- [Official .NET Docs](https://learn.microsoft.com/en-us/dotnet/)

Contributing (building, testing, benchmarking, profiling, etc.)
===============

If you want to contribute a code change to this repo, start here.

- [Contributor Guide](../CONTRIBUTING.md)
- [Testing a release](TESTING.md)

Coding Guidelines
=================

- [C# coding style](coding-guidelines/coding-style.md)
- [Service Implementation Guidelines — upstream reference (WIP)](https://gist.github.com/gdkchan/84ba88cd50efbe58d1babfaa7cd7c455)

Project Docs
============

To be added. Many project files contain basic XML docs for key functions and classes in the meantime.
