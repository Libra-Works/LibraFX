extends SceneTree


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	if GDExtensionManager.is_extension_loaded("res://liberafx.gdextension"):
		push_error("The control must run without loading the LibraFX extension.")
		quit(1)
		return
	var viewport := SubViewport.new()
	viewport.size = Vector2i(128, 128)
	viewport.world_3d = World3D.new()
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var camera := Camera3D.new()
	camera.position.z = 5
	viewport.add_child(camera)
	camera.make_current()
	var mesh := MeshInstance3D.new()
	mesh.mesh = BoxMesh.new()
	viewport.add_child(mesh)
	await process_frame
	await RenderingServer.frame_post_draw
	print("OBS: replacing World3D without retaining the old resource; no extension is loaded")
	viewport.world_3d = World3D.new()
	await process_frame
	await RenderingServer.frame_post_draw
	viewport.queue_free()
	await process_frame
	print("OBS: Godot-only world swap control finished; inspect engine diagnostics")
	quit(0)
