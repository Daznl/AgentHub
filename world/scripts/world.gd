extends Node3D
## AgentHub World. Draws world.json (schema 2: every project, the live sessions, the orchestrators) and tails the
## events file beside it, so each session's crab hops to whatever its session just touched. AgentHub writes both
## files (WorldService); this only draws. The world file comes from `-- --world <path>`, else $AGENTHUB_WORLD, else the
## bundled sample. `--screenshot <png>` saves one frame after `--screenshot-delay` seconds (default 3) and quits;
## `--follow-first-crab` starts the camera on the first crab.
##
## Layout: projects are islands on a ring, city (files) in front, git tree behind, facing a hub in the middle where
## the shared places are: GitHub (pushes, pull requests), Snowflake, Azure and the web.

const TreeBuilder := preload("res://scripts/world_builder.gd")
const CityBuilder := preload("res://scripts/city_builder.gd")
const Crab := preload("res://scripts/crab.gd")
const OrbitCamera := preload("res://scripts/orbit_camera.gd")
const SAMPLE := "res://sample/world.json"
const PICK_RADIUS := 16.0
const MAX_TREE_HEIGHT := 36.0
const LANDMARKS := {
	"github": {"pos": Vector3(0, 20, 0), "colour": "#E6EDF3", "label": "GitHub"},
	"azure": {"pos": Vector3(11, 11, -6), "colour": "#3B9BFF", "label": "Azure"},
	"snowflake": {"pos": Vector3(-11, 0, -6), "colour": "#29B5E8", "label": "Snowflake"},
	"web": {"pos": Vector3(0, 5, 11), "colour": "#A3E635", "label": "Web"},
}

var world_path := ""
var screenshot_path := ""
var screenshot_delay := 3.0
var follow_first_crab := false

var _root: Node3D            # islands, rebuilt from world.json
var _crab_root: Node3D       # crabs and orchestrators, kept across rebuilds
var _camera: OrbitCamera
var _last_md5 := ""
var _known := {}
var _first := true
var _projects := {}          # id -> {node, city, city_node, tree, tree_node, data}
var _live := {}              # session id -> live session dictionary
var _crabs := {}             # session id -> crab
var _orchestrators := {}     # id -> {crab, data, line_alpha}
var _landmarks := {}         # name -> Vector3 where a crab lands
var _pickables: Array = []
var _landmark_pickables: Array = []
var _selected: Dictionary = {}
var _follow: Node3D
var _time := 0.0
var _events_path := ""
var _events_offset := 0
var _feed: Array = []        # [{text, colour}]
var _lines: ImmediateMesh

var _header: Label
var _sub: Label
var _info: PanelContainer
var _info_title: Label
var _info_body: Label
var _tooltip: Label
var _ring: MeshInstance3D
var _status: Label
var _feed_label: RichTextLabel


func _ready() -> void:
	_read_args()
	_make_environment()
	_camera = OrbitCamera.new()
	_camera.fov = 55.0
	_camera.far = 3000.0
	add_child(_camera)
	_make_ui()
	_make_ring()
	_root = Node3D.new()
	add_child(_root)
	_make_landmarks()
	_crab_root = Node3D.new()
	add_child(_crab_root)
	var lines_node := MeshInstance3D.new()
	_lines = ImmediateMesh.new()
	lines_node.mesh = _lines
	var lm := StandardMaterial3D.new()
	lm.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	lm.vertex_color_use_as_albedo = true
	lm.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	lines_node.material_override = lm
	add_child(lines_node)

	var timer := Timer.new()
	timer.wait_time = 1.0
	timer.autostart = true
	timer.timeout.connect(_poll_world)
	add_child(timer)
	var events_timer := Timer.new()
	events_timer.wait_time = 0.2
	events_timer.autostart = true
	events_timer.timeout.connect(_poll_events)
	add_child(events_timer)
	_poll_world()
	if screenshot_path != "":
		_take_screenshot.call_deferred()


func _read_args() -> void:
	var args := OS.get_cmdline_user_args()
	var i := 0
	while i < args.size():
		match args[i]:
			"--world":
				if i + 1 < args.size():
					world_path = args[i + 1]
					i += 1
			"--screenshot":
				if i + 1 < args.size():
					screenshot_path = args[i + 1]
					i += 1
			"--screenshot-delay":
				if i + 1 < args.size():
					screenshot_delay = float(args[i + 1])
					i += 1
			"--follow-first-crab":
				follow_first_crab = true
		i += 1
	if world_path == "":
		world_path = OS.get_environment("AGENTHUB_WORLD")
	if world_path == "":
		world_path = ProjectSettings.globalize_path(SAMPLE)


func _sprite_dir() -> String:
	for dir in [ProjectSettings.globalize_path("res://sprites/crab"), ProjectSettings.globalize_path("res://").path_join("../src/AgentHub/Assets/Crab")]:
		if FileAccess.file_exists(dir.path_join("idle.png")):
			return dir
	return ""


# ---------------------------------------------------------------- world.json

