# RimMCP

<a href="https://steamcommunity.com/sharedfiles/filedetails/?id=3809698070"><img src="RiMCP/About/Preview.png" alt="RimMCP: agentic intelligence for the Rim" width="640"></a>

**[RimMCP - AI Agents for RimWorld (MCP)](https://steamcommunity.com/sharedfiles/filedetails/?id=3809698070)** on the Steam Workshop

RimMCP is a RimWorld mod that connects MCP-compatible clients to a live colony. The mod runs inside RimWorld and hosts a token-protected HTTP endpoint on `127.0.0.1`. MCP clients can then use the bundled stdio proxy.

Supports RimWorld 1.6 and 1.7 unstable using the same mod DLL. The 1.7 engine's compatibility layer loads the assembly built against the 1.6 API; future unstable updates may require further testing.

Current MCP tools:

| Area | Tools | Use these for |
| --- | --- | --- |
| Colony overview | `get_game_context`, `get_colony_status` | Current map, time, paused state, selected game speed, storyteller/difficulty, loaded mods, colony dashboard, top risks, bleeding colonists, most urgent first, with the hours until blood loss kills each, and food in nutrition with days left, what will rot soon, and its recent trend. |
| Mods and logs | `list_mods`, `get_mod`, `get_game_log` | Inspect loaded/installed mods, load order, loader warnings, settings panel names, and retained game logs with optional stack traces. |
| Game settings | `get_game_settings`, `set_game_settings` | Read or change storyteller/difficulty presets, background running, pause options, autosave interval, and master/music volume. |
| Pawns and work | `list_pawns`, `get_pawn`, `list_work`, `set_pawn_drafted`, `set_pawn_work_priority`, `set_pawn_assignment` | Inspect colonists, prisoners, animals, threats, work priorities, schedules, policies, medical care, allowed areas, and other Assign-tab settings. Each pawn shows its bleed rate and the hours until it dies of blood loss, as its Health tab computes them, and a pawn another pawn carries, such as a downed colonist being rescued or kidnapped, is still listed, with who carries it. |
| Production | `list_production`, `get_bill`, `set_bill`, `list_workshops`, `get_workshop`, `add_bill_to_workshop` | Inspect bills and workbenches, see whether each bill will run and what stops it, add new bills, update bill settings, suspend bills, or delete bills. |
| Map state | `list_resources`, `list_zones`, `get_zone`, `list_buildings`, `get_building`, `get_environment`, `get_power`, `list_threats` | Inspect stockpiles, growing zones, buildings, storage contents, weather, rooms, power grids, hazards, fires, and hostile activity. Growing zones count the cells under a roof, which get no sunlight. |
| Items, map view and placement | `find_things`, `get_map_view`, `check_placement` | Find items with positions, stack sizes, forbidden state and storage; see part of the map as a character grid with colonists, pawns, blueprints, and optional layers of roofs, the build-roof and no-roof areas, and orders; check where a building fits. Unexplored areas stay hidden. |
| Zones, storage, areas and owners | `set_zone`, `set_storage`, `set_growing_zone`, `set_area`, `assign_owner` | Create and edit stockpiles and growing zones, set what storage accepts and its priority, choose a zone's crop, paint the Home, allowed and roof areas (colonists remove roofs in the no-roof area and build them in the build-roof area, as with the game's roof tools), and assign beds, thrones and other buildings to their owners. Each rejected cell is listed with its reason, and growing zones report what stops them, such as the season. |
| Trading | `list_traders`, `get_trade`, `execute_trade` | Trade with caravans and orbital ships through the game's own trade session, with no window: see who is trading on the map and which colonists could negotiate (able to talk, a path to the caravan, or a powered comms console and orbital trade beacon for a ship), read the goods with prices at that negotiator's rates, and deal. Every count is checked against what each side has and will trade and the silver is checked as the window does; the deal runs with the game's own execution, and the answer verifies silver and item counts before and after and where the goods went (a caravan's are placed near its trader, a ship's arrive by drop pod near a beacon, and a ship takes only goods near a powered beacon). Tradeables have a stable key: `def[:stuff][@quality]` for items, with `#2`, `#3` where one key covers several tradeables (hit point bands, unique items), and `pawn:<thingId>` for animals and people. |
| Orders | `designate`, `place_blueprint`, `order_pawn` | Forbid, unforbid, haul, cut, harvest, chop wood, mine, deconstruct or cancel; place blueprints for colonists to build; and order a pawn to equip a weapon or wear apparel (at once or queued like shift-click), or, drafted, to move, attack, rescue, or tend. All with the game's own checks. |
| Right-click menu and buttons | `list_pawn_actions`, `choose_pawn_action`, `list_gizmos`, `set_building_toggle`, `use_gizmo` | Give any order a pawn's right-click menu offers, such as capturing, arresting, stripping, or prioritizing a job, and press the buttons a selected building or pawn shows: power switches, holding a door open, a turret's hold fire, a mortar's attack, or an ability on a target. Options and buttons come from the game's own lists and are chosen as a click does, a target is checked with the button's own rules before it is given, and flows agents cannot finish, such as rituals, are refused before anything runs (trading has its own tools, below). Mods' options and buttons work too, marked with their mod. |
| Health and mechs | `list_surgeries`, `add_surgery_bill`, `get_mechanitor`, `set_mech_control` | Queue operations as a pawn's Health tab does, with its recipes, body parts, disabled options and confirmations, and warnings such as no doctor with the skills or no medical bed; and see a mechanitor's bandwidth and control groups, move overseen mechs between groups, and set a group's work mode (Work, Escort, Recharge, Self-shutdown), as the Mechs tab and control group buttons do. |
| Animals | `designate_animal`, `set_animal_training` | Set hunt/tame/slaughter designations and colony animal training flags. |
| Research and ideology | `get_research`, `set_research_project`, `list_ideoligions`, `get_ideoligion` | Inspect or choose research, see what stops it progressing, and inspect Ideology data when the expansion is active. A project that cannot start yet is refused naming what it needs, such as its prerequisites, a research bench, or a mechanitor, and `get_def` shows each start requirement. |
| Test incidents | `fire_incident` | For testing only, and off unless you turn it on (see **Test incidents** below): fire a trade caravan, an orbital trader or a small raid on the map now, so a colony can practise trading or capturing a downed raider. Nothing else can be fired. |
| Time | `set_game_speed`, `advance_time`, `get_advance` | Pause or set the speed, or run the game for a set time, from 25 ticks to 24 in-game hours, and pause again. A run stops early on deaths, colonists who would die of blood loss within 6 in-game hours, downed or injured colonists, starving colonists, colonists stuck where they cannot reach a bed or food, mental breaks, fires in the Home area, hostiles, low food, or threat letters, and reports what happened. It is refused while a window pauses the game, naming it, and `get_advance` lists the windows holding time. |
| Events and decisions | `get_events`, `list_quests`, `answer_letter`, `respond_to_quest`, `answer_dialog`, `list_dialogs`, `dismiss_dialog` | See what the game is telling the player: alerts with the pawns, things, or places they concern, letters waiting with a choice letter's options, and the History tab's letters and messages, page by page from where the agent last read. Quests are listed as the Quests tab shows them, with whether each offer can be accepted, its rewards, and who can accept it. Answer letters and accept, dismiss, or restore quests through the game's own dialogs and checks; a question that follows, such as an "are you sure?", is answered page by page, and stays on screen so the player can answer it instead. Decisions show in game. Dialogs the game opens on its own, such as a scenario's intro, are listed too: those RimMCP can read are answered with `answer_dialog` (with Decisions on), including the game's question to name the faction and settlement, answered with the names under the dialog's own rules, and mods' windows with a close control, such as update notices, are closed with `dismiss_dialog`. Windows the player just opened, their own windows and what opens over them, and confirmations that cannot be undone stay the player's. |
| World and defs | `list_world`, `search_defs`, `get_def` | Inspect factions, world objects, and loaded definitions with their defining mod when known. |
| Sessions | `list_saves`, `save_game`, `load_game`, `leave_game`, `get_operation`, `cancel_operation` | List saves, and save, load, or return to the main menu. Each runs as an operation that keeps reporting through loads. A save counts only once verified, and replacing a loaded colony always asks for a save first or an explicit discard. |
| New games | `list_scenarios`, `get_setup`, `start_setup`, `set_setup_storyteller`, `generate_world`, `find_starting_sites`, `choose_starting_site`, `set_setup_ideoligion`, `edit_setup_ideoligion`, `set_starting_pawns`, `start_game`, `setup_back`, `cancel_setup`, `answer_confirmation` | Set up a new colony through the game's own screens, in view: scenario, storyteller and difficulty (a preset or custom), world, starting site (searched by biome, terrain, water, climate, growing period and distance to other factions), ideoligion (classic, a preset, a saved one, or a custom fixed or fluid one, with its memes, precepts, rituals, roles, buildings and the rest edited under the screen's rules), and colonists arranged, given age and xenotype preferences, and rerolled until they meet skill, trait, work, xenotype and age criteria. Each change takes the setup revision the agent last read, a change by the player stops the agent's next step, and the player approves the start. Screens RimMCP does not drive are left to the player. |

