# Third-party notices

MediaVerifier ships the components below in its app folder. This file is copied into the app folder
by `build.ps1`. The versions are those referenced by the projects; the FFmpeg section is completed
from the bundle's own `THIRD-PARTY.txt` when the first bundle release exists (subplan 17, task
FFB-07).

| Component | Version | Licence | Where it is used |
|-----------|---------|---------|------------------|
| Microsoft.Data.Sqlite.Core | 10.0.9 | MIT | The library database |
| SQLitePCLRaw.bundle_e_sqlite3 (and its core and provider packages) | 2.1.14 | Apache-2.0 | Loads the native SQLite library |
| SQLite (`e_sqlite3.dll`) | 3.54.0 | Public domain | The database engine |
| System.IO.Hashing | 10.0.15 | MIT | XXH3-128 file fingerprints |
| System.Numerics.Tensors | 10.0.15 | MIT | Vectorised audio analysis |
| FFmpeg (`ffmpeg\` folder, separate programs) | 9.0.2 (bundle) | LGPL-2.1-or-later | All media decoding |

## FFmpeg

MediaVerifier runs FFmpeg as separate programs (`ffmpeg.exe`, `ffprobe.exe`) built as an LGPL
configuration without any GPL or non-free component. The complete corresponding source code (the
exact FFmpeg source archive and the build script with its configure command) is published as a
release of the MediaVerifier repository next to the binaries, tagged `ffmpeg-bundle-<version>-mv<n>`;
Tools → Media engine → *Where to get the source* opens it. You may replace the FFmpeg DLLs; the app
then reports the media engine as changed and keeps working.

The libraries linked into the FFmpeg bundle (for example dav1d, BSD-2-Clause; libjxl, BSD-3-Clause,
with its own dependencies; zlib, zlib licence) and their licence texts are listed in
`ffmpeg\THIRD-PARTY.txt` and `ffmpeg\LICENSE.txt`, generated from the build image at the pinned
digest.

## Licence texts

- MIT: <https://opensource.org/license/mit>
- Apache-2.0: <https://www.apache.org/licenses/LICENSE-2.0>
- LGPL-2.1: <https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html> (also `ffmpeg\LICENSE.txt`)
- SQLite: <https://sqlite.org/copyright.html>
