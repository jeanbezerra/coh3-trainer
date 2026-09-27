# Automatic resource discovery

This document records the technical basis of the automatic resolver and its fail-safe criteria.

## Scope

- Campaign or solo/private matches against AI, including sessions that use the game's online services.
- Changes are limited to values owned by the local player.
- Game files are not modified, and the trainer does not attempt to bypass protection mechanisms.
- Two temporary hooks are installed in the local process to capture the player object: the primary player routine and the production queue as a fallback.
- Veterancy, healing, and cooldown requests are consumed by the primary hook from the game's execution path. The trainer process does not invoke those native simulation functions directly.
- The trainer does not launch the game, enable development mode, or alter its command line.

## Public reference and local verification

A public table for version `2.5.2.48837` identified player and production-queue routines by AOB and described these `float` fields in the captured player object:

- `+0x682`: marker used to distinguish the local player;
- `+0x6A8`: Fuel;
- `+0x6AC`: Manpower;
- `+0x6B0`: Munitions.

Static analysis located the same complete signature exactly once in the installed `RelicCoH3.exe`, version `5.1.50313.0`. Runtime code still validates the signature before every installation or recovery; this static result is not treated as forward compatibility.

References:

- https://www.gamepressure.com/download/company-of-heroes-3-final-stand-cheat-table-ct-v10-mod/z8157e6
- https://vgtimes.com/games/company-of-heroes-3/files/88903-table-for-cheat-engine-2-1-5-38066.html
- https://www.xbox.com/en-gb/games/store/company-of-heroes-3/9p014g2w3l83

## Automatic connection flow

1. Select the `RelicCoH3.exe` process with a visible window and the largest working set.
2. Scan the main module and require exactly one occurrence of each signature.
3. Compare every instruction that will be replaced.
4. Allocate the capture and dispatcher area.
5. Install temporary stubs that record only the local-player pointer and consume pending squad-action requests.
6. Validate the local-player marker and all three economic resources before binding them.
7. Before a resource write, validate its range; after writing, read it back for confirmation.
8. On disconnect, restore the original instructions and population data.
9. Keep the small executable allocation reserved until game termination to avoid a race with a stub that may still be executing.
10. After an abnormal trainer exit, recover only hooks whose generated code matches exactly.

## Fail-safe behavior

The resolver refuses a connection when a signature is absent, ambiguous, or has unexpected instructions. Approximate addresses are never accepted. Optional profiles require an exact executable version and remain disabled until independently validated.

Version-sensitive features are not automatically carried forward to a new game build. Unsupported features remain disabled even when the basic economic-resource signature is still available.

## Income multiplier

The income multiplier does not invoke SCAR and does not modify an internal simulation rate. It observes Manpower, Fuel, and Munitions independently:

1. The first sample establishes the reference value.
2. Positive increments up to 25 units per sample are treated as normal income.
3. The applied bonus is `increment × (multiplier - 1)`.
4. Spending, reductions, and larger jumps are ignored.
5. Manual trainer writes update the reference and are not multiplied again.

This intentionally conservative strategy prevents feedback from the trainer's own writes. If spending and income occur in the same 250 ms sample, only their positive net change can be recognized.

## Population cap

Static analysis of the SCAR bindings in version `5.1.50313.0` identified the following behavior:

- `Player_GetPopCapOverride` returns data at `player + 0x50C`;
- `Player_SetPopCapOverride` writes three `float` values, totaling 12 bytes, at `player + 0x50C`;
- the first value is the personnel cap;
- the other two values use `FLT_MAX`, the game's sentinel for categories that are not overridden;
- `Player_IsPopCapOverrideSet` compares the first field with the same sentinel.

The trainer reproduces only this data assignment. It preserves the original 12 bytes before the first change, confirms the complete block after every write, and restores the captured block when the feature is disabled or the trainer disconnects.

The feature is refused unless the executable version is exactly `5.1.50313.0`, the local player has been identified, and the original values form a plausible layout.

The static analysis can be reproduced without attaching to the game:

```powershell
py -3.13 .\tools\analyze_victory_points.py "C:\path\RelicCoH3.exe" --term Player_GetPopCapOverride
py -3.13 .\tools\analyze_victory_points.py "C:\path\RelicCoH3.exe" --term Player_SetPopCapOverride
```

## Command Points

Scripts in version `5.1.50313.0` use `Player_GetResource(player, RT_Command)`, `Player_SetResource(player, RT_Command, value)`, and `Player_AddUnspentCommandPoints`. Static analysis and read-only local-player inspection showed that `RT_Command` occupies index 3 in both relevant stores:

- `player + 0x6A4`: current resource value;
- `player + 0x188`: mirror read by the SCAR resource binding.

The validated absolute maximum is 32, matching the tunable maximum referenced by the game scripts. Command Points do not participate in the income multiplier.

The backend validates both values, writes both, reads both again, and restores the originals if any step fails. The feature remains unavailable on other executable versions.

`tools/inspect_live_player.py` reproduces the read-only inspection. It reads the pointer left by an already connected trainer instance and displays the resource stores used during analysis; it does not install hooks or write to the process.

## Veterancy, healing, and cooldowns

The installed official `Data.sga` archive contains the Essence Engine bindings used by game scripts. For version `5.1.50313.0`, local static analysis identified and validated native entries for:

- `Player_GetSquads`;
- `Squad_IncreaseVeterancyRank`;
- `Squad_SetHealth`;
- `Squad_AdjustAbilityCooldown`.

The trainer never calls these entries from a remote thread. It writes a compact request to its existing allocation. When the local-player routine reaches the hook from the game's execution path, a dispatcher:

1. atomically claims one pending request;
2. preserves the native register context;
3. obtains up to 256 squads owned by the local player;
4. validates the returned vector length and alignment;
5. applies one requested operation to every squad;
6. publishes the affected count and marks the request complete.

The operations are:

- veterancy: use the current rank plus one as the target rank;
- healing: apply health fraction `1.0` to every squad member;
- cooldowns: apply a sufficiently negative delta to reduce every positive remaining duration to zero.

The interface reports the affected squad count, detects a player without available squads, and times out when the game does not consume the request. All four native instruction prefixes are checked before the actions become available.

This player-owned enumeration replaced an earlier selection-based prototype. UI selection state is not reliable from the simulation-path hook.

## Victory Point research

Static analysis located `local_player_victory_points` and an `Int32` value at `player + 0x438`. Script inspection showed that this field represents the number of controlled Victory Point locations, not the team's remaining ticket score. It must not be written as a scoreboard value.

In `scar/winconditions/ticket_vp.scar` and `scar/winconditions/win_tickets.scar`, authoritative scores are stored in `_vp.teams[].tickets` and `_tickets.teams[].tickets`. Changes are propagated by the simulation through `Core_CallDelegateFunctions("OnTicketsChanged", ...)`.

Calling the SCAR executor from an external thread is unsafe. Victory Point modification therefore remains disabled until an implementation can update authoritative tickets in the correct simulation context or through a custom win condition.
