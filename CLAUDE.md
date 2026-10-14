# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**NoirMech** is a fork of [AnoMech](https://github.com/anomek/AnoMech) ("Another FFXIV mechanics simulator"). It is a Dalamud plugin that **simulates FFXIV raid mechanics client-side**: from an inn room it loads a raid arena locally, spawns fake `BattleChara` instances (party doppels and bosses), drives their casts and movement, and resolves mechanics so players can practice solo. There are 14 scenarios across TOP, Dancing Mad (UMAD), UWU and UCOB.

NoirMech's own changes are small (`git diff v0.4.2.0` shows them all):
- the EU LPDU strat for TOP P2 Party Synergy (`TopP2PartySynergyLpduAi`);
- adjustments to the NA one;
- TOP P2 Party Synergy scenario fixes: the Optical Laser unit sits on the NewNorthA side so the laser can hit, and Spotlight stacks are skipped in solo;
- the `/noirmech leave` gate (`Game.Leave` checks `MapController.HasZoneSession`, backed by `ZoneSession.HasSavedSession`);
- the rename.

Everything else, including other LPDU strats such as UMAD P2 `[LPDU] Buddies`, is upstream's. Prefer keeping upstream files close to upstream so tag merges stay easy.

The rename is shallow. `AssemblyName` is `NoirMech`, which gives `NoirMech.dll`, the `NoirMech.json` manifest and a Dalamud config separate from AnoMech's. But `RootNamespace` and every namespace are still `AnoMech`. `Plugin.DisplayName` holds the user-facing name. NoirMech must not be loaded alongside AnoMech: they install the same hooks and share ImGui window IDs.

## Repo, branches, releases

- Work happens on `noirmech-stable`: upstream tag `v0.4.2.0` plus NoirMech's changes.
  - `origin` is `NoirYamato/DalamudPlugins`, where `noirmech-stable` lives as its own branch.
  - `upstream` is anomek/AnoMech. Merge upstream *release tags* into `noirmech-stable`. The local `master` tracks `upstream/master`, which holds unreleased upstream work; don't merge it.
- Releases are made from a sibling checkout of `NoirYamato/DalamudPlugins` (`main`) with its `publish.ps1`. It expects this repo at `..\Anomech` relative to itself, on `noirmech-stable` with a clean tree. It then:
  - builds Release with `-p:Version=<major.minor.build of the csproj Version>.<n>`, where `n` is the previous `repo.json` revision + 1, reset to 1 when the base changes;
  - copies the packaged zip, the manifest and a `git archive` source zip into `plugins/NoirMech/`, keeping only the newest two builds;
  - runs `git add -A` on the whole DalamudPlugins checkout (so keep it clean), commits, and pushes its `main`.
- `publish.ps1` never pushes `noirmech-stable`: push source commits separately. Don't bump `<Version>` in the csproj by hand; it tracks the upstream base version.
- `.github/workflows/*` are upstream's and are not used for NoirMech. They use AnoMech names; `pr-build.yml` pins .NET 9 and `build.yml` calls anomek/MyDalamudPlugins' reusable workflow.

## Build / run

- Build with `dotnet build AnoMech/AnoMech.csproj` from the repo root. Output is `AnoMech/bin/Debug/NoirMech.dll`.
- It needs the **.NET 10 SDK**. `Dalamud.NET.Sdk/15.0.0` targets `net10.0-windows` with C# 14; the code uses `field` and `extension(...)` blocks. Dalamud assemblies resolve from `%AppData%/XIVLauncher/addon/Hooks/dev/`; override with `DALAMUD_HOME`.
- **There are no automated tests.** Verification is: build clean → load `NoirMech.dll` through Dalamud's Dev Plugin Locations → run the scenario in an inn and watch. For behaviour changes, ask the user to run it; you cannot.
- The chat command is `/noirmech` (alias `/noir`). Subcommands (exact match): `config`, `start`, `start solo`, `reset`, `leave`. `leave` is ignored without a zone session. Anything else toggles the main window.

## Comments

Write a comment only for what the code cannot say itself — rationale, a non-obvious constraint, why the obvious approach was avoided. Delete comments that:
- restate a symbol's name in prose (`// P5 arena state` above `InitP5Arena`);
- restate literals or contract visible at a glance (`// TerritoryId 1363` beside `TerritoryId => 1363`);
- narrate what an edit changed (git records that);
- re-explain what an interface member's own doc already says;
- pile on examples for something trivial.

