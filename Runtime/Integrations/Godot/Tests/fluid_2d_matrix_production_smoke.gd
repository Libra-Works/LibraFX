extends SceneTree

const WIDTH := 320
const HEIGHT := 240


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	const EXTENSION := "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) ==
				GDExtensionManager.LOAD_STATUS_OK, "Production extension loaded"):
			return
	if RenderingServer.get_rendering_device() == null:
		check(false, "Production Draw Matrix smoke requires a RenderingDevice")
		return

	var viewport := make_viewport()
	var drawOwner := Node3D.new()
	drawOwner.name = "DrawMatrixProductionOwner"
	viewport.add_child(drawOwner)
	var drawPlayer: Variant = load_player(drawOwner)
	if drawPlayer == null:
		return
	if not check(drawPlayer.play_package(FileAccess.get_file_as_bytes(
			"res://draw-matrix.lfxp")) == 0,
			"Compiler Draw Matrix package admitted: " + drawPlayer.get_gpu_error()):
		return
	drawPlayer.set_process(false)
	if not check(drawPlayer.advance(0.0) == 0 and
			drawPlayer.advance(1.0 / 60.0) == 0,
			"Compiler Draw Matrix rows resolve in production playback: " +
				drawPlayer.get_gpu_error()):
		return
	var authoredDraw: Dictionary = await capture(viewport)
	if not check(authoredDraw.colored > 0 and
			authoredDraw.centroid_x > WIDTH * 0.5 + 20.0,
			"Authored Transform.position is visible at its world-space location: " +
				str(authoredDraw)):
		return
	drawOwner.position = Vector3(20, 0, 0)
	var movedDrawOwner: Dictionary = await capture(viewport)
	if not check(abs(movedDrawOwner.centroid_x - authoredDraw.centroid_x) < 1.0,
			"World-space Draw Matrix is not double-applied with the spatial owner: " +
				str(authoredDraw) + " -> " + str(movedDrawOwner)):
		return
	print("PRODUCTION Draw Matrix centroids: ",
			authoredDraw.centroid_x, " -> ", movedDrawOwner.centroid_x)
	print("PRODUCTION Draw Matrix capture: ",
			save_capture(viewport, "fluid-production-draw-matrix.png"))
	drawPlayer.stop()

	var matrixOwner := Node3D.new()
	matrixOwner.name = "OwnerMatrixProductionOwner"
	matrixOwner.position = Vector3(20, 0, 0)
	viewport.add_child(matrixOwner)
	var ownerMatrixPlayer: Variant = load_player(matrixOwner)
	if ownerMatrixPlayer == null:
		return
	if not check(ownerMatrixPlayer.play_package(FileAccess.get_file_as_bytes(
			"res://owner-matrix.lfxp")) == 0,
			"Compiler OwnerMatrix package admitted: " + ownerMatrixPlayer.get_gpu_error()):
		return
	ownerMatrixPlayer.set_process(false)
	var hasOwnerMatrix := false
	for port in ownerMatrixPlayer.get_game_io_ports():
		if port.get("kind_name", "") == "owner_matrix" and int(port.get("width", 0)) == 16:
			hasOwnerMatrix = true
			break
	if not check(hasOwnerMatrix, "Production package exposes its 16-lane OwnerMatrix port"):
		return

	var identity := PackedFloat64Array([
		1.0, 0.0, 0.0, 0.0,
		0.0, 1.0, 0.0, 0.0,
		0.0, 0.0, 1.0, 0.0,
		0.0, 0.0, 0.0, 1.0
	])
	if not check(ownerMatrixPlayer.set_owner_matrix(identity) == 0 and
			ownerMatrixPlayer.advance(0.0) == 0 and
			ownerMatrixPlayer.advance(1.0 / 60.0) == 0,
			"Production OwnerMatrix identity update resolves: " +
				ownerMatrixPlayer.get_gpu_error()):
		return
	var identityPixels: Dictionary = await capture(viewport)
	if not check(identityPixels.colored > 0 and
			abs(identityPixels.centroid_x - WIDTH * 0.5) < 5.0,
			"World-space identity does not inherit the owner translation twice: " +
				str(identityPixels)):
		return

	var translated := PackedFloat64Array([
		1.0, 0.0, 0.0, 20.0,
		0.0, 1.0, 0.0, 0.0,
		0.0, 0.0, 1.0, 0.0,
		0.0, 0.0, 0.0, 1.0
	])
	if not check(ownerMatrixPlayer.set_owner_matrix(translated) == 0 and
			ownerMatrixPlayer.advance(1.0 / 60.0) == 0,
			"Production OwnerMatrix translation update resolves: " +
				ownerMatrixPlayer.get_gpu_error()):
		return
	var translatedPixels: Dictionary = await capture(viewport)
	if not check(translatedPixels.colored > 0 and
			translatedPixels.centroid_x > identityPixels.centroid_x + 20.0 and
			translatedPixels.centroid_x < identityPixels.centroid_x + 40.0,
			"OwnerMatrix translation moves the world-space plane once: " +
				str(identityPixels) + " -> " + str(translatedPixels)):
		return

	var rotated := PackedFloat64Array([
		0.0, -1.0, 0.0, 20.0,
		1.0, 0.0, 0.0, 0.0,
		0.0, 0.0, 1.0, 0.0,
		0.0, 0.0, 0.0, 1.0
	])
	if not check(ownerMatrixPlayer.set_owner_matrix(rotated) == 0 and
			ownerMatrixPlayer.advance(1.0 / 60.0) == 0,
			"Production OwnerMatrix rotation update resolves: " +
				ownerMatrixPlayer.get_gpu_error()):
		return
	var rotatedPixels: Dictionary = await capture(viewport)
	if not check(rotatedPixels.colored > 0 and
			rotatedPixels.centroid_x > translatedPixels.centroid_x + 20.0,
			"OwnerMatrix rotation updates the live 3D plane: " +
				str(translatedPixels) + " -> " + str(rotatedPixels)):
		return
	print("PRODUCTION OwnerMatrix centroids (identity/translation/rotation): ",
			identityPixels.centroid_x, " -> ", translatedPixels.centroid_x,
			" -> ", rotatedPixels.centroid_x)
	print("PRODUCTION OwnerMatrix captures: ",
			save_capture(viewport, "fluid-production-owner-identity.png"), " / ",
			save_capture(viewport, "fluid-production-owner-translated.png"), " / ",
			save_capture(viewport, "fluid-production-owner-rotated.png"))

	ownerMatrixPlayer.stop()
	viewport.queue_free()
	await process_frame
	print("PASS: Godot production Fluid2D Draw Matrix and OwnerMatrix world transforms")
	quit(0)


