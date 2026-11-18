extends SceneTree
## Headless checks of the builders against the bundled sample:
##   godot --headless --path world --script res://tests/test_world.gd
## Prints PASS or the failures, and exits 0 or 1.

const TreeBuilder := preload("res://scripts/world_builder.gd")
const CityBuilder := preload("res://scripts/city_builder.gd")
const Crab := preload("res://scripts/crab.gd")

var failures: Array = []


func _init() -> void:
	var data = JSON.parse_string(FileAccess.get_file_as_string("res://sample/world.json"))
	check(typeof(data) == TYPE_DICTIONARY and int(data["schema"]) == 2, "sample is schema 2")
	var project: Dictionary = data["projects"][0]

	# The git tree
	var root := Node3D.new()
	var tree := TreeBuilder.new()
	tree.build(project, root, {})
	check(tree.stats["commits"] == 6, "6 commits, got %s" % tree.stats["commits"])
	check(tree.stats["sessions"] == 3 and tree.stats["live"] == 1, "3 sessions, 1 live")
	var kinds := {}
	for p in tree.pickables:
		kinds[p["kind"]] = true
	for kind in ["commit", "merge", "session", "branch", "pull request", "worktree"]:
		check(kinds.has(kind), "tree has something of kind " + kind)
	check(tree.branch_tip("feature/api").distance_to(tree.trunk_top()) > 1.0, "an open branch's tip is off the trunk")
	check(tree.branch_tip("main") == tree.trunk_top(), "main's tip is the trunk top")
	check(tree.branch_tip("no-such-branch") == tree.trunk_top(), "an unknown branch falls back to the trunk")
	check(tree.reach > 3.0, "lanes widen the tree")

	# A new commit grows in; known ones do not
	var tree2 := TreeBuilder.new()
	var project2: Dictionary = project.duplicate(true)
	project2["commits"].append({"hash": "f100000000000000000000000000000000000000", "short": "f100000", "parents": [],
		"time": "2026-11-14T02:00:00+00:00", "author": "Ada", "subject": "fix", "branch": "feature/api@1", "row": 4})
	var root2 := Node3D.new()
	tree2.build(project2, root2, tree.ids)
	check(tree2.fresh.size() == 1, "one new node grows in, got %d" % tree2.fresh.size())

	# The file city
	var city_root := Node3D.new()
	var city := CityBuilder.new()
	city.build(project, city_root)
	check(city.buildings.size() == 9, "9 buildings, got %d" % city.buildings.size())
	check(city.multimesh.visible_instance_count == 9, "all buildings drawn")
	var a: Vector3 = city.file_pos("src/api/client.ts")
	var b: Vector3 = city.file_pos("src/api/retry.ts")
	var c: Vector3 = city.file_pos("README.md")
	check(a != b and a.distance_to(b) < c.distance_to(a) + 6.0, "files of one folder sit near each other")
	check(city.file_pos("src/api/new_file.ts").distance_to(city.districts["src/api"] + Vector3(0, 0.15, 0)) < 0.01, "a new file stands in its folder")
	check(abs(city.file_pos(null).z - (city.half + 1.0)) < 0.01, "no file: the city front")
	for f in project["files"]:
		var top: Vector3 = city.file_pos(f["path"])
		check(abs(top.x) <= city.half + 0.01 and abs(top.z) <= city.half + 0.01, "%s inside the city" % f["path"])
	city.flash("src/api/retry.ts")
	check(city.glow("src/api/retry.ts") > 0.9, "an edit lights the building")
	check(city.glow("README.md") == 0.0, "others stay dark")
	city.tick(10.0)
	check(city.glow("src/api/retry.ts") == 0.0, "and it fades")
	check(CityBuilder.colour_of("x/y.cs") != CityBuilder.colour_of("x/y.md"), "kinds have colours")

	# The crab cuts its strips into animations
	var frames := Crab.frames(ProjectSettings.globalize_path("res://").path_join("../src/AgentHub/Assets/Crab"))
	check(frames.has_animation("hop") and frames.get_frame_count("hop") == 9, "crab hop has 9 frames")
	check(frames.has_animation("pinch") and frames.has_animation("sleep"), "crab has pinch and sleep")

	# The tube helper
	check(TreeBuilder.tube(PackedVector3Array([Vector3.ZERO, Vector3.UP]), 0.1) != null, "tube builds")
	check(TreeBuilder.tube(PackedVector3Array([Vector3.ZERO]), 0.1) == null, "one point makes no tube")

	for n in [root, root2, city_root]:
		n.free()
	if failures.is_empty():
		print("PASS test_world")
		quit(0)
	else:
		for f in failures:
			printerr("FAIL ", f)
		quit(1)


func check(ok: bool, what: String) -> void:
	if not ok:
		failures.append(what)
