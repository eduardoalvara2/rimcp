# Changelog

All notable changes to this project are documented in this file.

## 1.1.0+rimplayer.5 (rimplayer fork) - 2026-11-08

### Added

- Trading through the game's own `TradeSession` and `TradeDeal`, with no trade window. `list_traders` lists the traders on a map: trade caravans (visitor pawns that can trade now) and orbital trade ships (`passingShipManager`), each with a stable `traderId` (the trader pawn's ThingID, or the ship's load id such as `PassingShip_12`), `kind` (`caravan` or `orbital`), label, trader kind, faction, `ticksUntilDeparture` for ships, `canTrade` with a `reason` when not, and `negotiators`, the colonists who could trade as the game allows: able to talk, not downed or in a mental state, Social not disabled, the permit a trader asks for, and for a caravan a path to the trader; for a ship also a powered, reachable comms console (the one used is reported) and a powered orbital trade beacon. `unavailable` says why each other colonist cannot. `get_trade` opens a session for a trader and a negotiator without a window, lists the tradeables with a stable `key`, label, defName, `colonyCount`, `traderCount`, `buyPrice` and `sellPrice` per unit at that negotiator's prices, and whether the trader and the colony will trade each (`traderWillTrade`, `colonyWillTrade`, `canBuy`, `canSell`), plus both sides' silver and the reasons the game gives for goods it will not sell, then closes the session; it filters with `query` or `def` and pages with `limit` and `cursor`, and `detail` summary gives short rows. `execute_trade` takes `buy` and `sell` lists of `{key, count}`, checks every count against what each side has and will trade (a ship only offers goods near a powered orbital trade beacon, a caravan goods in the home area or storage, as in game), checks the silver as the window does (the colony must afford the net cost, and the trader must be able to pay what it owes), runs the deal with `TradeDeal.TryExecute`, and verifies the result: silver and each item's counts before and after, where the goods went (a caravan's are placed near its trader, a ship's arrive by drop pod near a beacon) and `warnings` if anything differs. It is shown in game as a message, takes `expectedLoadId` and a `requestId` for safe retries, and refuses royal-favor traders. The key is `def[:stuff][@quality]` for items, with `#2`, `#3` where one key covers several tradeables (hit point bands, unique items, numbered by hit points), and `pawn:<thingId>` for animals and people, any pawn of a group finding it; it is the same between calls although the game builds its tradeables again each session.
- `fire_incident`, for testing only and off by default: it fires one allow-listed incident now (`TraderCaravanArrival`, `OrbitalTraderArrival` or `RaidEnemy`, the last with optional `points`, default 60) with the storyteller's default parameters for the map, forced, and reports whether it fired and the letter it produced. It is gated by a new mod setting, `AllowTestIncidents` (the **Allow test incidents (testing only)** checkbox on the Safety tab; `allowTestIncidents` in `Mod_RiMCP_RiMCPMod.xml`, or `Mod_3809698070_RiMCPMod.xml` for the Workshop copy, under `ModSettings`), which is off by default; while off the tool refuses before looking at anything and nothing reaches the game, and no tool can turn it on. It belongs to the Game settings group, so the Control preset leaves it out.

### Fixed

- `fire_incident` for `TraderCaravanArrival` no longer reports a caravan that is not there. The game sends the caravan's letter and spawns its pawns before it looks for the spot they wait at, and returns false, leaving them with no lord, when it finds none (a young colony has few buildings); the call then said `fired: false` with a letter anyway. It now gives such pawns the trade-with-colony lord at a spot near the colonists (`recoveredLord`), reports `arrivedPawns` and `traderIds`, only says `fired: true` when a trader is on the map, and when it did not fire says the letter it sent announces nothing.

### Changed

- The Trade float-menu option and a call to a trade ship through a comms console no longer say agents cannot trade: they point to `list_traders`, `get_trade` and `execute_trade`. Other comms calls stay refused as before.
- Not yet tried in game: everything above was checked by the regression suite without a game (manifest entries, input validation, key stability, the gate); the trade session, prices, the deal, the delivery report and the incident need a live colony.