When unsure, cut it — sparse and load-bearing beats thorough. Scenario AI strats (`*Ai.cs`) go stricter: no comments, intent carried entirely by descriptive method names.

Some existing comments describe older designs and are wrong; trust the code. Examples:
- "reverse-order teardown" in `SimWorld.CreateParty`;
- `SimOmen` "owned by SimCast";
- `IScenario.StratGroups`;
- `SimReap` in the `ISimObject.cs` header (the real helpers are `SimObjectExtensions.Update`/`Despawn`).

## Architecture

### Start sequence
`Game.RunScenario` → `Framework.Run(RunScenarioInternal)`:
1. Inn gate (`ZoneSession.IsInInn()`).
2. `ResetInternal` (`Events.Clear`, `World.Despawn`).
3. Hide the duty exit.
4. `World.Map.TryLoad`. It loads the territory only if no sim zone is loaded, and it never compares territories.
5. `ScenarioOrigin = zone.Origin`, then collider drops and waymarks.
6. `World.CreateParty`.
7. `zone.Run` → `phase.Run` → `scenario.Run(world, selectedAi)`.
8. Teleport, BGM and the chat line.

### Frame loop
`Plugin.OnFrameworkUpdate` → `Game.Tick(dt)`:
1. Return early while `Paused`.
2. `Events.Tick(dt * EventTimeScale)`.
3. `World.Tick(dt)`: `Map.Tick` → `children.Update` (tick every child in insertion order, then reap inactive ones) → `EnmityHud.Refresh` → `PartyHud.Refresh`.
4. `activeScenario.Tick(dt, scenarioElapsed)`; the elapsed value is cumulative and unscaled. Then `UpdateMechanicResult`.

After that, `Plugin` calls `UserActions.Tick`, which is not gated on `Paused`.

Everything runs on the framework thread, with two exceptions:
- `OpcodeUpdater`'s HTTP download runs on the thread pool; its config write is marshalled back.
- `ZoneSession.Revert`'s 1-second continuation runs on the thread pool and does native writes there: player position, Conditions, an ActorControl packet, and disabling the firewall.

The delayed weather and Social-resync continuations hop back through `Framework.Run`. Nothing catches exceptions in the frame loop: a throwing scheduler lambda skips the rest of that frame's tick.

### Zone session (`Core/Map/`)
`ZoneSession` is a port of Hyperborea. While a session is active:
- a send hook drops every outgoing packet except the heartbeat, so the server thinks the player is still in the inn;
- a receive hook only passes opcodes in `Config.ZoneDownOpcodes`. Release builds always do this; the `SafeMode` toggle is DEBUG-only.

**Opcode list.** On plugin load, `OpcodeUpdater` checks the stored `<game version>_<assembly version>` key. If it changed, it fetches `kawaii/Hyperborea/opcodes/<game version>.txt` in the background. The key is saved only on success, so a failed fetch retries on the next load. Until a fetch succeeds the previous list is used, and it is empty on a fresh install. With an empty list, Release drops every incoming packet.

**Enter and Revert.**
- `Enter` saves the inn territory, position and level state, enables the firewall, then runs the native load sequence:
  - start an `InstanceContentDirector` for the territory's ContentFinderCondition and set the duty title;
  - apply sync: level sync only when the player is above the CFC's `ClassJobLevelSync`; "ilvl sync" only recomputes Skill Speed and Spell Speed. `IZone.Level`/`ItemLevel` are just on/off switches;
  - call native `LoadZone` and teleport the player.
- If anything in `Enter`'s load sequence throws after the firewall is on (e.g. a `TerritoryId` with no TerritoryType row), the session is left half-entered: firewall on, `IsActive`/`IsInInstance` false. `/noirmech leave` recovers it because `Game.Leave` checks `HasZoneSession` (the saved inn territory). Plugin unload only disables the firewall in that state. A territory whose ContentFinderCondition is 0 does *not* throw: CFC row 0 exists, so `Enter` completes against an empty director. Every zone needs a real CFC.
- `Revert` reloads the saved inn territory, keeping the firewall on and the player `Occupied` for one more second. On plugin unload (`Revert(true)`) it drops the firewall immediately.

**`MapController`** (`world.Map`) is the facade: `TryLoad`/`Unload`, `AddEffect`, `DirectorUpdate`, `SuppressLayer`, and collider drops. `InstanceContentDirectorHelper.Commence` sends TOP-specific values for every zone.

