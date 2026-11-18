extends RefCounted
## Turns a world.json dictionary (schema 1, written by AgentHub's WorldSnapshotBuilder) into 3D nodes.
## Pure drawing: no files, no input. Height is the Timeline's row (oldest at the bottom); the trunk stands at the
## centre and every other branch is a lane around it, at the 2D tree's column (radius) and colour (angle).

const ROW_H := 0.8
const GOLDEN := 2.399963
# The 2D Timeline's lane colours (Controls/TimelineGutter.cs), so a branch looks the same in both views.
const LANE_PALETTE := ["#2FD4F5", "#C084FC", "#34D399", "#FBBF24", "#60A5FA", "#F472B6", "#FB923C", "#A3E635", "#E879F9", "#2DD4BF", "#F87171", "#FDE047"]
const TRUNK_COLOUR := Color("#9FB4D6")
const AGENT_COLOURS := {"claude": Color("#E07A52"), "codex": Color("#10B981"), "other": Color("#8AB4F8")}
const PR_OPEN := Color("#3FB950")
const PR_MERGED := Color("#A371F7")
const PANE_COLOUR := Color("#E8F6FF")

## Everything that can be pointed at: {node: Node3D, kind: String, title: String, body: String, id: String}
var pickables: Array = []
## Nodes that pulse (live sessions, pane beacons).
var pulsing: Array = []
## Nodes that were not in the previous build, for the grow-in animation.
var fresh: Array = []
## Counts for the header and the tests.
var stats := {}
## Ids seen in this build (commits, sessions, panes), to tell new things from old on the next one.
var ids := {}

var _lanes := {}          # branch key -> {angle, radius, colour, start, end}
var _commit_at := {}      # hash -> {row, branch}
var _materials := {}
var _sphere: SphereMesh
var _sphere_big: SphereMesh
var _top_row := 0


func build(data: Dictionary, root: Node3D, known: Dictionary) -> void:
	_sphere = SphereMesh.new()
	_sphere.radius = 0.16
	_sphere.height = 0.32
	_sphere.radial_segments = 16
	_sphere.rings = 8
	_sphere_big = SphereMesh.new()
	_sphere_big.radius = 0.24
	_sphere_big.height = 0.48

	var rows: Array = data.get("rows", [])
	var branches: Array = data.get("branches", [])
	var commits: Array = data.get("commits", [])
	var sessions: Array = data.get("sessions", [])
	var prs: Array = data.get("pullRequests", [])
	var worktrees: Array = data.get("worktrees", [])
	var panes: Array = data.get("panes", [])
	_top_row = max(rows.size() - 1, 0)

	for c in commits:
		_commit_at[c["hash"]] = {"row": int(c["row"]), "branch": c.get("branch")}

	_layout_lanes(branches)
	_draw_trunk(root, data)
	for b in branches:
		if not b["isTrunk"]:
			_draw_lane(root, b)
	_draw_commits(root, commits, known)
	_draw_sessions(root, sessions, known)
	_draw_pull_requests(root, prs, branches)
	_draw_worktrees(root, worktrees, branches)
	_draw_panes(root, panes, worktrees, branches, known)
	_draw_ruler(root, rows)

	stats = {
		"rows": rows.size(), "branches": branches.size(), "commits": commits.size(), "sessions": sessions.size(),
		"live": sessions.filter(func(s): return s.get("isLive", false)).size(),
		"pullRequests": prs.size(), "worktrees": worktrees.size(), "panes": panes.size(), "pickables": pickables.size(),
	}


# ---------------------------------------------------------------- layout

func _layout_lanes(branches: Array) -> void:
	_lanes["trunk"] = {"angle": 0.0, "radius": 0.0, "colour": TRUNK_COLOUR, "start": 0, "end": _top_row}
	for b in branches:
		if b["isTrunk"]:
			_lanes[b["key"]] = _lanes["trunk"]
			continue
		var colour_index: int = int(b["colour"])
		_lanes[b["key"]] = {
			"angle": colour_index * GOLDEN,
			"radius": 2.6 + 1.4 * max(int(b["column"]), 1),
			"colour": lane_colour(colour_index),
			"start": int(b["startRow"]),
			"end": int(b["endRow"]),
		}


static func lane_colour(index: int) -> Color:
	if index == 0:
		return TRUNK_COLOUR
	return Color(LANE_PALETTE[index % LANE_PALETTE.size()])


func lane_point(key, row: float) -> Vector3:
	var lane = _lanes.get(key if key != null else "trunk", _lanes["trunk"])
	var r: float = lane["radius"]
	var a: float = lane["angle"]
	return Vector3(cos(a) * r, row * ROW_H, sin(a) * r)


