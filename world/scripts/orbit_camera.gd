extends Camera3D
## Orbits a target point. Left drag turns, right or middle drag pans, the wheel zooms, WASD moves the target over the
## ground and Q/E down and up. Moves ease towards where they are going, so a refocus glides rather than jumps.

var target := Vector3.ZERO
var yaw := 0.6
var pitch := -0.25
var distance := 30.0

var _goal_target := Vector3.ZERO
var _goal_distance := 30.0
var _dragging := 0   # 0 none, 1 orbit, 2 pan
var drag_moved := 0.0


func focus(point: Vector3, dist: float = -1.0) -> void:
	_goal_target = point
	if dist > 0.0:
		_goal_distance = dist


func place(point: Vector3, dist: float) -> void:
	target = point
	_goal_target = point
	distance = dist
	_goal_distance = dist
	_apply()


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_WHEEL_UP and mb.pressed:
			_goal_distance = max(_goal_distance * 0.88, 2.0)
		elif mb.button_index == MOUSE_BUTTON_WHEEL_DOWN and mb.pressed:
			_goal_distance = min(_goal_distance * 1.14, 600.0)
		elif mb.button_index == MOUSE_BUTTON_LEFT:
			_dragging = 1 if mb.pressed else 0
			if mb.pressed:
				drag_moved = 0.0
		elif mb.button_index == MOUSE_BUTTON_RIGHT or mb.button_index == MOUSE_BUTTON_MIDDLE:
			_dragging = 2 if mb.pressed else 0
	elif event is InputEventMouseMotion and _dragging != 0:
		var mm := event as InputEventMouseMotion
		drag_moved += mm.relative.length()
		if _dragging == 1:
			yaw -= mm.relative.x * 0.006
			pitch = clamp(pitch - mm.relative.y * 0.006, -1.45, 1.2)
		else:
			var pan := _goal_distance * 0.0016
			var b := global_transform.basis
			_goal_target += (-b.x * mm.relative.x + b.y * mm.relative.y) * pan


func _process(delta: float) -> void:
	var move := Vector3.ZERO
	var flat_forward := Vector3(-sin(yaw), 0, -cos(yaw))
	var flat_right := Vector3(cos(yaw), 0, -sin(yaw))
	if Input.is_key_pressed(KEY_W): move += flat_forward
	if Input.is_key_pressed(KEY_S): move -= flat_forward
	if Input.is_key_pressed(KEY_D): move += flat_right
	if Input.is_key_pressed(KEY_A): move -= flat_right
	if Input.is_key_pressed(KEY_E): move += Vector3.UP
	if Input.is_key_pressed(KEY_Q): move -= Vector3.UP
	if move != Vector3.ZERO:
		var speed := 0.6 * _goal_distance * (3.0 if Input.is_key_pressed(KEY_SHIFT) else 1.0)
		_goal_target += move.normalized() * speed * delta
	var k: float = 1.0 - exp(-delta * 8.0)
	target = target.lerp(_goal_target, k)
	distance = lerp(distance, _goal_distance, k)
	_apply()


func _apply() -> void:
	var offset := Vector3(sin(yaw) * cos(pitch), -sin(pitch), cos(yaw) * cos(pitch)) * distance
	position = target + offset
	look_at(target, Vector3.UP)
