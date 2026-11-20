extends SceneTree

const WIDTH := 320
const HEIGHT := 240


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	const EXTENSION := "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK,
				"Godot extension loaded"):
			return
	var rendering_device := RenderingServer.get_rendering_device()
	if rendering_device == null:
		var unsupported: Variant = load_player()
		if unsupported == null:
			return
		var rejected: int = unsupported.play_package(
			FileAccess.get_file_as_bytes("res://fluid-only.lfxp"))
		if not check(rejected != 0 and
				unsupported.get_gpu_error().contains("RenderingDevice"),
				"Compatibility/null rendering explicitly rejects GPU fluid: " + unsupported.get_gpu_error()):
			return
		print("PASS: Godot Fluid2D rejects Compatibility and null RenderingDevice backends")
		quit(0)
		return

	var viewport := make_viewport()
	var owner := Node3D.new()
	owner.name = "FluidTestOwner"
	viewport.add_child(owner)
	var player: Variant = load_player(owner)
	if player == null:
		return
	if not check(player.play_package(FileAccess.get_file_as_bytes("res://fluid-only.lfxp")) == 0,
			"Fluid-only package admitted: " + player.get_gpu_error()):
		return
	var fluid_root := player.get_node_or_null("LibraFxFluid2DRoot") as Node3D
	if not check(fluid_root != null and fluid_root.get_child_count() == 0,
			"Fluid output uses a 3D scope and RenderingServer RIDs, not per-fluid scene nodes"):
		return
	player.set_process(false)
	if not check(player.advance(0.0) == 0, "Zero delta does not inject or advance"):
		return
	var blank: Dictionary = await capture(viewport)
	if not check(blank.colored == 0, "Zero delta leaves the initially cleared fluid output blank"):
		return
	if not check(player.advance(1.0 / 60.0) == 0,
			"Fluid-only Vulkan compute advances: " + player.get_gpu_error()):
		return
	var firstSmoke: Dictionary = await capture(viewport)
	print("Fluid-only Vulkan pixels: ", firstSmoke.colored,
			" translucent: ", firstSmoke.translucent,
			" capture: ", save_capture(viewport, "fluid-first-smoke.png"))
	if not check(player.advance(0.0) == 0,
			"Render-thread compute reports no asynchronous error: " + player.get_gpu_error()):
		return
	if not check(firstSmoke.colored > 0 and firstSmoke.translucent > 0,
			"GPU dye is visible with premultiplied translucent alpha: " + str(firstSmoke)):
		return
	var baselineX: float = firstSmoke.centroid_x
	owner.position = Vector3(20, 0, 10)
	var translatedOwner: Dictionary = await capture(viewport)
	if not check(translatedOwner.colored > 0 and translatedOwner.centroid_x > baselineX + 15.0,
			"Fluid plane follows its full 3D spatial owner translation: " + str(translatedOwner)):
		return
	print("3D translated capture: ", save_capture(viewport, "fluid-3d-translated.png"))
	owner.position = Vector3.ZERO
	owner.scale = Vector3(1.5, 1.0, 1.0)
	var scaledOwner: Dictionary = await capture(viewport)
	if not check(scaledOwner.bounds_width > firstSmoke.bounds_width * 1.25,
			"Fluid plane follows nonuniform 3D owner scale: " + str(scaledOwner)):
		return
	print("3D scaled capture: ", save_capture(viewport, "fluid-3d-scaled.png"))
	owner.scale = Vector3.ONE
	owner.rotation.x = PI / 3.0
	var tiltedOwner: Dictionary = await capture(viewport)
	if not check(tiltedOwner.colored > 0 and
			tiltedOwner.bounds_height < firstSmoke.bounds_height * 0.75,
			"Fluid plane remains in the full 3D local XY plane under out-of-plane rotation: " +
				str(tiltedOwner)):
		return
	print("3D tilted capture: ", save_capture(viewport, "fluid-3d-tilted.png"))
	owner.rotation = Vector3.ZERO
	owner.visible = false
	var hiddenOwner: Dictionary = await capture(viewport)
	if not check(hiddenOwner.colored == 0,
			"Fluid RenderingServer output honors hidden spatial owners: " + str(hiddenOwner)):
		return
	owner.visible = true
	var restoredOwner: Dictionary = await capture(viewport)
	if not check(restoredOwner.colored > 0,
			"Fluid RenderingServer output returns when the spatial owner is visible"):
		return
	print("3D transform pixels: owner translate x=", translatedOwner.centroid_x,
			", scaled bounds=", scaledOwner.bounds_width, "x", scaledOwner.bounds_height,
			", tilted bounds=", tiltedOwner.bounds_width, "x", tiltedOwner.bounds_height,
			", hidden=", hiddenOwner.colored, ", restored=", restoredOwner.colored)
	var heldMatrixTranslation := PackedFloat64Array([
		1.0, 0.0, 0.0, 20.0,
		0.0, 1.0, 0.0, 0.0,
		0.0, 0.0, 1.0, 10.0,
		0.0, 0.0, 0.0, 1.0
	])
	if not check(player._test_set_fluid_draw_matrix(heldMatrixTranslation) == 0,
			"Test Draw Matrix accepts a finite affine row-major 4x4 transform"):
		return
	var heldMatrixPixels: Dictionary = await capture(viewport)
	if not check(heldMatrixPixels.colored > 0 and
			heldMatrixPixels.centroid_x > restoredOwner.centroid_x + 15.0,
			"Held GPU output follows the 4x4 Draw Matrix translation: " + str(heldMatrixPixels)):
		return
	print("Held Draw Matrix capture: ",
			save_capture(viewport, "fluid-3d-held-matrix-translation.png"))
	var tiltedMatrix := PackedFloat64Array([
		1.0, 0.0, 0.0, 0.0,
		0.0, 0.5, -0.8660254037844386, 0.0,
		0.0, 0.8660254037844386, 0.5, 0.0,
		0.0, 0.0, 0.0, 1.0
	])
	if not check(player._test_set_fluid_draw_matrix(tiltedMatrix) == 0,
			"Test Draw Matrix accepts a full out-of-plane 3D rotation"):
		return
	var heldMatrixTilted: Dictionary = await capture(viewport)
	if not check(heldMatrixTilted.colored > 0 and
			heldMatrixTilted.bounds_height < firstSmoke.bounds_height * 0.75,
			"Held output follows the Draw Matrix's 3D basis, not an XY projection: " +
				str(heldMatrixTilted)):
		return
	if not check(player._test_set_fluid_draw_matrix(PackedFloat64Array([
			1.0, 0.0, 0.0, 0.0,
			0.0, 1.0, 0.0, 0.0,
			0.0, 0.0, 1.0, 0.0,
			0.0, 0.0, 0.0, 1.0
		])) == 0, "Held test Draw Matrix restores identity"):
		return
	var odd39Fields := read_fluid_fields(player, "odd 39-iteration field readback")
	var odd39Metrics := field_metrics(odd39Fields)
	odd39Metrics["normal_boundary_max"] = velocity_normal_boundary_max(odd39Fields)
	if not check(odd39Metrics.finite and odd39Metrics.max_density > 0 and
			odd39Metrics.max_speed > 0 and odd39Metrics.max_pressure > 0,
			"Odd-39 GPU fields are finite and injected: " + str(odd39Metrics)):
		return
	if not check(odd39Metrics.normal_boundary_max <= 0.00001,
			"Published odd-39 velocity has zero normal boundary values"):
		return
	print("Odd39 field metrics: ", odd39Metrics)

	var even: Variant = load_player(viewport)
	if even == null:
		return
	if not check(even.play_package(FileAccess.get_file_as_bytes("res://fluid-only.lfxp")) == 0,
			"Even-iteration comparison package admitted: " + even.get_gpu_error()):
		return
	even.set_process(false)
	if not check(even._test_set_fluid_pressure_iterations(40) == 0,
			"Even-40 uniform plan rebuilt: " + even.get_gpu_error()):
		return
	if not check(even.advance(0.0) == 0,
			"Even-40 zero delta dispatches no work"):
		return
	var evenBlankFields := read_fluid_fields(even, "even-40 initial field readback")
	if not check(all_fields_zero(evenBlankFields),
			"Even-40 field buffers start blank before positive delta"):
		return
	if not check(even.advance(1.0 / 60.0) == 0,
			"Even-40 Vulkan pressure dispatch advances: " + even.get_gpu_error()):
		return
	await RenderingServer.frame_post_draw
	var evenFields := read_fluid_fields(even, "even-40 field readback")
	var evenMetrics := field_metrics(evenFields)
	evenMetrics["normal_boundary_max"] = velocity_normal_boundary_max(evenFields)
	if not check(evenMetrics.finite and evenMetrics.max_density > 0 and
			evenMetrics.max_speed > 0 and evenMetrics.max_pressure > 0,
			"Even-40 GPU fields are finite and injected: " + str(evenMetrics)):
		return
	if not check(evenMetrics.normal_boundary_max <= 0.00001,
			"Published even-40 velocity has zero normal boundary values"):
		return
	print("Even40 field metrics: ", evenMetrics)
	var parityDifference := {
		"max_speed": abs(odd39Metrics.max_speed - evenMetrics.max_speed),
		"max_density": abs(odd39Metrics.max_density - evenMetrics.max_density),
		"max_pressure": abs(odd39Metrics.max_pressure - evenMetrics.max_pressure)
	}
	if not check(parityDifference.max_speed < 0.0001 and
			parityDifference.max_density < 0.0001 and
			parityDifference.max_pressure < 0.0001,
			"Odd-39 and even-40 pressure paths remain numerically consistent: " +
				str(parityDifference)):
		return
	print("Odd39/even40 absolute metric differences: ", parityDifference)
	even.stop()
	await capture(viewport)

	for _step in 4:
		if not check(player.advance(0.1) == 0,
				"Positive logical-Y smoke advances: " + player.get_gpu_error()):
			return
	var rising: Dictionary = await capture(viewport)
	print("Rising plume Vulkan pixels: ", rising.colored,
			" centroid-y: ", rising.centroid_y)
	if not check(rising.colored > 0 and rising.centroid_y < firstSmoke.centroid_y - 1.0,
			"Positive local-Y velocity and buoyancy move the plume upward on screen: " + str(rising)):
		return
	owner.rotation.z = PI / 2.0
	var rotatedBeforeAdvance: Dictionary = await capture(viewport)
	for _step in 4:
		if not check(player.advance(0.1) == 0,
				"Rotated local-XY plume advances: " + player.get_gpu_error()):
			return
	var rotatedAfterAdvance: Dictionary = await capture(viewport)
	owner.rotation = Vector3.ZERO
	if not check(rotatedBeforeAdvance.colored > 0 and
			rotatedAfterAdvance.centroid_x < rotatedBeforeAdvance.centroid_x - 1.0,
			"Positive local-Y plume motion follows the rotated 3D XY plane: " +
				str(rotatedBeforeAdvance) + " -> " + str(rotatedAfterAdvance)):
		return
	print("Rotated 3D plume centroid x: ", rotatedBeforeAdvance.centroid_x, " -> ",
			rotatedAfterAdvance.centroid_x)
	print("Rotated 3D captures: ",
			save_capture(viewport, "fluid-3d-rotated-before.png"), " / ",
			save_capture(viewport, "fluid-3d-rotated-after.png"))
	var beforeDisableFields := read_fluid_fields(player, "pre-disable field readback")
	var beforeDisableMetrics := field_metrics(beforeDisableFields)
	if not check(player._test_disable_fluid_source_and_zero_forces() == 0,
			"Source disabled with zero velocity and buoyancy: " + player.get_gpu_error()):
		return
	for _step in 10:
		if not check(player.advance(0.05) == 0,
				"Source-disabled decay advances: " + player.get_gpu_error()):
			return
		await process_frame
	var noSourceFields := read_fluid_fields(player, "source-disabled field readback")
	var noSourceMetrics := field_metrics(noSourceFields)
	noSourceMetrics["normal_boundary_max"] = velocity_normal_boundary_max(noSourceFields)
	if not check(noSourceMetrics.finite and
			noSourceMetrics.max_density < beforeDisableMetrics.max_density * 0.99 and
			noSourceMetrics.sum_density < beforeDisableMetrics.sum_density * 0.99,
			"Disabled source adds no dye and existing density decays: " +
				str(beforeDisableMetrics) + " -> " + str(noSourceMetrics)):
		return
	if not check(noSourceMetrics.max_speed <= 0.00001 and
			noSourceMetrics.normal_boundary_max <= 0.00001,
			"Zero-force source-off velocity remains zero with closed boundaries: " +
				str(noSourceMetrics)):
		return
	print("Source-off density/speed: ", beforeDisableMetrics.max_density, " -> ",
			noSourceMetrics.max_density, " / total ",
			beforeDisableMetrics.sum_density, " -> ", noSourceMetrics.sum_density,
			" / max speed ", noSourceMetrics.max_speed,
			" / boundary normal max ", noSourceMetrics.normal_boundary_max)

	if not check(player.advance(0.0) == 0, "Paused zero-delta update succeeds"):
		return
	var held: Dictionary = await capture(viewport)
	if not check(held.colored > 0, "Pausing preserves the last GPU output"):
		return
	player.stop()
	var cleared := await capture(viewport)
	if not check(cleared.colored == 0, "Stop removes the fluid 3D instance RID and clears its pixels"):
		return
	if not check(player.play_package(FileAccess.get_file_as_bytes("res://fluid-only.lfxp")) == 0,
			"Root playback restarts after Stop: " + player.get_gpu_error()):
		return
	player.set_process(false)
	if not check(player.advance(0.0) == 0, "Restart begins with a zero-delta blank"):
		return
	var restartBlank: Dictionary = await capture(viewport)
	if not check(restartBlank.colored == 0, "Stop/restart does not retain prior GPU dye"):
		return
	if not check(player.advance(1.0 / 60.0) == 0, "Restarted root injects after positive delta"):
		return
	var restarted: Dictionary = await capture(viewport)
	if not check(restarted.colored > 0, "Restarted root produces fresh GPU output"):
		return
	player.stop()
	var restartStopped: Dictionary = await capture(viewport)
	if not check(restartStopped.colored == 0, "Restarted root Stop clears its 3D instance"):
		return
	print("Root Stop/restart pixel counts: ", cleared.colored, " -> blank ",
			restartBlank.colored, " -> restarted ", restarted.colored,
			" -> stopped ", restartStopped.colored)

	var first: Variant = load_player(viewport)
	var second: Variant = load_player(viewport)
	if first == null or second == null:
		return
	if not check(first.play_package(FileAccess.get_file_as_bytes("res://fluid-only.lfxp")) == 0 and
			second.play_package(FileAccess.get_file_as_bytes("res://fluid-only.lfxp")) == 0,
			"Two independent fluid effects are admitted"):
		return
	first.set_process(false)
	second.set_process(false)
	if not check(first.advance(0.1) == 0 and second.advance(0.1) == 0,
			"Independent fluid effects dispatch"):
		return
	var both: Dictionary = await capture(viewport)
	first.stop()
	var one: Dictionary = await capture(viewport)
	if not check(both.colored > 0 and one.colored > 0,
			"Stopping one effect does not clear another effect's GPU texture"):
		return
	second.stop()
	var neither: Dictionary = await capture(viewport)
	if not check(neither.colored == 0, "Disposing the last effect clears the 3D output"):
		return

	var mixed: Variant = load_player(viewport)
	if mixed == null:
		return
	if not check(mixed.play_package(FileAccess.get_file_as_bytes("res://mixed.lfxp")) == 0,
			"Version 2 mixed particle and fluid package admitted: " + mixed.get_gpu_error()):
		return
	mixed.set_process(false)
	if not check(mixed.advance(0.0) == 0 and mixed.advance(0.1) == 0,
			"Mixed legacy particle and Fluid2D work advance"):
		return
	var mixedPixels: Dictionary = await capture(viewport)
	if not check(mixedPixels.colored > 0, "Mixed package retains visible fluid output"):
		return
	mixed.stop()
	var mixedStopped: Dictionary = await capture(viewport)
	if not check(mixedStopped.colored == 0, "Mixed package Stop releases both render paths"):
		return

	var nested: Variant = load_player(viewport)
	if nested == null:
		return
	if not check(nested.play_package(FileAccess.get_file_as_bytes("res://nested.lfxp")) == 0,
			"Nested Fluid2D package admitted: " + nested.get_gpu_error()):
		return
	nested.set_process(false)
	for _frame in 8:
		if not check(nested.advance(0.05) == 0, "Nested Fluid2D Spawnable instance advances"):
			return
		await process_frame
	var nestedPixels: Dictionary = await capture(viewport)
	if not check(nestedPixels.colored > 0,
			"Spawnable Fluid2D output is visible without a per-fluid scene node"):
		return
	for _frame in 24:
		if not check(nested.advance(0.1) == 0,
				"Nested Fluid2D explicit lifetime advances: " + nested.get_gpu_error()):
			return
		await process_frame
	var expiredNested: Dictionary = await capture(viewport)
	print("Nested Spawnable pixels after explicit lifetime: ", expiredNested.colored)
	if not check(expiredNested.colored == 0,
			"Explicit child lifetime removes its fluid 3D instance: " + str(expiredNested)):
		return
	nested.stop()
	var nestedStopped: Dictionary = await capture(viewport)
	if not check(nestedStopped.colored == 0, "Nested Stop releases child output"):
		return

	var matrixFixturesVerified: bool = await verify_compiled_matrix_fixtures(viewport)
	if not matrixFixturesVerified:
		return

	var oddGrid: Variant = load_player(viewport)
	if oddGrid == null:
		return
	if not check(oddGrid.play_package(FileAccess.get_file_as_bytes("res://odd-grid.lfxp")) == 0,
			"Odd-grid Fluid2D package admitted: " + oddGrid.get_gpu_error()):
		return
	oddGrid.set_process(false)
	if not check(oddGrid.advance(0.1) == 0,
			"Odd-grid ceil dispatch stays in bounds: " + oddGrid.get_gpu_error()):
		return
	var oddGridPixels: Dictionary = await capture(viewport)
	print("23x37 odd-grid Vulkan pixels: ", oddGridPixels.colored)
	if not check(oddGridPixels.colored > 0,
			"23x37 GPU grid writes visible in-bounds dye: " + str(oddGridPixels)):
		return
	var oddGridFields := read_fluid_fields(oddGrid, "23x37 field readback")
	var oddGridMetrics := field_metrics(oddGridFields)
	oddGridMetrics["normal_boundary_max"] = velocity_normal_boundary_max(oddGridFields)
	if not check(oddGridMetrics.finite and oddGridMetrics.checked_components == 23828 and
			oddGridMetrics.max_density > 0 and
			oddGridMetrics.normal_boundary_max <= 0.00001,
			"23x37 dispatch leaves finite fields and closed normal borders: " +
				str(oddGridMetrics)):
		return
	print("Odd-grid field metrics: ", oddGridMetrics)
	oddGrid.stop()
	var oddGridStopped: Dictionary = await capture(viewport)
	if not check(oddGridStopped.colored == 0, "Odd-grid Stop releases the output"):
		return

	viewport.queue_free()
	await process_frame
	print("PASS: Godot Fluid2D GPU output, v2 mixed package, nested scope, isolation and Stop")
	quit(0)