func _poll_world() -> void:
	if not FileAccess.file_exists(world_path):
		_status.text = "Waiting for %s" % world_path
		return
	var md5 := FileAccess.get_md5(world_path)
	if md5 == "" or md5 == _last_md5:
		return
	var data = JSON.parse_string(FileAccess.get_file_as_string(world_path))
	if typeof(data) != TYPE_DICTIONARY:
		return   # half written: next second
	if int(data.get("schema", 0)) != 2:
		_status.text = "world.json is schema %d; this Godot project draws schema 2." % int(data.get("schema", 0))
		_last_md5 = md5
		return
	_last_md5 = md5
	_rebuild(data)


func _rebuild(data: Dictionary) -> void:
	var selected_id: String = _selected.get("id", "")
	for child in _root.get_children():
		_root.remove_child(child)
		child.queue_free()
	_projects.clear()
	_pickables.clear()
	var new_ids := {}
	var projects: Array = data.get("projects", [])

	# Each island built at the origin first, so its size is known before the ring is laid out
	var islands: Array = []
	for p in projects:
		islands.append(_build_island(p, new_ids))
	var ring := _ring_layout(islands)
	for i in range(islands.size()):
		var island: Dictionary = islands[i]
		var node: Node3D = island["node"]
		node.position = ring[i]
		var to_hub: Vector3 = (-(ring[i] as Vector3)).normalized()
		node.rotation.y = atan2(to_hub.x, to_hub.z)
		_root.add_child(node)
		_projects[island["data"]["id"]] = island
	# Positions are only global once the islands stand in place
	for island in islands:
		var city_node: Node3D = island["city_node"]
		for pk in island["city"].pickables:
			var g: Dictionary = pk.duplicate()
			g["pos"] = city_node.to_global(pk["pos"])
			_pickables.append(g)
		for pk in island["tree"].pickables:
			_pickables.append(pk)
		for node in island["tree"].fresh:
			_grow_in(node)
	_known = new_ids

	var generated := str(data.get("generatedAt", ""))
	_generated_at = Time.get_unix_time_from_datetime_string(generated.substr(0, 19)) if generated.length() >= 19 else 0.0
	_live.clear()
	for s in data.get("live", []):
		_live[s["id"]] = s
	_sync_crabs()
	_sync_orchestrators(data.get("orchestrators", []))

	var commits := 0
	for p in projects:
		commits += (p.get("commits", []) as Array).size()
	_header.text = "AgentHub World"
	_sub.text = "%d projects · %d live sessions · %d orchestrators · %d commits   ·   read %s UTC" % [
		projects.size(), _live.size(), _orchestrators.size(), commits, TreeBuilder._time_text(str(data.get("generatedAt", "")))]
	var loading: Array = data.get("loading", [])
	if not loading.is_empty():
		var names := ", ".join(loading.slice(0, 4)) + (" and %d more" % (loading.size() - 4) if loading.size() > 4 else "")
		_sub.text += "\nStill reading: %s" % names
	var events_file := str(data.get("eventsFile", ""))
	var events_path := world_path.get_base_dir().path_join(events_file) if events_file != "" else ""
	if events_path != _events_path:
		_events_path = events_path
		_events_offset = 0
	_status.text = world_path

	if _first and not islands.is_empty():
		_first = false
		# Frame every island and the hub
		var reach := 20.0
		for i in range(islands.size()):
			reach = max(reach, (ring[i] as Vector3).length() + float(islands[i]["radius"]))
		_camera.pitch = -0.62
		_camera.place(Vector3(0, 2, 0), reach * 2.1)
	_selected = {}
	if selected_id != "":
		for pk in _pickables:
			if pk["id"] == selected_id:
				_select(pk, false)
				break
	if _selected.is_empty():
		_info.visible = false
		_ring.visible = false


