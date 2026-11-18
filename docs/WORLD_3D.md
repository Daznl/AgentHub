# 3D world (experimental)

The owner's idea, 2026-11-18: the coding space opens up into three dimensions. It shows how files, branches, sessions, APIs, frontends and data connect, all explorable, with every running session moving through it as it works. AgentHub reads; Godot draws.

## How it works

```text
MainWindow  🌐 World  (or 🌐 3D in a Timeline window)
    WorldService (runs while Godot is open)
        every 0.6 s   new lines of each live transcript -> TranscriptActions -> events-<start>.ndjson   (appended)
        every 3 s     which sessions are live, panes, orchestrators          -> world.json when that changes
        beside that   each project's git tree and files (cached)             -> world.json as each project arrives
    GodotLauncher -> Godot project world/ (beside AgentHub.exe)
        polls world.json (MD5, every second): islands, cities, trees
        tails the events file (every 0.2 s): crabs hop, buildings light up, orchestrators speak
```

Everything lives in `C:\data\agenthub\world\` (`AGENTHUB_WORLD_DIR` to move it): `world.json`, `events-*.ndjson` (the newest five are kept), `world-service.log` (timings and problems) and `godot.log`.

- **One reader.** Only AgentHub reads git, `gh`, transcripts and session records. Godot reads only the two files and has no network code.
- **Connections come from code.** Snowflake, Azure, APIs and frontends will be worked out by AgentHub from each repository's code (configuration, Bicep, SDK calls, SQL, routes), never by connecting to those services.
- **No setup in the sessions.** Claude Code and Codex already write every tool call to their transcripts. AgentHub tails them, reading only the bytes appended since the last look (`TranscriptTail`).

## Which projects and sessions

- **Projects:** the registered repositories, plus the repository of every live session (worktrees count as their main repository).
  - A project is read three at a time.
  - Its island appears at once with its files; its git tree follows when read (git log, branch tips, pull requests, squash matching, session attribution).
  - The tree is read again after a session of that project commits, pushes, merges or switches branch, or every 5 minutes. The file list is read again after a write, or every minute.
- **Live sessions:** a Claude Code or Codex transcript written in the last 15 minutes, plus any transcript a Cockpit pane is bound to. AgentHub's own sessions are left out (orchestrators, reviews, helpers, scratch folders).

## world.json, schema 2

Types are in `src/AgentHub/Services/World/WorldSnapshot.cs`, in camelCase. Godot refuses any other schema.

| Field | What |
| --- | --- |
| `eventsFile` | the events file beside it (new each time the service starts) |
| `projects[]` | id (hash of the root), name, root, trunk, folders (root and worktrees), then the Timeline's `rows`, `branches`, `commits`, `sessions`, plus `pullRequests`, `worktrees` and `files` (path and size from `git ls-files`; the largest 2,500 when there are more, flagged `filesTruncated`) |
| `live[]` | live sessions: id (the transcript path), agent, title, project, folder, branch (from the folder's git HEAD), pane |
| `orchestrators[]` | id (pane), title, worker (the pane's transcript), state, current template step |

**Events** (`WorldEvent`, one JSON object per line): `seq`, `time`, `session`, `kind`, `project`, `file` (relative to the project), `text`, `orchestrator`.

The kinds:
- **Files:** read, edit, write, search.
- **Git:** commit, push, merge, pr, branch.
- **Places:** snowflake, azure, web.
- **Commands and tools:** bash, build, test, agent, tool.
- **Talk:** prompt, reply, orchestrate.

Commands are sorted into these kinds by `TranscriptActions.Command` (for example `git commit`, `gh pr merge`, `snow sql`, `az`, Bicep, `dotnet test`). MCP tools are sorted by their name.

## What you see

- **Islands.** Each project is an island on a ring, facing the hub in the middle.
  - **The city:** in front, its files. Folders are districts, sized by how many files they hold. Files are buildings: colour by kind, height by size. The top-level folders are labelled.
  - **The git tree:** behind the city.
    - `main` is the trunk, with time going upwards. A long history is squeezed so the tree stays in scale with its city.
    - Branches fork out in the 2D Timeline's colours and curve back in where they merged.
    - Past sessions are orbs tied to their commits.
    - Pull requests are rings.
    - Worktrees are hexagon pads.
- **The hub.** GitHub (a white cloud), Azure (a blue cloud), Snowflake (ice crystals) and the Web (a globe).
- **Crabs.** Every live session is AgentHub's crab, captioned with its agent, pane, title and branch. It rests at its branch's tip, or at its city's front when on `main`. Then each event moves it:
  - **read, edit, write:** hops to the file's building, which lights up (brightly for an edit or write, faintly for a read). A new file it writes stands in its folder.
  - **commit, branch:** hops to its branch's tip. A **merge** goes to the top of the trunk.
  - **push, pull requests:** GitHub. **Snowflake, Azure, the web:** their landmark.
  - **builds, tests, other commands:** the front of its city.
  - **prompts and replies:** it waves where it stands, with the text as its caption.
  - It sleeps after 2 quiet minutes. Parallel sessions on parallel branches are separate crabs on separate lanes.
- **Orchestrators.** A big crab on a cloud above its worker's crab, captioned with its state and current template step. Each instruction it gives sends a flash of lightning down to the worker.
- **The feed** (bottom right) lists the latest actions.
- **Controls:**
  - Left drag turns, right or middle drag pans, the wheel zooms.
  - WASD moves, Q and E go down and up, Shift moves faster.
  - Click selects (anything, crabs included).
  - Double-click or F flies to the selection and follows a crab.
  - R resets the view, Esc clears the selection.

## Files

- `src/AgentHub/Services/World/WorldSnapshot.cs`: the format, building a project from a `TimelineModel`, worktree and file parsing, `Locate` (which project and relative path a path belongs to), `WorldStore`.
- `src/AgentHub/Services/World/TranscriptActions.cs`: transcript lines become actions, commands become kinds; `TranscriptTail`.
- `src/AgentHub/Services/World/WorldService.cs`: the loop, live session discovery, the project cache, the events file.
- `src/AgentHub/Services/World/WorldSource.cs`: worktrees; `GodotLauncher`.
- `src/AgentHub/MainWindow.World.cs`: 🌐 World; panes and orchestrators fed to the service. `TimelineWindow.World.cs`: 🌐 3D opens the same world.
- `world/`, the Godot 4.7 project:
  - `scripts/world.gd`: the islands, hub, crabs, orchestrators, event tail, picking and interface.
  - `scripts/city_builder.gd`: the file city.
  - `scripts/world_builder.gd`: the git tree.
  - `scripts/crab.gd`: AgentHub's crab sprite strips as an animated 3D sprite. The build copies them into `world/sprites/crab`; from the repository Godot reads them from `src/AgentHub/Assets/Crab`.
  - `scripts/orbit_camera.gd`: the camera.
  - `sample/`: a two-project world with events.

## Tests

- `WorldSnapshotTests`:
  - building a project, the JSON shape and its atomic replace, project ids, `Locate` through worktrees, file sizes and the cap, worktree parsing, finding Godot;
  - live, with `AGENTHUB_WORLD_LAUNCH=1`: the real Godot started through the launcher;
  - live, with `AGENTHUB_WORLD_EXPORT=<repo folder>`: the real service on this computer's data. `AGENTHUB_WORLD_HOLD=<seconds>` keeps it running so Godot can draw it.
- `TranscriptActionsTests`: Claude Code and Codex lines, prompts against tool results, 14 commands sorted into kinds, and the tail (appended whole lines only, starting near the end, a replaced file).
- `godot --headless --path world --script res://tests/test_world.gd`: the tree (branch tips, grow-in), the city (positions, a new file in its folder, glow and fade, colours) and the crab's animations.
- Screenshots:
  - `godot --path world --quit-after 3000 -- --screenshot C:\data\agenthub\uitest\world-sample.png --screenshot-delay 4` takes the overview.
  - Add `--follow-first-crab` for a close-up.
  - Add `--world <file>` for real data.

## Next

1. **Resources from code.** Scan each repository for:
   - Azure: Bicep, ARM, Terraform, workflow deploy steps, `*.azurewebsites.net`, Key Vault references.
   - Snowflake: `snowflake.connector` calls, `snow sql`, SQL that creates or reads `DB.SCHEMA.TABLE`, connection configuration.
   - APIs: routes such as FastAPI, ASP.NET `Map*` or `[Http*]`, and Express.
   - Frontends: `fetch` or `axios` calls.

   Hang the results on the hub as resources, with beams to the files that use them. Crabs then land on the exact table or resource.
2. **Links between projects:** an arc when two projects use the same table or resource, or when one calls the other's API.
3. **Pace:** a faster world refresh for live sessions, and a beam from a crab to the building it reads.
4. **Scale:** many projects and very large repositories (cap, level of detail, search).