func _outward(key) -> Vector3:
	var lane = _lanes.get(key if key != null else "trunk", _lanes["trunk"])
	if lane["radius"] == 0.0:
		return Vector3.ZERO
	var a: float = lane["angle"]
	return Vector3(cos(a), 0, sin(a))


# ---------------------------------------------------------------- trunk and lanes

func _draw_trunk(root: Node3D, data: Dictionary) -> void:
	var top := (_top_row + 1.5) * ROW_H
	var pts := PackedVector3Array([Vector3(0, -0.6, 0), Vector3(0, top, 0)])
	_add_tube(root, pts, 0.32, TRUNK_COLOUR, 0.9)
	var repo: Dictionary = data.get("repo", {})
	_add_label(root, Vector3(0, top + 0.9, 0), str(repo.get("trunk", "main")), TRUNK_COLOUR, 64)
	# The root: a soft disc where the first commit sits
	var base := MeshInstance3D.new()
	var disc := CylinderMesh.new()
	disc.top_radius = 1.4
	disc.bottom_radius = 1.6
	disc.height = 0.12
	base.mesh = disc
	base.material_override = _material(TRUNK_COLOUR, 0.6)
	base.position = Vector3(0, -0.6, 0)
	root.add_child(base)


func _draw_lane(root: Node3D, b: Dictionary) -> void:
	var key: String = b["key"]
	var lane: Dictionary = _lanes[key]
	var start: int = lane["start"]
	var end: int = lane["end"]
	if start < 0:
		return
	end = max(end, start)
	var colour: Color = lane["colour"]
	var pts := PackedVector3Array()
	var parent = b.get("parent")
	var p1 := lane_point(key, start)
	if parent != null and not b.get("isImported", false):
		var p0 := lane_point(parent, start - 0.7)
		_bezier(pts, p0, p0 + Vector3(0, 0.35, 0), p1 - Vector3(0, 0.45, 0), p1, 14)
	else:
		pts.append(p1)
	var merge_row := -1.0
	if b.get("isMerged", false) and b.get("mergeHash") != null and _commit_at.has(b["mergeHash"]):
		merge_row = float(_commit_at[b["mergeHash"]]["row"])
	var top_row: float = end
	if merge_row >= 0.0:
		top_row = max(min(float(end), merge_row - 0.7), float(start))
	var p2 := lane_point(key, top_row)
	if p2.distance_to(p1) > 0.01:
		pts.append(p2)
	if merge_row >= 0.0:
		var p3 := lane_point("trunk", merge_row)
		_bezier(pts, p2, p2 + Vector3(0, 0.45, 0), p3 - Vector3(0, 0.35, 0), p3, 14)
	if pts.size() < 2:
		pts.append(p1 + Vector3(0, 0.3, 0))
	var merged: bool = merge_row >= 0.0
	_add_tube(root, pts, 0.09 if merged else 0.12, colour, 0.9 if merged else 1.8)

	var label_pos := p2 + Vector3(0, 0.7, 0) + _outward(key) * 0.3
	var name: String = b["name"]
	_add_label(root, label_pos, name, colour, 40 if merged else 48)
	var state := "merged" + (" (squash)" if b.get("isSquash", false) else "") if merged else ("local only, not pushed" if b.get("isLocalOnly", false) else "open")
	_pick(_marker(root, label_pos, 0.0), "branch", name,
		"%s\n%d commits · rows %d–%d" % [state, int(b.get("commitCount", 0)), start, end], "branch:" + key)
	if not merged:
		# An open branch ends in a bright bud
		var bud := MeshInstance3D.new()
		bud.mesh = _sphere_big
		bud.material_override = _material(colour, 3.0)
		bud.position = p2 + Vector3(0, 0.25, 0)
		root.add_child(bud)


func _bezier(pts: PackedVector3Array, a: Vector3, b: Vector3, c: Vector3, d: Vector3, steps: int) -> void:
	for i in range(steps + 1):
		var t := float(i) / steps
		var u := 1.0 - t
		pts.append(a * u * u * u + b * 3.0 * u * u * t + c * 3.0 * u * t * t + d * t * t * t)


# ---------------------------------------------------------------- commits and sessions