func _build_island(p: Dictionary, new_ids: Dictionary) -> Dictionary:
	var node := Node3D.new()
	node.name = "island-" + str(p["id"])
	var city := CityBuilder.new()
	var city_node := Node3D.new()
	node.add_child(city_node)
	city.build(p, city_node)

	var tree := TreeBuilder.new()
	var rows := (p.get("rows", []) as Array).size()
	tree.row_h = min(TreeBuilder.ROW_H, MAX_TREE_HEIGHT / max(rows + 2, 1))
	var tree_node := Node3D.new()
	node.add_child(tree_node)
	tree.build(p, tree_node, _known)
	for id in tree.ids:
		new_ids[id] = true
	tree_node.position = Vector3(0, 0, -(city.half + tree.reach + 2.0))

	# The island: an ellipse under the city and the tree
	var extent_x: float = max(city.half, tree.reach) + 2.5
	var z_front: float = city.half + 2.5
	var z_back: float = -(city.half + 2.0 * tree.reach + 4.0)
	var ground := MeshInstance3D.new()
	var disc := CylinderMesh.new()
	disc.top_radius = 1.0
	disc.bottom_radius = 1.08
	disc.height = 0.6
	disc.radial_segments = 64
	ground.mesh = disc
	ground.scale = Vector3(extent_x * 1.25, 1.0, (z_front - z_back) / 2.0 * 1.15)
	ground.position = Vector3(0, -0.32, (z_front + z_back) / 2.0)
	var gm := StandardMaterial3D.new()
	gm.albedo_color = Color("#0B1220")
	gm.roughness = 0.95
	gm.emission_enabled = true
	gm.emission = Color("#16233A")
	gm.emission_energy_multiplier = 0.25
	ground.material_override = gm
	node.add_child(ground)

	var label := Label3D.new()
	label.text = str(p.get("name", "?"))
	label.font_size = 110
	label.pixel_size = 0.01
	label.outline_size = 16
	label.outline_modulate = Color(0.02, 0.04, 0.07, 0.95)
	label.modulate = Color("#E8F1FB")
	label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	label.position = Vector3(0, 2.2, z_front + 0.5)
	node.add_child(label)
	var island_pick := Node3D.new()
	island_pick.position = label.position
	node.add_child(island_pick)
	var s: Dictionary = tree.stats
	var truncated := " (largest shown)" if p.get("filesTruncated", false) else ""
	var body := "%s\n%d files%s · %d branches · %d commits · %d sessions" % [p.get("root", ""), (p.get("files", []) as Array).size(),
		truncated, s["branches"], s["commits"], s["sessions"]]
	_pickables.append({"node": island_pick, "kind": "project", "title": str(p.get("name", "")), "body": body, "id": "project:" + str(p["id"])})

	var radius: float = max(extent_x * 1.25, (z_front - z_back) / 2.0 * 1.15)
	return {"node": node, "city": city, "city_node": city_node, "tree": tree, "tree_node": tree_node, "data": p,
		"radius": radius, "centre_z": (z_front + z_back) / 2.0}


## Island positions on a ring round the hub, each given room for its size.
func _ring_layout(islands: Array) -> Array:
	var gap := 8.0
	var total := 0.0
	var biggest := 0.0
	for island in islands:
		total += island["radius"] * 2.0 + gap
		biggest = max(biggest, island["radius"])
	var ring_radius: float = max(total / TAU, biggest + 16.0)
	var out: Array = []
	var angle := PI / 2.0   # the first island in front of the camera's start
	for island in islands:
		var share: float = (island["radius"] * 2.0 + gap) / total * TAU
		angle += share / 2.0 if out.size() > 0 else 0.0
		var dir := Vector3(cos(angle), 0, sin(angle))
		# Its centre (half way between city front and tree back) sits on the ring
		var pos: Vector3 = dir * ring_radius + dir * (-float(island["centre_z"]))
		out.append(pos)
		angle += share / 2.0
	return out


func _grow_in(node: Node3D) -> void:
	var to := node.scale
	node.scale = Vector3.ONE * 0.01
	var tween := create_tween()
	tween.tween_property(node, "scale", to * 1.6, 0.5).set_trans(Tween.TRANS_BACK).set_ease(Tween.EASE_OUT)
	tween.tween_property(node, "scale", to, 0.4)


# ---------------------------------------------------------------- crabs

var _slots := {}             # session id -> its place in the row of crabs sharing a home
var _generated_at := 0.0     # world.json's time (unix seconds), to tell how long a session has been quiet


func _sync_crabs() -> void:
	# Crabs sharing a home (same project and branch) stand side by side, not on top of each other
	_slots.clear()
	var counts := {}
	for id in _live:
		var s: Dictionary = _live[id]
		var key := "%s/%s" % [s.get("project"), s.get("branch")]
		_slots[id] = counts.get(key, 0)
		counts[key] = _slots[id] + 1
	for id in _live:
		var s: Dictionary = _live[id]
		if not _crabs.has(id):
			_spawn_crab(s)
		else:
			var crab = _crabs[id]
			crab.title = _crab_title(s)
	for id in _crabs.keys():
		if not _live.has(id):
			var crab = _crabs[id]
			_crabs.erase(id)
			var tween := create_tween()
			tween.tween_property(crab, "scale", Vector3.ONE * 0.01, 0.6)
			tween.tween_callback(crab.queue_free)


func _spawn_crab(s: Dictionary) -> Node3D:
	var crab := Crab.new()
	var agent := str(s.get("agent", "claude"))
	var colour: Color = TreeBuilder.AGENT_COLOURS.get(agent, TreeBuilder.AGENT_COLOURS["other"])
	_crab_root.add_child(crab)
	crab.setup(_sprite_dir(), s["id"], _crab_title(s), colour, 1.0)
	crab.place(_home(s, crab))
	_crabs[s["id"]] = crab
	# A session quiet for a while (an open pane nobody is typing in) starts asleep
	var last := str(s.get("lastActivity", ""))
	if last.length() >= 19 and _generated_at > 0.0:
		var quiet: float = _generated_at - Time.get_unix_time_from_datetime_string(last.substr(0, 19))
		if quiet > Crab.SLEEP_AFTER:
			crab.doze()
	return crab


func _crab_title(s: Dictionary) -> String:
	var who := "Codex" if s.get("agent") == "codex" else "Claude"
	var where = s.get("pane")
	var branch = s.get("branch")
	var title := str(s.get("title", "")) if s.get("title") != null else ""
	if title.length() > 34:
		title = title.substr(0, 33) + "…"
	var head := "%s%s" % [who, (" · " + str(where)) if where != null else ""]
	return "%s\n%s%s" % [head, title, ("\n⎇ " + str(branch)) if branch != null else ""]