func make_viewport() -> SubViewport:
	var viewport := SubViewport.new()
	viewport.size = Vector2i(WIDTH, HEIGHT)
	viewport.transparent_bg = true
	viewport.process_mode = Node.PROCESS_MODE_ALWAYS
	viewport.world_3d = World3D.new()
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var camera := Camera3D.new()
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = 160.0
	camera.position = Vector3(0, 0, 100)
	camera.current = true
	viewport.add_child(camera)
	return viewport


func load_player(parent: Node) -> Variant:
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	if not check(player != null, "LibraEffectPlayer class is registered"):
		return null
	parent.add_child(player)
	return player


func capture(viewport: SubViewport) -> Dictionary:
	await process_frame
	await process_frame
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	var result := {"colored": 0, "centroid_x": 0.0}
	for y in range(image.get_height()):
		for x in range(image.get_width()):
			var pixel := image.get_pixel(x, y)
			if pixel.r > 0.08 and pixel.r > pixel.g * 1.4 and pixel.g > pixel.b:
				result.colored += 1
				result.centroid_x += x
	if result.colored > 0:
		result.centroid_x /= result.colored
	return result


func save_capture(viewport: SubViewport, file_name: String) -> String:
	var directory := OS.get_user_data_dir().path_join("captures")
	var error := DirAccess.make_dir_recursive_absolute(directory)
	if error != OK and error != ERR_ALREADY_EXISTS:
		return "capture directory error: " + str(error)
	var path := directory.path_join(file_name)
	error = viewport.get_texture().get_image().save_png(path)
	return path if error == OK else "capture save error: " + str(error)


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