func _draw_commits(root: Node3D, commits: Array, known: Dictionary) -> void:
	for c in commits:
		var key = c.get("branch")
		if key == null or not _lanes.has(key):
			key = "trunk"
		var pos := lane_point(key, float(c["row"]))
		var node := MeshInstance3D.new()
		var is_merge: bool = (c.get("parents", []) as Array).size() > 1
		node.mesh = _sphere_big if is_merge else _sphere
		node.material_override = _material(_lanes[key]["colour"], 2.2 if is_merge else 1.4)
		node.position = pos
		root.add_child(node)
		var hash: String = c["hash"]
		_remember(node, "c:" + hash, known)
		var when := _time_text(str(c.get("time", "")))
		_pick(node, "merge" if is_merge else "commit", "%s  %s" % [c.get("short", ""), c.get("subject", "")],
			"%s · %s\nbranch %s" % [c.get("author", ""), when, _lane_name(key)], "commit:" + hash)


func _draw_sessions(root: Node3D, sessions: Array, known: Dictionary) -> void:
	var lines := ImmediateMesh.new()
	var lines_node := MeshInstance3D.new()
	lines_node.mesh = lines
	var line_mat := StandardMaterial3D.new()
	line_mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	line_mat.vertex_color_use_as_albedo = true
	line_mat.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	lines_node.material_override = line_mat
	var any_line := false
	lines.surface_begin(Mesh.PRIMITIVE_LINES)
	var i := 0
	for s in sessions:
		var key = s.get("branch")
		if key == null or not _lanes.has(key):
			key = "trunk"
		var row: float = float(s["row"])
		var out := _outward(key)
		if out == Vector3.ZERO:
			# Sessions on the trunk spread around it so they do not stack in one column
			var a := i * GOLDEN
			out = Vector3(cos(a), 0, sin(a))
		i += 1
		var pos := lane_point(key, row) + out * 1.25 + Vector3(0, 0.15, 0)
		var agent: String = s.get("agent", "other")
		var colour: Color = AGENT_COLOURS.get(agent, AGENT_COLOURS["other"])
		var made: Array = s.get("commits", [])
		var live: bool = s.get("isLive", false)
		var orb := MeshInstance3D.new()
		var mesh := SphereMesh.new()
		var r: float = 0.22 + 0.035 * min(made.size(), 12)
		mesh.radius = r
		mesh.height = r * 2.0
		orb.mesh = mesh
		orb.material_override = _material(colour, 4.0 if live else 1.6)
		orb.position = pos
		root.add_child(orb)
		var id: String = s["id"]
		_remember(orb, "s:" + id, known)
		if live:
			pulsing.append(orb)
			var light := OmniLight3D.new()
			light.light_color = colour
			light.light_energy = 2.0
			light.omni_range = 5.0
			orb.add_child(light)
		for h in made:
			if _commit_at.has(h):
				var c = _commit_at[h]
				var ck = c["branch"] if c["branch"] != null and _lanes.has(c["branch"]) else "trunk"
				var target := lane_point(ck, float(c["row"]))
				lines.surface_set_color(Color(colour, 0.55))
				lines.surface_add_vertex(pos)
				lines.surface_set_color(Color(colour, 0.15))
				lines.surface_add_vertex(target)
				any_line = true
		var title = s.get("title")
		var body := "%s session%s\n%s → %s\n%d commit%s · %s" % [
			_agent_name(agent), " · LIVE" if live else "",
			_time_text(str(s.get("startedAt", ""))), _time_text(str(s.get("endedAt", ""))),
			made.size(), "" if made.size() == 1 else "s", str(s.get("folder", ""))]
		_pick(orb, "session", str(title) if title != null else "(untitled session)", body, "session:" + id)
	if not any_line:
		# An ImmediateMesh surface needs a vertex; draw nothing visible
		lines.surface_set_color(Color(0, 0, 0, 0))
		lines.surface_add_vertex(Vector3.ZERO)
		lines.surface_add_vertex(Vector3.ZERO)
	lines.surface_end()
	root.add_child(lines_node)


# ---------------------------------------------------------------- GitHub, worktrees, panes

func _branch_key_named(branches: Array, name) -> String:
	if name == null:
		return ""
	var found := ""
	for b in branches:
		if b["name"] == name:
			found = b["key"]   # the last one wins: a reused name's newest lane
	return found


func _tip(key: String) -> Vector3:
	var lane: Dictionary = _lanes.get(key, _lanes["trunk"])
	var row: float = lane["end"] if key != "trunk" else _top_row + 1.0
	return lane_point(key, row)