Most tools accept the same optional read controls:

- `mapId`: select a specific loaded map; defaults to the current map.
- `detail`: `summary`, `normal`, or `full`; defaults to compact `summary`.
- `include`: request specific heavier sections without switching to full detail, such as animal `training`.
- `limit` and `cursor`: page large lists deterministically.
- `idsOnly`: return identifiers only when supported by the list tool.

Tool responses use a shared envelope:

```json
{
  "schemaVersion": "1",
  "tick": 12345,
  "mapId": "1",
  "truncated": false,
  "data": {}
}
```

The MCP proxy mirrors the structuredContent JSON in text content for clients that do not read structured tool results yet (**cough, cough** opencode).

## Using The Mod

1. Open the RimMCP mod settings page.
2. On the **Connection** tab, click **Copy MCP config** to copy the MCP configuration including the URL and token.
3. Configure your preferred Agentic client with MCP support. (Most agents can configure themselves given the MCP config info)
4. Choose what agents may do on the **Access** tab. New installs start with **Full control**. Pick **Control** (everything but game settings and game setup), **Administration** (reads, colony settings and time control), or **Read only**. To go further, turn whole groups on or off (Observe, Diagnostics, Time, Administration, Orders, Pawn control, Decisions, Game settings and Game setup), or open a group and switch single tools; search finds a tool by name or description. Agents see only the tools that are on, and their tool list updates when you change it. Settings from earlier versions carry over: Allow changes on becomes Full control, and off becomes Read only.

