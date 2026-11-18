extends RefCounted
## One project's files as a city: each folder a district (nested rectangles, sized by how many files it holds), each
## file a building on a grid inside its folder, height from its size, colour from its kind. All buildings are one
## MultiMesh, so thousands cost one draw; a building flashes when a session touches its file.

const GAP := 0.35          # between sibling districts
const CELL := 1.25         # grid step for a file's building
const KIND_COLOURS := {
	"cs": "#A78BFA", "xaml": "#C4B5FD", "gd": "#60A5FA", "tscn": "#93C5FD", "py": "#FACC15", "ipynb": "#FDE047",
	"ts": "#38BDF8", "tsx": "#22D3EE", "js": "#FDE68A", "jsx": "#FCD34D", "css": "#F472B6", "html": "#FB7185",
	"sql": "#7DD3FC", "bicep": "#3B82F6", "tf": "#818CF8", "json": "#FB923C", "yml": "#F59E0B", "yaml": "#F59E0B",
	"toml": "#FDBA74", "md": "#CBD5E1", "txt": "#94A3B8", "ps1": "#2DD4BF", "cmd": "#5EEAD4", "sh": "#5EEAD4",
	"png": "#F9A8D4", "jpg": "#F9A8D4", "svg": "#F9A8D4", "ico": "#F9A8D4", "csproj": "#8B5CF6", "sln": "#8B5CF6",
}
const OTHER_COLOUR := "#64748B"

## Half the city's side, for laying the island out.
var half := 4.0
## path -> {index: int, top: Vector3 (local), colour: Color}
var buildings := {}
## folder path ("" is the root) -> centre (local, at street level)
var districts := {}
## Pickables for world.gd: {pos (local, converted by the caller), kind, title, body, id}
var pickables: Array = []
var multimesh: MultiMesh

var _flash := {}           # index -> remaining glow


func build(project: Dictionary, root: Node3D) -> void:
	var files: Array = project.get("files", [])
	var tree := {"name": "", "path": "", "dirs": {}, "files": [], "count": 0}
	for f in files:
		var path: String = f["path"]
		var parts := path.split("/")
		var node := tree
		node["count"] += 1
		for i in range(parts.size() - 1):
			var dirs: Dictionary = node["dirs"]
			if not dirs.has(parts[i]):
				var sub_path: String = parts[i] if node["path"] == "" else node["path"] + "/" + parts[i]
				dirs[parts[i]] = {"name": parts[i], "path": sub_path, "dirs": {}, "files": [], "count": 0}
			node = dirs[parts[i]]
			node["count"] += 1
		node["files"].append(f)

	var side: float = max(sqrt(float(max(files.size(), 1))) * CELL * 1.25, 6.0)
	half = side / 2.0
	var mesh := BoxMesh.new()
	multimesh = MultiMesh.new()
	multimesh.transform_format = MultiMesh.TRANSFORM_3D
	multimesh.use_colors = true
	multimesh.use_custom_data = true
	multimesh.mesh = mesh
	multimesh.instance_count = files.size()
	var placed := [0]
	_layout(tree, Rect2(-half, -half, side, side), 0, root, placed)
	multimesh.visible_instance_count = placed[0]

	var city := MultiMeshInstance3D.new()
	city.multimesh = multimesh
	var shader := Shader.new()
	shader.code = """
shader_type spatial;
varying float glow;
void vertex() { glow = INSTANCE_CUSTOM.r; }
void fragment() {
	ALBEDO = COLOR.rgb * 0.30;
	ROUGHNESS = 0.55;
	EMISSION = COLOR.rgb * (0.35 + glow * 4.0);
}
"""
	var mat := ShaderMaterial.new()
	mat.shader = shader
	city.material_override = mat
	root.add_child(city)


## Lays out one folder in `rect` (x/z on the ground): its sub-folders slice the rect by file count, its own files take a grid.
func _layout(node: Dictionary, rect: Rect2, depth: int, root: Node3D, placed: Array) -> void:
	districts[node["path"]] = Vector3(rect.get_center().x, 0, rect.get_center().y)
	if depth > 0 and depth <= 3:
		_plate(root, rect, depth)
	if depth == 1 and rect.size.x > 1.5:
		_label(root, Vector3(rect.position.x + 0.2, 0.12, rect.end.y - 0.2), node["name"])

	var items: Array = []
	for name in node["dirs"]:
		items.append({"weight": float(node["dirs"][name]["count"]), "dir": node["dirs"][name]})
	if not node["files"].is_empty():
		items.append({"weight": float(node["files"].size()), "files": node["files"]})
	items.sort_custom(func(a, b): return a["weight"] > b["weight"])
	var total := 0.0
	for it in items:
		total += it["weight"]
	if total <= 0.0:
		return

	var inner := rect.grow(-GAP * 0.5) if depth > 0 else rect
	var horizontal := inner.size.x >= inner.size.y
	var offset := 0.0
	for it in items:
		var share: float = it["weight"] / total
		var r: Rect2
		if horizontal:
			r = Rect2(inner.position.x + offset * inner.size.x, inner.position.y, inner.size.x * share, inner.size.y)
		else:
			r = Rect2(inner.position.x, inner.position.y + offset * inner.size.y, inner.size.x, inner.size.y * share)
		offset += share
		if it.has("dir"):
			_layout(it["dir"], r, depth + 1, root, placed)
		else:
			_grid(it["files"], r.grow(-GAP * 0.25), node["path"], placed)


