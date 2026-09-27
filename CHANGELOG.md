# Changelog

All notable changes to CoH3 Resource Trainer are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Planned

- Additional live-match validation for player-wide squad actions.
- A safe simulation-thread integration for authoritative Victory Point tickets.
- Authenticode signing for public distribution.

## [1.0.0] - 2026-09-27

### Added

- Windows WPF trainer with a dark charcoal interface and white typography.
- Automatic connection to the active 64-bit `RelicCoH3.exe` process.
- Local-player discovery using validated executable signatures.
- Live Manpower, Fuel, and Munitions readings refreshed every 250 milliseconds.
- Configurable resource increments and global `F1`–`F12` hotkeys.
- Default hotkeys for Manpower (`F6`), Fuel (`F7`), Munitions (`F8`), and Command Points (`F9`).
- Real-time Command Point support with validated current and SCAR mirror values.
- Command Point increments from 1 to 32, with rollback when a mirrored write cannot be confirmed.
- Configurable `1x`, `2x`, `3x`, and `5x` observed-income multipliers for economic resources.
- Configurable population cap from 100 to 1,000, defaulting to 250.
- Preservation and restoration of the original population override.
- Match dashboard with connection state, observed elapsed time, current resources, and rolling net change per minute.
- Experimental player-wide squad actions for veterancy, full healing, and ability cooldown resets.
- Simulation-path request dispatcher that avoids calling game functions from a trainer-created remote thread.
- Exact-version and native-instruction validation for version-sensitive squad actions.
- Persistent JSON settings under `%LOCALAPPDATA%\Coh3Trainer`.
- Daily UTF-8 logs and menu shortcuts to the settings and log directories.
- Runtime localization for Portuguese (Brazil), English, Simplified Chinese, and Spanish.
- Application menus for language, folders, help, contributors, and version information.
- Contributor dialog crediting LordSteelHand as the project originator.
- Reusable trainer-family icon embedded in the executable and application windows.
- Larger `1060 × 820` main window with a `900 × 720` minimum size and scrollable content.
- Single-instance protection to prevent competing hooks and hotkey registrations.
- Windows UAC `requireAdministrator` manifest.
- Self-contained, compressed, single-file Windows x64 publishing profile.
- Smoke tests for localization completeness, settings, validation, memory-layout rules, income tracking, dashboard calculations, logging, and persistence.
- Optional read-only live-game integration test.
- Static-analysis tools for executable strings, native bindings, and local-player inspection.
- Technical documentation for signature discovery, Command Points, population, squad actions, and Victory Point research.

### Changed

- Squad actions now enumerate every squad owned by the local player instead of depending on UI selection state.
- The distributable build excludes the disabled profile template while still supporting separately supplied external profiles.
- The README is now maintained in English for GitHub distribution.
- Project metadata now declares version `1.0.0`, product description, and LordSteelHand as author.

### Fixed

- Population override is restored during disconnect instead of only when the feature is disabled during an active refresh cycle.
- Duplicate hotkeys loaded from a damaged or manually edited settings file are normalized to distinct function keys.
- Settings validation now rejects incomplete resource dictionaries instead of applying a partial configuration.
- Removed a temporary manifest-inspection file accidentally created in the repository root.

### Safety

- Resource writes are range-checked and confirmed by rereading memory.
- Failed mirrored writes restore their original values.
- Hook installation requires exact instruction matches and refuses ambiguous signatures.
- Version-sensitive features fail closed on unsupported executable versions.
- The trainer does not launch the game or add development command-line arguments.

### Known limitations

- Victory Point ticket modification is not exposed because the authoritative value belongs to the match simulation.
- Squad actions are experimental and require further live-match feedback.
- Public binaries are not Authenticode-signed and may display an unknown-publisher or SmartScreen warning.
