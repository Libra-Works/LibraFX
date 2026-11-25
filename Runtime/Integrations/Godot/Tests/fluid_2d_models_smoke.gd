extends "res://fluid_2d_gpu_smoke.gd"


func run() -> void:
	const EXTENSION := "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK, "Extension loads"):
			return
	var viewport := make_viewport()
	var player: Variant = load_player(viewport)
	player.set_process(false)
	var results := {}
	for mode: String in ["free-surface", "free-surface-source", "shallow-water", "shallow-water-source", "combustion", "cold-fuel", "force-acceleration", "force-noise", "force-curl", "force-masked", "force-masked-empty"]:
		if not check(player.play_package(FileAccess.get_file_as_bytes("res://" + mode + ".lfxp")) == 0, mode + " loads: " + player.get_gpu_error()):
			return
		var initial: Dictionary = player._test_read_fluid_fields()
		if not check(initial.get("result", -1) == 0, mode + " initial fields available"):
			return
		var water := mode.begins_with("free-surface") or mode.begins_with("shallow-water")
		var lane := 0 if mode.begins_with("shallow-water") else 3
		var initial_mass := lane_sum(initial.dye, lane)
		for frame in range(120):
			if not check(player.advance(1.0 / 60.0) == 0, mode + " advances: " + player.get_gpu_error()):
				return
		var fields: Dictionary = player._test_read_fluid_fields()
		if not finite_fields(fields, mode):
			return
		var mass := lane_sum(fields.dye, lane)
		if mode == "force-masked-empty" and not check(max_abs(fields.velocity) == 0, "Density mask suppresses all force in an empty field"):
			return
		if mode == "force-masked" and not check(max_abs(fields.velocity) > .000001, "Cell position/time -> curl -> density multiply evaluates on the GPU"):
			return
		if water:
			if mode.ends_with("-source"):
				if not check(mass > initial_mass + .01, mode + " adds water"):
					return
			elif not check(absf(mass - initial_mass) <= maxf(.0001, initial_mass * .0001), mode + " preserves closed-domain mass: " + str([initial_mass, mass])):
				return
			if mode.begins_with("shallow-water"):
				if not check(lane_sum(fields.dye, 3) == 0, mode + " does not activate the numerical depth clamp"):
					return
				if mode == "shallow-water":
					for value in fields.velocity:
						if not check(absf(value) < .000001, "Lake at rest stays at rest"):
							return
			elif mode == "free-surface":
				if not check(max_abs(fields.velocity) < .15, "Pool remains close to hydrostatic rest: " + str(max_abs(fields.velocity))):
					return
		if mode == "cold-fuel":
			if not check(lane_sum(fields.dye, 0) > 0 and lane_sum(fields.dye, 2) == 0 and lane_sum(fields.dye, 3) == 0, "Cold fuel does not ignite"):
				return
		if mode == "combustion":
			if not check(lane_sum(fields.dye, 1) > 0 and lane_sum(fields.dye, 2) > 0 and lane_sum(fields.dye, 3) > 0, "Hot fuel produces temperature, smoke and reaction"):
				return
		var camera := viewport.get_child(0) as Camera3D
		if mode.begins_with("shallow-water"):
			camera.position = Vector3(0, 1000, 0)
			camera.rotation_degrees = Vector3(-90, 0, 0)
			camera.size = 1600
		else:
			camera.position = Vector3(0, 0, 100)
			camera.rotation = Vector3.ZERO
			camera.size = 160
		var pixels: Dictionary = await capture(viewport, true)
		if mode != "cold-fuel" and mode != "force-masked-empty" and not check(pixels.colored > 10, mode + " dedicated renderer produces framebuffer pixels: " + str(pixels)):
			return
		save_capture(viewport, "fluid-model-" + mode + ".png")
		results[mode] = {"initial_mass": initial_mass, "mass": mass, "max_velocity": max_abs(fields.velocity), "pixels": pixels}
		player.stop()
		var blank: Dictionary = await capture(viewport, true)
		if not check(blank.colored == 0, mode + " Stop removes dedicated renderer"):
			return
	if not await verify_reaction(player):
		return
	print("PASS Fluid2D GPU models and dedicated renderers: ", results)
	quit(0)


func verify_reaction(player: Variant) -> bool:
	if not check(player.play_package(FileAccess.get_file_as_bytes("res://combustion-oracle.lfxp")) == 0, "Reaction oracle loads"):
		return false
	for frame in range(60):
		if not check(player.advance(1.0 / 60.0) == 0, "Reaction warms"):
			return false
	if not check(player._test_disable_fluid_source_and_zero_forces() == 0, "Reaction source switches off"):
		return false
	var before: Dictionary = player._test_read_fluid_fields()
	if not check(player.advance(1.0 / 60.0) == 0, "Reaction oracle single step"):
		return false
	var after: Dictionary = player._test_read_fluid_fields()
	var reacted := 0
	for cell in range(24 * 40):
		var i := cell * 4
		var fuel: float = before.dye[i]
		var temperature: float = before.dye[i + 1]
		var ignition := clampf((temperature - .5) / .5, 0, 1)
		var burnt := fuel * (1.0 - exp(-4.0 * ignition / 60.0))
		if not check(absf(after.dye[i] - (fuel - burnt)) < .00002 and absf(after.dye[i + 1] - (temperature + burnt * 2)) < .00002 and absf(after.dye[i + 2] - before.dye[i + 2] - burnt) < .00002, "GPU fuel/heat/smoke match independent reaction oracle"):
			return false
		if burnt > .000001:
			reacted += 1
	if not check(reacted > 0, "Reaction oracle exercises burning cells"):
		return false
	player.stop()
	return true


func finite_fields(fields: Dictionary, mode: String) -> bool:
	if not check(fields.get("result", -1) == 0, mode + " diagnostic read succeeds"):
		return false
	for name in ["velocity", "velocityScratch", "dye", "dyeScratch", "divergence", "pressure", "pressureScratch"]:
		for value in fields[name]:
			if not check(is_finite(value), mode + " finite " + name):
				return false
	return true


func lane_sum(values: PackedFloat32Array, lane: int) -> float:
	var result := 0.0
	for index in range(lane, values.size(), 4):
		result += values[index]
	return result


func max_abs(values: PackedFloat32Array) -> float:
	var result := 0.0
	for value in values:
		result = maxf(result, absf(value))
	return result
