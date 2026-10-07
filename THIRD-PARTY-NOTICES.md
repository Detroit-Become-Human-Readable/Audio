# Third-party components

The C# remake is MIT licensed. Codec tools run as separate executables.

| Component | Terms | Source |
|---|---|---|
| .NET / WPF | MIT and runtime notices | https://github.com/dotnet/runtime |
| CommunityToolkit.Mvvm 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| NAudio 2.2.1 | MIT | https://github.com/naudio/NAudio |
| Microsoft.Data.Sqlite 10.0.10 | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw.bundle_e_sqlite3 / provider.e_sqlite3 / lib.e_sqlite3 2.1.12; bundled SQLite 3.53.3 | Apache-2.0 (managed packages); SQLite is public domain | https://github.com/ericsink/SQLitePCL.raw and https://www.sqlite.org/copyright.html |
| vgmstream | Permissive ISC-style notice | https://github.com/vgmstream/vgmstream |
| libvorbis / libogg | BSD-3-Clause | https://github.com/xiph/vorbis and https://github.com/xiph/ogg |
| ww2ogg 0.24 | BSD-3-Clause | https://github.com/hcs64/ww2ogg |
| ReVorb 1.0 | Permissive ISC-style source header | https://github.com/ItsBranK/ReVorb |
| FFmpeg shared LGPL build | LGPL-3.0-or-later; enabled dependencies have additional terms | https://github.com/FFmpeg/FFmpeg and https://github.com/BtbN/FFmpeg-Builds |

The portable folder includes upstream license texts under `tools/notices` and version/hash information in `tools/manifest.json`. The decoder profile includes vgmstream and libvorbis, without optional proprietary-codec DLLs. Original bank/media export does not require a decoder.

FFmpeg is dynamically linked, with GPL and nonfree features disabled in its build. Users may replace its binaries and shared libraries. Its exact source revision and build project are recorded in `tools/components.lock.json`; the build project's per-library scripts identify enabled dependencies. A distributor must provide corresponding source and the notices for enabled libraries in its own distribution; an upstream download link alone does not replace these obligations. The local portable package is not a completed public redistribution audit.

wwiser is used as a structural research reference. Its Python code is not included or translated into this distribution. See https://github.com/bnnm/wwiser and the pinned reference in the format documentation. The legacy console/native sources retain their original ownership and are outside the new MIT license.