func verify_compiled_matrix_fixtures(viewport: SubViewport) -> bool:
	var drawOwner := Node3D.new()
	drawOwner.name = "DrawMatrixFixtureOwner"
	viewport.add_child(drawOwner)
	var drawPlayer: Variant = load_player(drawOwner)
	if drawPlayer == null:
		return false
	if not check(drawPlayer.play_package(FileAccess.get_file_as_bytes(
			"res://draw-matrix.lfxp")) == 0,
			"Compiler-authored Draw Matrix fixture admitted: " + drawPlayer.get_gpu_error()):
		return false
	drawPlayer.set_process(false)
	drawOwner.position = Vector3.ZERO
	if not check(drawPlayer.advance(0.0) == 0 and drawPlayer.advance(1.0 / 60.0) == 0,
			"Authored Transform.position Draw Matrix resolves and advances: " +
				drawPlayer.get_gpu_error()):
		return false
	var authoredDraw: Dictionary = await capture(viewport)
	if not check(authoredDraw.colored > 0 and authoredDraw.centroid_x > WIDTH * 0.5 + 20.0,
			"Compiler-produced Draw Matrix translation reaches world pixels: " + str(authoredDraw)):
		return false
	drawOwner.position = Vector3(20, 0, 0)
	var authoredDrawWithMovedOwner: Dictionary = await capture(viewport)
	if not check(abs(authoredDrawWithMovedOwner.centroid_x - authoredDraw.centroid_x) < 1.0,
			"World-space authored Draw Matrix is not multiplied by its spatial owner twice: " +
				str(authoredDraw) + " -> " + str(authoredDrawWithMovedOwner)):
		return false
	print("Compiler Draw Matrix pixel centroid with owner at 0/20: ",
			authoredDraw.centroid_x, " / ", authoredDrawWithMovedOwner.centroid_x)
	print("Compiler Draw Matrix capture: ",
			save_capture(viewport, "fluid-compiled-draw-matrix.png"))
	drawPlayer.stop()
	drawOwner.queue_free()

	var matrixOwner := Node3D.new()
	matrixOwner.name = "OwnerMatrixFixtureOwner"
	matrixOwner.position = Vector3(20, 0, 0)
	viewport.add_child(matrixOwner)
	var ownerMatrixPlayer: Variant = load_player(matrixOwner)
	if ownerMatrixPlayer == null:
		return false
	if not check(ownerMatrixPlayer.play_package(FileAccess.get_file_as_bytes(
			"res://owner-matrix.lfxp")) == 0,
			"Compiler OwnerMatrix fixture admitted: " + ownerMatrixPlayer.get_gpu_error()):
		return false
	ownerMatrixPlayer.set_process(false)
	var ports: Array = ownerMatrixPlayer.get_game_io_ports()
	var hasOwnerMatrix := false
	for port in ports:
		if port.get("kind_name", "") == "owner_matrix" and int(port.get("width", 0)) == 16:
			hasOwnerMatrix = true
			break
	if not check(hasOwnerMatrix,
			"OwnerMatrix compiler fixture exposes its 16-lane named Game I/O port: " + str(ports)):
		return false
	var identity := PackedFloat64Array([
		1.0, 0.0, 0.0, 0.0,
		0.0, 1.0, 0.0, 0.0,
		0.0, 0.0, 1.0, 0.0,
		0.0, 0.0, 0.0, 1.0
	])
	if not check(ownerMatrixPlayer.set_owner_matrix(identity) == 0 and
			ownerMatrixPlayer.advance(0.0) == 0 and
			ownerMatrixPlayer.advance(1.0 / 60.0) == 0,
			"OwnerMatrix identity is applied to compiled fluid rows: " +
				ownerMatrixPlayer.get_gpu_error()):
		return false
	var ownerIdentity: Dictionary = await capture(viewport)
	if not check(ownerIdentity.colored > 0 and
			abs(ownerIdentity.centroid_x - WIDTH * 0.5) < 5.0,
			"World-space identity OwnerMatrix overrides (does not add) the Node3D owner transform: " +
				str(ownerIdentity)):
		return false
	var translatedMatrix := PackedFloat64Array([
		1.0, 0.0, 0.0, 20.0,
		0.0, 1.0, 0.0, 0.0,
		0.0, 0.0, 1.0, 0.0,
		0.0, 0.0, 0.0, 1.0
	])
	if not check(ownerMatrixPlayer.set_owner_matrix(translatedMatrix) == 0 and
			ownerMatrixPlayer.advance(1.0 / 60.0) == 0,
			"OwnerMatrix translation update resolves on the next runtime frame"):
		return false
	var ownerTranslated: Dictionary = await capture(viewport)
	if not check(ownerTranslated.colored > 0 and
			ownerTranslated.centroid_x > ownerIdentity.centroid_x + 20.0 and
			ownerTranslated.centroid_x < ownerIdentity.centroid_x + 40.0,
			"OwnerMatrix translation is applied exactly once in world pixels: " +
				str(ownerIdentity) + " -> " + str(ownerTranslated)):
		return false
	var rotatedMatrix := PackedFloat64Array([
		0.0, -1.0, 0.0, 20.0,
		1.0, 0.0, 0.0, 0.0,
		0.0, 0.0, 1.0, 0.0,
		0.0, 0.0, 0.0, 1.0
	])
	if not check(ownerMatrixPlayer.set_owner_matrix(rotatedMatrix) == 0 and
			ownerMatrixPlayer.advance(1.0 / 60.0) == 0,
			"OwnerMatrix rotation update resolves on the next runtime frame"):
		return false
	var ownerRotated: Dictionary = await capture(viewport)
	if not check(ownerRotated.colored > 0 and
			ownerRotated.centroid_x > ownerTranslated.centroid_x + 20.0,
			"OwnerMatrix rotation updates the already-running world-space fluid plane: " +
				str(ownerTranslated) + " -> " + str(ownerRotated)):
		return false
	print("OwnerMatrix identity/translate/rotate centroids: ",
			ownerIdentity.centroid_x, " -> ", ownerTranslated.centroid_x,
			" -> ", ownerRotated.centroid_x)
	print("OwnerMatrix pixel captures: ",
			save_capture(viewport, "fluid-owner-matrix-identity.png"), " / ",
			save_capture(viewport, "fluid-owner-matrix-translated.png"), " / ",
			save_capture(viewport, "fluid-owner-matrix-rotated.png"))
	ownerMatrixPlayer.stop()
	matrixOwner.queue_free()
	return true


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


