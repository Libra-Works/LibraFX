extends SceneTree


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	const EXTENSION = "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK,
				"Extension loaded"):
			return
	var player: Variant = ClassDB.instantiate("LibraFxDebugPlayer")
	root.add_child(player)
	if not check(player.load_package(FileAccess.get_file_as_bytes("res://spawnable.godot-gpu.lfxp")) == 0,
			"Root GPU debug package loads: " + player.get_gpu_error()):
		return
	if not check(player.advance(0.0) == 0, "Root GPU initial evaluation submitted"):
		return
	var status := await completed(player)
	if not check(status["result"] == 0 and status["gpu_emitters"] == 1, "Root GPU scope published: %s" % status):
		return
	if not check(player.advance(0.1) == 0, "Root GPU step submitted"):
		return
	status = await completed(player)
	var gpu_root := player.get_node_or_null("LibraFxGpuRoot") as Node3D
	var draw: MultiMeshInstance3D
	if gpu_root != null and gpu_root.get_child_count() == 1:
		draw = gpu_root.get_child(0) as MultiMeshInstance3D
	if not check(status["result"] == 0 and draw != null
			and draw.multimesh.visible_instance_count > 0, "Root GPU debug particles published"):
		return
	player.close()
	if not check(player.poll().get("closed", false), "Root GPU debug closes"):
		return
	await process_frame
	await process_frame
	if not check(player.get_child_count() == 0, "Deferred root GPU scene cleanup"):
		return
	if not check(player.load_package(FileAccess.get_file_as_bytes("res://spawnable.godot-gpu-matrix.lfxp")) == 0,
			"Child GPU debug package loads: " + player.get_gpu_error()):
		return
	if not check(player.advance(0.0) == 0, "Child GPU initial evaluation submitted"):
		return
	status = await completed(player)
	if not check(status["result"] == 0 and status["gpu_emitters"] == 1, "Child GPU scope published"):
		return
	var spawn := find_spawn(player)
	if not check(spawn.get("has_event", false), "GPU rendering preserves child Spawn event"):
		return
	gpu_root = player.get_node_or_null("LibraFxGpuRoot") as Node3D
	if not check(gpu_root != null and gpu_root.get_child_count() == 1, "Child GPU scene attached"):
		return
	draw = gpu_root.get_child(0) as MultiMeshInstance3D
	if not check(player.advance(0.1) == 0, "Child GPU step submitted"):
		return
	status = await completed(player)
	if not check(status["result"] == 0 and draw != null
			and draw.multimesh.visible_instance_count > 0, "Completed worker publishes child particles: %s" % status):
		return
	var center := draw.multimesh.get_instance_transform(0).origin
	if not check(absf(center.x - 250.0) < 10.0, "Authored Draw Matrix applies in debug playback"):
		return
	var count := draw.multimesh.visible_instance_count
	for _poll in range(4):
		player.poll()
	if not check(draw.multimesh.visible_instance_count == count
			and draw.multimesh.get_instance_transform(0).origin == center,
			"Repeated idle polls neither simulate nor republish GPU particles"):
		return
	var camera := Camera3D.new()
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = 300.0
	camera.position = Vector3(250, 0, 100)
	camera.environment = Environment.new()
	camera.environment.background_mode = Environment.BG_COLOR
	camera.environment.background_color = Color.BLACK
	root.add_child(camera)
	camera.make_current()
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	var pixels := root.get_texture().get_image()
	var middle := pixels.get_pixel(pixels.get_width() / 2, pixels.get_height() / 2)
	if not check(middle.r > 0.1, "GPU debug billboard produced center pixels: %s" % middle):
		return
	if not check(player.advance(0.1) == 0
			and draw.multimesh.visible_instance_count == count
			and draw.multimesh.get_instance_transform(0).origin == center,
			"Pending work preserves the last published GPU frame"):
		return
	status = await completed(player)
	if not check(status["result"] == 0, "Second child GPU step completed"):
		return
	if not check(player.advance(2.0) == 0, "Child retirement submitted"):
		return
	status = await completed(player)
	if not check(status["result"] == 0 and status["gpu_emitters"] == 0 and not draw.visible,
			"Completed child retirement hides and retires GPU emitter"):
		return
	await process_frame
	await process_frame
	if not check(not is_instance_valid(draw), "Retirement frees child draw node"):
		return
	player.close()
	if not check(player.poll().get("closed", false), "Child GPU debug cleanup"):
		return
	print("PASS: Godot debug root/child GPU scene, pixels, acquire boundary and retirement")
	quit(0)


func find_spawn(player: Node) -> Dictionary:
	for _index in range(128):
		var event: Dictionary = player.next_package_event()
		if not event.get("has_event", false) or event["kind"] == 1:
			return event
	return {}


func completed(player: Node) -> Dictionary:
	var deadline := Time.get_ticks_msec() + 10000
	var status: Dictionary = player.poll()
	while status["busy"] and Time.get_ticks_msec() < deadline:
		await process_frame
		status = player.poll()
	return status


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
