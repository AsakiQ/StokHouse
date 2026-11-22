# Third-party notices / Уведомления о сторонних компонентах

Stok House's original source code is licensed under MIT (see `LICENSE`).
This license **does not** relicense Stardew Valley, SMAPI, Harmony, MonoGame, or .NET.

## Stardew Valley
- Proprietary game by ConcernedApe (Eric Barone).
- Stok House requires a legitimate installed copy of the game.
- Game binaries, assets, and `Stardew Valley.dll` are **not** bundled with the mod's ZIP or source repository.
- https://www.stardewvalley.net/

## SMAPI 4.5.2
- Official project: https://github.com/Pathoschild/SMAPI
- License: GNU Lesser General Public License version 3.0 (LGPL-3.0).
- Stok House uses SMAPI as an external mod loader. If absent, the Windows installer fetches the official SMAPI distribution from its pinned upstream release, validates its checksum, and installs the upstream components.
- Upstream license and source: https://github.com/Pathoschild/SMAPI/blob/develop/LICENSE.txt and https://github.com/Pathoschild/SMAPI
- This file is **not** a substitute for the full SMAPI license text supplied with its official distribution.

## Harmony / 0Harmony
- Harmony (Andreas Pardeike): https://github.com/pardeike/Harmony
- License: MIT.
- Stok House references the Harmony assembly installed by SMAPI; the Stok House mod ZIP does **not** bundle a separate Harmony DLL.
- Upstream license: https://github.com/pardeike/Harmony/blob/master/LICENSE

## MonoGame
- MonoGame: https://github.com/MonoGame/MonoGame
- License: Microsoft Public License (Ms-PL), except components with separate notices.
- The build uses `MonoGame.Framework.DesktopGL` for compilation when the installed game is unavailable; the package is configured with `ExcludeAssets="runtime"` and is **not** bundled in the Stok House mod ZIP.
- Upstream license: https://github.com/MonoGame/MonoGame/blob/develop/LICENSE.txt

## Microsoft .NET
- The Windows x64 installer is a self-contained .NET 8 Windows Forms executable; the mod targets the game's .NET 6 environment.
- .NET contains software distributed under Microsoft and third-party notices.
- The release packager collects the appropriate installed runtime license and third-party notice files into `StokHouse-docs.zip/licenses/` and fails if required files are absent.
- Licensing information: https://github.com/dotnet/core/blob/main/license-information.md

## Notices and verification
- The release distribution should include `LICENSE`, this document, `StokHouse-docs.zip`, and `SHA256SUMS.txt`.
- If changing dependencies or bundling new binaries/assets, re-check their licenses before distribution.
- These references describe the current build configuration and must be rechecked for the exact published binary.