## Where a session's crab rests: the tip of its branch when it works off the trunk, else the front of its city.
func _home(s: Dictionary, crab: Node3D) -> Vector3:
	var island = _projects.get(s.get("project"))
	if island == null:
		return Vector3(0, 0.2, 0)
	var branch = s.get("branch")
	var trunk := str(island["data"].get("trunk", "main"))
	var slot: int = _slots.get(s.get("id"), 0)
	var step := Vector3((slot % 2 * 2 - 1) * ((slot + 1) / 2) * 2.6, 0, 0)   # 0, -2.6, +2.6, -5.2, …
	if branch != null and str(branch) != trunk and str(branch) != "main" and str(branch) != "master":
		return island["tree_node"].to_global(island["tree"].branch_tip(branch) + step * 0.6)
	return island["city_node"].to_global(Vector3(0, 0.1, island["city"].half + 1.4) + step)


func _jitter(crab: Node3D) -> Vector3:
	var h := float(abs(hash(crab.get("session_id"))) % 1000) / 1000.0 * TAU
	return Vector3(cos(h), 0, sin(h)) * 1.1


## Where an event sends its crab, or null to act in place.
func _target(e: Dictionary, crab: Node3D):
	var kind := str(e.get("kind", ""))
	var island = _projects.get(e.get("project"))
	if island == null and _live.has(e.get("session")):
		island = _projects.get(_live[e["session"]].get("project"))
	match kind:
		"read", "edit", "write", "search":
			# A path outside every project (a log, a screenshot): act where it stands
			if island == null or e.get("file") == null:
				return null
			return island["city_node"].to_global(island["city"].file_pos(e.get("file"))) + _jitter(crab) * 0.5
		"commit", "branch":
			if island == null:
				return null
			var s: Dictionary = _live.get(e.get("session"), {})
			return island["tree_node"].to_global(island["tree"].branch_tip(s.get("branch"))) + _jitter(crab)
		"merge":
			if island == null:
				return null
			return island["tree_node"].to_global(island["tree"].trunk_top()) + _jitter(crab)
		"push", "pr":
			return _landmarks["github"] + _jitter(crab) * 2.0
		"snowflake", "azure", "web":
			return _landmarks[kind] + _jitter(crab) * 2.0
		"build", "test", "bash", "tool", "agent":
			if island == null:
				return null
			return island["city_node"].to_global(Vector3(0, 0.1, island["city"].half + 1.2)) + _jitter(crab)
	return null


func _apply_event(e: Dictionary) -> void:
	var kind := str(e.get("kind", ""))
	if kind == "orchestrate":
		_orchestrator_said(e)
		return
	var id := str(e.get("session", ""))
	var crab = _crabs.get(id)
	if crab == null:
		if not _projects.has(e.get("project")):
			return
		crab = _spawn_crab({"id": id, "agent": "claude", "title": null, "project": e.get("project")})
	var text := str(e.get("text", "")) if e.get("text") != null else kind
	var to = _target(e, crab)
	if to == null:
		crab.emote(kind, text)
	else:
		crab.act(to, kind, text)
	if kind in ["edit", "write", "read"]:
		var island = _projects.get(e.get("project"))
		if island != null:
			island["city"].flash(e.get("file"), 1.0 if kind != "read" else 0.3)
	_add_feed(crab, text)


# ---------------------------------------------------------------- orchestrators

func _sync_orchestrators(list: Array) -> void:
	var seen := {}
	for o in list:
		var id := str(o["id"])
		seen[id] = true
		if not _orchestrators.has(id):
			var crab := Crab.new()
			_crab_root.add_child(crab)
			crab.setup(_sprite_dir(), "orchestrator:" + id, "", Color("#FDE68A"), 1.8)
			var cloud := _cloud(Color("#E8F1FB"), 1.4)
			cloud.position = Vector3(0, 0.1, 0)
			crab.add_child(cloud)
			_orchestrators[id] = {"crab": crab, "data": o, "line": 0.0}
		_orchestrators[id]["data"] = o
		var step = o.get("step")
		_orchestrators[id]["crab"].title = "🧭 %s · %s\n%s%s" % [o.get("title", "Orchestrator"), id, o.get("state", ""),
			("\n▶ " + str(step)) if step != null else ""]
		_orchestrators[id]["crab"].emote("idle", "")
	for id in _orchestrators.keys():
		if not seen.has(id):
			_orchestrators[id]["crab"].queue_free()
			_orchestrators.erase(id)


func _orchestrator_said(e: Dictionary) -> void:
	var id := str(e.get("orchestrator", ""))
	if not _orchestrators.has(id):
		return
	var o: Dictionary = _orchestrators[id]
	o["line"] = 1.0
	var text := str(e.get("text", ""))
	o["crab"].emote("wave" if text.begins_with("Sent") else "pinch", text)
	_add_feed(o["crab"], text)


