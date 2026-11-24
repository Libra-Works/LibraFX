@tool
class_name LibraFxEffect
extends Node3D
## Inspector-friendly wrapper around LibraEffectPlayer for imported LibraFxPackage assets.

@export var package: Resource
@export var autoplay: bool = true
@export var duration_seconds: float = -1.0
@export var mesh_bindings: Dictionary = {}
@export var texture_bindings: Dictionary = {}
@export var material_bindings: Dictionary = {}

var _player: Node


func _ready() -> void:
	if Engine.is_editor_hint():
		return
	_ensure_player()
	if autoplay and package != null and not (package.get("package_bytes") as PackedByteArray).is_empty():
		play()


func play() -> int:
	_ensure_player()
	if _player == null or package == null:
		return ERR_INVALID_DATA
	var bytes: PackedByteArray = package.get("package_bytes")
	if bytes.is_empty():
		return ERR_INVALID_DATA
	var auto_resolved: Variant = package.get("auto_resolved_resources")
	if auto_resolved is Dictionary:
		_apply_resource_map(auto_resolved)
	var overrides: Variant = package.get("resource_overrides")
	if overrides is Dictionary:
		_apply_resource_map(overrides)
	for key in mesh_bindings:
		var mesh := mesh_bindings[key] as Mesh
		if mesh != null:
			_player.call("set_mesh_binding", str(key), mesh)
	for key in texture_bindings:
		var tex := texture_bindings[key] as Texture2D
		if tex != null:
			_player.call("set_texture_binding", str(key), tex)
	for key in material_bindings:
		var mat := material_bindings[key] as Material
		if mat != null:
			_player.call("set_material_binding", str(key), mat)
	return int(_player.call("play", bytes, duration_seconds))


func _apply_resource_map(entries: Dictionary) -> void:
	for key in entries:
		var res: Variant = entries[key]
		if res is Mesh:
			_player.call("set_mesh_binding", str(key), res)
		elif res is Texture2D:
			_player.call("set_texture_binding", str(key), res)
		elif res is Material:
			_player.call("set_material_binding", str(key), res)


func stop() -> void:
	if _player != null:
		_player.call("stop")


func pause() -> int:
	return int(_player.call("pause")) if _player != null else ERR_UNAVAILABLE


func resume() -> int:
	return int(_player.call("resume")) if _player != null else ERR_UNAVAILABLE


func is_paused() -> bool:
	return bool(_player.call("is_paused")) if _player != null else false


func is_playing() -> bool:
	return bool(_player.call("is_playing")) if _player != null else false


func _ensure_player() -> void:
	if _player != null and is_instance_valid(_player):
		return
	if not ClassDB.class_exists(&"LibraEffectPlayer"):
		return
	_player = ClassDB.instantiate(&"LibraEffectPlayer") as Node
	if _player != null:
		_player.name = "LibraEffectPlayer"
		add_child(_player)