## 1.1.0+rimplayer.4 (rimplayer fork) - 2026-11-08

### Added

- `set_area` paints the game's two roof areas: `areaId` `noRoof` (colonists with Construction remove the roofs over its cells, and the game stops roofing them by itself when a room is closed) and `buildRoof` (colonists build roofs there). Their `list_zones` ids work too. RimWorld keeps roofs as these areas, painted by its Remove roof, Build roof and Ignore roof area tools, not as designations, so they belong to `set_area` rather than `designate`. `add` follows the matching tool: unexplored cells are refused, `noRoof` refuses overhead mountain (`Overhead mountain: nothing can remove a thick rock roof.`), and cells already in the area are `alreadySatisfied`; each takes the cells it paints out of the other area, as the game's tools do. `remove` takes cells out of the named area only, as the Ignore roof area tool does for both, and refuses unexplored cells as it does. `added` also reports, for `noRoof`, `roofsToRemove` (painted cells under a roof) and `takenFromBuildRoof`, and for `buildRoof`, `roofsToBuild` (painted cells with no roof yet, which colonists roof where a wall or column within 6.9 cells holds it) and `takenFromNoRoof`. The roof areas cannot be renamed, deleted or created, and the other areas, such as snow clearing, stay refused.
- Growing zones (`list_zones`, `get_zone`, and `set_growing_zone`'s answer) count `roofedCells`, the zone's cells under any roof: a roof keeps out the sun, so a crop there grows only under a sun lamp. `thickRoofedCells` counts those under overhead mountain, which nothing can remove, and `roofRemovalQueued` the roofed cells already in the no-roof area, waiting for a colonist to remove their roof; both are left out when there are none.
- `get_map_view` has a `roofAreas` layer: `B` for the build-roof area, `N` for the no-roof area, as the game's roof overlay shows them. The `roof` layer still shows the roofs themselves.
- Not yet tried in game: painting the roof areas on a live map. The regression suite checks every cell rule and the roof counts on a map made without the game, with real roof grids and roof areas; writing the cells needs the game's path and region grids.

## 1.1.0+rimplayer.3 (rimplayer fork) - 2026-11-08

### Added

- Every pawn read (`list_pawns`, at any detail, and `get_pawn`) has `bleedRatePerDay`, the share of the pawn's blood lost per day (0.45 is the Health tab's 45%/day), and `hoursUntilBloodLossDeath`, the in-game hours until blood loss kills, both as the Health tab computes them: the rate is `HediffSet.BleedRateTotal`, and the hours are the game's `HealthUtility.TicksUntilDeathDueToBloodLoss` over 2,500. Both are null when the pawn is not bleeding, which, as on the tab, is a rate of 1% a day or less. The hours are null too for a Deathless pawn (Biotech), whom blood loss cannot kill, and are given past a day, where the tab says Won't bleed out soon.
- `get_colony_status.medical.bleedingPawns` lists the bleeding core pawns (colonists, slaves and prisoners), most urgent first: the fewest hours left, then the fastest bleeding, and those blood loss cannot kill last. Each has `id`, `name`, `role`, `downed`, `bleedRatePerDay` and `hoursUntilBloodLossDeath`. A `bleedingCorePawns` risk heads `topRisks` while blood loss would kill anyone within a day, when the Health tab gives a time; slower bleeding, and pawns it cannot kill, are only in `bleedingPawns`.
- `advance_time` stops on `bleedingCritical`: blood loss would kill a colonist in under 6 in-game hours (`AdvanceRun.BleedingCriticalHours`), time for a doctor to reach them and tend. Its event names the colonist, and its detail the hours left and the rate, such as `5.9 in-game hours until death by blood loss, bleeding 45% a day`. It is reported once per bleeding, even if tending some wounds buys time and the bleeding turns critical again, and again only after the colonist stops bleeding and bleeds again. What was reported is kept from run to run within one loaded game, like a fire's episode, and only an event emitted counts: unlike the other stops, a bleeding already critical when a run starts that was never reported, because it turned critical between runs or in runs too short to see it, stops that run on its first frame. Bleeding is checked every frame, as the game caches each pawn's bleed rate. It is in the default `stopOn`, ahead of `downed`: a colonist bleeding out in the same frame as they go down is reported as the cause.
- A pawn another pawn carries, such as a downed colonist being rescued or carried off by kidnappers, is not spawned while carried, so `list_pawns` left it out and `get_pawn` answered `Pawn not found.`. Both now find it, at its carrier's position, with `carriedBy` (the carrier's ids, name and role). `advance_time` watches carried colonists too, so going down, bleeding or dying while carried is seen. Commands still act only on spawned pawns: one on a carried pawn is refused with a 409 naming the carrier, instead of `Pawn not found.`.
- `get_pawn` for a pawn that is not on the map says, when it can, where the pawn is instead: dead with the corpse on this map, on another map (pass `mapId`), kidnapped by a faction, in a caravan, or on no map. Pawns under fog stay `Pawn not found.`.
- Not yet tried in game: the bleeding reads and stop on a live colony, and a carried pawn's read. The regression suite covers the bleeding logic on objects made without the game; a carried pawn needs a map.