## A soft cloud of spheres (orchestrators ride one; Azure and GitHub are drawn as one).
func _cloud(colour: Color, size: float) -> Node3D:
	var cloud := Node3D.new()
	var m := StandardMaterial3D.new()
	m.albedo_color = colour.darkened(0.2)
	m.emission_enabled = true
	m.emission = colour
	m.emission_energy_multiplier = 0.35
	m.roughness = 1.0
	for i in range(7):
		var puff := MeshInstance3D.new()
		var sphere := SphereMesh.new()
		var r := size * (0.45 + 0.25 * sin(i * 1.7))
		sphere.radius = r
		sphere.height = r * 1.4
		puff.mesh = sphere
		puff.material_override = m
		var a := i * TAU / 7.0
		puff.position = Vector3(cos(a) * size * 0.8, -0.2 + 0.15 * cos(i * 2.3), sin(a) * size * 0.5)
		cloud.add_child(puff)
	return cloud


# ---------------------------------------------------------------- events file

func _poll_events() -> void:
	if _events_path == "" or not FileAccess.file_exists(_events_path):
		return
	var f := FileAccess.open(_events_path, FileAccess.READ)
	if f == null:
		return
	var length := f.get_length()
	if length < _events_offset:
		_events_offset = 0
	if length == _events_offset:
		return
	var skip_partial := false
	if _events_offset == 0 and length > 200000:
		_events_offset = length - 50000   # opened late: only the last stretch, not the whole day
		skip_partial = true
	f.seek(_events_offset)
	var bytes := f.get_buffer(length - _events_offset)
	var last := bytes.rfind(10)
	if last < 0:
		return
	var text := bytes.slice(0, last).get_string_from_utf8()
	_events_offset += last + 1
	var lines := text.split("\n", false)
	if skip_partial and lines.size() > 0:
		lines.remove_at(0)
	for line in lines:
		var e = JSON.parse_string(line)
		if typeof(e) == TYPE_DICTIONARY:
			_apply_event(e)


# ---------------------------------------------------------------- frame

func _process(delta: float) -> void:
	_time += delta
	for island in _projects.values():
		island["city"].tick(delta)
	var pulse := 1.0 + 0.12 * sin(_time * 3.2)
	for island in _projects.values():
		for node in island["tree"].pulsing:
			if is_instance_valid(node):
				node.scale = Vector3.ONE * pulse
	_lines.clear_surfaces()
	var any := false
	for id in _orchestrators:
		var o: Dictionary = _orchestrators[id]
		var orch: Node3D = o["crab"]
		var worker = _crabs.get(o["data"].get("worker"))
		var goal: Vector3
		if worker != null and is_instance_valid(worker):
			goal = (worker as Node3D).position + Vector3(0, 5.5, 0)
		else:
			goal = Vector3(0, 9, 0)
		orch.position = orch.position.lerp(goal, 1.0 - exp(-delta * 2.0)) + Vector3(0, 0.004 * sin(_time * 1.4), 0)
		if worker != null and is_instance_valid(worker) and o["line"] > 0.0:
			if not any:
				_lines.surface_begin(Mesh.PRIMITIVE_LINES)
				any = true
			var from := orch.position + Vector3(0, 0.2, 0)
			var to := (worker as Node3D).position + Vector3(0, 1.0, 0)
			var segments := 8
			for k in range(segments):
				var a := from.lerp(to, float(k) / segments) + _zigzag(k, segments)
				var b := from.lerp(to, float(k + 1) / segments) + _zigzag(k + 1, segments)
				_lines.surface_set_color(Color(1.0, 0.9, 0.5, o["line"]))
				_lines.surface_add_vertex(a)
				_lines.surface_set_color(Color(1.0, 0.9, 0.5, o["line"]))
				_lines.surface_add_vertex(b)
			o["line"] = max(0.0, o["line"] - delta * 0.6)
	if any:
		_lines.surface_end()
	if _follow != null and is_instance_valid(_follow):
		_camera.focus(_follow.global_position + Vector3(0, 1, 0))
	if _ring.visible and not _selected.is_empty():
		_ring.global_position = _pick_pos(_selected)
		_ring.look_at(_camera.global_position, Vector3.UP)
		_ring.rotate_object_local(Vector3.RIGHT, PI / 2)


func _zigzag(k: int, n: int) -> Vector3:
	if k == 0 or k == n:
		return Vector3.ZERO
	return Vector3(0.25 * sin(k * 2.7 + _time * 20.0), 0, 0.25 * cos(k * 1.9 + _time * 17.0))


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseMotion:
		_hover((event as InputEventMouseMotion).position)
	elif event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_LEFT and mb.double_click:
			var hit := _nearest(mb.position)
			if not hit.is_empty():
				_select(hit, true)
		elif mb.button_index == MOUSE_BUTTON_LEFT and not mb.pressed and _camera.drag_moved < 4.0:
			var hit := _nearest(mb.position)
			if hit.is_empty():
				_clear_selection()
			else:
				_select(hit, false)
	elif event is InputEventKey and (event as InputEventKey).pressed:
		match (event as InputEventKey).keycode:
			KEY_F:
				if not _selected.is_empty():
					_select(_selected, true)
			KEY_R:
				_first = true
				_last_md5 = ""
				_follow = null
				_poll_world()
			KEY_ESCAPE:
				_clear_selection()
			KEY_W, KEY_A, KEY_S, KEY_D, KEY_Q, KEY_E:
				_follow = null


