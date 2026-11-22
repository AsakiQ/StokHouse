# Stok House — Stardew Valley co-op housing mod

**Stok House** is an unofficial SMAPI mod for Stardew Valley that lets multiplayer characters share a house without getting married in-game, while keeping their own identity and game data.

**Status:** `0.1.4-beta.1` is a **release candidate being prepared**, not a public release yet. Back up your saves and use a fresh test farm before trying the mod.

## Features

- Invite another player to live in your house using the **O** key; invitations can be accepted or declined.
- Hide the guest's extra cabin while retaining its technical state for moving back.
- Grant or restrict interior decoration and shared-house upgrade permissions.
- Let residents request a shared-house upgrade from **Robin**. Payment and wood are taken from the requesting player when using separate vanilla wallets, with owner approval where required.
- Read each character's personal mail from their shared household mailbox.
- Install with a Windows x64 setup application and launch the game normally through Steam and SMAPI.

## Compatibility and important limitations

- Target environment: **Stardew Valley 1.6.15**, **SMAPI 4.5.2**, Steam on **Windows 10/11 x64**.
- The **family-budget experiment is NOT included** in this public beta candidate. It remains on a separate development branch and is not verified through real two-PC multiplayer testing.
- The basic shared-house flow has been tested in multiplayer, but larger groups, spouses/children, overlapping housing mods, sleeping arrangements, and save recovery need further real-world validation.
- This is not affiliated with ConcernedApe, Stardew Valley, SMAPI, or their maintainers.

## Install (once a release is approved)

1. Close Stardew Valley and SMAPI; make a backup of your saves.
2. Get `StokHouseSetup.exe` and the checksum file from the official [Releases](https://github.com/AsakiQ/StokHouse/releases) page after publication.
3. Run the installer, select your Stardew Valley folder if needed, and install the mod. The installer can download and verify the pinned SMAPI version.
4. If required, copy the SMAPI Steam launch command provided by the installer into Stardew Valley → Properties → General → Launch Options.
5. Install **the same Stok House version** for all players, then launch from Steam and open **O** to manage the shared home.

For manual installation, extract `StokHouse-mod.zip` into `Stardew Valley/Mods` so that `Mods/StokHouse/manifest.json` exists. SMAPI must already be installed.

## Help, license, and development

- [Detailed Russian README](README.md)
- [Host guide (Russian)](docs/HOST.md)
- [Troubleshooting and recovery](docs/RECOVERY.md)
- [Beta publication checklist](docs/PUBLICATION_CHECKLIST.md)
- [MIT license](LICENSE) and [third-party notices](THIRD_PARTY_NOTICES.md)

Project creator and maintainer: **AsakiQ**. Developed with assistance from ChatGPT and ChatGPT Work.