func _grid(files: Array, rect: Rect2, folder: String, placed: Array) -> void:
	var n := files.size()
	var aspect: float = max(rect.size.x, 0.01) / max(rect.size.y, 0.01)
	var cols: int = max(1, int(ceil(sqrt(n * aspect))))
	var rows: int = max(1, int(ceil(float(n) / cols)))
	var cw := rect.size.x / cols
	var ch := rect.size.y / rows
	var foot: float = clamp(min(cw, ch) * 0.72, 0.08, 1.0)
	for i in range(n):
		var f: Dictionary = files[i]
		var cx := rect.position.x + (i % cols + 0.5) * cw
		var cz := rect.position.y + (i / cols + 0.5) * ch
		var size := float(f.get("size", 0))
		var h: float = 0.25 + clamp(log(size + 1.0) / log(10.0) - 2.0, 0.0, 4.5) * 0.75
		var index: int = placed[0]
		placed[0] += 1
		var t := Transform3D(Basis().scaled(Vector3(foot, h, foot)), Vector3(cx, h / 2.0 + 0.06, cz))
		multimesh.set_instance_transform(index, t)
		var colour := colour_of(f["path"])
		multimesh.set_instance_color(index, colour)
		multimesh.set_instance_custom_data(index, Color(0, 0, 0, 0))
		var top := Vector3(cx, h + 0.06, cz)
		buildings[f["path"]] = {"index": index, "top": top, "colour": colour}
		pickables.append({"pos": top, "kind": "file", "title": f["path"],
			"body": "%s · %s" % [_size_text(size), folder if folder != "" else "(project root)"], "id": "file:" + f["path"]})


static func colour_of(path: String) -> Color:
	var ext := path.get_extension().to_lower()
	return Color(KIND_COLOURS.get(ext, OTHER_COLOUR))


## Where a session touching `path` stands: on the building, else in the nearest folder that exists, else mid-city.
func file_pos(path) -> Vector3:
	if path == null or str(path) == "":
		return Vector3(0, 0.1, half + 1.0)
	var p := str(path)
	if buildings.has(p):
		return buildings[p]["top"]
	var folder := p.get_base_dir()
	while folder != "":
		if districts.has(folder):
			return districts[folder] + Vector3(0, 0.15, 0)
		folder = folder.get_base_dir()
	return districts.get("", Vector3.ZERO) + Vector3(0, 0.15, 0)


## Lights a file's building (an edit or write); it fades over a few seconds.
func flash(path, strength: float = 1.0) -> void:
	if path == null or not buildings.has(str(path)):
		return
	var index: int = buildings[str(path)]["index"]
	_flash[index] = max(float(_flash.get(index, 0.0)), strength)
	multimesh.set_instance_custom_data(index, Color(_flash[index], 0, 0, 0))


## How brightly a file's building glows now (0 when quiet).
func glow(path) -> float:
	if path == null or not buildings.has(str(path)):
		return 0.0
	return float(_flash.get(buildings[str(path)]["index"], 0.0))


func tick(delta: float) -> void:
	for index in _flash.keys():
		var v: float = _flash[index] - delta * 0.35
		if v <= 0.0:
			_flash.erase(index)
			v = 0.0
		else:
			_flash[index] = v
		multimesh.set_instance_custom_data(index, Color(v, 0, 0, 0))


func _plate(root: Node3D, rect: Rect2, depth: int) -> void:
	var plate := MeshInstance3D.new()
	var box := BoxMesh.new()
	box.size = Vector3(rect.size.x, 0.04, rect.size.y)
	plate.mesh = box
	var m := StandardMaterial3D.new()
	var shade: float = 0.07 + 0.03 * depth
	m.albedo_color = Color(shade, shade + 0.02, shade + 0.05)
	m.roughness = 0.9
	plate.material_override = m
	plate.position = Vector3(rect.get_center().x, 0.02 * depth, rect.get_center().y)
	root.add_child(plate)


func _label(root: Node3D, pos: Vector3, text: String) -> void:
	var label := Label3D.new()
	label.text = text + "/"
	label.font_size = 40
	label.pixel_size = 0.006
	label.outline_size = 8
	label.outline_modulate = Color(0.02, 0.04, 0.07, 0.9)
	label.modulate = Color("#8A9BB5")
	label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	label.position = pos + Vector3(0, 0.25, 0)
	label.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	root.add_child(label)


static func _size_text(size: float) -> String:
	if size < 1024.0:
		return "%d B" % int(size)
	if size < 1048576.0:
		return "%.1f KB" % (size / 1024.0)
	return "%.1f MB" % (size / 1048576.0)
