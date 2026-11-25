extends SceneTree


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	const EXTENSION = "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK,
				"Extension library loaded"):
			return

	var package := FileAccess.get_file_as_bytes("res://cpu-particles.lfxp")
	if not check(not package.is_empty(), "Isolated CPU particle fixture is available"):
		return
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	if not check(player != null, "Extension class loaded"):
		return
	root.add_child(player)
	if not check(player.play_package(package) == 0, "CPU particle package loads"):
		return
	player.set_process(false)
	if not check(player.advance(0.0) == 0 and player.advance(1.0) == 0,
			"CPU particle simulation advances"):
		return

	var stats: Dictionary = player.get_render_stats()
	if not check(stats.get("cpu_multimesh_batches", 0) == 1 and
			stats.get("render_server_instances", 0) == 1,
			"One CPU emitter owns one direct MultiMesh batch and server instance"):
		return
	if not check(stats.get("cpu_multimesh_buffer_uploads", 0) > 0 and
			stats.get("cpu_particle_records_transferred", 0) > 0,
			"Populated CPU particles transfer through bulk MultiMesh buffer uploads"):
		return
	if not check(stats.get("cpu_particle_records_transferred", 0) >
			stats.get("cpu_particle_active_records", 0) and
			stats.get("cpu_particle_bytes_transferred", 0) ==
			stats.get("cpu_particle_records_transferred", 0) * 16 * 4,
			"Capacity padding is included in submitted records and bytes, not active records"):
		return
	if not check(not has_scene_tree_render_nodes(player),
			"CPU Sprite rendering creates no MultiMeshInstance3D or MeshInstance3D Nodes"):
		return
	print("OBS: CPU render submission stats = ", stats)

	player.stop()
	if not check(player.get_render_stats().is_empty(),
			"Stopping playback releases the effect-owned CPU render state"):
		return
	player.free()
	print("PASS: Godot submitted CPU Sprite particles through a direct MultiMesh RID")
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
