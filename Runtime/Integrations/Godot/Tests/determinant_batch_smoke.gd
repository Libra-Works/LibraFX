extends SceneTree


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	var extension := "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(extension):
		if not check(GDExtensionManager.load_extension(extension) == GDExtensionManager.LOAD_STATUS_OK,
				"Extension loaded"):
			return
	var ids: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://adapter.ids.json"))
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	root.add_child(player)
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	var mesh := BoxMesh.new()
	mesh.material = material
	if not check(player.set_mesh_binding("/Engine/BasicShapes/Cube.Cube", mesh) == 0 and
			player.play_package(FileAccess.get_file_as_bytes("res://adapter.lfxp")) == 0,
			"Opaque fixture loads"):
		return
	player.set_process(false)
	if not check(player.advance(0.01) == 0 and
			player.dispatch_signal(str(ids["rootSignalNodeId"])) == 0 and
			player.advance(0.25) == 0 and
			player.dispatch_signal(str(ids["rootSignalNodeId"])) == 0 and
			player.advance(0.25) == 0, "Two child scopes spawn"):
		return
	var scope: Node3D = player.get_child(0)
	if not check(scope.get_child_count() == 2, "Two live child scopes exist"):
		return
	var first: Node3D = scope.get_child(0)
	var second: Node3D = scope.get_child(1)
	var stats: Dictionary = player.get_render_stats()
	if not check(stats.get("opaque_model_multimesh_batches") == 1 and
			stats.get("opaque_model_multimesh_members") == 3,
			"Three positive determinants share one batch"):
		return
	first.scale.x = -1.0
	await process_frame
	await process_frame
	stats = player.get_render_stats()
	if not check_mixed(stats, "One mirrored child stays direct while two positive draws batch"):
		return
	first.scale.x = 0.0
	await process_frame
	await process_frame
	stats = player.get_render_stats()
	if not check_mixed(stats, "One singular child stays direct while two positive draws batch"):
		return
	scope.scale.x = -1.0
	first.scale.x = -1.0
	second.scale.x = -1.0
	await process_frame
	await process_frame
	stats = player.get_render_stats()
	if not check_mixed(stats, "Two mirrored children under a mirrored parent have positive world parity"):
		return
	scope.scale.x = 1.0
	await process_frame
	await process_frame
	stats = player.get_render_stats()
	if not check(stats.get("opaque_model_multimesh_batches") == 0 and
			stats.get("allocated_draw_instances") == 3 and
			stats.get("allocated_render_server_instances") == 3,
			"Two negative world determinants and one positive determinant all stay direct"):
		return
	first.scale.x = 1.0
	second.scale.x = 1.0
	await process_frame
	await process_frame
	stats = player.get_render_stats()
	if not check(stats.get("opaque_model_multimesh_batches") == 1 and
			stats.get("opaque_model_multimesh_members") == 3,
			"Positive determinants can batch again while held"):
		return
	print("OBS restored positive determinants: ", stats)
	player.stop()
	if not check(player.get_render_stats().is_empty(), "Stop releases the render state"):
		return
	player.free()
	if not await check_draw_local_scale(ids):
		return
	print("PASS: effective world determinant batching eligibility")
	quit(0)


func check_draw_local_scale(ids: Dictionary) -> bool:
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	root.add_child(player)
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	var mesh := BoxMesh.new()
	mesh.material = material
	var transparent_material := StandardMaterial3D.new()
	transparent_material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	var transparent_mesh := BoxMesh.new()
	transparent_mesh.material = transparent_material
	if not check(player.set_mesh_binding("/Engine/BasicShapes/Cube.Cube", mesh) == 0 and
			player.set_mesh_binding("/Tests/Transparent", transparent_mesh) == 0 and
			player.play_package(FileAccess.get_file_as_bytes("res://opaque-mirrored-local.lfxp")) == 0,
			"Draw-local negative-scale fixture loads"):
		return false
	player.set_process(false)
	if not check(player.advance(0.01) == 0 and
			player.dispatch_signal(str(ids["rootSignalNodeId"])) == 0 and
			player.advance(0.25) == 0 and
			player.dispatch_signal(str(ids["rootSignalNodeId"])) == 0 and
			player.advance(0.25) == 0, "Draw-local fixture spawns two children"):
		return false
	var scope: Node3D = player.get_child(0)
	if not check(scope.get_child_count() == 2, "Draw-local fixture has two child scopes"):
		return false
	var first: Node3D = scope.get_child(0)
	var second: Node3D = scope.get_child(1)
	var stats: Dictionary = player.get_render_stats()
	if not check(stats.get("opaque_model_multimesh_batches") == 1 and
			stats.get("opaque_model_multimesh_members") == 2 and
			stats.get("allocated_draw_instances") == 2,
			"A negative draw-local scale stays separate while positive children batch"):
		return false
	scope.scale.x = -1.0
	await process_frame
	await process_frame
	stats = player.get_render_stats()
	if not check(stats.get("opaque_model_multimesh_batches") == 0 and
			stats.get("allocated_draw_instances") == 4,
			"A reflected scope flips the draw-local and child world determinants"):
		return false
	first.scale.x = -1.0
	second.scale.x = -1.0
	await process_frame
	await process_frame
	stats = player.get_render_stats()
	if not check(stats.get("opaque_model_multimesh_batches") == 1 and
			stats.get("opaque_model_multimesh_members") == 3 and
			stats.get("allocated_draw_instances") == 1,
			"Double reflections produce three batchable positive world determinants"):
		return false
	print("OBS draw-local double reflection: ", stats)
	player.stop()
	player.free()
	return true


func check_mixed(stats: Dictionary, label: String) -> bool:
	print("OBS ", label, ": ", stats)
	return check(stats.get("opaque_model_multimesh_batches") == 1 and
			stats.get("opaque_model_multimesh_members") == 2 and
			stats.get("allocated_draw_instances") == 1 and
			stats.get("allocated_render_server_instances") == 2, label)


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
