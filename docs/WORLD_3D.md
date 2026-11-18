# 3D world (experimental)

The owner's idea, 2026-11-18: the coding space opens up into three dimensions, showing how files, branches, sessions, APIs, frontends and data connect, all explorable and updating in real time. AgentHub is the reader; Godot draws. Stage 1 covers the AgentHub repository's own git and sessions.

## How it works

```text
Timeline window (re-reads every 20 s)
    TimelineModel + pull requests + git worktree list + live hosted sessions
        -> WorldSnapshotBuilder -> C:\data\agenthub\world\<repo>-<hash of root>\world.json   (temp file + replace)
             -> Godot project world/ (run from beside AgentHub.exe), polls the file's MD5 every second, redraws
```

- **One reader.** Only AgentHub reads git, `gh`, transcripts and session records. Godot reads only `world.json`. Godot has no network code. The file is plain JSON, so other viewers can read it too.
- **Connections come from code.** Later layers (Snowflake, Azure, APIs, frontends) are worked out by AgentHub from each repository's code (configuration, Bicep, SDK calls, imports), never by connecting to those services.
- **Launch.** **🌐 3D** in the Timeline header writes the file and starts `GodotCommand` (settings; default `godot` on `PATH`; a `.cmd` launcher works) with `--path <AgentHub>\world -- --world <file>`. Godot's output goes to `godot.log` beside the file. Closing the Timeline window stops the updates; Godot keeps showing the last state.

## world.json, schema 1

`WorldSnapshot` in `src/AgentHub/Services/World/WorldSnapshot.cs`, camelCase. Godot refuses a newer schema rather than drawing it wrongly.

| Field | What |
| --- | --- |
| `repo` | name, root, trunk name, isGit |
| `rows` | the Timeline's rows, oldest first. A row's index is the height in the world, so days with no work leave no gaps |
| `branches` | key, name, trunk/imported/merged/squash/local-only, parent lane, the 2D column and colour, start/end row, merge commit |
| `commits` | hash, parents, time, author, subject, lane, row, the session that made it |
| `sessions` | transcript path (id), agent, title, start/end, live, folder, lane, row, commits made |
| `pullRequests` | from `gh pr list`: number, title, head, base, state, merge commit |
| `worktrees` | `git worktree list --porcelain`: path, branch, head, main |
| `panes` | hosted sessions whose host still runs, in the repository's folders: id, title, folder, command |

## What the shapes mean

- **Trunk:** `main`, a vertical column. Height is time (Timeline rows), newest at the top.
- **Branches:** tubes in the 2D tree's colour. Each branch sits at an angle set by its colour and a radius set by its column. It forks from its parent lane and curves back into the trunk at its merge commit. An unmerged branch ends in a bright bud and is drawn thicker and brighter.
- **Commits:** spheres on their lane. Merge commits are larger.
- **Sessions:** orbs beside their lane: orange for Claude Code, green for Codex. The more commits a session made, the bigger its orb. Lines run to each of its commits. A live session pulses and lights its surroundings.
- **Pull requests:** rings. Purple for merged, at the merge commit; green for open, pulsing at the branch tip.
- **Worktrees:** hexagon pads above their branch's tip. Pads for `main`, or for branches with no lane, ring the top of the trunk.
- **Live panes:** white beams of light over the worktree they run in.
- New commits, sessions and panes grow in when the file changes.

**Controls:** left drag turns, right or middle drag pans, the wheel zooms. WASD moves, Q/E go down/up, Shift is faster. Click selects (details at the top right), double-click or F flies to the selection, R resets the camera, Esc clears the selection.

## Files

- `src/AgentHub/Services/World/WorldSnapshot.cs`: the format, the builder from a `TimelineModel`, worktree parsing, pane filtering, the file path and atomic write.
- `src/AgentHub/Services/World/WorldSource.cs`: reads worktrees and live panes, `ReadAsync` (the whole snapshot from scratch), `GodotLauncher`.
- `src/AgentHub/TimelineWindow.World.cs`: the button, and the export on each refresh while Godot runs.
- `world/`: the Godot 4.7 project. `scripts/world.gd` handles the file watch, interface, picking and animation. `scripts/world_builder.gd` turns data into meshes. `scripts/orbit_camera.gd` is the camera. `sample/world.json` is drawn when no file is given.

## Tests

- `WorldSnapshotTests`: the builder, the JSON shape and atomic replace, the stable path, worktree parsing, pane filtering, Godot lookup. Two opt-in live tests:
  - `AGENTHUB_WORLD_EXPORT=<repo folder>` writes the real world file.
  - `AGENTHUB_WORLD_LAUNCH=1` starts the real Godot through the launcher, with a path containing spaces, and checks that it keeps running with no errors.
- `godot --headless --path world --script res://tests/test_world.gd`: builds the sample and checks counts, kinds and grow-in of a new commit.
- Screenshot: `godot --path world -- --world <file> --screenshot C:\data\agenthub\uitest\world-3d.png`.

## Next

1. Faster updates while the world is open (every 2 to 5 s), and an event stream for commits, pushes, merges and pane output, so activity shows as it happens rather than at each snapshot.
2. Files: each branch's changed files as a growing structure, with sessions shown building the files they touch (from transcripts' tool calls).
3. From code: APIs, frontends, Snowflake objects and Azure resources found in the repositories, with the links between them.
4. Several repositories in one world, connected where one uses another.
5. Orchestrators and advanced-template runs: a run as a path along its planned steps, with the current step lit.