func _draw_pull_requests(root: Node3D, prs: Array, branches: Array) -> void:
	for p in prs:
		var state := str(p.get("state", "")).to_upper()
		var pos: Vector3
		var colour: Color
		if state == "MERGED" and p.get("mergeCommit") != null and _commit_at.has(p["mergeCommit"]):
			var c = _commit_at[p["mergeCommit"]]
			var ck = c["branch"] if c["branch"] != null and _lanes.has(c["branch"]) else "trunk"
			pos = lane_point(ck, float(c["row"]))
			colour = PR_MERGED
		elif state == "OPEN":
			var key := _branch_key_named(branches, p.get("head"))
			if key == "":
				continue
			pos = _tip(key) + Vector3(0, 0.25, 0)
			colour = PR_OPEN
		else:
			continue
		var ring := MeshInstance3D.new()
		var torus := TorusMesh.new()
		torus.inner_radius = 0.42
		torus.outer_radius = 0.52
		ring.mesh = torus
		ring.material_override = _material(colour, 2.5)
		ring.position = pos
		root.add_child(ring)
		if state == "OPEN":
			pulsing.append(ring)
		_add_label(root, pos + Vector3(0.7, 0.1, 0), "#%d" % int(p["number"]), colour, 36)
		_pick(ring, "pull request", "#%d  %s" % [int(p["number"]), p.get("title", "")],
			"%s%s · %s → %s\nby %s" % [state.to_lower(), " (draft)" if p.get("isDraft", false) else "",
				p.get("head", ""), p.get("base", ""), p.get("author", "?")], "pr:%d" % int(p["number"]))


func _worktree_key(w: Dictionary, branches: Array) -> String:
	var key := _branch_key_named(branches, w.get("branch"))
	if key == "" or _lanes[key]["radius"] == 0.0:
		return "trunk"
	return key


func _draw_worktrees(root: Node3D, worktrees: Array, branches: Array) -> void:
	var on_trunk := 0
	for w in worktrees:
		var key := _worktree_key(w, branches)
		var pos := _tip(key) + Vector3(0, 0.9, 0)
		if key == "trunk":
			# Worktrees on main (or on branches with no lane) ring the top of the trunk, under its label
			var a := on_trunk * GOLDEN
			pos = _tip("trunk") + Vector3(cos(a) * (1.6 + 0.12 * on_trunk), -0.4 - 0.25 * on_trunk, sin(a) * (1.6 + 0.12 * on_trunk))
			on_trunk += 1
		var pad := MeshInstance3D.new()
		var hex := CylinderMesh.new()
		hex.radial_segments = 6
		hex.top_radius = 0.6
		hex.bottom_radius = 0.6
		hex.height = 0.06
		pad.mesh = hex
		pad.material_override = _material(Color("#5A6B85"), 0.8)
		pad.position = pos
		root.add_child(pad)
		var path := str(w.get("path", ""))
		var folder := path.get_file()
		_pick(pad, "worktree", folder, "%s\nbranch %s%s" % [path, w.get("branch", "(detached)"), " · main working tree" if w.get("isMain", false) else ""],
			"worktree:" + path)


func _draw_panes(root: Node3D, panes: Array, worktrees: Array, branches: Array, known: Dictionary) -> void:
	var by_folder := {}
	for w in worktrees:
		by_folder[str(w.get("path", "")).to_lower()] = w
	var stacked := {}
	for p in panes:
		var folder := str(p.get("folder", "")).to_lower()
		var key := "trunk"
		if by_folder.has(folder):
			key = _worktree_key(by_folder[folder], branches)
		var n: int = stacked.get(key, 0)
		stacked[key] = n + 1
		var base := _tip(key) + Vector3(0, 1.2, 0) + _outward(key) * (0.8 * n) + Vector3(0.8 * n if key == "trunk" else 0.0, 0, 0)
		var beam := MeshInstance3D.new()
		var cyl := CylinderMesh.new()
		cyl.top_radius = 0.05
		cyl.bottom_radius = 0.16
		cyl.height = 7.0
		beam.mesh = cyl
		var mat := _material(PANE_COLOUR, 3.0).duplicate() as StandardMaterial3D
		mat.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
		mat.albedo_color = Color(PANE_COLOUR, 0.35)
		beam.material_override = mat
		beam.position = base + Vector3(0, 3.5, 0)
		root.add_child(beam)
		var light := OmniLight3D.new()
		light.light_color = PANE_COLOUR
		light.light_energy = 1.5
		light.omni_range = 6.0
		light.position = Vector3(0, -3.0, 0)
		beam.add_child(light)
		pulsing.append(beam)
		var id: String = p["id"]
		_remember(beam, "p:" + id, known)
		var title := str(p.get("title", "pane"))
		_add_label(root, base + Vector3(0, 7.5, 0), title, PANE_COLOUR, 40)
		_pick(beam, "live pane", title, "%s\n%s\nstarted %s" % [p.get("command", ""), p.get("folder", ""), _time_text(str(p.get("startedAt", "")))],
			"pane:" + id)


