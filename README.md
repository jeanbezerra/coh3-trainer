# CoH3 Resource Trainer

A Windows desktop trainer for studying the resource systems in **Company of Heroes 3** campaign and solo/private matches against AI.

> [!WARNING]
> Use this project only in campaign or matches without other human players. A match against AI may still use the game's online services. This project does not claim compatibility with competitive or public multiplayer sessions.

## Current status

The current memory layouts and native entry points are validated for Company of Heroes 3 version `5.1.50313.0`. Economic resource discovery uses code signatures, while version-sensitive features remain disabled when the executable version does not match exactly.

| Feature | Status | Notes |
| --- | --- | --- |
| Manpower, Fuel, and Munitions | Available | Live readout and configurable increments |
| Command Points | Available | Configurable increments from 1 to 32 |
| Income multiplier | Available | `1x`, `2x`, `3x`, or `5x` for observed economic income |
| Population cap | Available | Configurable from 100 to 1,000 on the validated game version |
| Match dashboard | Available | Session state, elapsed observation time, values, and net change per minute |
| Squad veterancy, healing, and cooldowns | Experimental | Applies to every squad owned by the local player |
| Victory Points | Not exposed | The authoritative ticket score has not yet been integrated safely |

## Features

- Automatic connection to the active 64-bit `RelicCoH3.exe` process.
- Local-player discovery through validated executable signatures.
- Real-time resource sampling every 250 milliseconds.
- Configurable global hotkeys from `F1` through `F12`.
- Default hotkeys:
  - `F6`: Manpower
  - `F7`: Fuel
  - `F8`: Munitions
  - `F9`: Command Points
- Configurable resource increments with validation and write confirmation.
- Mirrored Command Point writes with rollback if either value cannot be confirmed.
- Income tracking that distinguishes small observed gains from spending and manual trainer writes.
- Population-cap override with preservation and restoration of the original game value.
- Player-wide squad actions executed from the game's own simulation path:
  - promote all squads by one veterancy rank;
  - restore all squad members to full health;
  - clear remaining squad ability cooldowns.
- Match dashboard with a rolling 60-second net-change calculation.
- Persistent settings stored under the current Windows user profile.
- Daily application logs.
- Dark charcoal interface with a larger `1060 × 820` workspace and scrollable tabs.
- Runtime language switching between:
  - Portuguese (Brazil), `pt-BR`;
  - English, `en-US`;
  - Simplified Chinese, `zh-CN`;
  - Spanish, `es-ES`.
- Settings, logs, help, contributors, and application information menus.
- Reusable application branding for this trainer family.
- Single-instance protection to prevent multiple trainers from competing for the same hooks.
- Windows UAC manifest that requests administrator access before startup.
- Self-contained, single-file Windows x64 publishing profile.

See [CHANGELOG.md](CHANGELOG.md) for the full release history.

## Requirements

For the distributed executable:

- Windows 10 or Windows 11, x64;
- Company of Heroes 3;
- administrator approval at startup.

The self-contained release does not require a separate .NET installation.

For development:

- .NET 8 SDK;
- Windows x64;
- PowerShell for the helper scripts.

## Running from source

```powershell
dotnet run --project .\src\Coh3Trainer.App\Coh3Trainer.App.csproj
```

Open Company of Heroes 3 normally. The trainer attempts to connect during startup. If the game is not running yet, start it and select **Try again**.

The connection button reflects the active workflow: **Connecting**, **Try again**, **Disconnect**, or the ready state. The trainer does not launch the game, add development arguments, or modify the game's command line.

## Usage

1. Start Company of Heroes 3.
2. Start the trainer and approve the Windows administrator prompt.
3. Enter a campaign or solo/private match against AI.
4. Wait until the connection status reports that resources are available.
5. Use the resource buttons or configured function keys.
6. Use the **Units** tab for the experimental player-wide squad actions.
7. Adjust hotkeys, amounts, income, and population settings in the **Settings** tab.

Resource increments are added to the current value; they do not replace it.

### Income multiplier

The multiplier watches positive Manpower, Fuel, and Munitions changes. A small detected gain receives an additional bonus of:

```text
observed gain × (configured multiplier - 1)
```

Spending, reductions, large jumps, and writes made directly by the trainer are excluded. Selecting `1x` restores the default behavior immediately.

### Command Points

Command Points are handled separately from economic income. The trainer updates the current resource value and the mirror used by the SCAR binding. Both writes must be confirmed; otherwise, the original values are restored.

### Population cap

The default trainer setting is enabled at `250`. The status remains in a waiting state until the local player is available. Disabling the feature or disconnecting restores the original 12-byte population override captured before the trainer changed it.

### Squad actions

The **Units** tab operates on every squad owned by the local player; no UI selection is required. Requests are placed in a small shared dispatch area and consumed by a hook already running in the game's execution path. Native simulation functions are never invoked from a trainer-created remote thread.

These actions are experimental and version-gated. The interface reports how many squads were processed, returns a safe error when no squads are available, and times out if the game does not consume the request.

## Application data

The trainer stores user data outside the installation directory:

