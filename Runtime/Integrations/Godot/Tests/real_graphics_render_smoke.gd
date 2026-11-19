extends SceneTree

const SIZE := Vector2i(320, 240)
const TEXTURE_IDENTITY := "/Game/FX/T_Ribbon"
var artifact_dir := ""


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	artifact_dir = OS.get_environment("LIBERAFX_PIXEL_ARTIFACTS")
	if not check(not artifact_dir.is_empty(), "An isolated artifact directory is required"):
		return
	var extension := "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(extension):
		if not check(GDExtensionManager.load_extension(extension) == GDExtensionManager.LOAD_STATUS_OK,
				"Extension loaded"):
			return
	print("OBS rendering device: ", RenderingServer.get_video_adapter_name(), " / ",
			RenderingServer.get_video_adapter_vendor())
	if not check(DisplayServer.get_name() != "headless", "A real display backend is required"):
		return
	if not await exercise("plane", "res://flexible-plane.lfxb", true):
		return
	if not await exercise("cpu", "res://cpu-particles.lfxb", false):
		return
	if not await exercise_model_batch():
		return
	if not await exercise_failure():
		return
	print("PASS: real rendered CPU Sprite and fixed-point Flexible Plane lifecycle")
	quit(0)


func exercise(label: String, path: String, plane: bool) -> bool:
	var viewport := SubViewport.new()
	viewport.size = SIZE
	viewport.process_mode = Node.PROCESS_MODE_ALWAYS
	viewport.world_3d = World3D.new()
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color.BLACK
	viewport.world_3d.environment = environment
	var camera := Camera3D.new()
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = 6.0
	camera.position = Vector3(1.0 if plane else 10.0, 0.0, 10.0)
	viewport.add_child(camera)
	camera.make_current()
	var control := MeshInstance3D.new()
	control.mesh = BoxMesh.new()
	control.position.x = 0.0 if plane else 10.0
	var control_material := StandardMaterial3D.new()
	control_material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	control_material.albedo_color = Color.WHITE
	control.material_override = control_material
	control.mesh.surface_set_material(0, control_material)
	viewport.add_child(control)
	var control_pixels := await capture(viewport, label + "-control")
	if not check(control_pixels.count > 10, label + ": control mesh renders in test viewport"):
		return false
	control.hide()
	var direct := RenderingServer.instance_create()
	RenderingServer.instance_set_base(direct, control.mesh.get_rid())
	RenderingServer.instance_set_scenario(direct, viewport.find_world_3d().scenario)
	RenderingServer.instance_set_transform(direct, control.transform)
	RenderingServer.instance_set_visible(direct, true)
	var direct_pixels := await capture(viewport, label + "-direct-control")
	RenderingServer.free_rid(direct)
	if not check(direct_pixels.count > 10, label + ": direct RID control renders"):
		return false
	var owner := Node3D.new()
	viewport.add_child(owner)
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	owner.add_child(player)
	if plane:
		var image := Image.create_empty(2, 2, false, Image.FORMAT_RGBA8)
		image.fill(Color.WHITE)
		if not check(player.set_texture_binding(TEXTURE_IDENTITY, ImageTexture.create_from_image(image)) == 0 and
				player.set_mesh_binding("/Engine/BasicShapes/Cube.Cube", BoxMesh.new()) == 0,
				label + ": bindings"):
			return false
	var package := FileAccess.get_file_as_bytes(path)
	if not check(player.play_package(package) == 0, label + ": package loads"):
		return false
	player.set_process(false)
	if not check(player.advance(0.0) == 0 and player.advance(0.1 if plane else 1.0) == 0,
			label + ": effect advances"):
		return false
	var initial_stats: Dictionary = player.get_render_stats()
	if not check(initial_stats.get("allocated_render_server_instances") == 1 and
			initial_stats.get("allocated_flexible_plane_instances") == (1 if plane else 0) and
			initial_stats.get("allocated_cpu_particle_instances") == (0 if plane else 1) and
			initial_stats.get("allocated_render_server_multimeshes") == (0 if plane else 1),
			label + ": allocated instance and MultiMesh RIDs are counted"):
		return false
	if not plane:
		if not check(initial_stats.get("cpu_multimesh_buffer_uploads") == 2 and
				initial_stats.get("cpu_particle_active_records") == 6 and
				initial_stats.get("cpu_particle_records_transferred") == 8 and
				initial_stats.get("cpu_particle_bytes_transferred") == 512,
				"cpu: transfer accounting includes the complete capacity-sized buffers"):
			return false
	print("OBS ", label, " stats: ", initial_stats, " playing=", player.is_playing())
	paused = true
	var original := await capture(viewport, label + "-original")
	if not check(original.count > 10, label + ": rendered pixels are visible"):
		return false
	if plane:
		var pixels := viewport.get_texture().get_image()
		var left := pixels.get_pixel(125, 120)
		var right := pixels.get_pixel(194, 120)
		if not check(left.r > left.g and right.g > right.r,
				"plane: dynamic mesh retains its red-to-green vertex color gradient"):
			return false
	owner.scale = Vector3(0.9, 1.3, 1.0)
	var scaled := await capture(viewport, label + "-scaled")
	if not check(scaled.count > original.count * 1.05,
			label + ": held owner scale changes the rendered bounds"):
		return false
	owner.scale = Vector3.ONE
	owner.position.x = 1.5
	var moved := await capture(viewport, label + "-moved")
	if not check(moved.count > 10 and moved.x > original.x + 25.0,
			label + ": owner transform changes pixels while paused"):
		return false
	owner.hide()
	var hidden := await capture(viewport, label + "-hidden")
	if not check(hidden.count == 0 and
			player.get_render_stats().get("allocated_render_server_instances") == 1,
			label + ": owner hide removes pixels but retains its allocated RID"):
		return false
	owner.show()
	var shown := await capture(viewport, label + "-shown")
	if not check(shown.count > 10, label + ": owner show restores pixels while paused"):
		return false
	var next_world := World3D.new()
	next_world.environment = environment
	var previous_world := viewport.world_3d
	viewport.world_3d = next_world
	var world_changed := await capture(viewport, label + "-world")
	if not check(previous_world != viewport.world_3d and world_changed.count > 10,
			label + ": pixels survive a world/scenario change"):
		return false
	var old_viewport := SubViewport.new()
	old_viewport.size = SIZE
	old_viewport.process_mode = Node.PROCESS_MODE_ALWAYS
	old_viewport.world_3d = previous_world
	old_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(old_viewport)
	var old_camera := Camera3D.new()
	old_camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	old_camera.size = camera.size
	old_camera.position = camera.position
	old_viewport.add_child(old_camera)
	old_camera.make_current()
	var old_pixels := await capture(old_viewport, label + "-old-world")
	if not check(old_pixels.count == 0, label + ": no stale pixels remain in the old scenario"):
		return false
	player.stop()
	var stopped := await capture(viewport, label + "-stopped")
	if not check(stopped.count == 0 and player.get_render_stats().is_empty(),
			label + ": Stop removes the submitted pixels"):
		return false
	paused = false
	old_viewport.queue_free()
	viewport.queue_free()
	await process_frame
	return true


