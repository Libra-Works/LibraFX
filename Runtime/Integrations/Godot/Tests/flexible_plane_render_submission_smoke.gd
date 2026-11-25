extends SceneTree


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	const EXTENSION = "res://liberafx.gdextension"
	const TEXTURE_IDENTITY = "/Game/FX/T_Ribbon"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK,
				"Extension library loaded"):
			return

	var package := FileAccess.get_file_as_bytes("res://flexible-plane.lfxp")
	if not check(not package.is_empty(), "Synthetic Flexible Plane package is available"):
		return
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	if not check(player != null, "Extension class loaded"):
		return
	root.add_child(player)
	if not check(player.set_mesh_binding("/Engine/BasicShapes/Cube.Cube", BoxMesh.new()) == 0,
			"Template ModelDraw binding is accepted"):
		return
	var image := Image.create_empty(2, 2, false, Image.FORMAT_RGBA8)
	image.fill(Color.WHITE)
	if not check(player.set_texture_binding(TEXTURE_IDENTITY,
			ImageTexture.create_from_image(image)) == 0,
			"Flexible Plane texture binding is accepted"):
		return
	if not check(player.play_package(package) == 0, "Flexible Plane package loads"):
		return
	player.set_process(false)
	if not check(player.advance(0.0) == 0 and player.advance(0.1) == 0,
			"Flexible Plane creates and then updates its dynamic mesh surface"):
		return
	var stats: Dictionary = player.get_render_stats()
	if not check(stats.get("render_server_instances", 0) == 1 and
			stats.get("effect_scope_nodes", 0) == 1 and
			stats.get("flexible_plane_surface_updates", 0) >= 2,
			"Flexible Plane creates and updates one direct mesh in one effect scope"):
		return
	if not check(not has_scene_tree_render_nodes(player),
			"Flexible Plane creates no MeshInstance3D scene-tree Node"):
		return
	print("OBS: Flexible Plane render submission stats = ", stats)

	player.stop()
	player.free()
	print("PASS: Godot updated Flexible Plane geometry through a persistent direct mesh RID")
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
