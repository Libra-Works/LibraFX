extends SceneTree

const SIZE := Vector2i(320, 240)
var artifact_dir := ""


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	artifact_dir = OS.get_environment("LIBERAFX_PIXEL_ARTIFACTS")
	if not check(not artifact_dir.is_empty() and DisplayServer.get_name() != "headless",
			"Real graphics and an isolated artifact directory are required"):
		return
	var extension := "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(extension):
		if not check(GDExtensionManager.load_extension(extension) == GDExtensionManager.LOAD_STATUS_OK,
				"Extension loaded"):
			return
	print("OBS graphics: ", RenderingServer.get_video_adapter_name())
	var viewport := SubViewport.new()
	viewport.size = SIZE
	viewport.process_mode = Node.PROCESS_MODE_ALWAYS
	viewport.world_3d = World3D.new()
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color.BLACK
	viewport.world_3d.environment = environment
	root.add_child(viewport)
	var camera := Camera3D.new()
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = 6.0
	camera.position = Vector3(1, 0, 10)
	viewport.add_child(camera)
	camera.make_current()
	var owner := Node3D.new()
	viewport.add_child(owner)
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	owner.add_child(player)
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.cull_mode = BaseMaterial3D.CULL_BACK
	material.render_priority = 1 # Explicitly disqualify the baseline from batching.
	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = PackedVector3Array([
		Vector3(-0.5, -0.5, 0), Vector3(-0.5, 0.5, 0), Vector3(0.5, -0.5, 0)])
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	mesh.surface_set_material(0, material)
	var ids: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://adapter.ids.json"))
	if not check(player.set_mesh_binding("/Engine/BasicShapes/Cube.Cube", mesh) == 0 and
			player.play_package(FileAccess.get_file_as_bytes("res://adapter.lfxp")) == 0,
			"One-sided ModelDraw package loads"):
		return
	player.set_process(false)
	if not check(player.advance(0.01) == 0 and
			player.dispatch_signal(str(ids["rootSignalNodeId"])) == 0 and
			player.advance(0.25) == 0, "Root and child draws advance"):
		return
	var scope: Node3D = player.get_child(0)
	if not check(scope.get_child_count() == 1, "Child scope exists"):
		return
	var child_scope: Node3D = scope.get_child(0)
	child_scope.position.x = 2.0
	child_scope.scale.x = -1.0
	paused = true
	var negative_direct := await capture(viewport, "negative-direct")
	var stats: Dictionary = player.get_render_stats()
	if not check(stats.get("opaque_model_multimesh_batches") == 0 and
			stats.get("allocated_draw_instances") == 2,
			"Negative-scale baseline uses two direct instance RIDs"):
		return
	var negative_regions := count_regions(negative_direct)
	print("OBS direct negative pixels: ", negative_regions)
	if not check(negative_regions.left > 100 and negative_regions.right > 100,
			"Both one-sided triangles are visible in the separate-instance baseline"):
		return
	material.render_priority = 0
	var negative_candidate := await capture(viewport, "negative-candidate")
	stats = player.get_render_stats()
	var negative_difference := difference(negative_direct, negative_candidate)
	print("OBS negative candidate stats: ", stats, " differing_pixels=", negative_difference)
	if not check(stats.get("opaque_model_multimesh_batches") == 0 and
			stats.get("allocated_draw_instances") == 2 and negative_difference == 0,
			"Mirrored member stays direct and exactly matches the direct culling baseline"):
		return
	child_scope.scale.x = 1.0
	material.render_priority = 1
	var positive_direct := await capture(viewport, "positive-direct")
	material.render_priority = 0
	var positive_batched := await capture(viewport, "positive-batched")
	stats = player.get_render_stats()
	var positive_difference := difference(positive_direct, positive_batched)
	var positive_regions := count_regions(positive_direct)
	print("OBS positive candidate stats: ", stats, " differing_pixels=", positive_difference,
			" direct_pixels=", positive_regions)
	if not check(stats.get("opaque_model_multimesh_batches") == 1 and
			stats.get("opaque_model_multimesh_members") == 2 and positive_difference == 0 and
			positive_regions.left > 100 and positive_regions.right > 100,
			"Positive determinants still batch and match the direct baseline"):
		return
	player.stop()
	paused = false
	viewport.queue_free()
	await process_frame
	print("PASS: one-sided direct and batched determinant parity")
	quit(0)


func capture(viewport: SubViewport, label: String) -> Image:
	await process_frame
	await process_frame
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	var error := image.save_png(artifact_dir.path_join(label + ".png"))
	print("OBS ", label, ": ", count_regions(image), ", png_error=", error)
	return image


func count_regions(image: Image) -> Dictionary:
	var counts := {"left": 0, "right": 0}
	for y in range(image.get_height()):
		for x in range(image.get_width()):
			var color := image.get_pixel(x, y)
			if maxf(color.r, maxf(color.g, color.b)) > 0.1:
				if x < image.get_width() / 2:
					counts.left += 1
				else:
					counts.right += 1
	return counts


func difference(a: Image, b: Image) -> int:
	var differing := 0
	for y in range(a.get_height()):
		for x in range(a.get_width()):
			if a.get_pixel(x, y) != b.get_pixel(x, y):
				differing += 1
	return differing


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		paused = false
		quit(1)
	return condition