func load_player(parent: Node = null) -> Variant:
	if parent == null:
		parent = root
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	if not check(player != null, "LibraEffectPlayer class is registered"):
		return null
	parent.add_child(player)
	return player


func capture(viewport: SubViewport, all_colors: bool = false) -> Dictionary:
	await process_frame
	await process_frame
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	var result := {
		"colored": 0, "translucent": 0, "centroid_x": 0.0, "centroid_y": 0.0,
		"bounds_width": 0, "bounds_height": 0
	}
	var min_x := image.get_width()
	var max_x := -1
	var min_y := image.get_height()
	var max_y := -1
	for y in range(image.get_height()):
		for x in range(image.get_width()):
			var pixel := image.get_pixel(x, y)
			if (maxf(pixel.r, maxf(pixel.g, pixel.b)) > .02) if all_colors else (pixel.r > 0.08 and pixel.r > pixel.g * 1.4 and pixel.g > pixel.b):
				result.colored += 1
				result.centroid_x += x
				result.centroid_y += y
				min_x = min(min_x, x)
				max_x = max(max_x, x)
				min_y = min(min_y, y)
				max_y = max(max_y, y)
				if pixel.r < 0.95:
					result.translucent += 1
	if result.colored > 0:
		result.centroid_x /= result.colored
		result.centroid_y /= result.colored
		result.bounds_width = max_x - min_x + 1
		result.bounds_height = max_y - min_y + 1
	return result


