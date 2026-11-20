extends SceneTree

func _initialize() -> void:
	run.call_deferred()

func require(condition: bool, message: String) -> bool:
	if not condition:
		push_error(message)
		quit(1)
	return condition

func capture(viewport: SubViewport) -> Image:
	await process_frame
	await RenderingServer.frame_post_draw
	return viewport.get_texture().get_image()

func colored(image: Image) -> int:
	var count := 0
	for y in range(image.get_height()):
		for x in range(image.get_width()):
			var c := image.get_pixel(x, y)
			if max(c.r, max(c.g, c.b)) > 0.02:
				count += 1
	return count

func run() -> void:
	if not GDExtensionManager.is_extension_loaded("res://liberafx.gdextension"):
		if not require(GDExtensionManager.load_extension("res://liberafx.gdextension") == GDExtensionManager.LOAD_STATUS_OK, "Cannot load procedural extension"):
			return
	if not require(RenderingServer.get_rendering_device() != null, "A real RenderingDevice is required"):
		return
	RenderingServer.set_default_clear_color(Color.BLACK)
	var output := "res://proof"
	if OS.get_cmdline_user_args().size() > 0:
		output = OS.get_cmdline_user_args()[0]
	DirAccess.make_dir_recursive_absolute(output)
	var viewport := SubViewport.new()
	viewport.size = Vector2i(320, 240)
	viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var camera := Camera3D.new()
	viewport.add_child(camera)
	camera.position = Vector3(4, 3, 6)
	camera.look_at(Vector3(0, 0.8, 0))
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = 5.0
	camera.current = true
	for sample in ["Slash", "Shockwave", "Beam"]:
		var owner := Node3D.new()
		viewport.add_child(owner)
		var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
		owner.add_child(player)
		if not require(player.play_package(FileAccess.get_file_as_bytes("res://" + sample + ".lfxp")) == 0, "Cannot play " + sample + ": " + player.get_gpu_error()):
			return
		player.set_process(false)
		if not require(player.advance(0.1) == 0, "Cannot advance " + sample):
			return
		var first: Image = await capture(viewport)
		if not require(colored(first) > 20, sample + " produced no visible pixels"):
			return
		first.save_png(output + "/" + sample + "-first.png")
		if not require(player.advance(0.5) == 0, "Cannot animate " + sample):
			return
		var second: Image = await capture(viewport)
		if not require(first.get_data() != second.get_data(), sample + " did not animate"):
			return
		second.save_png(output + "/" + sample + "-animated.png")
		player.stop()
		var stopped: Image = await capture(viewport)
		if not require(colored(stopped) == 0, "Stopped procedural output remained visible"):
			return
		owner.queue_free()
		await process_frame
	print("PASS: compiler-produced procedural samples; Vulkan compute, generated Model Draw, animation and stop disappearance")
	quit(0)