**Agents on** at the top of the settings is the master switch: off stops the bridge, and your access choices are kept for when you turn it back on. In game, the RimMCP button on the bottom bar shows whether agents are on and working; click it to pause them, change the access level, or open the settings. The **Activity** tab counts each tool's calls and lists recent requests. On the **Safety** tab, **Unattended game setup** lets agents answer the game's questions during setup and start the colony without asking you; it is off by default. **Mods' menu options and buttons**, on by default, lets agents use what mods add to right-click menus and buttons; RimMCP cannot check those before they run, so they run as your click would, with a target checked against the mod's own rules.

If you want MCP to work while RimWorld is alt-tabbed or minimized, enable RimWorld's **Run in background** setting. Otherwise requests will timeout when the game is not in focus.

### Test incidents (off by default)

`fire_incident` fires one allow-listed incident (`TraderCaravanArrival`, `OrbitalTraderArrival` or `RaidEnemy`, with optional `points`, default 60) on demand. It is for testing only, so it is gated behind a mod setting that is off by default: while it is off, the tool refuses with a clear reason and nothing reaches the game. No tool can switch the setting on; only you can, and the tool also needs the **Game settings** group (on with Full control, off with Control).

To turn it on, either:

- tick **Allow test incidents (testing only)** on the **Safety** tab of the RimMCP mod settings, or
- with RimWorld closed, add `<allowTestIncidents>True</allowTestIncidents>` inside the `<ModSettings Class="RiMCP.Settings.RiMCPSettings">` block of the mod's settings file in RimWorld's Config folder. On Windows that is `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\`, and the file is `Mod_RiMCP_RiMCPMod.xml` for a copy installed as `Mods/RiMCP` (the local build) or `Mod_3809698070_RiMCPMod.xml` for the Workshop copy. The key is absent when the setting is off; setting it to `False` or deleting the line turns it off again.

Current command HTTP endpoints:

- `PUT /v1/pawns/{pawnId}/drafted`
- `PUT /v1/pawns/{pawnId}/work/{workTypeDefName}`
- `PUT /v1/pawns/{pawnId}/assignments/{assignmentKind}`
- `PUT /v1/bills/{id}`
- `POST /v1/workshops/{workshopId}/bills`
- `PUT /v1/animals/{pawnId}/designation`
- `PUT /v1/animals/{pawnId}/training`
- `PUT /v1/research/current`
- `PUT /v1/time/speed`
- `POST /v1/time/advance`
- `PUT /v1/designations`
- `PUT /v1/zones`
- `PUT /v1/storage`
- `PUT /v1/areas`
- `POST /v1/pawns/{pawnId}/orders`
- `GET /v1/traders`
- `GET /v1/trade` and `POST /v1/trade`
- `POST /v1/incidents` (refused unless Allow test incidents is on)

## Building From Source

Requirements:

- RimWorld 1.6 reference DLLs, even when running RimWorld 1.7 (see below)
- .NET 8 SDK or newer
- Rust (the toolchain is pinned in `rust-toolchain.toml`)
- Python 3
- A native C/C++ toolchain.
- Linux requires `musl-tools`

On macOS with Homebrew:

```sh
brew install --cask dotnet-sdk
```

Build the RimWorld mod DLL against **RimWorld 1.6**. Set `RimWorldManagedPath` to its managed-assembly directory, or a retained copy containing `Assembly-CSharp.dll`, `UnityEngine.CoreModule.dll`, and `UnityEngine.IMGUIModule.dll` from the same 1.6 installation. On Windows/Linux the normal location is `RimWorld/RimWorldWin64_Data/Managed` or `RimWorld/Data/Managed`, depending on the installation; on macOS it is inside the app bundle:

```sh
dotnet build RiMCP/Source/RiMCP.csproj -c Release \
  -p:RimWorldManagedPath="/path/to/RimWorld/RimWorldMac.app/Contents/Resources/Data/Managed"
