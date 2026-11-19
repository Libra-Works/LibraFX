extends SceneTree


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	const EXTENSION = "res://liberafx.gdextension"
	const ASSET_IDENTITY = "/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK,
				"Extension library loaded"):
			return

	var package := FileAccess.get_file_as_bytes("res://composition.lfxp")
	if not check(not package.is_empty(), "Compiler-produced sprite composition package exists"):
		return
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	if not check(player != null, "Extension class loaded"):
		return
	root.add_child(player)
	if not check(player.play_package(package) != 0 and not player.is_playing(),
			"Missing Texture2D binding rejects the package"):
		return

	var image := Image.create(64, 32, false, Image.FORMAT_RGBA8)
	image.fill(Color.WHITE)
	var texture := ImageTexture.create_from_image(image)
	if not check(player.set_texture_binding(ASSET_IDENTITY, texture) == 0,
			"Explicit in-memory Texture2D binding is accepted"):
		return
	if not check(player.play_package(package) == 0, "Sprite composition package loads"):
		return
	player.set_process(false)
	if not check(player.advance(0.01) == 0 and player.advance(0.74) == 0,
			"Composition clock advances"):
		return
	if not check(player.set_texture_binding(ASSET_IDENTITY, texture) != 0 and
			player.clear_texture_binding(ASSET_IDENTITY) != 0,
			"Texture bindings cannot change during playback"):
		return

	var scope: Node3D
	for child in player.get_children():
		if child is Node3D and str(child.name).begins_with("LibraFxRootScope"):
			scope = child
			break
	if not check(scope != null, "Root render scope exists"):
		return
	if not check(not has_scene_tree_render_nodes(player),
			"Sprite draws use direct RenderingServer instances"):
		return
	var render_stats: Dictionary = player.get_render_stats()
	if not check(render_stats.get("render_server_instances", 0) == 2 and
			render_stats.get("effect_scope_nodes", 0) == 1,
			"Two sprite draws retain separate direct instances within one effect scope"):
		return

	player.stop()
	if not check(player.clear_texture_binding(ASSET_IDENTITY) == 0,
			"Texture binding can be removed after playback stops"):
		return
	player.free()
	print("PASS: Godot admitted explicit Texture2D sprite composition as direct RenderingServer instances")
	quit(0)


func has_scene_tree_render_nodes(parent: Node) -> bool:
	for child in parent.get_children():
		if child is MeshInstance3D or child is MultiMeshInstance3D:
			return true
		if has_scene_tree_render_nodes(child):
			return true
	return false


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
