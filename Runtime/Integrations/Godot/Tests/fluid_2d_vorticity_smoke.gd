extends SceneTree


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	if not GDExtensionManager.is_extension_loaded("res://liberafx.gdextension"):
		if not check(GDExtensionManager.load_extension("res://liberafx.gdextension") == GDExtensionManager.LOAD_STATUS_OK, "Extension loads"):
			return
	if not check(RenderingServer.get_rendering_device() != null, "Vorticity needs a real RenderingDevice"):
		return
	var owner := Node3D.new()
	root.add_child(owner)
	var baseline: Variant = ClassDB.instantiate("LibraEffectPlayer")
	var confined: Variant = ClassDB.instantiate("LibraEffectPlayer")
	owner.add_child(baseline)
	owner.add_child(confined)
	baseline.set_process(false)
	confined.set_process(false)
	var base_package := FileAccess.get_file_as_bytes("res://fluid-only.lfxp")
	var confined_package := FileAccess.get_file_as_bytes("res://vorticity.lfxp")
	if not check(baseline.play_package(base_package) == 0, "Legacy fluid package loads: " + baseline.get_gpu_error()):
		return
	if not check(confined.play_package(confined_package) == 0, "Compiler vorticity package loads: " + confined.get_gpu_error()):
		return
	for step in range(30):
		if not check(baseline.advance(1.0 / 60.0) == 0 and confined.advance(1.0 / 60.0) == 0, "Both fields advance"):
			return
	await RenderingServer.frame_post_draw
	var original: Dictionary = baseline._test_read_fluid_fields()
	var actual: Dictionary = confined._test_read_fluid_fields()
	if not check(original.get("width", 0) == 24 and actual.get("height", 0) == 40, "Non-square GPU fields can be read"):
		return
	var before: PackedFloat32Array = original.velocity
	var after: PackedFloat32Array = actual.velocity
	if not check(before.size() == after.size() and after.size() == 24 * 40 * 4, "Field counts match"):
		return
	var maximum_difference := 0.0
	for index in range(after.size()):
		if not check(is_finite(after[index]), "Confined field stays finite"):
			return
		maximum_difference = maxf(maximum_difference, absf(after[index] - before[index]))
	for y in range(40):
		for x in range(24):
			var offset := (y * 24 + x) * 4
			if x == 0 or x == 23:
				if not check(absf(after[offset]) < 0.000001, "Closed X boundary"):
					return
			if y == 0 or y == 39:
				if not check(absf(after[offset + 1]) < 0.000001, "Closed Y boundary"):
					return
	if not check(maximum_difference > 0.000001, "Confinement changes velocity relative to zero-strength control"):
		return
	baseline.stop()
	if not check(confined.advance(0.0) == 0, "Stopping a different owner leaves confined storage valid"):
		return
	var held: Dictionary = confined._test_read_fluid_fields()
	if not check(held.velocity == after, "Paused confined field survives other owner retirement"):
		return
	confined.stop()
	if not check(confined.play_package(confined_package) == 0 and confined.advance(0.0) == 0, "Confined effect restarts"):
		return
	var restarted: Dictionary = confined._test_read_fluid_fields()
	for value in restarted.velocity:
		if not check(value == 0.0, "Restart clears the velocity field"):
			return
	confined.stop()
	owner.queue_free()
	await process_frame
	await process_frame
	print("PASS Fluid2D Vulkan vorticity: 24x40, 30 steps, max velocity difference=", maximum_difference, "; finite fields, closed boundaries, independent retirement, restart")
	quit(0)


func check(condition: bool, message: String) -> bool:
	if not condition:
		push_error(message)
		quit(1)
	return condition
