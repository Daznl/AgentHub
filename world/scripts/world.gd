extends Node3D
## AgentHub World. Watches a world.json (from `-- --world <path>`, else $AGENTHUB_WORLD, else the bundled sample) and
## redraws it whenever AgentHub rewrites it, keeping the camera where it is. New commits, sessions and panes grow in.
## Other user arguments: `--screenshot <png>` saves one frame after loading and quits (for tests and docs).

const Builder := preload("res://scripts/world_builder.gd")
const OrbitCamera := preload("res://scripts/orbit_camera.gd")
const SAMPLE := "res://sample/world.json"
const PICK_RADIUS := 16.0

var world_path := ""
var screenshot_path := ""

var _root: Node3D
var _camera: OrbitCamera
var _builder: Builder
var _last_md5 := ""
var _known := {}
var _first := true
var _selected: Dictionary = {}
var _hovered: Dictionary = {}
var _time := 0.0

var _header: Label
var _sub: Label
var _info: PanelContainer
var _info_title: Label
var _info_body: Label
var _tooltip: Label
var _ring: MeshInstance3D
var _status: Label


func _ready() -> void:
	_read_args()
	_make_environment()
	_camera = OrbitCamera.new()
	_camera.fov = 55.0
	_camera.far = 2000.0
	add_child(_camera)
	_make_ui()
	_make_ring()
	_root = Node3D.new()
	add_child(_root)
	var timer := Timer.new()
	timer.wait_time = 1.0
	timer.autostart = true
	timer.timeout.connect(_poll)
	add_child(timer)
	_poll()
	if screenshot_path != "":
		_take_screenshot.call_deferred()


func _read_args() -> void:
	var args := OS.get_cmdline_user_args()
	var i := 0
	while i < args.size():
		if args[i] == "--world" and i + 1 < args.size():
			world_path = args[i + 1]
			i += 1
		elif args[i] == "--screenshot" and i + 1 < args.size():
			screenshot_path = args[i + 1]
			i += 1
		i += 1
	if world_path == "":
		world_path = OS.get_environment("AGENTHUB_WORLD")
	if world_path == "":
		world_path = SAMPLE


# ---------------------------------------------------------------- loading

func _poll() -> void:
	if not FileAccess.file_exists(world_path):
		_status.text = "Waiting for %s" % world_path
		return
	var md5 := FileAccess.get_md5(world_path)
	if md5 == "" or md5 == _last_md5:
		return
	var text := FileAccess.get_file_as_string(world_path)
	var data = JSON.parse_string(text)
	if typeof(data) != TYPE_DICTIONARY:
		_status.text = "Could not read %s (half written? trying again)" % world_path
		return
	if int(data.get("schema", 0)) > 1:
		_status.text = "world.json is schema %d; this Godot project draws schema 1. Update AgentHub World." % int(data["schema"])
		_last_md5 = md5
		return
	_last_md5 = md5
	_rebuild(data)


func _rebuild(data: Dictionary) -> void:
	var selected_id: String = _selected.get("id", "")
	for child in _root.get_children():
		_root.remove_child(child)
		child.queue_free()
	_builder = Builder.new()
	_builder.build(data, _root, _known)
	_known = _builder.ids
	for node in _builder.fresh:
		_grow_in(node)
	var s := _builder.stats
	var repo: Dictionary = data.get("repo", {})
	_header.text = "AgentHub World · %s" % repo.get("name", "?")
	_sub.text = "%d branches · %d commits · %d sessions (%d live) · %d pull requests · %d worktrees · %d live panes   ·   read %s" % [
		s["branches"], s["commits"], s["sessions"], s["live"], s["pullRequests"], s["worktrees"], s["panes"],
		Builder._time_text(str(data.get("generatedAt", ""))) + " UTC"]
	_status.text = world_path
	if _first:
		_first = false
		# Open on the newest work: the top of the tree, seen from slightly below the canopy
		var top_y := (int(s["rows"]) - 1) * Builder.ROW_H
		_camera.place(Vector3(0, max(top_y - 10.0, 4.0), 0), 34.0)
	_selected = {}
	if selected_id != "":
		for p in _builder.pickables:
			if p["id"] == selected_id:
				_select(p, false)
				break
	if _selected.is_empty():
		_info.visible = false
		_ring.visible = false


func _grow_in(node: Node3D) -> void:
	var to := node.scale
	node.scale = Vector3.ONE * 0.01
	var tween := create_tween()
	tween.tween_property(node, "scale", to * 1.6, 0.5).set_trans(Tween.TRANS_BACK).set_ease(Tween.EASE_OUT)
	tween.tween_property(node, "scale", to, 0.4)


# ---------------------------------------------------------------- frame