func _clear_selection() -> void:
	_selected = {}
	_follow = null
	_info.visible = false
	_ring.visible = false


func _all_pickables() -> Array:
	var all := _pickables + _landmark_pickables
	for id in _crabs:
		var crab = _crabs[id]
		var s: Dictionary = _live.get(id, {})
		all.append({"node": crab, "kind": "session", "title": str(crab.title).replace("\n", " · "),
			"body": "%s\n%s\nclick then F to follow" % [s.get("folder", ""), id], "id": "crab:" + id, "follow": true})
	for id in _orchestrators:
		var o: Dictionary = _orchestrators[id]
		all.append({"node": o["crab"], "kind": "orchestrator", "title": str(o["crab"].title).replace("\n", " · "),
			"body": "over %s" % str(o["data"].get("worker", "?")), "id": "orch:" + id, "follow": true})
	return all


func _pick_pos(p: Dictionary) -> Vector3:
	if p.has("pos"):
		return p["pos"]
	var node = p.get("node")
	if node != null and is_instance_valid(node):
		return (node as Node3D).global_position + (Vector3(0, 0.6, 0) if p.get("follow", false) else Vector3.ZERO)
	return Vector3.ZERO


func _nearest(screen: Vector2) -> Dictionary:
	var best := {}
	var best_d := PICK_RADIUS
	var best_depth := INF
	var cam_pos := _camera.global_position
	for p in _all_pickables():
		var pos := _pick_pos(p)
		var depth := cam_pos.distance_to(pos)
		if depth > 160.0 or _camera.is_position_behind(pos):
			continue
		var d := _camera.unproject_position(pos).distance_to(screen)
		if d < best_d - 2.0 or (d <= best_d + 2.0 and depth < best_depth and d < PICK_RADIUS):
			best = p
			best_d = d
			best_depth = depth
	return best


func _hover(screen: Vector2) -> void:
	var hit := _nearest(screen)
	if hit.is_empty():
		_tooltip.visible = false
		Input.set_default_cursor_shape(Input.CURSOR_ARROW)
		return
	_tooltip.text = "%s · %s" % [hit["kind"], hit["title"]]
	_tooltip.position = screen + Vector2(16, 12)
	_tooltip.visible = true
	Input.set_default_cursor_shape(Input.CURSOR_POINTING_HAND)


func _select(p: Dictionary, fly: bool) -> void:
	_selected = p
	_info_title.text = p["title"]
	_info_body.text = "%s\n%s" % [str(p["kind"]).to_upper(), p["body"]]
	_info.visible = true
	_ring.visible = true
	if fly:
		if p.get("follow", false):
			_follow = p["node"]
			_camera.focus(_pick_pos(p), 14.0)
		else:
			_follow = null
			_camera.focus(_pick_pos(p), 12.0)


func _add_feed(crab: Node3D, text: String) -> void:
	var who := str(crab.get("title")).split("\n")[0]
	var colour: Color = crab.get("tint")
	var one_line := text.replace("\r", " ").replace("\n", " ").replace("[", "(")
	_feed.append("[color=#%s]%s[/color]  %s" % [colour.lightened(0.2).to_html(false), who, one_line])
	while _feed.size() > 9:
		_feed.pop_front()
	_feed_label.text = "\n".join(_feed)


# ---------------------------------------------------------------- scene dressing

func _make_environment() -> void:
	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color("#05080F")
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color("#2A3A55")
	env.ambient_light_energy = 0.8
	env.tonemap_mode = Environment.TONE_MAPPER_FILMIC
	env.glow_enabled = true
	env.glow_intensity = 0.8
	env.glow_strength = 1.0
	env.glow_bloom = 0.04
	env.glow_hdr_threshold = 0.9
	env.glow_blend_mode = Environment.GLOW_BLEND_MODE_ADDITIVE
	env.fog_enabled = true
	env.fog_light_color = Color("#05080F")
	env.fog_density = 0.0035
	var we := WorldEnvironment.new()
	we.environment = env
	add_child(we)

	var sun := DirectionalLight3D.new()
	sun.light_energy = 0.7
	sun.light_color = Color("#C8D6F0")
	sun.rotation = Vector3(deg_to_rad(-55), deg_to_rad(35), 0)
	add_child(sun)

	var ground := MeshInstance3D.new()
	var plane := PlaneMesh.new()
	plane.size = Vector2(1200, 1200)
	ground.mesh = plane
	ground.position = Vector3(0, -0.7, 0)
	var shader := Shader.new()
	shader.code = """
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;
uniform vec4 line_colour : source_color = vec4(0.16, 0.26, 0.40, 0.45);
varying vec3 world_pos;
void vertex() { world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
void fragment() {
	vec2 p = world_pos.xz / 4.0;
	vec2 g = abs(fract(p - 0.5) - 0.5) / fwidth(p);
	float line = 1.0 - min(min(g.x, g.y), 1.0);
	float fade = 1.0 - smoothstep(30.0, 260.0, length(world_pos.xz));
	ALBEDO = line_colour.rgb;
	ALPHA = line * line_colour.a * fade;
}
"""
	var mat := ShaderMaterial.new()
	mat.shader = shader
	ground.material_override = mat
	add_child(ground)


