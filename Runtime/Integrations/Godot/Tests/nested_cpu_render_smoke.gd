extends SceneTree


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	const EXTENSION := "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) ==
				GDExtensionManager.LOAD_STATUS_OK, "Production extension loads"):
			return
	var fixture := OS.get_environment("LIBERAFX_HIERARCHY_FIXTURE")
	var bytes := FileAccess.get_file_as_bytes(fixture)
	if not check(not bytes.is_empty(), "Compiler nested-cpu-only-retention fixture exists"):
		return
	var viewport := SubViewport.new()
	viewport.size = Vector2i(256, 256)
	viewport.transparent_bg = true
	viewport.world_3d = World3D.new()
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var camera := Camera3D.new()
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = 128.0
	camera.position.z = 100
	camera.current = true
	viewport.add_child(camera)
	var owner := Node3D.new()
	viewport.add_child(owner)
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	owner.add_child(player)
	for restart in range(2):
		if not check(player.play_package(bytes) == 0, "Nested CPU package starts"):
			return
		player.set_process(false)
		if not check(player.advance(0.0) == 0, "Nested start advances"):
			return
		for tick in range(30):
			if not check(player.advance(0.1) == 0, "Scoped CPU advances"):
				return
		var stats: Dictionary = player.get_render_stats()
		if not check(player.is_playing() and stats.get("cpu_multimesh_batches", 0) == 1 and
				stats.get("allocated_cpu_particle_instances", 0) == 1 and
				stats.get("cpu_particle_records_transferred", 0) > 0,
				"Child CPU rendering survives parent Life=1 and child Life=2: " + str(stats)):
			return
		if not check(not has_render_nodes(owner),
				"Nested CPU rendering uses RIDs rather than scene renderer nodes"):
			return
		var image: Image = await capture(viewport)
		var output := OS.get_environment("LIBERAFX_HIERARCHY_OUTPUT")
		if not output.is_empty():
			image.save_png(output.path_join("godot-nested-cpu-%d.png" % restart))
		if not check(colored_pixels(image) > 0, "Retained child CPU produces real pixels"):
			return
		print("OBS: retained nested CPU rendering = ", stats)
		for tick in range(15):
			if not player.is_playing():
				break
			if not check(player.advance(0.1) == 0, "CPU particles finish naturally"):
				return
		if not check(not player.is_playing() and player.get_render_stats().is_empty(),
				"Natural particle death retires both nested scopes and their render state"):
			return
		image = await capture(viewport)
		if not check(colored_pixels(image) == 0, "Retired CPU RIDs leave no pixels"):
			return
		if not check(player.pop_scoped_cpu_particle_event(
				"40000000-0000-4000-8000-000000000005", -1).is_empty(),
				"Invalid event scope is rejected"):
			return
	player.stop()
	viewport.queue_free()
	await process_frame
	print("PASS: Godot compiler-produced nested CPU rendering, lifetime retention, cleanup and restart")
	quit(0)


func capture(viewport: SubViewport) -> Image:
	await process_frame
	await process_frame
	await RenderingServer.frame_post_draw
	return viewport.get_texture().get_image()


func colored_pixels(image: Image) -> int:
	var count := 0
	for y in range(image.get_height()):
		for x in range(image.get_width()):
			var color := image.get_pixel(x, y)
			if color.a > 0.1 and maxf(color.r, maxf(color.g, color.b)) > 0.1:
				count += 1
	return count


func has_render_nodes(parent: Node) -> bool:
	for child in parent.get_children():
		if child is MeshInstance3D or child is MultiMeshInstance3D or has_render_nodes(child):
			return true
	return false


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
