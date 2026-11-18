extends SceneTree
## Headless check of the world builder against the bundled sample:
##   godot --headless --path world --script res://tests/test_world.gd
## Prints PASS or the failures, and exits 0 or 1.

const Builder := preload("res://scripts/world_builder.gd")

var failures: Array = []


func _init() -> void:
	var data = JSON.parse_string(FileAccess.get_file_as_string("res://sample/world.json"))
	check(typeof(data) == TYPE_DICTIONARY, "sample parses")
	var root := Node3D.new()
	var b := Builder.new()
	b.build(data, root, {})
	check(b.stats["commits"] == 6, "6 commits, got %s" % b.stats["commits"])
	check(b.stats["sessions"] == 3 and b.stats["live"] == 1, "3 sessions, 1 live")
	check(b.stats["panes"] == 1, "1 pane")
	check(root.get_child_count() > 20, "nodes drawn: %d" % root.get_child_count())
	var kinds := {}
	for p in b.pickables:
		kinds[p["kind"]] = kinds.get(p["kind"], 0) + 1
	for kind in ["commit", "merge", "session", "branch", "pull request", "worktree", "live pane"]:
		check(kinds.has(kind), "something to pick of kind " + kind)
	check(b.pulsing.size() >= 3, "live session, open pull request and pane pulse")

	# The same file again with one new commit: only it grows in
	var b2 := Builder.new()
	var data2: Dictionary = data.duplicate(true)
	data2["commits"].append({"hash": "f000000000000000000000000000000000000000", "short": "f000000", "parents": ["e000000000000000000000000000000000000000"],
		"time": "2026-11-14T02:00:00+00:00", "author": "Ada", "subject": "fix", "branch": "feature/api@1", "row": 4, "session": null})
	var root2 := Node3D.new()
	b2.build(data2, root2, b.ids)
	check(b2.fresh.size() == 1, "one new node grows in, got %d" % b2.fresh.size())

	# A lane that merged ends on the trunk at the merge commit's row
	check(b.lane_point("trunk", 2).is_equal_approx(Vector3(0, 2 * Builder.ROW_H, 0)), "trunk point at row 2")
	check(Builder.tube(PackedVector3Array([Vector3.ZERO, Vector3.UP]), 0.1) != null, "tube builds")
	check(Builder.tube(PackedVector3Array([Vector3.ZERO]), 0.1) == null, "one point makes no tube")

	root.free()
	root2.free()
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