## The hub: the places every project shares. Stage 5 hangs the resources found in the code on these.
func _make_landmarks() -> void:
	for name in LANDMARKS:
		var spec: Dictionary = LANDMARKS[name]
		var colour := Color(spec["colour"])
		var pos: Vector3 = spec["pos"]
		var node: Node3D
		match name:
			"github", "azure":
				node = _cloud(colour, 2.6)
			"snowflake":
				node = Node3D.new()
				var m := StandardMaterial3D.new()
				m.albedo_color = colour.darkened(0.3)
				m.emission_enabled = true
				m.emission = colour
				m.emission_energy_multiplier = 1.2
				m.metallic = 0.3
				m.roughness = 0.15
				for i in range(6):
					var shard := MeshInstance3D.new()
					var prism := CylinderMesh.new()
					prism.radial_segments = 6
					prism.top_radius = 0.0
					prism.bottom_radius = 0.7 + 0.2 * (i % 3)
					prism.height = 3.0 + 1.6 * ((i * 7) % 4)
					shard.mesh = prism
					shard.material_override = m
					var a := i * TAU / 6.0
					shard.position = Vector3(cos(a) * 1.4, prism.height / 2.0, sin(a) * 1.4)
					shard.rotation = Vector3(0.25 * sin(a), 0, 0.25 * cos(a))
					node.add_child(shard)
			"web":
				node = MeshInstance3D.new()
				var sphere := SphereMesh.new()
				sphere.radius = 1.8
				sphere.height = 3.6
				node.mesh = sphere
				var wm := StandardMaterial3D.new()
				wm.albedo_color = Color(colour, 0.25)
				wm.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
				wm.emission_enabled = true
				wm.emission = colour
				wm.emission_energy_multiplier = 0.8
				node.material_override = wm
		node.position = pos
		add_child(node)
		var label := Label3D.new()
		label.text = spec["label"]
		label.font_size = 80
		label.pixel_size = 0.01
		label.outline_size = 14
		label.outline_modulate = Color(0.02, 0.04, 0.07, 0.95)
		label.modulate = colour.lightened(0.2)
		label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
		var top := 6.5 if name == "snowflake" else 3.2
		label.position = pos + Vector3(0, top, 0)
		add_child(label)
		_landmarks[name] = pos + Vector3(0, (5.0 if name == "snowflake" else 1.6), 0)
		var pick := Node3D.new()
		pick.position = label.position
		add_child(pick)
		_landmark_pickables.append({"node": pick, "kind": "place", "title": spec["label"],
			"body": "Crabs come here when their session %s." % {
				"github": "pushes or works with pull requests", "azure": "runs an Azure command or deploys",
				"snowflake": "runs Snowflake commands", "web": "fetches from the web"}[name], "id": "place:" + name})


func _make_ring() -> void:
	_ring = MeshInstance3D.new()
	var torus := TorusMesh.new()
	torus.inner_radius = 0.55
	torus.outer_radius = 0.62
	_ring.mesh = torus
	var m := StandardMaterial3D.new()
	m.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	m.albedo_color = Color.WHITE
	m.no_depth_test = true
	_ring.material_override = m
	_ring.visible = false
	add_child(_ring)


