extends SceneTree


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	var extension := "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(extension):
		if not check(GDExtensionManager.load_extension(extension) == GDExtensionManager.LOAD_STATUS_OK,
				"Extension loaded"):
			return
	if not check(not ClassDB.class_exists("RenderScopeNode"),
			"Generic scope helper class is not registered"):
		return
	print("OBS internal helper: class_exists=", ClassDB.class_exists("LibraRenderScopeNode"),
			" can_instantiate=", ClassDB.can_instantiate("LibraRenderScopeNode"),
			" in_node3d_inheriters=", ClassDB.get_inheriters_from_class("Node3D").has("LibraRenderScopeNode"))
	if not check(ClassDB.class_exists("LibraRenderScopeNode") and
			not ClassDB.can_instantiate("LibraRenderScopeNode"),
			"Namespaced helper is registered internally and not script-instantiable"):
		return
	var package := FileAccess.get_file_as_bytes("res://opaque-batch.lfxp")
	var identities: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://adapter.ids.json"))
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	root.add_child(player)
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	var mesh := BoxMesh.new()
	mesh.material = material
	var other_material := StandardMaterial3D.new()
	other_material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	var other_mesh := BoxMesh.new()
	other_mesh.material = other_material
	if not check(player.set_mesh_binding("/Engine/BasicShapes/Cube.Cube", mesh) == 0 and
			player.set_mesh_binding("/Tests/Transparent", other_mesh) == 0 and
			player.play_package(package) == 0, "Opaque ModelDraw package loads"):
		return
	player.set_process(false)
	var initial_stats: Dictionary = player.get_render_stats()
	if not check(initial_stats.get("render_server_instances") == 2 and
			initial_stats.get("allocated_render_server_instances") == 2 and
			initial_stats.get("allocated_draw_instances") == 2 and
			initial_stats.get("allocated_render_server_multimeshes") == 0,
			"Before advance, both root submission slots have allocated instance RIDs"):
		return
	if not check(player.advance(0.01) == 0 and
			player.dispatch_signal(str(identities["rootSignalNodeId"])) == 0 and
			player.advance(0.25) == 0, "Root and child draws are submitted"):
		return
	var stats: Dictionary = player.get_render_stats()
	if not check(stats.get("opaque_model_multimesh_batches") == 1 and
			stats.get("opaque_model_multimesh_members") == 2 and
			stats.get("render_server_instances") == 2 and
			stats.get("allocated_render_server_instances") == 2 and
			stats.get("allocated_draw_instances") == 1 and
			stats.get("allocated_opaque_model_batch_instances") == 1 and
			stats.get("allocated_render_server_multimeshes") == 1,
			"Two compatible root/child draws share one batch alongside a separate transparent draw"):
		return
	print("OBS opaque batch: ", stats)
	var scope: Node3D = player.get_child(0)
	scope.hide()
	await process_frame
	await process_frame
	stats = player.get_render_stats()
	if not check(stats.get("render_server_instances") == 2 and
			stats.get("allocated_render_server_instances") == 2 and
			stats.get("allocated_draw_instances") == 1 and
			stats.get("allocated_opaque_model_batch_instances") == 1,
			"Hiding the scope does not deallocate or change submission-slot counts"):
		return
	scope.show()
	material.set_flag(BaseMaterial3D.FLAG_DISABLE_DEPTH_TEST, true)
	await process_frame
	await process_frame
	stats = player.get_render_stats()
	if not check(stats.get("opaque_model_multimesh_batches") == 0 and
			stats.get("render_server_instances") == 3 and
			stats.get("allocated_render_server_instances") == 3 and
			stats.get("allocated_draw_instances") == 3 and
			stats.get("allocated_opaque_model_batch_instances") == 0 and
			stats.get("allocated_render_server_multimeshes") == 0,
			"Nonstandard depth behavior falls back to separate instances"):
		return
	material.set_flag(BaseMaterial3D.FLAG_DISABLE_DEPTH_TEST, false)
	await process_frame
	await process_frame
	if not check(player.get_render_stats().get("opaque_model_multimesh_batches") == 1,
			"Compatible opaque state can be batched again"):
		return
	material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	await process_frame
	await process_frame
	stats = player.get_render_stats()
	if not check(stats.get("opaque_model_multimesh_batches") == 0 and
			stats.get("render_server_instances") == 3,
			"Mutating the shared source material to transparent restores three separate instances"):
		return
	var custom := ShaderMaterial.new()
	var shader := Shader.new()
	shader.code = "shader_type spatial; void fragment() { ALBEDO = vec3(1.0); }"
	custom.shader = shader
	mesh.material = custom
	await process_frame
	await process_frame
	stats = player.get_render_stats()
	if not check(stats.get("opaque_model_multimesh_batches") == 0 and
			stats.get("render_server_instances") == 3 and mesh.material == custom and
			other_mesh.material == other_material,
			"Custom shader state stays separate and source mesh is not replaced"):
		return
	print("OBS incompatible fallback: ", stats)
	player.stop()
	if not check(player.get_render_stats().is_empty(), "Stop releases the renderer and its statistics"):
		return
	player.free()
	print("PASS: conservative root/child opaque batching and mutable-material fallback")
	quit(0)


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