### Changed

- `get_colony_status.medical.bleeding` counts pawns bleeding as the Health tab shows it (a total over 1% a day), the same pawns as `bleedingPawns`, instead of pawns with any wound that bleeds at all.

## 1.1.0+rimplayer.2 (rimplayer fork) - 2026-11-08

A fork of RimMCP 1.1.0 at `eduardoalvara2/rimcp`, branch `rimplayer`, for the rimplayer project. It keeps the package id `rakanssh.rimcp`, so saves keep matching; its versions are upstream's 1.1.0 with `+rimplayer.N` (the DLL's assembly version stays 1.1.0.0), and the mod list names it RimMCP (rimplayer fork) to tell it from the Workshop copy.

### Added

- `list_dialogs` lists the game's question to name the faction and settlement (`Dialog_NamePlayerFactionAndSettlement`, or `Dialog_NamePlayerFaction` or `Dialog_NamePlayerSettlement` when it asks for one name) as a naming dialog: kind `naming`, option `ok`, and `names`, with each field it asks `answer_dialog` for (`factionName`, `settlementName`), the text in its box (the game's suggestion), the longest name its own check takes (40 characters for a faction name, which also has to be a valid file name, and 64 for a settlement's), and the settlement's name now. `answer_dialog` answers it with those names: checked with the dialog's own rules before anything is typed, a refused name says which part of the check it failed and gives the game's message, and the names are then applied as its OK button does, with the dialog's own naming code, message and close, so the game does not ask again. In commitment mode the game renames the save then, as it does for the player. `dismiss_dialog` is unchanged, and the gravship's naming dialog (Odyssey) stays the player's.
- `advance_time` stops on three more conditions: `fire` (a fire starts on the ground in the Home area of a home map, unfogged cells only, and once more as that fire reaches 10 and then 50 burning cells; maps that are not homes and burning pawns are not watched, unlike the game's Fire in home area alert), `starving` (a colonist's hunger reaches Starving, or malnutrition starts or reaches a worse stage), and `stuck` (an awake, undrafted colonist who is not downed, on a home map, has been unable to reach any of the colony's beds or food for an in-game hour of the run). Like the others, each stops on a change during the run, not on what was so when it started, and its event names the colonist, or where the fire burns. A fire is one episode until no fire has burned in a Home area for 1,000 ticks, across runs of the same game, so a fire that shrinks and grows again while colonists fight it stops a run once for each size. `stuck` is checked every 250 ticks and only for a run whose `stopOn` has it, with its timers started over for each run, and each check tries one bed or food per district, nearest first, up to 24.
- Not yet tried in game: the scans of a live map for `fire`, `starving` and `stuck`, and answering a real naming dialog. The regression suite covers their logic on objects made without the game; an in-game smoke test after installing is still to do.
- `get_def` for a research project shows the rest of what the game checks before it can start: `requiresMechanitor`, and where the def sets them `hiddenPrerequisites`, `techprintCount`, `requiredStudied` and `requiresGravEngineInspected`; with a colony loaded, `canStartNow` and the `startRequirements` not met, each with a kind and a message.

### Changed

- `set_research_project` refuses a project that cannot start naming each requirement not met (`prerequisitesUnfinished`, `techprintsMissing`, `missingResearchBench`, `noMechanitor`, `notStudied`, `undiscovered`, `gravEngineNotInspected`), instead of only saying it cannot be started now.
- `advance_time`'s default `stopOn` adds `fire`, `starving` and `stuck`, so it is every condition but `letter`: what a player stops for. Pass `stopOn` to leave them out.

## 1.1.0+rimplayer.1 (rimplayer fork) - 2026-11-08

Built as version 1.1.0.

### Added

- `list_dialogs` lists every window that pauses the game, and every dialog. Only one the game opened on its own, such as a scenario's intro or a mod's notice, is the agents': if RimMCP can read it, it gets an interactionId for `answer_dialog`, answered under `dismiss_dialog`'s access, so only with Decisions on, and otherwise, for a mod's window such as an update notice, `dismiss_dialog` closes it as its close button does, where it has one (a repeat is alreadySatisfied). Windows that appear just after the player clicks or presses a key, such as a psycast's confirmation, the player's own windows and anything opened over those that pause the game (a save list's delete confirmation, the options, mod settings, trade, caravan forming, a bill's settings, the main menu), confirmations of something that cannot be undone, comms console calls, dialogs an agent's call opened that were then left to the player, and the game's questions during an operation, such as approving a new colony, are listed as the player's, with the reason. RimMCP's own windows are not listed, and a dialog that cannot be read is listed with the error instead of failing the list.
- `list_surgeries` lists the operations a pawn's Health tab offers, with the same recipes, body parts and disabled options, and the operations queued; `add_surgery_bill` queues one as clicking it does, asking first where the game does (`answer_dialog`), and warns when no doctor has the skills or there is no medical bed.
- `get_mechanitor` shows a mechanitor's bandwidth, control groups with their work modes and mechs, and mechs beyond the bandwidth; `set_mech_control` moves overseen mechs into a group and sets its work mode, as the Mechs tab and control group buttons do.
- `scripts/build-local.ps1` builds the DLL and the proxy on Windows, installing the .NET 8 SDK and Rust for the current user if missing, runs the regression suite with `-Test`, and installs a local copy of the mod with `-Install`: staged next to `Mods\RiMCP` and swapped in, so a failure leaves the previous copy whole, and refused while RimWorld or a program from the copy, such as its MCP proxy, runs, and when the copy is or holds a link.

### Changed

- `advance_time` is refused while a window pauses the game, naming it and whether it is the player's, instead of sitting at its first tick; `get_advance` lists the windows holding time as `time.heldByWindows`, RimMCP's own marked `RimMCP:`.
- A dialog that can no longer be read is left to the player instead of failing every read, and a mod's dialog read again stays a mod's for the Safety setting.

## [1.1.0] - 2026-10-18

### Added

- Sessions: `save_game`, `load_game` and `leave_game` run as operations you can follow with `get_operation` and stop with `cancel_operation`, and `list_saves` shows each save's game version, whether its mods match, and commitment mode. A save counts only once verified, replacing a loaded colony always needs a save first or an explicit discard, and saving first holds the game unchanged until it is replaced, so nothing done after the save is lost. Commitment mode keeps the game's own save rules.
- New games, set up through the game's own screens while you watch:
  - `start_setup` opens them for a scenario from `list_scenarios`, with `get_setup`, `setup_back` and `cancel_setup`.
  - Storyteller and difficulty, preset or custom (`set_setup_storyteller`), the world (`generate_world`), and the starting site: `find_starting_sites` searches by biome, terrain, water, climate, growing period and distance to other factions, and `choose_starting_site` picks one.
  - Ideoligion: classic, a preset, a saved one, or a custom fixed or fluid one (`set_setup_ideoligion`), with its memes, culture, styles and precepts edited under the screen's own rules (`edit_setup_ideoligion`).
  - Colonists (`set_starting_pawns`): arranged, given an age and xenotype, rerolled until they meet skill, passion, trait, work, xenotype and age criteria, and named.
  - A change you make stops the agent's next step, and `start_game` asks you to approve the colony. The Unattended game setup setting, off by default, lets agents start it without you, answering the game's questions with `answer_confirmation`.
- `get_events` shows what the game is telling you: alerts and who or what they concern, letters waiting and their options, and the History tab, read from where the agent left off, with nothing repeated and entries the game dropped first flagged.
- Decisions, which count as ordinary play, need no approval and show in game: `answer_letter` answers a letter in its own dialog or dismisses it, `respond_to_quest` accepts a quest for a chosen reward and colonist or dismisses or restores it, and `answer_dialog` answers what an answer leads to, such as a letter's next page, an "are you sure?" box, or how many to pick up. Dialogs stay on screen so you can answer first, and a waiting dialog stops `advance_time` until it is answered.
- `assign_owner` sets the owners of beds, thrones and other buildings, with the Assign dialog's checks.
- Right-click menus and buttons:
  - `list_pawn_actions` and `choose_pawn_action` use the menu a pawn gets on a thing or cell, so any order it offers works, such as capture, arrest, strip, or prioritize.
  - `list_gizmos` lists the buttons a building or pawn shows, `set_building_toggle` sets building toggles (power switches, doors held open, medical beds, hold fire), and `use_gizmo` presses the rest, checking targets with the button's own rules, such as a mortar's attack or a psycast.
  - Mods' options and buttons work too, marked with their mod. A Safety setting, Mods' menu options and buttons (on by default), turns them off.
- Guardrails for all of these: what agents cannot finish yet, such as trading, rituals, the comms console, the world map, or a child's growth moment, is refused before anything runs or left to you; choices read before something changed, or before another game was loaded, are refused; and agents wait while you are aiming and never touch your selection, camera or map.
- New access groups: Decisions (on under Full control and Control) and Game setup (Full control only).

### Changed

- `list_quests` follows the Quests tab: by tab, without hidden quests, with what an offer needs (requirements, expiry, reward choices, who can accept it) and, once accepted, the accepter and the reward chosen.
- Settings are in tabs (Connection, Access, Activity, Safety) under an Agents on master switch. Access browses and searches every tool by group and turns single tools on or off on top of the access level, and Activity counts calls per tool.
- Agents see only the tools that are on, and are told when that changes.
- A RimMCP button on the bottom bar shows whether agents are on and working, and pauses them or changes the access level in a click.
- Tool descriptions are about a fifth shorter, so agents load less.
- While RimWorld loads, saves or changes games, agents get a 503 instead of reading a half-built game.

## [1.0.0] - 2026-10-17

### Added

- Colony control, through the game's own designators, right-click menus, and placement rules, so every in-game check applies and refusals give the game's reason:
  - Storage and zones: `set_zone` (stockpiles and growing zones), `set_storage` (priority, categories, things, and special filters such as AllowRotten), `set_area` (Home and allowed areas), and `set_growing_zone`.
  - Work orders: `designate` (forbid, haul, cut, harvest, chop, mine, deconstruct, cancel), and `check_placement` and `place_blueprint` for building.
  - Pawn orders: `order_pawn` to equip, wear, move, attack, rescue, or tend, replacing the current job or queued like a shift-click.
- Time control: `set_game_speed`, and `advance_time`/`get_advance` to run the game for 0.01 to 24 in-game hours, then pause, stopping early when a colonist dies, goes down, is hurt, or has a mental break, or on hostiles, low food, or a threat letter.
- Finding things: `find_things`, and `get_map_view`, a text map with colonists by letter, other pawns, terrain, and roof and designation layers.
- Blockers that say why something will not happen: bills (ingredients, workers, fuel, power), growing zones (season, sowable cells, growers, and why sown plants are not growing), blueprints (materials, skill), and research (no bench or researcher).
- The dashboard tracks food: stored meals and raw nutrition, daily use, days left, what rots soon, and the trend, with risks for hungry pawns, blocked bills, and rotting food.
- Safe retries: commands that create or order something take `requestId`, and a retry returns the first result without acting again, so it never undoes what a player did in between.

### Changed

- Access presets (Full control, Control, Administration, Read only) and per-group switches replace Allow changes, which carries over. Turning RimMCP or a group off applies at once, also to requests still waiting and a running advance, and writes and time control are refused during Multiplayer sessions.
- Stockpiles report what they accept, by category and including special filters, building limits, and quality and hit point ranges, instead of an ingredient summary.
- Power is reported in watts (`netPowerW`), and stored food uses the game's Low food count (`storedHumanNutrition`).
- The proxy runs tool calls concurrently, honours cancellation, and fails at once when RimWorld is in the background with Run in background off.
- Def search and large reads cost far less main-thread time, bursts of requests are spread across frames, and the settings log shows each request's main-thread time.

### Fixed

- Commands follow the game's own checks for drafting, prisoner modes (Convert now works), work priorities, animal designations, and bills.
- Reads no longer log errors, modify the save, or reveal anything under fog, such as an unopened ancient shrine's sleepers and loot.
- `get_environment` lists real rooms only, without doorways or the outdoors.
- `search_defs` finds modded def subclasses, and `list_work` lists every active work type.

## [0.5.6] - 2026-10-13

### Changed

- Declare RimWorld 1.7 support while retaining the shared .NET Framework 4.7.2 build for RimWorld 1.6 and 1.7.

### Fixed

- Read research definitions, quests, ideologies, and world context at the main menu without dereferencing a missing game or world. Research progress and completion are omitted when no colony is loaded.
- Return a controlled error when setting research without a loaded colony.
- Include the remaining repeat count in production bill reads.
- Return numeric pagination cursors matching the MCP tool input schema.
- Respond to MCP `ping` requests without contacting the game bridge.

## [0.5.5] - 2026-10-11

### Changed

- switched proxy to rust, package size down to 8mb 🦀

## [0.5.4] - 2026-10-11

### Changed

- reduced mod size by trimming dependencies

## [0.5.3] - 2026-10-10

### Added

- Mod inspection with loaded and installed inventories, load order, loader warnings, dependencies, assemblies, and settings panel discovery.
- Recent in-game log reads with severity/search filters and optional stack traces.
- Game settings tools for storyteller/difficulty presets, background running, pause options, autosave interval, and master/music volume.
- Defining mod information in definition search and detail results.
- Paused state and selected game speed in game context.
- A manual GitHub Actions workflow to sign and notarize both macOS helpers using a Developer ID Application certificate.

### Changed

- Rename the mod to **RimMCP** across the artwork, descriptions, settings, and messages.
- Rename the access toggle to **Allow changes** to cover colony commands and global game settings.

### Fixed

- Return game context at the main menu without requiring a loaded world.

## [0.5.2] - 2026-09-28

### Changed

- Rename the public mod to **Rim-MCP**, including its artwork, descriptions, settings, and messages. Existing package IDs, MCP configuration, and internal paths stay unchanged.

## [0.5.1] - 2026-09-28

### Changed

- Use the full Workshop description in the in-game mod page, with plain headings and lists that render in RimWorld.

## [0.5.0] - 2026-09-28

### Fixed

- Simplify the pawn-assignment tool schema so MCP clients can discover all arguments.
- Bundle native .NET runtime libraries and their license notices with the connection helpers.
- Restore the Unix helper's executable permission when it is missing after installation.

### Added

- An **Allow colony changes** setting, enabled by default, that can block commands while keeping reads available. Existing configurations without this setting also default to enabled; a saved off setting stays off.
- Workshop description and final 1280 × 720 preview artwork with editable SVG source.
- Clean release packaging with a Workshop folder, ZIP archive, and SHA-256 checksum.

### Changed

- Settings labels and bridge health responses now distinguish read-only access from enabled colony commands.

## [0.4.0] - 2026-09-27

### Fixed

- Reject target-count bills for recipes whose product counter cannot support them.
- Contain HTTP client disconnect and listener shutdown errors within the bridge request handler.
- Calculate calendar fields from absolute game ticks while preserving elapsed `ticksGame` values.
- Return protocol errors for malformed MCP request envelopes without terminating the proxy.

### Added

- Production command tools and endpoints: `set_bill` (update/delete) and `add_bill_to_workshop` (add).
- Request queue limits and rate limiting (HTTP 429) to the main thread dispatcher under heavy load.
- Multi-platform packaging and launcher scripts to support native proxy execution on Windows, Linux, and macOS (arm64/x64).

### Changed

- Paginate threats before constructing response objects and count threat categories in a single pass.
- Increased the main thread dispatch wait timeout to 10 seconds.
- Downgraded the MCP proxy target framework from .NET 10.0 to .NET 8.0.
- Updated the mod's configuration generator to delegate execution to the platform-specific launcher scripts.
- Replaced duplicated bridge route and MCP tool catalogs with a shared manifest and `/v1/tools` introspection endpoint.
- Made def search mod-aware by discovering loaded def subclasses and accepting arbitrary def kind names.
- Made production bill repeat/store mode settings def-driven, including stockpile targets for `SpecificStockpile`.
- Consolidated repeated summary DTO construction for pawn, thing, building, workshop, bill, and faction responses.
- Hardened JSON serialization so non-finite numeric values emit `null` and unsupported DTO values fail explicitly instead of falling back to `ToString()`.

## [0.3.0] - 2026-05-26

### Added

- Typed MCP command tools and PUT endpoints for pawn drafting, work priorities, Assign-style pawn settings, animal designations, animal training, and current research selection.
- Ideoligion read tools for listing and inspecting Ideology beliefs when the expansion is active.
- MCP tool annotations for read-only reads, idempotent commands, destructive commands, and closed-world bridge behavior.
- Pawn assignment, ideoligion identity, and animal training details in pawn reads when requested with full detail or targeted `include` sections.
- Request body size limit for command payloads.
- MIT license, contributor licensing note, and RimWorld/Ludeon EULA notice.

### Changed

- Refactored bridge dispatch to a unified method-aware route table shared by read and command endpoints.
- Simplified MCP proxy endpoint definitions with method-aware metadata and templated paths.
- Moved map, pawn, and def resolution into shared command helpers where appropriate.
- Removed `sinceTick`/`notModified` read controls so reads always return current game state.
- Reduced allocation work in zone and resource reads by avoiding unnecessary cell/resource rescans.
- Removed the hard-coded local RimWorld managed DLL path from the mod project.

## [0.2.0] - 2026-05-25

### Added

- HTTP read endpoints for production workshops and available bills
- MCP tools for production workshops and available bills

## [0.1.0] - 2026-05-25

### Added

- Initial RimWorld mod bridge exposing a token-protected local HTTP endpoint.
- Bundled MCP stdio proxy for MCP-compatible clients.
- Shared read response envelope with schema version, tick, map id, truncation metadata, and structured data payloads.
- Core colony inspection tools: game context, colony status, pawns, resources, work, production, bills, zones, environment, power, research, quests, world, threats, buildings, and defs.
- Detail, include, pagination, map selection, and ids-only controls for supported read tools.
- RimWorld mod settings flow for copying MCP client configuration.

[1.1.0]: https://github.com/rakanssh/rimcp/releases/tag/v1.1.0
[1.0.0]: https://github.com/rakanssh/rimcp/releases/tag/v1.0.0
[0.5.4]: https://github.com/rakanssh/rimcp/releases/tag/v0.5.4
[0.5.3]: https://github.com/rakanssh/rimcp/releases/tag/v0.5.3
[0.5.2]: https://github.com/rakanssh/rimcp/releases/tag/v0.5.2
[0.5.1]: https://github.com/rakanssh/rimcp/releases/tag/v0.5.1
[0.5.0]: https://github.com/rakanssh/rimcp/releases/tag/v0.5.0
[0.4.0]: https://github.com/rakanssh/rimcp/releases/tag/v0.4.0
[0.3.0]: https://github.com/rakanssh/rimcp/releases/tag/v0.3.0
[0.2.0]: https://github.com/rakanssh/rimcp/releases/tag/v0.2.0
[0.1.0]: https://github.com/rakanssh/rimcp/releases/tag/v0.1.0