func exercise_model_batch() -> bool:
	var viewport := SubViewport.new()
	viewport.size = SIZE
	viewport.process_mode = Node.PROCESS_MODE_ALWAYS
	viewport.world_3d = World3D.new()
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color.BLACK
	viewport.world_3d.environment = environment
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var camera := Camera3D.new()
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = 6.0
	camera.position = Vector3(1.0, 0.0, 10.0)
	viewport.add_child(camera)
	camera.make_current()
	var owner := Node3D.new()
	viewport.add_child(owner)
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	owner.add_child(player)
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	var mesh := BoxMesh.new()
	mesh.material = material
	var other_material := StandardMaterial3D.new()
	other_material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	other_material.albedo_color = Color.BLUE
	other_material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	var other_mesh := BoxMesh.new()
	other_mesh.material = other_material
	var ids: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://adapter.ids.json"))
	if not check(player.set_mesh_binding("/Engine/BasicShapes/Cube.Cube", mesh) == 0 and
			player.set_mesh_binding("/Tests/Transparent", other_mesh) == 0 and
			player.play_package(FileAccess.get_file_as_bytes("res://opaque-batch.lfxp")) == 0,
			"model: opaque fixture loads"):
		return false
	player.set_process(false)
	if not check(player.advance(0.01) == 0 and
			player.dispatch_signal(str(ids["rootSignalNodeId"])) == 0 and
			player.advance(0.25) == 0, "model: root and child advance"):
		return false
	var scope: Node3D = player.get_child(0)
	if not check(scope.get_child_count() == 1, "model: child scope exists"):
		return false
	var child_scope: Node3D = scope.get_child(0)
	child_scope.position.x = 2.0
	paused = true
	var batched := await capture(viewport, "model-batched")
	var stats: Dictionary = player.get_render_stats()
	if not check(stats.get("opaque_model_multimesh_batches") == 1 and
			stats.get("opaque_model_multimesh_members") == 2 and
			stats.get("render_server_instances") == 2 and
			stats.get("allocated_render_server_instances") == 2 and
			stats.get("allocated_draw_instances") == 1 and
			stats.get("allocated_opaque_model_batch_instances") == 1 and batched.count > 4000,
			"model: one opaque batch and one incompatible draw render separately"):
		return false
	print("OBS model batch stats: ", stats)
	owner.position.x = 1.0
	var moved_batch := await capture(viewport, "model-batched-moved")
	if not check(moved_batch.count > 3500 and moved_batch.x > batched.x + 20,
			"model: held opaque batch follows owner transform"):
		return false
	owner.hide()
	var hidden_batch := await capture(viewport, "model-batched-hidden")
	if not check(hidden_batch.count == 0, "model: owner hides held opaque batch"):
		return false
	owner.show()
	owner.position.x = 0.0
	material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	var separate := await capture(viewport, "model-transparent")
	stats = player.get_render_stats()
	if not check(stats.get("opaque_model_multimesh_batches") == 0 and
			stats.get("render_server_instances") == 3 and
			stats.get("allocated_render_server_instances") == 3 and
			stats.get("allocated_draw_instances") == 3 and separate.count > 4000,
			"model: transparent material switches to three rendered direct instances"):
		return false
	print("OBS model transparent stats: ", stats)
	owner.position.x = 1.0
	var moved := await capture(viewport, "model-moved")
	if not check(moved.count > 3500 and moved.x > separate.x + 20,
			"model: held direct instances follow owner transform"):
		return false
	player.stop()
	var stopped := await capture(viewport, "model-stopped")
	if not check(stopped.count == 0, "model: Stop removes all pixels"):
		return false
	paused = false
	viewport.queue_free()
	await process_frame
	return true