```text
%LOCALAPPDATA%\Coh3Trainer\settings.json
%LOCALAPPDATA%\Coh3Trainer\logs\trainer-YYYY-MM-DD.log
```

Both directories can be opened from the application menu.

## Build and test

```powershell
dotnet build .\Coh3Trainer.sln -c Release
dotnet run --project .\tests\Coh3Trainer.SmokeTests\Coh3Trainer.SmokeTests.csproj -c Release
```

With the game already open, a read-only integration check can also be run:

```powershell
dotnet run --project .\tests\Coh3Trainer.SmokeTests\Coh3Trainer.SmokeTests.csproj -c Release -- --game-integration
```

The integration check does not invoke resource writes or squad actions.

## Publish a distributable executable

```powershell
dotnet publish .\src\Coh3Trainer.App\Coh3Trainer.App.csproj `
  -p:PublishProfile=win-x64-single-file `
  -o .\artifacts\Coh3Trainer-win-x64-admin
```

The result is:

```text
artifacts\Coh3Trainer-win-x64-admin\Coh3Trainer.exe
```

The output is a self-contained Windows x64 executable. The embedded manifest uses `requireAdministrator`, so Windows displays a UAC prompt before the process starts.

The project does not currently include a code-signing certificate. Distributed builds may therefore show **Unknown publisher** in UAC or trigger Microsoft Defender SmartScreen. Code signing is a release/distribution concern and is not bypassed by this project.

## Automated GitHub releases

The [release workflow](.github/workflows/release.yml) builds and publishes the Windows x64 executable whenever a semantic-version tag is pushed:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The same workflow can be started manually from **Actions → Build and publish release → Run workflow** by entering a tag such as `v1.0.0`.

Before creating the tag, add a matching version section to `CHANGELOG.md`, for example `## [1.0.0] - 2026-09-27`. The workflow stops if that section is missing. Its contents become the GitHub Release description automatically.

Each GitHub Release contains:

- `CoH3-Resource-Trainer-vX.Y.Z-win-x64.exe`;
- `SHA256SUMS.txt` for download verification.

The workflow also keeps the same two files as a GitHub Actions artifact for that run. Re-running the workflow updates the release notes and replaces existing assets for the tag.

## Project structure

```text
.github/workflows/              Automated build and GitHub Release publishing
assets/                         Reusable application branding
docs/                           Memory-layout and discovery notes
profiles/                       Disabled external profile template
src/Coh3Trainer.App/            WPF application
  Interop/                      Native Windows API declarations
  Localization/                 Runtime translation catalogs
  Models/                       Settings, rules, and profile models
  Services/                     Backend, persistence, hotkeys, telemetry, and hooks
  Properties/PublishProfiles/   Single-file publishing configuration
tests/Coh3Trainer.SmokeTests/   Executable smoke-test suite
tools/                          Read-only analysis and branding helpers
```

The main responsibilities are separated behind contracts:

- `ITrainerBackend` isolates game-process integration from the UI.
- `ISettingsStore` owns settings persistence.
- `ITrainerSettingsValidator` validates settings atomically.
- `IncomeMultiplierTracker` calculates income bonuses without memory-access dependencies.
- `MatchDashboardTracker` owns session and telemetry calculations.
- version-specific Command Point, population, and squad-action layouts are isolated in dedicated types.
- `IAppLogger` and `IShellService` isolate logging and Windows shell integration.

## External profiles

The repository includes `profiles/coh3-5.1.50313.0.template.json` as a disabled development template. It is intentionally excluded from the single-file release.

To use a separately validated external profile, create a `profiles` directory next to the executable. A profile is loaded only when all of the following match:

- `enabled` is `true`;
- `schemaVersion` is supported;
- process name and executable version match exactly;
- every initial value is readable and inside its configured safe range.

Never enable a template that still contains placeholder offsets such as `0x0`.

## Safety and compatibility

- Every patched instruction sequence is checked before a hook is installed or recovered.
- Updated game versions do not inherit version-sensitive offsets automatically.
- Resource writes are range-checked and read back after writing.
- Mirrored writes roll back when confirmation fails.
- Original hook instructions are restored during a normal disconnect.
- Population data is restored when the feature is disabled or the trainer disconnects.
- Only one trainer instance is allowed per Windows session.

Close the trainer before entering any match that contains other human players.

## Known limitations

- Game updates may disable features until signatures and layouts are validated again.
- Squad veterancy, healing, and cooldown actions remain experimental until they receive broader live-match validation.
- Victory Point tickets are controlled by the authoritative match simulation and are not currently exposed.
- The income multiplier observes net changes in 250 ms samples; simultaneous income and spending may be represented only by their net result.

Technical discovery details are documented in [docs/address-discovery.md](docs/address-discovery.md).

## Credits

**LordSteelHand** — creator and project originator.

## Support this project

CoH3 Resource Trainer is developed and maintained as a free open-source project. If it has been useful to you and you would like to support its continued development, you can make a voluntary donation through GitHub's **Sponsor** button or directly on [Buy Me a Coffee](https://buymeacoffee.com/jeanbezerra).

Donations are appreciated but never required. Bug reports, feedback, documentation, code contributions, and sharing the project are also valuable ways to help.