func read_fluid_fields(player: Variant, label: String) -> Dictionary:
	var fields: Dictionary = player._test_read_fluid_fields()
	if not check(int(fields.get("result", -1)) == 0,
			label + ": " + str(fields.get("error", "unknown readback failure"))):
		return {}
	return fields


func field_metrics(fields: Dictionary) -> Dictionary:
	var width := int(fields.get("width", 0))
	var height := int(fields.get("height", 0))
	var cells := width * height
	var names := ["velocity", "velocityScratch", "dye", "dyeScratch",
			"divergence", "pressure", "pressureScratch"]
	var finite := width > 0 and height > 0
	var checked_components := 0
	for name in names:
		var values: PackedFloat32Array = fields.get(name, PackedFloat32Array())
		if values.size() != cells * 4:
			finite = false
			continue
		for value in values:
			checked_components += 1
			if not is_finite(value):
				finite = false
	var velocity: PackedFloat32Array = fields.get("velocity", PackedFloat32Array())
	var dye: PackedFloat32Array = fields.get("dye", PackedFloat32Array())
	var pressure: PackedFloat32Array = fields.get("pressure", PackedFloat32Array())
	var pressure_scratch: PackedFloat32Array = fields.get("pressureScratch", PackedFloat32Array())
	var max_speed := 0.0
	var max_density := 0.0
	var sum_density := 0.0
	var max_pressure := 0.0
	for cell in range(cells):
		var base := cell * 4
		if velocity.size() > base + 1:
			max_speed = max(max_speed, Vector2(velocity[base], velocity[base + 1]).length())
		if dye.size() > base + 3:
			var density: float = max(0.0, dye[base + 3])
			max_density = max(max_density, density)
			sum_density += density
		if pressure.size() > base:
			max_pressure = max(max_pressure, abs(pressure[base]))
		if pressure_scratch.size() > base:
			max_pressure = max(max_pressure, abs(pressure_scratch[base]))
	return {
		"finite": finite,
		"width": width,
		"height": height,
		"pressure_iterations": int(fields.get("pressureIterations", 0)),
		"checked_components": checked_components,
		"max_speed": max_speed,
		"max_density": max_density,
		"sum_density": sum_density,
		"max_pressure": max_pressure
	}


func velocity_normal_boundary_max(fields: Dictionary) -> float:
	var width := int(fields.get("width", 0))
	var height := int(fields.get("height", 0))
	var velocity: PackedFloat32Array = fields.get("velocity", PackedFloat32Array())
	if width < 2 or height < 2 or velocity.size() != width * height * 4:
		return INF
	var maximum := 0.0
	for y in range(height):
		maximum = max(maximum, abs(velocity[(y * width) * 4]))
		maximum = max(maximum, abs(velocity[(y * width + width - 1) * 4]))
	for x in range(width):
		maximum = max(maximum, abs(velocity[x * 4 + 1]))
		maximum = max(maximum,
				abs(velocity[((height - 1) * width + x) * 4 + 1]))
	return maximum


func all_fields_zero(fields: Dictionary) -> bool:
	for name in ["velocity", "velocityScratch", "dye", "dyeScratch",
			"divergence", "pressure", "pressureScratch"]:
		var values: PackedFloat32Array = fields.get(name, PackedFloat32Array())
		if values.is_empty():
			return false
		for value in values:
			if value != 0.0:
				return false
	return true


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