```

For PowerShell, pass the property as one quoted argument:

```powershell
dotnet build RiMCP/Source/RiMCP.csproj -c Release "-p:RimWorldManagedPath=D:\RimWorld-1.6\Data\Managed"
```

The output is `RiMCP/Assemblies/RiMCP.dll`, targeting .NET Framework 4.7.2 for RimWorld 1.6 and 1.7. The DLL version is read from `RiMCP/About/About.xml`.

RimWorld 1.7 puts its assemblies in the game root and uses .NET 10 with `UnityStubs.dll` in place of the Unity modules. Those are **runtime assemblies, not compatible build references** for this shared `net472` DLL. Keep the 1.6 references before switching branches, or obtain them from a separate 1.6 installation. The build checks the reference version and required modules and reports a specific error if the 1.7 directory is used. Do not distribute the game reference DLLs with the mod.

On Windows, `scripts/build-local.ps1` runs this build and the proxy build below for a local install in one step: it installs the .NET 8 SDK and Rust for the current user if they are missing (no admin rights, no PATH changes outside the script), builds the DLL and the proxy, and with `-Test` runs the compatibility regression suite. `-Install` copies the mod to RimWorld's `Mods\RiMCP` folder, refusing while RimWorld runs, and carries the Workshop copy's settings over to it:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-local.ps1 -Test
```

Build and publish the MCP proxy for the current platform:

```sh
python scripts/publish-mcp-proxy.py
```

The **Build MCP helpers** workflow builds all four platforms on native runners. Mac helpers are signed and notarized. Published executables are at:

- `RiMCP/Tools/McpProxy/win-x64/RiMCP.McpProxy.exe`
- `RiMCP/Tools/McpProxy/linux-x64/RiMCP.McpProxy`
- `RiMCP/Tools/McpProxy/osx-x64/RiMCP.McpProxy`
- `RiMCP/Tools/McpProxy/osx-arm64/RiMCP.McpProxy`

## License

RimMCP's original code and assets are licensed under the MIT License. See [LICENSE](LICENSE).

RimWorld, Ludeon Studios, and related names and assets are owned by Ludeon Studios. RimMCP is an unofficial community mod and is not endorsed by Ludeon Studios. Distribution and use as a RimWorld mod remains subject to the RimWorld EULA and any platform terms that apply, such as Steam Workshop terms.