### Two-layer Core
**`Core/SimObjects/`** holds the in-world entities, plus their spawn configs, tether-end specs and extension helpers. Every entity implements `ISimObject` (`Tick(float)`, `bool IsActive`, `Despawn()`); position-bearing ones implement `IPositioned`. Read the rules in the header of `ISimObject.cs` before adding a type. Containers reap with `SimObjectExtensions.Update` and tear down with `.Despawn`.

**Liveness.** `IsActive` means presence, not life: a KO'd doppel stays active. Liveness is the `IsAlive()` extension on `SimCharacter?` (`SimCharacterDeathExtensions` in `ISimPartyMember.cs`): every character must be `IsActive`, a party member must also not be `Dead`, and null is never alive.

**Character types.**
- `SimCharacter` is abstract. It wraps a `BattleChara*` and owns its `SimStatus`/`SimVfx` children and a `Movement`.
- `SimNpc : SimCharacter` owns a `CharacterManager.BattleCharas` slot. `CharacterManagerHelper.CreateCharacter` allocates it (native `CreateCharacterAtIndex`, entity id `0xE0000000 + index`); a synthetic DespawnCharacter packet frees it. Subclasses:
  - `SimEnemy`: bosses and invisible helpers; casts through `SimCast`.
  - `SimPartyNpc`: the Lalafell doppels.
- `SimPlayer : SimCharacter` wraps the real local player. It never allocates or frees anything. Its `MoveTo` is a no-op, though knockback still moves it.
- `ISimPartyMember` (`Role`, `Dead`, `OnKilled`, `Knockback`) is the party-slot interface, implemented only by `SimPartyNpc` and `SimPlayer`. `SimParty` slots are typed `SimCharacter`, so cast to reach it.
- `SimParty` has 8 slots indexed by `PartyRole`: `MainTank`, `OffTank`, `RegenHealer`, `ShieldHealer`, `MeleeDpsA`, `MeleeDpsB`, `PhysRangedDps`, `CasterDps` = 0..7. The UI labels them MT, OT, H1, H2, M1, M2, R1, R2.
- `SimWorld` is the root, owned by `Game`.

**Other entities.**
- `SimCast`, `SimOmen`, `SimEventObject`, `SimTower`; `SimArenaBoundary` and `SimHiddenObject` live in `Core/Map`.
- `SimTether`: its ends are `ITetherEnd`s from `TetherEnd.cs`. A fixed end is passed as a plain `SimCharacter`; dynamic ends are `End.Passable(...)` or `End.FarthestPlayer()`. The `SimWorld.Tether` overloads require at least one fixed end.

**Spawning.**
- Spawn through `SimWorld`: `SpawnEnemy`, `SpawnEventObject`, `SpawnTower`, `Tether`/`TetherFarestPlayer`, `SpawnOmen`/`SpawnActionOmen`, `CreateParty`, `HideObject`, `EnforceArenaBoundary`. Never `new` these from outside.
- Never spawn into `SimWorld` from inside a child's `Tick`, because the children list is being iterated. Scheduler lambdas and `IScenario.Tick` are fine.

**Teardown.** `SimWorld.Despawn()` is the between-scenario teardown; `Dispose` calls it. It despawns children in **forward** insertion order, resets `Party` to `SimParty.Empty` (so `Game.Player` is null between runs), then clears the HUDs, markers, waymarks, obstacles and `ScenarioOrigin`.

**Everything else** is engine plumbing that is *not* a game object, and it does NOT go in SimObjects:
- `Core/Game/`: `Game` (orchestrator, scenario catalog, single death sink), `EventScheduler`, `Movement`, `Placement`, `Coordinates`, `CharacterFind`/`AoeQuery` (AoE hit tests), `Waymarks`, `Bgm`, `Party/` (`PartyCreator`, `PartyHud`, `PartyPresets`, `PartyRole`), `Ai/`, `Geometry/`.
- `Core/Native/`: `VfxFunctions`, `Statuses`, `ActionEffects`, `DamageNumbers`, `LocalPlayerInputHooks`, `TimelineFunctions`, `RsvFunctions`/`RsfFunctions`.
- `Core/` root: `EnmityHud`, `Markings`, `MathUtil`, `ActionLookup`, `KnockbackLookup`, `LogManager`.
- `Pointers/`: `[Signature]` delegates, initialized at the end of the `Plugin` constructor.
  - Non-nullable signatures are infallible: a miss after a patch breaks plugin load. Make optional ones nullable.
  - Signatures must be unique. `ModelContainerPointers.CalculateUnscaledRadius` currently duplicates `EventObjectPointers.SetEventObjectState`'s.