func _process(delta: float) -> void:
	_time += delta
	if _builder != null:
		var pulse := 1.0 + 0.12 * sin(_time * 3.2)
		for node in _builder.pulsing:
			if is_instance_valid(node):
				node.scale = Vector3(pulse, 1.0, pulse) if node.mesh is CylinderMesh else Vector3.ONE * pulse
	if _ring.visible and not _selected.is_empty() and is_instance_valid(_selected["node"]):
		_ring.global_position = (_selected["node"] as Node3D).global_position
		_ring.look_at(_camera.global_position, Vector3.UP)
		_ring.rotate_object_local(Vector3.RIGHT, PI / 2)


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseMotion:
		_hover((event as InputEventMouseMotion).position)
	elif event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_LEFT and not mb.pressed and _camera.drag_moved < 4.0:
			var hit := _nearest(mb.position)
			if hit.is_empty():
				_selected = {}
				_info.visible = false
				_ring.visible = false
			else:
				_select(hit, false)
		elif mb.button_index == MOUSE_BUTTON_LEFT and mb.double_click:
			var hit := _nearest(mb.position)
			if not hit.is_empty():
				_select(hit, true)
	elif event is InputEventKey and (event as InputEventKey).pressed:
		match (event as InputEventKey).keycode:
			KEY_F:
				if not _selected.is_empty():
					_select(_selected, true)
			KEY_R:
				_first = true
				_last_md5 = ""
				_poll()
			KEY_ESCAPE:
				_selected = {}
				_info.visible = false
				_ring.visible = false


func _nearest(screen: Vector2) -> Dictionary:
	if _builder == null:
		return {}
	var best := {}
	var best_d := PICK_RADIUS
	var best_depth := INF
	for p in _builder.pickables:
		var node = p["node"]
		if not is_instance_valid(node):
			continue
		var pos: Vector3 = (node as Node3D).global_position
		if _camera.is_position_behind(pos):
			continue
		var d := _camera.unproject_position(pos).distance_to(screen)
		var depth := _camera.global_position.distance_to(pos)
		if d < best_d - 2.0 or (d <= best_d + 2.0 and depth < best_depth and d < PICK_RADIUS):
			best = p
			best_d = d
			best_depth = depth
	return best


func _hover(screen: Vector2) -> void:
	var hit := _nearest(screen)
	_hovered = hit
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
		_camera.focus((p["node"] as Node3D).global_position, 12.0)


# ---------------------------------------------------------------- scene dressing

func _make_environment() -> void:
	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color("#05080F")
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color("#2A3A55")
	env.ambient_light_energy = 0.7
	env.tonemap_mode = Environment.TONE_MAPPER_FILMIC
	env.glow_enabled = true
	env.glow_intensity = 0.9
	env.glow_strength = 1.0
	env.glow_bloom = 0.05
	env.glow_hdr_threshold = 0.85
	env.glow_blend_mode = Environment.GLOW_BLEND_MODE_ADDITIVE
	env.fog_enabled = true
	env.fog_light_color = Color("#05080F")
	env.fog_density = 0.006
	var we := WorldEnvironment.new()
	we.environment = env
	add_child(we)

	var sun := DirectionalLight3D.new()
	sun.light_energy = 0.5
	sun.light_color = Color("#B8C8E8")
	sun.rotation = Vector3(deg_to_rad(-50), deg_to_rad(35), 0)
	add_child(sun)

	var ground := MeshInstance3D.new()
	var plane := PlaneMesh.new()
	plane.size = Vector2(400, 400)
	ground.mesh = plane
	ground.position = Vector3(0, -0.7, 0)
	var shader := Shader.new()
	shader.code = """
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;
uniform vec4 line_colour : source_color = vec4(0.16, 0.26, 0.40, 0.55);
varying vec3 world_pos;
void vertex() { world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
void fragment() {
	vec2 p = world_pos.xz / 2.0;
	vec2 g = abs(fract(p - 0.5) - 0.5) / fwidth(p);
	float line = 1.0 - min(min(g.x, g.y), 1.0);
	float fade = 1.0 - smoothstep(8.0, 70.0, length(world_pos.xz));
	ALBEDO = line_colour.rgb;
	ALPHA = line * line_colour.a * fade;
}
"""
	var mat := ShaderMaterial.new()
	mat.shader = shader
	ground.material_override = mat
	add_child(ground)


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

	# Header, top left, on a panel so the scene never runs through the text
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

	# Legend and controls, bottom left
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
		["●", Builder.TRUNK_COLOUR, "main (trunk)"],
		["●", Color(Builder.LANE_PALETTE[1]), "branch"],
		["●", Builder.AGENT_COLOURS["claude"], "Claude Code session"],
		["●", Builder.AGENT_COLOURS["codex"], "Codex session"],
		["◯", Builder.PR_MERGED, "merged pull request"],
		["◯", Builder.PR_OPEN, "open pull request"],
		["⬡", Color("#8A9BB5"), "worktree"],
		["▮", Builder.PANE_COLOUR, "live pane"],
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
	help.text = "Left drag turn  ·  Right drag pan  ·  Wheel zoom  ·  WASD / Q E move (Shift faster)  ·  Click select  ·  Double-click or F fly to  ·  R reset"
	foot_box.add_child(help)

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
	for i in range(45):
		await get_tree().process_frame
	var image := get_viewport().get_texture().get_image()
	DirAccess.make_dir_recursive_absolute(screenshot_path.get_base_dir())
	image.save_png(screenshot_path)
	print("SCREENSHOT ", screenshot_path)
	get_tree().quit()