func _draw_ruler(root: Node3D, rows: Array) -> void:
	if rows.is_empty():
		return
	var step: int = max(1, rows.size() / 24)
	var last_day := ""
	for i in range(0, rows.size(), step):
		var day := str(rows[i].get("time", "")).substr(0, 10)
		if day == last_day:
			continue
		last_day = day
		_add_label(root, Vector3(-0.9, i * ROW_H, -0.9), _day_text(day), Color("#5A6B85"), 28)


# ---------------------------------------------------------------- helpers

func _remember(node: Node3D, id: String, known: Dictionary) -> void:
	ids[id] = true
	if not known.is_empty() and not known.has(id):
		fresh.append(node)


func _pick(node: Node3D, kind: String, title: String, body: String, id: String) -> void:
	pickables.append({"node": node, "kind": kind, "title": title, "body": body, "id": id})


func _marker(root: Node3D, pos: Vector3, _size: float) -> Node3D:
	var n := Node3D.new()
	n.position = pos
	root.add_child(n)
	return n


func _lane_name(key) -> String:
	return "main" if key == "trunk" else str(key)


static func _agent_name(agent: String) -> String:
	match agent:
		"claude": return "Claude Code"
		"codex": return "Codex"
	return "Agent"


static func _time_text(iso: String) -> String:
	# 2026-11-18T10:49:04.123+00:00 -> 2026-11-18 10:49
	if iso.length() < 16:
		return iso
	return iso.substr(0, 10) + " " + iso.substr(11, 5)


static func _day_text(day: String) -> String:
	var months := ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]
	if day.length() < 10:
		return day
	var m := int(day.substr(5, 2))
	return "%d %s" % [int(day.substr(8, 2)), months[clamp(m - 1, 0, 11)]]


func _material(colour: Color, glow: float) -> StandardMaterial3D:
	var key := "%s/%.2f" % [colour.to_html(), glow]
	if _materials.has(key):
		return _materials[key]
	var m := StandardMaterial3D.new()
	m.albedo_color = colour.darkened(0.45)
	m.metallic = 0.2
	m.roughness = 0.35
	m.emission_enabled = true
	m.emission = colour
	m.emission_energy_multiplier = glow
	_materials[key] = m
	return m


func _add_tube(root: Node3D, pts: PackedVector3Array, radius: float, colour: Color, glow: float) -> void:
	var mesh := tube(pts, radius)
	if mesh == null:
		return
	var node := MeshInstance3D.new()
	node.mesh = mesh
	node.material_override = _material(colour, glow)
	root.add_child(node)


func _add_label(root: Node3D, pos: Vector3, text: String, colour: Color, size: int) -> void:
	var label := Label3D.new()
	label.text = text
	label.font_size = size
	label.pixel_size = 0.006
	label.outline_size = 10
	label.outline_modulate = Color(0.02, 0.04, 0.07, 0.9)
	label.modulate = colour.lightened(0.15)
	label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	label.position = pos
	root.add_child(label)


## A tube along the points, with rings turned by parallel transport so it does not twist.
static func tube(points: PackedVector3Array, radius: float, sides: int = 10) -> ArrayMesh:
	var n := points.size()
	if n < 2:
		return null
	var st := SurfaceTool.new()
	st.begin(Mesh.PRIMITIVE_TRIANGLES)
	var normal := Vector3.RIGHT
	for i in range(n):
		var t: Vector3
		if i == 0:
			t = points[1] - points[0]
		elif i == n - 1:
			t = points[n - 1] - points[n - 2]
		else:
			t = points[i + 1] - points[i - 1]
		t = t.normalized()
		normal = normal - t * normal.dot(t)
		if normal.length() < 0.001:
			normal = t.cross(Vector3.UP if abs(t.y) < 0.9 else Vector3.RIGHT)
		normal = normal.normalized()
		var binormal := t.cross(normal)
		for s in range(sides + 1):
			var ang := TAU * s / sides
			var dir := normal * cos(ang) + binormal * sin(ang)
			st.set_normal(dir)
			st.add_vertex(points[i] + dir * radius)
	var ring := sides + 1
	for i in range(n - 1):
		for s in range(sides):
			var a := i * ring + s
			var b := a + 1
			var c := a + ring
			var d := c + 1
			st.add_index(a); st.add_index(c); st.add_index(b)
			st.add_index(b); st.add_index(c); st.add_index(d)
	return st.commit()