func exercise_failure() -> bool:
	var viewport := SubViewport.new()
	viewport.size = SIZE
	viewport.process_mode = Node.PROCESS_MODE_ALWAYS
	viewport.world_3d = World3D.new()
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color.BLACK
	viewport.world_3d.environment = environment
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var camera := Camera3D.new()
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = 6.0
	camera.position = Vector3(1.0, 0.0, 10.0)
	viewport.add_child(camera)
	camera.make_current()
	var owner := Node3D.new()
	viewport.add_child(owner)
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	owner.add_child(player)
	var texture_image := Image.create_empty(2, 2, false, Image.FORMAT_RGBA8)
	texture_image.fill(Color.WHITE)
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.albedo_color = Color.YELLOW
	material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	var mesh := BoxMesh.new()
	mesh.material = material
	if not check(player.set_texture_binding(TEXTURE_IDENTITY, ImageTexture.create_from_image(texture_image)) == 0 and
			player.set_mesh_binding("/Engine/BasicShapes/Cube.Cube", mesh) == 0 and
			player.play_package(FileAccess.get_file_as_bytes("res://camera-failure-plane.lfxb")) == 0,
			"failure: mixed ModelDraw and camera-facing plane loads"):
		return false
	player.set_process(false)
	var ids: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://adapter.ids.json"))
	if not check(player.advance(0.0) == 0 and
			player.dispatch_signal(str(ids["rootSignalNodeId"])) == 0 and
			player.advance(0.1) == 0 and
			player.get_render_stats().get("render_server_instances") == 3,
			"failure: root plane, root ModelDraw, and child ModelDraw were submitted"):
		return false
	var scope: Node3D = player.get_child(0)
	var child_scope: Node3D = scope.get_child(0)
	child_scope.position.x = 2.5
	paused = true
	var before := await capture(viewport, "failure-before")
	if not check(before.count > 10, "failure: scope renders before the induced error"):
		return false
	owner.scale = Vector3.ZERO
	await process_frame
	await process_frame
	print("OBS failure: the following singular-transform error is intentionally induced")
	if not check(player.advance(0.1) != 0, "failure: singular camera transform is rejected"):
		return false
	owner.scale = Vector3.ONE
	var failed := await capture(viewport, "failure-hidden")
	if not check(failed.count == 0, "failure: all submitted scope RIDs stay hidden"):
		return false
	if not check(player.advance(0.1) == 0, "failure: scope can recover on a valid update"):
		return false
	var recovered := await capture(viewport, "failure-recovered")
	if not check(recovered.count > 10, "failure: valid update restores scope pixels"):
		return false
	player.stop()
	paused = false
	viewport.queue_free()
	await process_frame
	return true


func capture(viewport: SubViewport, label: String) -> Dictionary:
	await process_frame
	await process_frame
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	var result := {"count": 0, "x": 0.0}
	for y in range(image.get_height()):
		for x in range(image.get_width()):
			var color := image.get_pixel(x, y)
			if maxf(color.r, maxf(color.g, color.b)) > 0.1:
				result.count += 1
				result.x += x
	if result.count > 0:
		result.x /= result.count
	var save_error := image.save_png(artifact_dir.path_join(label + ".png"))
	print("OBS ", label, ": ", result, ", png_error=", save_error)
	return result


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		paused = false
		quit(1)
	return condition