func _make_ui() -> void:
	var layer := CanvasLayer.new()
	add_child(layer)

	var head := _panel()
	head.position = Vector2(20, 18)
	layer.add_child(head)
	var top := VBoxContainer.new()
	top.add_theme_constant_override("separation", 2)
	head.add_child(top)
	_header = _label(24, Color("#E8F1FB"))
	_header.text = "AgentHub World"
	top.add_child(_header)
	_sub = _label(13, Color("#7E8CA6"))
	top.add_child(_sub)

	var foot := _panel()
	foot.anchor_top = 1.0
	foot.anchor_bottom = 1.0
	foot.grow_vertical = Control.GROW_DIRECTION_BEGIN
	foot.offset_left = 20
	foot.offset_top = -20
	foot.offset_bottom = -20
	layer.add_child(foot)
	var foot_box := VBoxContainer.new()
	foot_box.add_theme_constant_override("separation", 6)
	foot.add_child(foot_box)
	var legend := HBoxContainer.new()
	legend.add_theme_constant_override("separation", 18)
	foot_box.add_child(legend)
	for item in [
		["🦀", TreeBuilder.AGENT_COLOURS["claude"], "live session (hops to what it touches)"],
		["🧭", Color("#FDE68A"), "orchestrator"],
		["▮", Color("#A78BFA"), "file (colour = kind, height = size)"],
		["●", TreeBuilder.TRUNK_COLOUR, "git tree"],
		["◯", TreeBuilder.PR_MERGED, "pull request"],
	]:
		var pair := HBoxContainer.new()
		pair.add_theme_constant_override("separation", 6)
		var glyph := _label(14, item[1])
		glyph.text = item[0]
		pair.add_child(glyph)
		var text := _label(13, Color("#B6C2D6"))
		text.text = item[2]
		pair.add_child(text)
		legend.add_child(pair)
	var help := _label(12, Color("#5A6B85"))
	help.text = "Left drag turn  ·  Right drag pan  ·  Wheel zoom  ·  WASD / Q E move (Shift faster)  ·  Click select  ·  Double-click or F fly to (follows a crab)  ·  R reset"
	foot_box.add_child(help)

	var feed := _panel()
	feed.anchor_left = 1.0
	feed.anchor_right = 1.0
	feed.anchor_top = 1.0
	feed.anchor_bottom = 1.0
	feed.grow_horizontal = Control.GROW_DIRECTION_BEGIN
	feed.grow_vertical = Control.GROW_DIRECTION_BEGIN
	feed.offset_right = -20
	feed.offset_bottom = -44
	feed.offset_left = -520
	feed.offset_top = -44
	layer.add_child(feed)
	var feed_box := VBoxContainer.new()
	feed.add_child(feed_box)
	var feed_title := _label(12, Color("#5A6B85"))
	feed_title.text = "WHAT THE CRABS ARE DOING"
	feed_box.add_child(feed_title)
	_feed_label = RichTextLabel.new()
	_feed_label.bbcode_enabled = true
	_feed_label.fit_content = true
	_feed_label.scroll_active = false
	_feed_label.custom_minimum_size = Vector2(480, 0)
	_feed_label.add_theme_font_size_override("normal_font_size", 12)
	_feed_label.add_theme_color_override("default_color", Color("#B6C2D6"))
	_feed_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_feed_label.text = "(waiting for the first event)"
	feed_box.add_child(_feed_label)

	_status = _label(11, Color("#3E4C63"))
	_status.anchor_left = 1.0
	_status.anchor_right = 1.0
	_status.anchor_top = 1.0
	_status.anchor_bottom = 1.0
	_status.offset_left = -900
	_status.offset_right = -24
	_status.offset_top = -30
	_status.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	layer.add_child(_status)

	_info = PanelContainer.new()
	var style := StyleBoxFlat.new()
	style.bg_color = Color(0.07, 0.10, 0.15, 0.92)
	style.border_color = Color("#1E2C40")
	style.set_border_width_all(1)
	style.set_corner_radius_all(8)
	style.content_margin_left = 16
	style.content_margin_right = 16
	style.content_margin_top = 12
	style.content_margin_bottom = 14
	_info.add_theme_stylebox_override("panel", style)
	_info.anchor_left = 1.0
	_info.anchor_right = 1.0
	_info.offset_left = -420
	_info.offset_right = -24
	_info.offset_top = 24
	_info.visible = false
	layer.add_child(_info)
	var box := VBoxContainer.new()
	box.add_theme_constant_override("separation", 6)
	_info.add_child(box)
	_info_title = _label(17, Color("#E8F1FB"))
	_info_title.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	box.add_child(_info_title)
	_info_body = _label(13, Color("#B6C2D6"))
	_info_body.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	box.add_child(_info_body)

	_tooltip = _label(13, Color("#E8F1FB"))
	var tip_style := StyleBoxFlat.new()
	tip_style.bg_color = Color(0.05, 0.08, 0.12, 0.92)
	tip_style.set_corner_radius_all(4)
	tip_style.content_margin_left = 8
	tip_style.content_margin_right = 8
	tip_style.content_margin_top = 4
	tip_style.content_margin_bottom = 4
	_tooltip.add_theme_stylebox_override("normal", tip_style)
	_tooltip.visible = false
	_tooltip.mouse_filter = Control.MOUSE_FILTER_IGNORE
	layer.add_child(_tooltip)


func _panel() -> PanelContainer:
	var p := PanelContainer.new()
	var style := StyleBoxFlat.new()
	style.bg_color = Color(0.03, 0.05, 0.08, 0.82)
	style.border_color = Color("#1A2638")
	style.set_border_width_all(1)
	style.set_corner_radius_all(8)
	style.content_margin_left = 14
	style.content_margin_right = 14
	style.content_margin_top = 10
	style.content_margin_bottom = 10
	p.add_theme_stylebox_override("panel", style)
	p.mouse_filter = Control.MOUSE_FILTER_IGNORE
	return p


func _label(size: int, colour: Color) -> Label:
	var l := Label.new()
	l.add_theme_font_size_override("font_size", size)
	l.add_theme_color_override("font_color", colour)
	l.mouse_filter = Control.MOUSE_FILTER_IGNORE
	return l


func _take_screenshot() -> void:
	var waited := 0.0
	while waited < screenshot_delay:
		await get_tree().process_frame
		waited += get_process_delta_time()
		if follow_first_crab and _follow == null and not _crabs.is_empty():
			var crab: Node3D = _crabs.values()[0]
			_follow = crab
			_camera.place(crab.global_position + Vector3(0, 1, 0), 16.0)
			_camera.pitch = -0.35
	var image := get_viewport().get_texture().get_image()
	DirAccess.make_dir_recursive_absolute(screenshot_path.get_base_dir())
	image.save_png(screenshot_path)
	print("SCREENSHOT ", screenshot_path)
	get_tree().quit()