- `Helpers/`: `CharacterManagerHelper`, `EventObjectHelper`, `GameObjectHelper`, `InstanceContentDirectorHelper`.
- `Core/UserActions/`: client-side resolution of the player's own actions, needed because the server never answers: combos, gauges, procs, Swiftcast/Triplecast/Dualcast, cast interrupts and Sprint.
  - Data is in `JobActions.cs`, per-job state in `Jobs/*StateHandler.cs`, coverage notes in `Core/UserActions/docs/`. It is keyed on level-100 action ids.
  - The scenario engine doesn't read its state, but the two are coupled. `Game` calls `OnSessionStart`/`OnScenarioStart`/`OnSessionEnd` from the start and leave paths. Statuses go through `AddStatusParam`/`AddStatus` on the `SimPlayer`, so they are ordinary `SimStatus` children. Combos and cast interrupts go through synthetic ActionEffect/ActorControl packets. Only the job gauge is written straight to native memory; it is snapshotted at session start and restored at session end.

### Coordinates
- Every `SimWorld`/`Sim*` API position is **scenario-local**: a pure translation from `IZone.Origin` via `SimWorld.ScenarioOrigin`. +X is east, +Z is south, north is −Z. Conversion to world coordinates happens only at native calls, reading `ScenarioOrigin` live.
- Exceptions: `ScenarioOrigin` itself and the `world.Map` positional methods (`ArmColliderDrops`, `Load`) take world coordinates. Convert with `world.Coordinates.ToGlobal`.
- `Placement.Rotation` 0 faces +Z (south); forward is `(sin r, cos r)`.
- `Direction` (in `Scenarios/ScenarioStates.cs`) is a clockwise bearing from north, not a rotation: to face bearing θ use rotation `π − θ`. `Direction.Apply` rotates around the arena centre and turns `Rotation` by the same angle.

### Scenarios
- **`IZone`** is a singleton: territory, origin, sync switches, waymark presets (at least one; `[0]` is the default), collider points and `Run` (e.g. the arena boundary). `TerritoryId` must have a valid ContentFinderCondition.
- **`IPhase`** is a `static readonly Phase` on the zone: weather, BGM and an optional init. Zones and phases are matched by reference identity, so never return a new instance.
- **`IScenario`** members:
  - `Name`, `Phase`, `SupportsSolo`, `AiStrats`;
  - `Run(SimWorld, int? selectedAi)`, where `null` means solo;
  - `Tick(delta, elapsed)`, `DrawSettings`;
  - `IsFinished`. It defaults to `world.Events.IsEmpty`, so a scenario whose timeline lives on a private `EventScheduler` must override it. UMAD P5 Exaflares does; UCOB P5 Exaflares doesn't yet (a known bug: it reports success about 2s in).
- **Registration.** Scenarios are listed in the hard-coded array in the `Game` constructor. The menu groups them by zone, then phase, each in first-appearance order; within a phase, array order is menu order. `Game.FullName` (`{Zone} — {Phase} {Scenario}`, with the phase omitted when its name is empty) must be unique. It is the persisted `LastSelectedScenario` key and the ImGui ID of the menu button.
- **Mechanic folders.** Each one has `<X>Scenario` (timeline, spawns, casts, damage), `<X>State` (per-run randomization, `overrides.F ?? rng...`), `<X>StateOverrides`, `<X>SettingsWindow` (ImGui via `SettingsGrid`) and one or more `<X>Ai` strats.
- **Constants.** Family-shared IDs live in `<Family>Constants.cs` (`TopConstants`, `UmadConstants`, ...), usually brought in with `using static`.
- **Notable scenarios.**
  - **TOP P5 Delta** (`Scenarios/Top/P5Delta/`) is upstream's canonical example of how a scenario consumes the engine.
  - **TOP P2 Party Synergy** is the upstream scenario NoirMech extends: upstream's NA `TopP2PartySynergyAi` (index 0; NoirMech added `Group => "NA"`, spreads at 15.1s and the Mid-glitch 16y stacks) and NoirMech's EU `TopP2PartySynergyLpduAi` (index 1).

