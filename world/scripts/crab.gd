extends Node3D
## One session's crab: AgentHub's own mascot (the sprite strips in src/AgentHub/Assets/Crab, 80 px cells) standing in
## the 3D world. It hops in an arc to wherever its session's latest tool call points, then acts: pinches on an edit,
## looks on a read, dances on a commit or merge, waves on a reply. Asleep when its session goes quiet.
## Moves queue up; a long queue is played faster so the crab never falls behind its session.

const CELL := 80
const CLIPS := {"idle": 150, "scuttle": 80, "hop": 70, "pinch": 90, "wave": 110, "dance": 110, "roll": 75, "sleep": 170}
const ACTION_CLIP := {"edit": "pinch", "write": "pinch", "read": "idle", "search": "scuttle", "bash": "pinch",
	"commit": "dance", "push": "roll", "merge": "dance", "pr": "wave", "reply": "wave", "prompt": "wave",
	"snowflake": "pinch", "azure": "pinch", "web": "scuttle", "think": "idle", "start": "wave"}
const SLEEP_AFTER := 120.0

static var _frames: SpriteFrames

var session_id := ""
var title := ""
var tint := Color.WHITE

var _sprite: AnimatedSprite3D
var _label: Label3D
var _queue: Array = []        # [{pos: Vector3, action: String}]
var _busy := false
var _quiet := 0.0
var _home := Vector3.ZERO


## The crab strips, cut into one SpriteFrames once. `dir` holds idle.png, hop.png and the rest.
static func frames(dir: String) -> SpriteFrames:
	if _frames != null:
		return _frames
	_frames = SpriteFrames.new()
	_frames.remove_animation("default")
	for clip in CLIPS:
		var image := Image.load_from_file(dir.path_join(clip + ".png"))
		if image == null or image.is_empty():
			continue
		_frames.add_animation(clip)
		_frames.set_animation_speed(clip, 1000.0 / CLIPS[clip])
		_frames.set_animation_loop(clip, true)
		for i in range(image.get_width() / CELL):
			var cell := image.get_region(Rect2i(i * CELL, 0, CELL, CELL))
			_frames.add_frame(clip, ImageTexture.create_from_image(cell))
	return _frames


func setup(sprite_dir: String, id: String, name_text: String, colour: Color, scale_factor: float = 1.0) -> void:
	session_id = id
	title = name_text
	tint = colour
	_sprite = AnimatedSprite3D.new()
	_sprite.sprite_frames = frames(sprite_dir)
	_sprite.billboard = BaseMaterial3D.BILLBOARD_FIXED_Y
	_sprite.texture_filter = BaseMaterial3D.TEXTURE_FILTER_NEAREST
	_sprite.pixel_size = 0.016 * scale_factor
	_sprite.shaded = false
	_sprite.alpha_cut = SpriteBase3D.ALPHA_CUT_DISCARD
	_sprite.modulate = Color.WHITE.lerp(colour, 0.25)
	_sprite.position = Vector3(0, 0.55 * scale_factor, 0)
	add_child(_sprite)
	_play("wave")

	_label = Label3D.new()
	_label.text = name_text
	_label.font_size = 36
	_label.pixel_size = 0.006
	_label.outline_size = 10
	_label.outline_modulate = Color(0.02, 0.04, 0.07, 0.9)
	_label.modulate = colour.lightened(0.3)
	_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	_label.position = Vector3(0, 1.35 * scale_factor, 0)
	add_child(_label)

	# A faint pillar of light over the crab, so a live session can be spotted from across the world
	var pillar := MeshInstance3D.new()
	var beam := CylinderMesh.new()
	beam.top_radius = 0.02
	beam.bottom_radius = 0.09 * scale_factor
	beam.height = 9.0
	pillar.mesh = beam
	var pm := StandardMaterial3D.new()
	pm.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	pm.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	pm.albedo_color = Color(colour, 0.28)
	pm.cull_mode = BaseMaterial3D.CULL_DISABLED
	pillar.material_override = pm
	pillar.position = Vector3(0, 1.8 * scale_factor + 4.5, 0)
	add_child(pillar)

	var shadow := MeshInstance3D.new()
	var disc := CylinderMesh.new()
	disc.top_radius = 0.38 * scale_factor
	disc.bottom_radius = 0.38 * scale_factor
	disc.height = 0.01
	shadow.mesh = disc
	var m := StandardMaterial3D.new()
	m.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	m.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	m.albedo_color = Color(0, 0, 0, 0.45)
	shadow.material_override = m
	shadow.position = Vector3(0, 0.02, 0)
	add_child(shadow)


## Places the crab without a hop (first sight, or after a rebuild moved its ground).
func place(pos: Vector3) -> void:
	position = pos
	_home = pos


## Queues a hop to `pos`, then the action's animation. `label` replaces the caption for a moment (what it is doing).
func act(pos: Vector3, action: String, label: String = "") -> void:
	_quiet = 0.0
	_queue.append({"pos": pos, "action": action, "label": label})
	if _queue.size() > 6:
		_queue = _queue.slice(_queue.size() - 6)   # far behind: skip to the latest few
	if not _busy:
		_next()


## Asleep at once (its session has been quiet since before the crab was made).
func doze() -> void:
	_quiet = SLEEP_AFTER
	_play("sleep")


## Acts where it stands (a reply, a prompt, an orchestrator's instruction): the action's clip and a caption for a while.
func emote(action: String, label: String = "") -> void:
	act(position, action, label)


func _next() -> void:
	if _queue.is_empty():
		_busy = false
		_label.text = title
		_play("idle")
		return
	_busy = true
	var step: Dictionary = _queue.pop_front()
	var to: Vector3 = step["pos"]
	var hurry: float = 1.0 if _queue.size() < 2 else 0.45
	var dist := position.distance_to(to)
	var flight: float = clamp(0.25 + dist * 0.04, 0.3, 1.4) * hurry
	var height: float = clamp(0.6 + dist * 0.25, 0.8, 6.0)
	if step["label"] != "":
		_label.text = "%s\n%s" % [title, step["label"]]
	_play("hop" if dist > 0.05 else ACTION_CLIP.get(step["action"], "idle"))
	var from := position
	var tween := create_tween()
	tween.tween_method(func(t: float) -> void:
		position = from.lerp(to, t) + Vector3(0, 4.0 * height * t * (1.0 - t), 0), 0.0, 1.0, flight if dist > 0.05 else 0.01)
	tween.tween_callback(func() -> void: _play(ACTION_CLIP.get(step["action"], "idle")))
	tween.tween_interval(0.7 * hurry)
	tween.tween_callback(_next)
	_home = to


func _process(delta: float) -> void:
	if _busy:
		return
	_quiet += delta
	if _quiet > SLEEP_AFTER and _sprite.animation != "sleep":
		_play("sleep")


func _play(clip: String) -> void:
	if _sprite == null:
		return
	if _sprite.sprite_frames.has_animation(clip):
		_sprite.play(clip)
	elif _sprite.sprite_frames.has_animation("idle"):
		_sprite.play("idle")
