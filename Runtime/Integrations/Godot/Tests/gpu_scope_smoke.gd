extends SceneTree


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	const EXTENSION = "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK,
				"Extension library loaded"):
			return
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	if not check(player != null, "Extension class loaded"):
		return
	root.add_child(player)
	var package := FileAccess.get_file_as_bytes("res://spawnable.godot-gpu-matrix.lfxp")
	if not check(player.play_package(package, 5.0) == 0, "GPU package admitted: " + player.get_gpu_error()):
		return
	player.set_process(false)
	if not check(player.advance(0.0) == 0 and player.advance(0.1) == 0,
			"Child GPU advance: " + player.get_gpu_error()):
		return
	var gpu_root := player.get_node_or_null("LibraFxGpuRoot") as Node3D
	if not check(gpu_root != null and gpu_root.get_child_count() == 1, "One child emitter attached"):
		return
	var draw := gpu_root.get_child(0) as MultiMeshInstance3D
	if not check(draw != null and draw.multimesh.visible_instance_count > 0, "Child dispatch produced particles"):
		return
	var center := draw.multimesh.get_instance_transform(0).origin
	if not check(absf(center.x - 250.0) < 10.0 and gpu_root.visible, "Draw Matrix placed the child particles"):
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
	if not check(middle.r > 0.1, "GPU billboard rendered at the Matrix-transformed camera center: %s" % middle):
		return
	if not check(player.advance(2.0) == 0 and not draw.visible, "Child retirement hides its GPU draw immediately"):
		return
	await process_frame
	await process_frame
	if not check(not is_instance_valid(draw), "Retirement frees the draw node"):
		return
	player.stop()
	var flow_package := FileAccess.get_file_as_bytes("res://spawnable.godot-flow-map.lfxp")
	if not check(player.play_package(flow_package) != 0, "FlowMap requires an explicit Godot texture binding"):
		return
	var image := Image.create_empty(2, 2, false, Image.FORMAT_RGBAF)
	image.set_pixel(0, 0, Color(0.25, 0.25, 0, 1))
	image.set_pixel(1, 0, Color(0.75, 0.25, 0, 1))
	image.set_pixel(0, 1, Color(0.25, 0.75, 0, 1))
	image.set_pixel(1, 1, Color(0.75, 0.75, 0, 1))
	player.set_texture_binding("/Game/FX/T_GodotFlow.T_GodotFlow", ImageTexture.create_from_image(image))
	await process_frame
	if not check(player.play_package(flow_package) == 0, "FlowMap package admitted: " + player.get_gpu_error()):
		return
	player.set_process(false)
	if not check(player.advance(0.1) == 0, "FlowMap dispatch: " + player.get_gpu_error()):
		return
	gpu_root = player.get_node_or_null("LibraFxGpuRoot") as Node3D
	if not check(gpu_root != null and gpu_root.get_child_count() == 1, "FlowMap root emitter attached"):
		return
	draw = gpu_root.get_child(0) as MultiMeshInstance3D
	if not check(draw != null and draw.multimesh.visible_instance_count > 0, "FlowMap produced particles"):
		return
	var flow_center := draw.multimesh.get_instance_transform(0).origin
	var flow_size := draw.multimesh.get_instance_custom_data(0)
	if not check(is_zero_approx(flow_center.x) and is_equal_approx(flow_center.z, 0.5)
			and is_equal_approx(flow_size.r, 50.0) and is_equal_approx(flow_size.g, -50.0),
			"FlowMap world mapping and clamped UV sampling: center=%s size=%s" % [flow_center, flow_size]):
		return
	print("PASS: Godot child GPU dispatch, draw Matrix, retirement and FlowMap")
	quit(0)


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