### Scenario timeline conventions
- **Use absolute time literals in `world.Events.Add`.** Every entry in a scenario's `Run` should be `world.Events.Add(<absolute t>, ...)` from scenario start (e.g. `55f`, `56.5f`). Don't use `<base> + offset` arithmetic, named time constants, or chained `Events.Add` calls inside event handlers. The whole timeline should read top-to-bottom as one list of absolute timestamps. If state needs to flow between events, store it on the scenario-state object and have each handler read or mutate it.
- `EventScheduler.Add` offsets are relative to the scheduler's current elapsed time. That is 0 while `Run` executes (Start clears the queue) and "now" inside a handler, which is why engine helpers (`AiManager`, `Game.Kill`) may chain relative adds.
- Equal times fire in insertion order. Scenarios call the strat's `Run` before scheduling their own events, so AI moves fire first at the same timestamp.

### Bot AI (`Core/Game/Ai/`)
- **Strats.** A strat implements `IScenarioAi<TState>` (`Name`, optional `Group`, `Run(state, world)`). It normally schedules moves with `AiManager`, which always uses `world.Events`. Strats for private-timeline scenarios (UMAD/UCOB P5 Exaflares) schedule on `state.Timeline` instead.
- **`AiManager.Move(time, positions, jitter = 0.3f, arrivalTime)`** evaluates `positions` when it fires. `arrivalTime` is a deadline at 6 y/s: the `MoveTo` is deferred so the bot arrives then. That deferred `MoveTo` can't be cancelled, so keep each `arrivalTime` before the next `Move`. The delay is computed from the bot's position when the move fires, but the bot keeps walking toward its *previous* destination until the deferred `MoveTo` fires, so it departs on schedule from somewhere other than where the delay was measured and can miss the deadline; the straight-line estimate also ignores obstacle detours. Either let the previous move finish first or drop `arrivalTime` (leave immediately). Example: an interim LPDU strat with the symbol flex at 10s and an `arrivalTime` dodge at 11s lost a bot in about 36% of simulated runs, so the shipped strat drops that `arrivalTime`. Dead or empty slots are skipped, and the player's slot never moves.
- **Positions** use the `AiMove` pipeline:
  1. `Create(8 coords)`.
  2. `.Assignments(roles)` or `.NaturalOrder()`.
  3. `.ApplySwaps(...)` (`IAiRoles`): permutes who goes where; coordinates never move.
  4. `.ApplyPositions(...)` (`IAiPositions`): transforms coordinates. Put the `Direction.Apply` frame rotation last.
- **Strat order.** `selectedAi` is an absolute index into `AiStrats` and is not persisted. Order decides the default strat (index 0, or the first in the selected region) and the order of the region buttons, so append new strats rather than reorder existing ones.
- **Regions.** Once any strat of a scenario has a `Group`, strats without one become unselectable, so give every strat a `Group` ("NA"/"EU").
- `AiStrats` is expression-bodied, so each access creates fresh instances.

### Damage and death
- **`DamageSolver.Resolve`** hit-tests an action's AoE using its shape from the Action sheet (`CharacterFind.InsideActionAoe`). Characters are points; there is no hitbox. Lethality comes from:
  - `DamageType.Lethal` in the damage types (always kills; the most common case);
  - vuln statuses registered with `SetStatuses` (`DamageType.Any` is always included; wild-charge targets also check `wildChargeDamageType`);
  - `stackMinTargets`;
  - tank busters.
- **`killTargets`.** It defaults to true, and the return value changes meaning with it. With `true` it kills the targets that are lethal and returns every target hit; survivors get `statusesToApply`. With `false` it kills nobody and returns only those who would have died; snapshot-then-kill callers use this.
- **Death path.** Every death goes through the `Die(cause)` extension on `SimCharacter` (in `ISimPartyMember.cs`) → `Game.Kill`. `Die` is a logged no-op for non-party characters: bosses are despawned, not killed. `Game.Kill` handles god mode, invuln status 409, the streak and the freeze (`Paused` 5s after the first real death). Never call `OnKilled` directly.
- Cast completion deals no damage by itself; resolve hits as separate timeline events.

## Non-obvious things to know before changing engine code

- **Scenarios are inn-only.** The hard gate is in `Game.RunScenarioInternal`; the gates in `Plugin.StartSelectedScenario` and `MainWindow` are UX on top of it.
  - `IsInInn()` keeps returning true inside the sim zone, because Dalamud's `ClientState.TerritoryType` isn't updated by a client-side load. That is what lets in-zone restarts work.
  - Every spawned BattleChara, doppels and bosses alike, sits in `CharacterManager.BattleCharas`. The engine's `LookupBattleCharaByEntityId` therefore resolves them, and `_PartyList` drives their status icons and targeting on its own.
  - That is safe only because the inn keeps BattleChara density low. The render-cache teardown crash path is documented in `Core/EnmityHud.cs`.
  - A `Hook<>` on `LookupBattleCharaByEntityId` was tried as an alternative and removed after it crashed.
- **HUD mirroring.** `PartyHud` writes `GroupManager.MainGroup` every frame from `SimWorld.Tick` while the sim party has doppels (not in solo runs), snapshotting and restoring the real party. `EnmityHud` writes the `_EnemyList` number/string arrays in an `AddonLifecycle.PreRequestedUpdate` listener and deliberately avoids `UIState.Hate/Hater`.
- **Statuses are ours, not the engine's.**
  - `SimStatus` creates a status with `Statuses.AddStatusInit`, which uses native `AddStatus` for sheet side effects. On the local player native `AddStatus` is a no-op, so it falls back to a direct slot write plus `OnGainStatus`.
  - It then re-stamps `RemainingTime` every tick via `Statuses.Apply`. A duration of 0 is shown as 20s.
  - `Stacks` doubles as the status Param; transformation forms pass `overrideStacks: true`. Without `overrideStacks`, `AddStatus` on an existing status *adds* stacks.
- **Casts go through a synthetic packet.** `SimCast` sends an ActorCast packet via `PacketDispatcherPointers.HandleActorCastPacket`, so the engine owns `CastInfo` and the cast bar. On completion it fires a synthetic `ActionEffectHandler.Receive` so the release animation and VFX play. Instant casts skip the packet.
- **Bad VFX paths crash on the file thread.** `VfxFunctions.Spawn*` do not validate, so callers must check `DataManager.FileExists` first; `SimCharacter.AddVfx`, `SimOmen.ResolveOmenPath` and `SimArenaBoundary` do. Only persistent actor VFX are tracked and removed. Never remove a fire-and-forget one, because the game may already have freed it.
- **Movement needs `SetPosition` plus a timeline override.**
  - `Movement` (`Core/Game/Movement.cs`) goes through `SimCharacter.SetPosition`, which calls native `SetPosition` and also writes `DrawObject->Object.Position` itself. It plays run timeline 22 as `BaseOverride` so the model animates.
  - Knockback uses timeline 156 and ignores obstacles. `world.Obstacles` affects doppels only.
  - `MoveTo` cancels neither `Intercept` nor `Follow`. Call `StopMoving()` first to cancel an intercept. To cancel a follow, use `Follow(null)`, which exists only on `SimEnemy`; `StopMoving()` leaves the follow target set.
- **`EventTimeScale` only scales the shared `EventScheduler`.** That includes AI move starts and `Game.Kill`'s freeze delay. Casts, movement, animations and statuses run in real time, and `AiManager`'s arrival math assumes scale 1. It can only be set by the DEBUG-only speed buttons (`DebugMenu`, drawn inside `MainWindow`).
- **Interop.** Most engine code is `unsafe` and walks FFXIVClientStructs types directly:
  - `BattleChara`, `CastInfo`, `CharacterManager` in SimObjects;
  - `StatusManager` in `Statuses` (`Core/Native`);
  - `DrawData` and `GroupManager.MainGroup` in `Party/`;
  - `MarkingController` in `Markings`/`Waymarks`;
  - `NumberArrayData`/`StringArrayData` in `EnmityHud`.

  Prefer a ClientStructs binding over a new signature.

## Tools (`tools/`, not part of the build)
- **`parser.py`** turns an ACT/IINACT `Network_*.log` into a baseline C# scenario (`--code`). Its output targets an older engine API and needs hand-porting: the `IScenario` shape, `EventObjectSpawnConfig` initializer syntax, and transformations via status Param. After that, add randomization, failure checks and bot AI.
- **`replay_export_rsv_rsf.py`** turns an ARealmRecorded replay into `<Family>ReplayData.cs`, which seeds the RSV names and RSF file unlocks the server never sends in the inn.
  - The generated `Seed()` does nothing until the family's zone `Run` calls it, as `UmadZone.Run` does.
  - The default output path, class name and namespace are UMAD-specific; pass a `.cs` path or `--class`/`--namespace` for another family.
  - `Scenarios/Umad/UmadReplayData.cs` is generated, so don't hand-edit it.
